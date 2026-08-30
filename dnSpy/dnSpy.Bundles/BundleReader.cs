// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace dnSpy.Bundles {
	/// <summary>Entry point for opening official .NET single-file bundles.</summary>
	public sealed class BundleReader {
		readonly BundleReaderOptions options;

		/// <summary>Creates a reader using the supplied or secure default limits.</summary>
		public BundleReader(BundleReaderOptions? options = null) => this.options = options ?? new BundleReaderOptions();

		/// <summary>
		/// Attempts to open a file as an official bundle.
		/// </summary>
		public BundleOpenResult Open(string filename) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));

			MemoryMappedFile? mapping = null;
			try {
				using var stream = File.OpenRead(filename);
				long fileLength = stream.Length;
				BundleSignatureScanResult scan = BundleSignatureScanner.Scan(
					stream, fileLength, options.MaximumSignatureSearchBytes);
				if (!scan.SignatureFound)
					return new BundleOpenResult(BundleOpenStatus.NotBundle);
				if (scan.MultipleValidMatches) {
					return Failure(BundleOpenStatus.InvalidBundle, new BundleReadError(
						BundleReadErrorCode.AmbiguousBundle,
						"More than one valid bundle marker was found."));
				}
				if (scan.FirstValidMatch is null) {
					return Failure(BundleOpenStatus.InvalidBundle,
						scan.FirstInvalidPointer ?? new BundleReadError(
							BundleReadErrorCode.InvalidHeaderOffset,
							"The bundle header pointer is invalid."));
				}

				BundleManifestHeader header = BundleManifestReader.Read(stream,
					scan.FirstValidMatch.HeaderOffset, fileLength, options);
				var manifest = new BundleManifest(header.MajorVersion, header.MinorVersion,
					header.BundleId, header.Flags, header.DepsJson, header.RuntimeConfigJson);
				if (header.Entries.Count == 0) {
					var emptyBundle = new BundleFile(filename, fileLength,
						scan.FirstValidMatch.MarkerOffset, scan.FirstValidMatch.HeaderOffset,
						manifest, header.Entries);
					return new BundleOpenResult(BundleOpenStatus.Success, emptyBundle);
				}
				// Keep the source mapped for the lifetime of the metadata object. The map is
				// deliberately created only after every untrusted manifest field is validated.
				mapping = MemoryMappedFile.CreateFromFile(filename, FileMode.Open, null, 0,
					MemoryMappedFileAccess.Read);
				var bundle = new BundleFile(filename, fileLength,
					scan.FirstValidMatch.MarkerOffset, scan.FirstValidMatch.HeaderOffset,
					manifest, header.Entries, header.ManifestEndOffset, mapping,
					options.MaximumEntrySize);
				mapping = null;
				return new BundleOpenResult(BundleOpenStatus.Success, bundle);
			}
			catch (BundleReadException ex) {
				BundleOpenStatus status = ex.Code == BundleReadErrorCode.UnsupportedVersion
					? BundleOpenStatus.UnsupportedVersion
					: BundleOpenStatus.InvalidBundle;
				return Failure(status, new BundleReadError(ex.Code, ex.Message, ex.EntryIndex, ex.Offset));
			}
			finally {
				mapping?.Dispose();
			}
		}

		static BundleOpenResult Failure(BundleOpenStatus status, BundleReadError error) =>
			new BundleOpenResult(status, error: error);
	}
}
