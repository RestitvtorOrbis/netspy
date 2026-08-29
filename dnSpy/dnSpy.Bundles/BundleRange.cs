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
