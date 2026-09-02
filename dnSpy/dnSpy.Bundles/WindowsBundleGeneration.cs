// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.NET.HostModel.Bundle;

namespace dnSpy.Bundles {
	/// <summary>
	/// Generates a new Windows single-file bundle in a private temporary directory.
	/// </summary>
	/// <remarks>
	/// The result owns the temporary directory and must be disposed by the caller. This type does
	/// not publish the generated file or replace an existing destination; those operations belong
	/// to the later save/publication stage.
	/// </remarks>
	public sealed class WindowsBundleGeneration : IDisposable {
		readonly WindowsAppHostReconstruction reconstruction;
		int disposed;

		internal WindowsBundleGeneration(WindowsAppHostReconstruction reconstruction, string bundlePath) {
			this.reconstruction = reconstruction ?? throw new ArgumentNullException(nameof(reconstruction));
			BundlePath = bundlePath ?? throw new ArgumentNullException(nameof(bundlePath));
		}

		/// <summary>Path to the generated bundle in the private temporary directory.</summary>
		public string BundlePath { get; }

		/// <summary>Alias for <see cref="BundlePath"/>.</summary>
		public string OutputPath => BundlePath;

		/// <summary>Private directory owned by this generated result.</summary>
		public string TemporaryDirectory => reconstruction.TemporaryDirectory;

		/// <summary>Releases the generated bundle and all temporary input files.</summary>
		public void Dispose() {
			if (Interlocked.Exchange(ref disposed, 1) == 0)
				reconstruction.Dispose();
		}

		/// <summary>
		/// Allows a caller that only needs the path to consume the disposable result. Such a caller
		/// remains responsible for disposing the result before returning from the save operation.
		/// </summary>
		public static implicit operator string(WindowsBundleGeneration generation) {
			if (generation is null)
				throw new ArgumentNullException(nameof(generation));
			return generation.BundlePath;
		}
	}

	/// <summary>
	/// Builds the private HostModel input set for a parsed Windows bundle and invokes the official
	/// vendored HostModel bundler.
	/// </summary>
	/// <remarks>
	/// The original bundle is opened read-only. All logical entry content is copied to generated
	/// flat temporary names; manifest relative paths are never interpreted as disk paths.
	/// </remarks>
	public sealed class WindowsBundleRebuilder : IWindowsBundleGenerator {
		const string RuntimeConfigSuffix = ".runtimeconfig.json";
		const string DepsSuffix = ".deps.json";
		const int CopyBufferSize = 64 * 1024;

		/// <summary>Generates a bundle using the current logical workspace bytes.</summary>
		/// <param name="workspace">The parsed workspace whose source remains unchanged.</param>
		/// <param name="cancellationToken">Cancellation checked before and during materialization.</param>
		/// <returns>A disposable private generated bundle.</returns>
		public WindowsBundleGeneration Generate(BundleWorkspace workspace,
			CancellationToken cancellationToken = default) {
			if (workspace is null)
				throw new ArgumentNullException(nameof(workspace));
			return GenerateCore(workspace, cancellationToken);
		}

		/// <summary>Alias for <see cref="Generate(BundleWorkspace, CancellationToken)"/>.</summary>
		public WindowsBundleGeneration Rebuild(BundleWorkspace workspace,
			CancellationToken cancellationToken = default) => Generate(workspace, cancellationToken);

		/// <summary>Alias matching the official HostModel operation name.</summary>
		public WindowsBundleGeneration GenerateBundle(BundleWorkspace workspace,
			CancellationToken cancellationToken = default) => Generate(workspace, cancellationToken);

