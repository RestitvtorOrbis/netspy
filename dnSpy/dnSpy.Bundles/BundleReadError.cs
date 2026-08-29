/*
    Copyright (C) 2026 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

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
