// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


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
		/// <remarks>The returned stream is bounded to this entry's logical content.</remarks>
		public Stream OpenLogicalRead() {
			if (owner is null)
				throw new InvalidOperationException("The entry is not attached to an opened bundle.");
			return owner.OpenLogicalRead(this);
		}

		/// <summary>
		/// Reads logical content after checking the caller-provided bound.
		/// </summary>
		/// <remarks>The caller-provided bound is checked before any byte array is allocated.</remarks>
		public byte[] ReadAllBytes(long maximumBytes) {
			if (maximumBytes < 0)
				throw new ArgumentOutOfRangeException(nameof(maximumBytes));
			if (Size > maximumBytes)
				throw new InvalidOperationException("The entry exceeds the requested read limit.");
			if (owner is null)
				throw new InvalidOperationException("The entry is not attached to an opened bundle.");
			return owner.ReadAllBytes(this, maximumBytes);
		}
	}
}
