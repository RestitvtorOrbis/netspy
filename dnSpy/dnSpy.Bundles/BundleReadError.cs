// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;

namespace dnSpy.Bundles {
	/// <summary>
	/// Stable categories used when a bundle cannot be opened safely.
	/// </summary>
	public enum BundleReadErrorCode {
		/// <summary>No more specific error category is available.</summary>
		Unknown,
		/// <summary>The input is not a valid official bundle.</summary>
		InvalidBundle,
		/// <summary>The manifest version is unsupported.</summary>
		UnsupportedVersion,
		/// <summary>The manifest contains an unknown flag bit.</summary>
		UnknownManifestFlags,
		/// <summary>The manifest header offset is invalid.</summary>
		InvalidHeaderOffset,
		/// <summary>The manifest ended before the required data was read.</summary>
		TruncatedManifest,
		/// <summary>The manifest entry count is invalid.</summary>
		InvalidFileCount,
		/// <summary>A manifest string is invalid or exceeds its limit.</summary>
		InvalidString,
		/// <summary>An entry path is invalid.</summary>
		InvalidPath,
		/// <summary>Two entries have the same normalized path.</summary>
		DuplicatePath,
		/// <summary>An entry range is invalid.</summary>
		InvalidEntryRange,
		/// <summary>Entry data ranges overlap.</summary>
		EntryOverlap,
		/// <summary>Compressed entry data is invalid.</summary>
		InvalidCompression,
		/// <summary>The configured logical-size limit was exceeded.</summary>
		LogicalSizeLimitExceeded,
		/// <summary>More than one valid bundle marker was found.</summary>
		AmbiguousBundle,
	}

	/// <summary>
	/// Safe diagnostic information for a failed bundle read.
	/// </summary>
	public sealed class BundleReadError {
		/// <summary>Creates a safe bundle-read diagnostic.</summary>
		public BundleReadError(BundleReadErrorCode code, string message,
			int? entryIndex = null, long? offset = null) {
			if (message is null)
				throw new ArgumentNullException(nameof(message));
			Code = code;
			Message = message;
			EntryIndex = entryIndex;
			Offset = offset;
		}

		/// <summary>Stable error category.</summary>
		public BundleReadErrorCode Code { get; }

		/// <summary>Safe message that does not include arbitrary file content.</summary>
		public string Message { get; }

		/// <summary>Manifest entry associated with the error, if known.</summary>
		public int? EntryIndex { get; }

		/// <summary>File offset associated with the error, if known.</summary>
		public long? Offset { get; }
	}
}
