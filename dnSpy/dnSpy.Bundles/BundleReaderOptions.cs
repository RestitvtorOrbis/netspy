// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


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