		WindowsBundleGeneration GenerateCore(BundleWorkspace workspace,
			CancellationToken cancellationToken) {
			ThrowIfCancellationRequested(cancellationToken);
			BundleFile bundle = workspace.Bundle;
			bundle.EnsureNotDisposed();
			EnsureEligible(workspace);

			string hostName = GetHostName(bundle.Filename);
			Version targetFramework = GetTargetFramework(bundle.Manifest.MajorVersion);
			string appAssemblyName = GetAppAssemblyName(bundle, hostName);
			BundleOptions options = GetOptions(bundle);

			// The reconstructor creates the one private root used by this operation. Entry files and
			// the HostModel output are placed below it, so reconstruction and generation have one
			// ownership/lifetime boundary and no source file is ever opened for writing.
			WindowsAppHostReconstruction? reconstruction = null;
			try {
				ThrowIfCancellationRequested(cancellationToken);
				reconstruction = new WindowsAppHostReconstructor().Reconstruct(workspace);
				string outputDirectory = CreateOutputDirectory(reconstruction.TemporaryDirectory);
				List<FileSpec> fileSpecs = MaterializeInputs(workspace, reconstruction, hostName,
					cancellationToken);
				ValidateHostInput(fileSpecs, reconstruction.HostPath, hostName);
				ThrowIfCancellationRequested(cancellationToken);

				var bundler = new Bundler(hostName, outputDirectory, options,
					System.Runtime.InteropServices.OSPlatform.Windows,
					System.Runtime.InteropServices.Architecture.X64,
					targetFramework,
					diagnosticOutput: false,
					appAssemblyName: appAssemblyName,
					macosCodesign: false);
				string bundlePath = bundler.GenerateBundle(fileSpecs);
				ThrowIfCancellationRequested(cancellationToken);
				if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
					throw new InvalidDataException("HostModel did not produce a bundle file.");

				var result = new WindowsBundleGeneration(reconstruction, bundlePath);

				reconstruction = null;
				return result;
			}
			catch {
				reconstruction?.Dispose();
				throw;
			}
		}

