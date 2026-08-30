// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;

namespace dnSpy.Bundles {
	/// <summary>
	/// A validated file range used by bundle metadata.
	/// </summary>
	public sealed class BundleRange {
		/// <summary>Creates a non-negative file range.</summary>
		public BundleRange(long offset, long size) {
			if (offset < 0)
				throw new ArgumentOutOfRangeException(nameof(offset));
			if (size < 0)
				throw new ArgumentOutOfRangeException(nameof(size));
			Offset = offset;
			Size = size;
		}

		/// <summary>Absolute byte offset of the range.</summary>
		public long Offset { get; }

		/// <summary>Length of the range in bytes.</summary>
		public long Size { get; }
	}
}
