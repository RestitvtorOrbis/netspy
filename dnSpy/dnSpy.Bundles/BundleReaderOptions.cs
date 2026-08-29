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
	/// Resource limits applied while reading untrusted bundle files.
	/// </summary>
	public sealed class BundleReaderOptions {
		/// <summary>Maximum marker-search window (32 MiB).</summary>
		public const long DefaultMaximumSignatureSearchBytes = 32 * 1024 * 1024;
		/// <summary>Maximum number of manifest entries.</summary>
		public const int DefaultMaximumFileCount = 100_000;
		/// <summary>Maximum UTF-8 byte length of an identifier or path.</summary>
		public const int DefaultMaximumStringByteLength = 16_383;
		/// <summary>Maximum logical size of one entry (2 GiB).</summary>
		public const long DefaultMaximumEntrySize = 2L * 1024 * 1024 * 1024;
		/// <summary>Maximum aggregate logical entry size (16 GiB).</summary>
		public const long DefaultMaximumTotalLogicalSize = 16L * 1024 * 1024 * 1024;

		/// <summary>Creates options with secure defaults.</summary>
		public BundleReaderOptions(
			long maximumSignatureSearchBytes = DefaultMaximumSignatureSearchBytes,
			int maximumFileCount = DefaultMaximumFileCount,
			int maximumStringByteLength = DefaultMaximumStringByteLength,
			long maximumEntrySize = DefaultMaximumEntrySize,
			long maximumTotalLogicalSize = DefaultMaximumTotalLogicalSize) {
			if (maximumSignatureSearchBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumSignatureSearchBytes));
			if (maximumFileCount <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumFileCount));
			if (maximumStringByteLength <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumStringByteLength));
			if (maximumEntrySize <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumEntrySize));
			if (maximumTotalLogicalSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumTotalLogicalSize));
			MaximumSignatureSearchBytes = maximumSignatureSearchBytes;
			MaximumFileCount = maximumFileCount;
			MaximumStringByteLength = maximumStringByteLength;
			MaximumEntrySize = maximumEntrySize;
			MaximumTotalLogicalSize = maximumTotalLogicalSize;
		}

		/// <summary>Maximum number of bytes searched for the official marker.</summary>
		public long MaximumSignatureSearchBytes { get; }
		/// <summary>Maximum manifest entry count.</summary>
		public int MaximumFileCount { get; }
		/// <summary>Maximum UTF-8 string byte length.</summary>
		public int MaximumStringByteLength { get; }
		/// <summary>Maximum logical size of an individual entry.</summary>
		public long MaximumEntrySize { get; }
		/// <summary>Maximum total logical size of all entries.</summary>
		public long MaximumTotalLogicalSize { get; }
	}
}
