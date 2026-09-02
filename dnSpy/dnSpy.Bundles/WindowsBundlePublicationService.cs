// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading;

namespace dnSpy.Bundles {
	/// <summary>
	/// Validates a private HostModel generation and publishes it to a new destination atomically.
	/// </summary>
	/// <remarks>
	/// The source bundle is never opened for writing. Validation happens before a destination-side
	/// staging file is created, and an existing destination is replaced only after the complete
	/// generated file has been copied and flushed. The generated result is always disposed, which
	/// removes its private HostModel inputs and output after the operation.
	/// </remarks>
	public sealed class WindowsBundlePublicationService {
		readonly IWindowsBundleGenerator generator;

		/// <summary>Creates a publication service using the official HostModel rebuilder.</summary>
		public WindowsBundlePublicationService()
			: this(new WindowsBundleRebuilder()) {
		}

		/// <summary>
		/// Creates a publication service using a supplied generation implementation.
		/// </summary>
		/// <param name="generator">Generator used to produce a private bundle.</param>
		public WindowsBundlePublicationService(IWindowsBundleGenerator generator) {
			this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
		}

		/// <summary>
		/// Generates, validates, and publishes a bundle to <paramref name="destinationPath"/>.
		/// </summary>
		/// <returns>The canonical destination path.</returns>
		/// <exception cref="ArgumentException">
		/// Thrown when the destination is canonically equal to the source.
		/// </exception>
		public string Publish(BundleWorkspace workspace, string destinationPath,
			CancellationToken cancellationToken = default) {
			if (workspace is null)
				throw new ArgumentNullException(nameof(workspace));
			if (destinationPath is null)
				throw new ArgumentNullException(nameof(destinationPath));
			if (String.IsNullOrWhiteSpace(destinationPath))
				throw new ArgumentException("The bundle destination path is empty.", nameof(destinationPath));

			workspace.Bundle.EnsureNotDisposed();
			string sourcePath = GetCanonicalPath(workspace.Bundle.Filename, nameof(workspace));
			string destination = GetCanonicalPath(destinationPath, nameof(destinationPath));
			if (PathsEqual(sourcePath, destination))
				throw new ArgumentException(
					"The bundle destination must differ from the source bundle.", nameof(destinationPath));

			ThrowIfCancellationRequested(cancellationToken);
			WindowsBundleGeneration? generated = null;
			string? stagedPath = null;
			try {
				generated = generator.Generate(workspace, cancellationToken);
				if (generated is null)
					throw new InvalidDataException("The bundle generator returned no generated output.");
				ThrowIfCancellationRequested(cancellationToken);
				ValidateGeneratedBundle(workspace, generated.BundlePath, cancellationToken);
				ThrowIfCancellationRequested(cancellationToken);
				stagedPath = StageForPublication(generated.BundlePath, destination, cancellationToken);
				ThrowIfCancellationRequested(cancellationToken);
				PublishStagedFile(stagedPath, destination);
				stagedPath = null;
				return destination;
			}
			finally {
				if (stagedPath is not null)
					WindowsAppHostReconstruction.TryDelete(stagedPath);
				generated?.Dispose();
			}
		}

		/// <summary>
		/// Validates a generated bundle against the source workspace's ordered logical inventory.
		/// </summary>
		static void ValidateGeneratedBundle(BundleWorkspace workspace, string generatedPath,
			CancellationToken cancellationToken = default) {
			if (workspace is null)
				throw new ArgumentNullException(nameof(workspace));
			if (generatedPath is null)
				throw new ArgumentNullException(nameof(generatedPath));
			if (String.IsNullOrWhiteSpace(generatedPath))
				throw new ArgumentException("The generated bundle path is empty.", nameof(generatedPath));
			workspace.Bundle.EnsureNotDisposed();
			ThrowIfCancellationRequested(cancellationToken);

			BundleOpenResult opened;
			try {
				opened = new BundleReader().Open(generatedPath);
			}
			catch (Exception ex) when (IsGeneratedReadFailure(ex)) {
				throw new InvalidDataException("The generated bundle could not be reopened safely.", ex);
			}
			if (opened.Status != BundleOpenStatus.Success || opened.Bundle is null)
				throw new InvalidDataException("The generated output is not a valid .NET single-file bundle.");

			using BundleFile generated = opened.Bundle;
			ValidateManifest(workspace.Bundle, generated);
			if (workspace.Bundle.Entries.Count != generated.Entries.Count)
				throw new InvalidDataException("The generated bundle entry count does not match the source bundle.");

			for (int index = 0; index < workspace.Bundle.Entries.Count; index++) {
				ThrowIfCancellationRequested(cancellationToken);
				BundleEntry sourceEntry = workspace.Bundle.Entries[index];
				BundleEntry generatedEntry = generated.Entries[index];
				ValidateEntryMetadata(workspace.Bundle.Manifest.MajorVersion, sourceEntry, generatedEntry, index);
				CompareLogicalContent(workspace, sourceEntry, generatedEntry, cancellationToken);
			}

			// Keep the replacement requirement explicit. The complete ordered comparison above already
			// covers these entries, but this check ensures a future inventory comparison cannot omit a
			// modified entry accidentally.
			foreach (BundleEntry replacement in workspace.ModifiedEntries) {
				ThrowIfCancellationRequested(cancellationToken);
				BundleEntry generatedEntry = generated.Entries[replacement.Index];
				CompareLogicalContent(workspace, replacement, generatedEntry, cancellationToken);
			}
		}

