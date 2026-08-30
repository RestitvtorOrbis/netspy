// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


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
			MemoryMappedFile mapping, long maximumEntrySize = BundleReaderOptions.DefaultMaximumEntrySize) {
			if (mapping is null)
				throw new ArgumentNullException(nameof(mapping));
			Initialize(filename, fileLength, markerOffset, headerOffset, manifest, entries,
				headerEndOffset, mapping, maximumEntrySize);
		}

		void Initialize(string filename, long fileLength, long markerOffset, long headerOffset,
			BundleManifest manifest, IReadOnlyList<BundleEntry> entries, long headerEndOffset,
			MemoryMappedFile? mapping, long maximumEntrySize = BundleReaderOptions.DefaultMaximumEntrySize) {
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
			if (maximumEntrySize <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumEntrySize));
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
			MaximumEntrySize = maximumEntrySize;
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
		internal long MaximumEntrySize { get; private set; }

		MemoryMappedFile? mappingFile;
		int disposed;

		internal Stream OpenLogicalRead(BundleEntry entry) {
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			if (!ReferenceEquals(entry.Owner, this))
				throw new ArgumentException("The entry does not belong to this bundle.", nameof(entry));
			EnsureNotDisposed();
			if (entry.Size > MaximumEntrySize)
				throw new InvalidDataException("The entry exceeds the bundle read limit.");
			if (mappingFile is null)
				throw new InvalidOperationException("The bundle has no source mapping.");
			if (entry.Size == 0)
				return new BoundedReadStream(this,
					new MemoryStream(Array.Empty<byte>(), writable: false), 0);
			long physicalSize = entry.IsCompressed ? entry.CompressedSize : entry.Size;
			Stream view = mappingFile.CreateViewStream(entry.Offset, physicalSize,
				MemoryMappedFileAccess.Read);
			var bounded = new BoundedReadStream(this, view, physicalSize);
			if (!entry.IsCompressed)
				return bounded;
			var deflate = new System.IO.Compression.DeflateStream(bounded,
				System.IO.Compression.CompressionMode.Decompress, leaveOpen: false);
			return new ExactLengthReadStream(this, deflate, entry.Size,
				() => ValidateCompressedEntry(entry));
		}

		void ValidateCompressedEntry(BundleEntry entry) {
			if (mappingFile is null)
				throw new InvalidOperationException("The bundle has no source mapping.");
			using Stream view = mappingFile.CreateViewStream(entry.Offset, entry.CompressedSize,
				MemoryMappedFileAccess.Read);
			using var bounded = new BoundedReadStream(this, view, entry.CompressedSize);
			DeflateEndValidator.Validate(bounded);
		}

		/// <summary>Materializes one entry after checking caller and bundle limits.</summary>
		public byte[] ReadAllBytes(BundleEntry entry, long maximumBytes) {
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			if (maximumBytes < 0)
				throw new ArgumentOutOfRangeException(nameof(maximumBytes));
			if (!ReferenceEquals(entry.Owner, this))
				throw new ArgumentException("The entry does not belong to this bundle.", nameof(entry));
			EnsureNotDisposed();
			if (entry.Size > MaximumEntrySize)
				throw new InvalidOperationException("The entry exceeds the bundle read limit.");
			if (entry.Size > maximumBytes)
				throw new InvalidOperationException("The entry exceeds the requested read limit.");
			if (entry.Size > int.MaxValue)
				throw new InvalidOperationException("The entry is too large to materialize in memory.");

			byte[] result = new byte[(int)entry.Size];
			using Stream stream = OpenLogicalRead(entry);
			int position = 0;
			while (position < result.Length) {
				int read = stream.Read(result, position, result.Length - position);
				if (read <= 0)
					throw new InvalidDataException("The bundle entry ended before its declared logical length.");
				position += read;
			}
			return result;
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
