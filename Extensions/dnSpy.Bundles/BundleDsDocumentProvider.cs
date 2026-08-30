// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel.Composition;
using System.IO;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Detects official single-file bundles before dnSpy's normal document provider.
	/// </summary>
	[Export(typeof(IDsDocumentProvider))]
	public sealed class BundleDsDocumentProvider : IDsDocumentProvider {
		// Keep the provider ahead of the default provider while leaving room for other
		// specialized providers. The default provider deliberately remains last.
		public const double ProviderOrder = 1000d;

		readonly object cacheLock = new object();
		CachedFileProbe? cachedProbe;
		readonly Func<string, BundleOpenResult> openBundle;

		/// <summary>Creates a provider using the production bundle reader.</summary>
		public BundleDsDocumentProvider()
			: this(static filename => new BundleReader().Open(filename)) {
		}

		/// <summary>
		/// Creates a provider with a reader delegate. The delegate is a narrow deterministic
		/// test seam for I/O failures; production composition uses the parameterless constructor.
		/// </summary>
		public BundleDsDocumentProvider(Func<string, BundleOpenResult> openBundle) =>
			this.openBundle = openBundle ?? throw new ArgumentNullException(nameof(openBundle));

		/// <inheritdoc/>
		public double Order => ProviderOrder;

		/// <inheritdoc/>
		public IDsDocument? Create(IDsDocumentService documentService, DsDocumentInfo documentInfo) {
			if (!TryGetCandidateFilename(documentInfo, out string filename))
				return null;

			if (!IsExecutable(filename))
				return null;

			BundleOpenResult result;
			try {
				result = openBundle(filename);
			}
			catch (IOException) {
				// An executable magic is only a cheap candidate check. Until the reader
				// proves that the official marker is present, let the default provider
				// retain ownership of ordinary files and transient I/O failures.
				return null;
			}
			catch (UnauthorizedAccessException) {
				return null;
			}
			catch (ArgumentException) {
				return null;
			}

			switch (result.Status) {
				case BundleOpenStatus.NotBundle:
					return null;
				case BundleOpenStatus.Success:
					if (result.Bundle is null)
						return null;
					return new BundleDsDocument(documentInfo, result.Bundle,
						assemblyResolver: documentService is null ? null : documentService.AssemblyResolver,
						documentService: documentService);
				case BundleOpenStatus.InvalidBundle:
				case BundleOpenStatus.UnsupportedVersion:
					return new BundleErrorDocument(documentInfo, result.Status,
						result.Error ?? new BundleReadError(BundleReadErrorCode.Unknown,
							"The .NET single-file bundle is invalid."));
				default:
					return null;
			}
		}

		/// <inheritdoc/>
		public IDsDocumentNameKey? CreateKey(IDsDocumentService documentService, DsDocumentInfo documentInfo) {
			if (!TryGetCandidateFilename(documentInfo, out string filename) || !IsExecutable(filename))
				return null;
			return new FilenameKey(filename);
		}

		static bool TryGetCandidateFilename(DsDocumentInfo documentInfo, out string filename) {
			filename = string.Empty;
			// A bundle is an on-disk executable. In-memory documents and resolver/reference
			// identities must continue through their existing providers.
			if (documentInfo.Type != DocumentConstants.DOCUMENTTYPE_FILE ||
				string.IsNullOrEmpty(documentInfo.Name))
				return false;
			filename = documentInfo.Name;
			return true;
		}

		bool IsExecutable(string filename) {
			FileProbeState state;
			try {
				string canonicalFilename = Path.GetFullPath(filename);
				var fileInfo = new FileInfo(canonicalFilename);
				if (!fileInfo.Exists)
					return false;
				state = new FileProbeState(canonicalFilename, fileInfo.Length, fileInfo.LastWriteTimeUtc);

				lock (cacheLock) {
					if (cachedProbe is not null && cachedProbe.Matches(state))
						return cachedProbe.IsExecutable;
				}

				bool isExecutable = ReadExecutableMagic(canonicalFilename, state.Length);
				lock (cacheLock)
					cachedProbe = new CachedFileProbe(state, isExecutable);
				return isExecutable;
			}
			catch (IOException) {
				return false;
			}
			catch (UnauthorizedAccessException) {
				return false;
			}
			catch (ArgumentException) {
				return false;
			}
		}

		static bool ReadExecutableMagic(string filename, long length) {
			if (length < 2)
				return false;

			byte[] magic = new byte[4];
			using (var stream = new FileStream(filename, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, bufferSize: 4, FileOptions.SequentialScan)) {
			int read = stream.Read(magic, 0, magic.Length);
			if (read < 2)
				return false;

			// PE (DOS), ELF, Mach-O (32/64-bit and either byte order), and fat Mach-O.
			if (magic[0] == 0x4D && magic[1] == 0x5A)
				return true;
			if (read < 4)
				return false;
			uint value = (uint)(magic[0] | (magic[1] << 8) | (magic[2] << 16) | (magic[3] << 24));
			return value == 0x464C457Fu ||
				value == 0xFEEDFACEu || value == 0xFEEDFACFu ||
				value == 0xCEFAEDFEu || value == 0xCFFAEDFEu ||
				value == 0xCAFEBABEu || value == 0xBEBAFECAu ||
				value == 0xCAFEBABFu || value == 0xBFBAFECAu ||
				value == 0xCAFED00Du || value == 0xD00DFECAu;
			}
		}

		readonly struct FileProbeState {
			public FileProbeState(string filename, long length, DateTime lastWriteTimeUtc) {
				Filename = filename;
				Length = length;
				LastWriteTimeUtc = lastWriteTimeUtc;
			}

			public string Filename { get; }
			public long Length { get; }
			public DateTime LastWriteTimeUtc { get; }
		}

		sealed class CachedFileProbe {
			readonly FileProbeState state;

			public CachedFileProbe(FileProbeState state, bool isExecutable) {
				this.state = state;
				IsExecutable = isExecutable;
			}

			public bool IsExecutable { get; }

			public bool Matches(FileProbeState other) =>
				StringComparer.OrdinalIgnoreCase.Equals(state.Filename, other.Filename) &&
				state.Length == other.Length && state.LastWriteTimeUtc == other.LastWriteTimeUtc;
		}
	}
}