		static void ValidateManifest(BundleFile source, BundleFile generated) {
			if (source.Manifest.MajorVersion != generated.Manifest.MajorVersion ||
				source.Manifest.MinorVersion != generated.Manifest.MinorVersion)
				throw new InvalidDataException("The generated bundle manifest version does not match the source.");
			if (source.Manifest.Flags != generated.Manifest.Flags)
				throw new InvalidDataException("The generated bundle manifest flags do not match the source.");
		}

		static void ValidateEntryMetadata(uint manifestVersion, BundleEntry source,
			BundleEntry generated, int index) {
			if (!String.Equals(source.RelativePath, generated.RelativePath, StringComparison.Ordinal))
				throw new InvalidDataException("The generated bundle entry path at index " + index +
					" does not match the source inventory.");

			if (manifestVersion == 1) {
				// v1 has no serialized type information. Raw zero and Unknown are the format-defined
				// semantics and must remain true even though the HostModel preflight inferred input kinds.
				if (source.RawFileType != 0 || source.FileType != BundleFileType.Unknown ||
					generated.RawFileType != 0 || generated.FileType != BundleFileType.Unknown)
					throw new InvalidDataException("The generated v1 bundle does not preserve raw-zero file types.");
			}
			else if (manifestVersion == 2 || manifestVersion == 6) {
				if (source.RawFileType != generated.RawFileType || source.FileType != generated.FileType)
					throw new InvalidDataException("The generated bundle entry type at index " + index +
						" does not match the source inventory.");
			}
			else {
				throw new InvalidDataException("The generated bundle manifest version is unsupported.");
			}
		}

		static void CompareLogicalContent(BundleWorkspace workspace, BundleEntry source,
			BundleEntry generated, CancellationToken cancellationToken) {
			using Stream expected = workspace.OpenCurrentRead(source);
			using Stream actual = generated.OpenLogicalRead();
			byte[] expectedBuffer = new byte[64 * 1024];
			byte[] actualBuffer = new byte[64 * 1024];
			while (true) {
				ThrowIfCancellationRequested(cancellationToken);
				int expectedCount = Fill(expected, expectedBuffer);
				int actualCount = Fill(actual, actualBuffer);
				if (expectedCount != actualCount)
					throw new InvalidDataException("The generated bundle logical content does not match the workspace.");
				if (expectedCount == 0)
					return;
				for (int index = 0; index < expectedCount; index++) {
					if (expectedBuffer[index] != actualBuffer[index])
						throw new InvalidDataException("The generated bundle logical content does not match the workspace.");
				}
			}
		}

		static int Fill(Stream stream, byte[] buffer) {
			int count = 0;
			while (count < buffer.Length) {
				int read = stream.Read(buffer, count, buffer.Length - count);
				if (read < 0)
					throw new InvalidDataException("A bundle content stream returned an invalid read length.");
				if (read == 0)
					break;
				count = checked(count + read);
			}
			return count;
		}

		static string StageForPublication(string generatedPath, string destination,
			CancellationToken cancellationToken) {
			string? parent = Path.GetDirectoryName(destination);
			if (String.IsNullOrEmpty(parent))
				throw new IOException("The bundle destination does not have a parent directory.");
			string staged = Path.Combine(parent, ".dnspy-bundle-" + Guid.NewGuid().ToString("N") + ".tmp");
			try {
				ThrowIfCancellationRequested(cancellationToken);
				using (FileStream source = new FileStream(generatedPath, FileMode.Open, FileAccess.Read,
					FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
				using (FileStream target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
					FileShare.None, 64 * 1024, FileOptions.SequentialScan)) {
					byte[] buffer = new byte[64 * 1024];
					while (true) {
						ThrowIfCancellationRequested(cancellationToken);
						int read = source.Read(buffer, 0, buffer.Length);
						if (read == 0)
							break;
						target.Write(buffer, 0, read);
					}
					target.Flush(flushToDisk: true);
				}
				return staged;
			}
			catch {
				WindowsAppHostReconstruction.TryDelete(staged);
				throw;
			}
		}

		static void PublishStagedFile(string stagedPath, string destination) {
			try {
				if (File.Exists(destination))
					File.Replace(stagedPath, destination, destinationBackupFileName: null);
				else
					File.Move(stagedPath, destination);
			}
			catch (Exception ex) when (IsPublicationFailure(ex)) {
				throw new IOException("The validated bundle could not be published atomically.", ex);
			}
		}

		static string GetCanonicalPath(string path, string parameterName) {
			try {
				return Path.GetFullPath(path);
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
				ex is PathTooLongException) {
				throw new ArgumentException("The bundle path is invalid.", parameterName, ex);
			}
		}

		static bool PathsEqual(string left, string right) =>
			String.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);

		static void ThrowIfCancellationRequested(CancellationToken cancellationToken) =>
			cancellationToken.ThrowIfCancellationRequested();

		static bool IsGeneratedReadFailure(Exception ex) => ex is IOException ||
			ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException ||
			ex is PathTooLongException;

		static bool IsPublicationFailure(Exception ex) => ex is IOException ||
			ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException ||
			ex is PlatformNotSupportedException;
	}
}
