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
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace dnSpy.Bundles {
	/// <summary>
	/// Immutable metadata shell for an opened bundle file.
	/// </summary>
	public sealed class BundleFile : IDisposable {
		/// <summary>Creates bundle metadata from validated fields.</summary>
		public BundleFile(string filename, long fileLength, long markerOffset, long headerOffset,
			BundleManifest manifest, IReadOnlyList<BundleEntry> entries) {
			Initialize(filename, fileLength, markerOffset, headerOffset, manifest, entries,
				headerEndOffset: 0, mapping: null);
		}

		internal BundleFile(string filename, long fileLength, long markerOffset, long headerOffset,
			BundleManifest manifest, IReadOnlyList<BundleEntry> entries, long headerEndOffset,
			MemoryMappedFile mapping) {
			if (mapping is null)
				throw new ArgumentNullException(nameof(mapping));
			Initialize(filename, fileLength, markerOffset, headerOffset, manifest, entries,
				headerEndOffset, mapping);
		}

		void Initialize(string filename, long fileLength, long markerOffset, long headerOffset,
			BundleManifest manifest, IReadOnlyList<BundleEntry> entries, long headerEndOffset,
			MemoryMappedFile? mapping) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			if (fileLength < 0)
				throw new ArgumentOutOfRangeException(nameof(fileLength));
			if (markerOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(markerOffset));
			if (headerOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(headerOffset));
			if (manifest is null)
				throw new ArgumentNullException(nameof(manifest));
			if (entries is null)
				throw new ArgumentNullException(nameof(entries));
			if (mapping is not null && (headerEndOffset < headerOffset || headerEndOffset > fileLength))
				throw new ArgumentOutOfRangeException(nameof(headerEndOffset));
			Filename = filename;
			FileLength = fileLength;
			MarkerOffset = markerOffset;
			HeaderOffset = headerOffset;
			Manifest = manifest;
			var copiedEntries = new List<BundleEntry>(entries.Count);
			foreach (BundleEntry entry in entries) {
				if (entry is null)
					throw new ArgumentException("The entry list contains a null entry.", nameof(entries));
				entry.Owner = this;
				copiedEntries.Add(entry);
			}
			Entries = copiedEntries.AsReadOnly();
			HeaderEndOffset = headerEndOffset;
			mappingFile = mapping;
		}

		/// <summary>Source filename.</summary>
		public string Filename { get; private set; } = null!;
		/// <summary>Length of the source file.</summary>
		public long FileLength { get; private set; }
		/// <summary>Offset of the official bundle marker.</summary>
		public long MarkerOffset { get; private set; }
		/// <summary>Offset of the manifest header.</summary>
		public long HeaderOffset { get; private set; }
		/// <summary>Manifest metadata.</summary>
		public BundleManifest Manifest { get; private set; } = null!;
		/// <summary>Entries in manifest order.</summary>
		public IReadOnlyList<BundleEntry> Entries { get; private set; } = null!;
		/// <summary>Exclusive end offset of the serialized header and entry records.</summary>
		public long HeaderEndOffset { get; private set; }

		MemoryMappedFile? mappingFile;
		int disposed;

		internal Stream OpenLogicalRead(BundleEntry entry) {
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			if (!ReferenceEquals(entry.Owner, this))
				throw new ArgumentException("The entry does not belong to this bundle.", nameof(entry));
			EnsureNotDisposed();
			if (entry.IsCompressed)
				throw new NotSupportedException("Compressed bundle entries are not supported by this reader yet.");
			if (mappingFile is null)
				throw new InvalidOperationException("The bundle has no source mapping.");
			if (entry.Size == 0)
				return new BoundedReadStream(this,
					new MemoryStream(Array.Empty<byte>(), writable: false), 0);
			Stream view = mappingFile.CreateViewStream(entry.Offset, entry.Size,
				MemoryMappedFileAccess.Read);
			return new BoundedReadStream(this, view, entry.Size);
		}

		internal void EnsureNotDisposed() {
			if (Volatile.Read(ref disposed) != 0)
				throw new ObjectDisposedException(nameof(BundleFile));
		}

		internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

		/// <summary>Releases resources held by a future entry reader.</summary>
		public void Dispose() {
			if (Interlocked.Exchange(ref disposed, 1) == 0)
				mappingFile?.Dispose();
		}
	}
}
