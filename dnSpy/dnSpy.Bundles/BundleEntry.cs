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
using System.IO;

namespace dnSpy.Bundles {
	/// <summary>
	/// Immutable metadata shell for one logical bundle entry.
	/// </summary>
	public sealed class BundleEntry {
		/// <summary>Creates entry metadata from validated fields.</summary>
		public BundleEntry(int index, long offset, long size, long compressedSize,
			byte rawFileType, BundleFileType fileType, string relativePath) {
			if (index < 0)
				throw new ArgumentOutOfRangeException(nameof(index));
			if (offset < 0)
				throw new ArgumentOutOfRangeException(nameof(offset));
			if (size < 0)
				throw new ArgumentOutOfRangeException(nameof(size));
			if (compressedSize < 0)
				throw new ArgumentOutOfRangeException(nameof(compressedSize));
			if (relativePath is null)
				throw new ArgumentNullException(nameof(relativePath));
			Index = index;
			Offset = offset;
			Size = size;
			CompressedSize = compressedSize;
			RawFileType = rawFileType;
			FileType = fileType;
			RelativePath = relativePath;
		}

		/// <summary>Manifest ordinal.</summary>
		public int Index { get; }
		/// <summary>Physical offset of the entry data.</summary>
		public long Offset { get; }
		/// <summary>Declared logical size.</summary>
		public long Size { get; }
		/// <summary>Declared compressed size, or zero for an uncompressed entry.</summary>
		public long CompressedSize { get; }
		/// <summary>Raw manifest file-type byte, retained for unknown types.</summary>
		public byte RawFileType { get; }
		/// <summary>Classified file type.</summary>
		public BundleFileType FileType { get; }
		/// <summary>Normalized relative path.</summary>
		public string RelativePath { get; }
		/// <summary>Whether this entry uses compressed logical content.</summary>
		public bool IsCompressed => CompressedSize != 0;

		BundleFile? owner;

		internal BundleFile? Owner {
			get => owner;
			set {
				if (owner is not null && !ReferenceEquals(owner, value))
					throw new InvalidOperationException("A bundle entry already belongs to another bundle.");
				owner = value;
			}
		}

		/// <summary>
		/// Opens a bounded logical content stream.
		/// </summary>
		/// <remarks>Entry streams are implemented by the parser ticket.</remarks>
		public Stream OpenLogicalRead() {
			if (owner is null)
				throw new InvalidOperationException("The entry is not attached to an opened bundle.");
			return owner.OpenLogicalRead(this);
		}

		/// <summary>
		/// Reads logical content after checking the caller-provided bound.
		/// </summary>
		/// <remarks>Entry materialization is implemented by the parser ticket.</remarks>
		public byte[] ReadAllBytes(long maximumBytes) {
			if (maximumBytes < 0)
				throw new ArgumentOutOfRangeException(nameof(maximumBytes));
			if (Size > maximumBytes)
				throw new InvalidOperationException("The entry exceeds the requested read limit.");
			throw new NotSupportedException("Bundle entry reading is not implemented yet.");
		}
	}
}