		static string GetHostName(string filename) {
			if (string.IsNullOrWhiteSpace(filename))
				throw new ArgumentException("The bundle source filename is empty.", nameof(filename));
			string hostName = Path.GetFileName(filename);
			if (string.IsNullOrWhiteSpace(hostName) || hostName == "." || hostName == ".." ||
				hostName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new ArgumentException("The bundle source filename does not provide a valid host name.",
					nameof(filename));
			return hostName;
		}

		static void EnsureEligible(BundleWorkspace workspace) {
			WindowsBundleEligibilityResult eligibility =
				new WindowsBundleEligibilityInspector().Inspect(workspace);
			if (!eligibility.IsEligible)
				throw new InvalidOperationException(eligibility.Message);
		}

		static Version GetTargetFramework(uint manifestMajorVersion) => manifestMajorVersion switch {
			1u => new Version(3, 1),
			2u => new Version(5, 0),
			6u => new Version(6, 0),
			_ => throw new InvalidOperationException(
				"The bundle manifest version is not supported by the Windows HostModel rebuilder."),
		};

		static BundleOptions GetOptions(BundleFile bundle) {
			// Eligibility rejects unknown raw entries before this point, so only native binaries and
			// symbols need explicit inventory flags. BundleAllContent is the official v3-compatible
			// mode mapping for both v2 (.NET 5) and v6 (.NET 6+) manifests.
			BundleOptions options = BundleOptions.BundleNativeBinaries |
				BundleOptions.BundleSymbolFiles;
			if ((bundle.Manifest.MajorVersion == 2 || bundle.Manifest.MajorVersion == 6) &&
				(bundle.Manifest.Flags & BundleManifestFlags.NetcoreApp3CompatMode) != 0)
				options |= BundleOptions.BundleAllContent;
			if (bundle.Manifest.MajorVersion == 6 && bundle.Entries.Any(entry => entry.IsCompressed))
				options |= BundleOptions.EnableCompression;
			return options;
		}

		static string GetAppAssemblyName(BundleFile bundle, string hostName) {
			// Runtimeconfig is the authoritative app name when present. A deps entry is the fallback
			// used by framework-dependent layouts that omit runtimeconfig from the manifest.
			string? name = FindConfigBaseName(bundle, RuntimeConfigSuffix) ??
				FindConfigBaseName(bundle, DepsSuffix);
			if (string.IsNullOrWhiteSpace(name))
				name = RemoveExtension(hostName);
			if (string.IsNullOrWhiteSpace(name))
				throw new InvalidDataException("The bundle does not provide a usable application assembly name.");
			return name!;
		}

		static string? FindConfigBaseName(BundleFile bundle, string suffix) {
			foreach (BundleEntry entry in bundle.Entries) {
				string path = entry.RelativePath;
				if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
					continue;
				int slash = path.LastIndexOf('/');
				string fileName = slash < 0 ? path : path.Substring(slash + 1);
				string baseName = fileName.Substring(0, fileName.Length - suffix.Length);
				if (!string.IsNullOrWhiteSpace(baseName))
					return baseName;
			}
			return null;
		}

		static string RemoveExtension(string filename) {
			int dot = filename.LastIndexOf('.');
			return dot <= 0 ? filename : filename.Substring(0, dot);
		}

		static string CreateOutputDirectory(string temporaryDirectory) {
			string directory = Path.Combine(temporaryDirectory, "generated");
			try {
				Directory.CreateDirectory(directory);
				return directory;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
				ex is ArgumentException || ex is NotSupportedException) {
				throw new IOException("The private HostModel output directory could not be created.", ex);
			}
		}

		static List<FileSpec> MaterializeInputs(BundleWorkspace workspace,
			WindowsAppHostReconstruction reconstruction, string hostName,
			CancellationToken cancellationToken) {
			var fileSpecs = new List<FileSpec>(workspace.Bundle.Entries.Count + 1) {
				// This exact first input is required by HostModel. Its relative path must be the same
				// hostName passed to Bundler, while SourcePath is the reconstructed clean host.
				new FileSpec(reconstruction.HostPath, hostName),
			};

			foreach (BundleEntry entry in workspace.Bundle.Entries) {
				ThrowIfCancellationRequested(cancellationToken);
				string temporaryEntryPath = Path.Combine(reconstruction.TemporaryDirectory,
					"entry-" + entry.Index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".bin");
				MaterializeEntry(workspace, entry, temporaryEntryPath, cancellationToken);
				fileSpecs.Add(new FileSpec(temporaryEntryPath, entry.RelativePath));
			}
			return fileSpecs;
		}

		static void MaterializeEntry(BundleWorkspace workspace, BundleEntry entry,
			string destination, CancellationToken cancellationToken) {
			try {
				using Stream source = workspace.OpenCurrentRead(entry);
				using FileStream target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
					FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
				byte[] buffer = new byte[CopyBufferSize];
				while (true) {
					ThrowIfCancellationRequested(cancellationToken);
					int read = source.Read(buffer, 0, buffer.Length);
					if (read == 0)
						break;
					target.Write(buffer, 0, read);
				}
				target.Flush(flushToDisk: true);
			}
			catch {
				WindowsAppHostReconstruction.TryDelete(destination);
				throw;
			}
		}

		static void ValidateHostInput(IReadOnlyList<FileSpec> fileSpecs,
			string reconstructedHostPath, string hostName) {
			if (fileSpecs is null)
				throw new ArgumentNullException(nameof(fileSpecs));
			int matchCount = 0;
			FileSpec? matched = null;
			foreach (FileSpec fileSpec in fileSpecs) {
				if (fileSpec is null)
					throw new ArgumentException("The HostModel input contains a null file specification.",
						nameof(fileSpecs));
				if (!fileSpec.IsValid())
					throw new ArgumentException("The HostModel input contains an invalid file specification.",
						nameof(fileSpecs));
				if (string.Equals(fileSpec.BundleRelativePath, hostName, StringComparison.Ordinal)) {
					matchCount++;
					matched = fileSpec;
				}
			}
			if (matchCount != 1 || matched is null)
				throw new ArgumentException("The HostModel input must contain exactly one host file specification.",
					nameof(fileSpecs));
			if (!string.Equals(matched.SourcePath, reconstructedHostPath, StringComparison.Ordinal))
				throw new ArgumentException("The HostModel host file specification does not match the reconstructed host.",
					nameof(fileSpecs));
			if (!File.Exists(matched.SourcePath))
				throw new FileNotFoundException("The reconstructed host file specification is missing.",
					matched.SourcePath);
			if (!ReferenceEquals(fileSpecs[0], matched))
				throw new ArgumentException("The reconstructed host file specification must be first.",
					nameof(fileSpecs));
		}

		static void ThrowIfCancellationRequested(CancellationToken cancellationToken) =>
			cancellationToken.ThrowIfCancellationRequested();
	}
}
