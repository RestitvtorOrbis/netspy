// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Collections.Generic;
using System.IO;

namespace dnSpy.Bundles {
	sealed class BundleManifestHeader {
		public BundleManifestHeader(uint majorVersion, uint minorVersion, int fileCount,
			string bundleId, BundleManifestFlags flags, BundleRange? depsJson,
			BundleRange? runtimeConfigJson, IReadOnlyList<BundleEntry> entries,
			long manifestEndOffset) {
			MajorVersion = majorVersion;
			MinorVersion = minorVersion;
			FileCount = fileCount;
			BundleId = bundleId;
			Flags = flags;
			DepsJson = depsJson;
			RuntimeConfigJson = runtimeConfigJson;
			Entries = entries;
			ManifestEndOffset = manifestEndOffset;
		}

		public uint MajorVersion { get; }
		public uint MinorVersion { get; }
		public int FileCount { get; }
		public string BundleId { get; }
		public BundleManifestFlags Flags { get; }
		public BundleRange? DepsJson { get; }
		public BundleRange? RuntimeConfigJson { get; }
		public IReadOnlyList<BundleEntry> Entries { get; }
		public long ManifestEndOffset { get; }
	}

	/// <summary>Reads the fixed header portion of an official bundle manifest.</summary>
	static class BundleManifestReader {
		const ulong KnownFlags = (ulong)BundleManifestFlags.NetcoreApp3CompatMode;

		public static BundleManifestHeader Read(Stream stream, long headerOffset, long fileLength,
			BundleReaderOptions options) {
			if (stream is null)
				throw new ArgumentNullException(nameof(stream));
			if (options is null)
				throw new ArgumentNullException(nameof(options));
			if (headerOffset < 0 || headerOffset >= fileLength)
				throw new BundleReadException(BundleReadErrorCode.InvalidHeaderOffset,
					"The bundle header offset is outside the file.", headerOffset);

			stream.Position = headerOffset;
			var reader = new BoundedBinaryReader(stream, fileLength, options.MaximumStringByteLength);
			uint majorVersion = reader.ReadUInt32();
			uint minorVersion = reader.ReadUInt32();
			if (majorVersion != 1 && majorVersion != 2 && majorVersion != 6)
				throw new BundleReadException(BundleReadErrorCode.UnsupportedVersion,
					"The bundle manifest version is not supported.", headerOffset);

			int fileCount = reader.ReadInt32();
			if (fileCount < 0 || fileCount > options.MaximumFileCount)
				throw new BundleReadException(BundleReadErrorCode.InvalidFileCount,
					"The bundle manifest file count is invalid.", reader.Position - sizeof(int));

			// Every entry has at least two Int64 values, one type byte, and a one-byte
			// length prefix. This prevents a crafted count from driving a large list
			// allocation when the manifest cannot possibly contain that many records.
			long remaining = fileLength - reader.Position;
			long minimumEntryLength = majorVersion >= 6 ? 26L : 18L;
			if (fileCount > remaining / minimumEntryLength)
				throw new BundleReadException(BundleReadErrorCode.InvalidFileCount,
					"The bundle manifest file count exceeds the available data.", reader.Position - sizeof(int));

			string bundleId = reader.ReadUtf8String();
			BundleManifestFlags flags;
			BundleRange? depsJson = null;
			BundleRange? runtimeConfigJson = null;
			if (majorVersion == 1) {
				flags = BundleManifestFlags.NetcoreApp3CompatMode;
			}
			else {
				depsJson = ReadRange(reader, fileLength);
				runtimeConfigJson = ReadRange(reader, fileLength);
				ulong rawFlags = reader.ReadUInt64();
				if ((rawFlags & ~KnownFlags) != 0)
					throw new BundleReadException(BundleReadErrorCode.UnknownManifestFlags,
						"The bundle manifest contains unknown flags.", reader.Position - sizeof(ulong));
				flags = (BundleManifestFlags)rawFlags;
			}

			var entries = new List<BundleEntry>();
			var paths = new HashSet<string>(StringComparer.Ordinal);
			long totalLogicalSize = 0;
			for (int index = 0; index < fileCount; index++) {
				long recordOffset = reader.Position;
				long offset = reader.ReadInt64();
				long size = reader.ReadInt64();
				long compressedSize = 0;
				if (majorVersion >= 6)
					compressedSize = reader.ReadInt64();
				byte rawFileType = reader.ReadByteValue();
				string relativePath = BundlePathValidator.NormalizeAndValidate(
					reader.ReadUtf8String(), index);
				BundlePathValidator.AddUnique(paths, relativePath, index);

				ValidateEntryRange(index, recordOffset, offset, size, compressedSize,
					majorVersion, fileLength, options, ref totalLogicalSize);
				BundleFileType fileType = ClassifyFileType(rawFileType);
				entries.Add(new BundleEntry(index, offset, size, compressedSize,
					rawFileType, fileType, relativePath));
			}

			long manifestEndOffset = reader.Position;
			ValidateOverlaps(entries, headerOffset, manifestEndOffset);
			if (majorVersion >= 2) {
				ValidateConfigRange(depsJson, BundleFileType.DepsJson, entries);
				ValidateConfigRange(runtimeConfigJson, BundleFileType.RuntimeConfigJson, entries);
			}

			return new BundleManifestHeader(majorVersion, minorVersion, fileCount,
				bundleId, flags, depsJson, runtimeConfigJson, entries, manifestEndOffset);
		}

		static BundleFileType ClassifyFileType(byte rawFileType) {
			return rawFileType <= (byte)BundleFileType.Symbols
				? (BundleFileType)rawFileType
				: BundleFileType.Unknown;
		}

		static void ValidateEntryRange(int index, long recordOffset, long offset, long size,
			long compressedSize, uint majorVersion, long fileLength,
			BundleReaderOptions options, ref long totalLogicalSize) {
			if (offset < 0 || size < 0 || compressedSize < 0)
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A bundle entry has a negative offset or size.", index, recordOffset);
			if (majorVersion < 6)
				compressedSize = 0;
			else if (compressedSize != 0 && (size == 0 || compressedSize >= size))
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A compressed bundle entry has an inconsistent logical size.", index, recordOffset);
			if (size > options.MaximumEntrySize)
				throw new BundleReadException(BundleReadErrorCode.LogicalSizeLimitExceeded,
					"A bundle entry exceeds the configured logical-size limit.", index, recordOffset);
			try {
				totalLogicalSize = checked(totalLogicalSize + size);
			}
			catch (OverflowException ex) {
				throw new BundleReadException(BundleReadErrorCode.LogicalSizeLimitExceeded,
					"The bundle logical-size total overflows the supported range.", index, recordOffset, ex);
			}
			if (totalLogicalSize > options.MaximumTotalLogicalSize)
				throw new BundleReadException(BundleReadErrorCode.LogicalSizeLimitExceeded,
					"The bundle exceeds the configured aggregate logical-size limit.", index, recordOffset);

			long physicalSize = compressedSize == 0 ? size : compressedSize;
			long end;
			try {
				end = checked(offset + physicalSize);
			}
			catch (OverflowException ex) {
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A bundle entry range overflows the file offset space.", index, recordOffset, ex);
			}
			if (end > fileLength)
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A bundle entry range exceeds the file.", index, recordOffset);
		}

		static void ValidateOverlaps(IReadOnlyList<BundleEntry> entries, long manifestOffset,
			long manifestEndOffset) {
			var ranges = new List<EntryRange>();
			for (int i = 0; i < entries.Count; i++) {
				BundleEntry entry = entries[i];
				long physicalSize = entry.IsCompressed ? entry.CompressedSize : entry.Size;
				if (physicalSize == 0)
					continue;
				long end = checked(entry.Offset + physicalSize);
				if (entry.Offset < manifestEndOffset && manifestOffset < end)
					throw new BundleReadException(BundleReadErrorCode.EntryOverlap,
						"A bundle entry overlaps the manifest.", entry.Index, entry.Offset);
				ranges.Add(new EntryRange(entry.Offset, end, entry.Index));
			}
			ranges.Sort((left, right) => {
				int result = left.Start.CompareTo(right.Start);
				return result != 0 ? result : left.Index.CompareTo(right.Index);
			});
			for (int i = 1; i < ranges.Count; i++) {
				EntryRange previous = ranges[i - 1];
				EntryRange current = ranges[i];
				if (current.Start < previous.End)
					throw new BundleReadException(BundleReadErrorCode.EntryOverlap,
						"Bundle entry data ranges overlap.", current.Index, current.Start);
			}
		}

		readonly struct EntryRange {
			public EntryRange(long start, long end, int index) {
				Start = start;
				End = end;
				Index = index;
			}
			public long Start { get; }
			public long End { get; }
			public int Index { get; }
		}

		static void ValidateConfigRange(BundleRange? range, BundleFileType fileType,
			IReadOnlyList<BundleEntry> entries) {
			int matchCount = 0;
			BundleEntry? matchingEntry = null;
			foreach (BundleEntry entry in entries) {
				if (entry.FileType != fileType)
					continue;
				matchCount++;
				matchingEntry = entry;
			}
			// Header-only consumers (and older v2 producers) can carry a non-zero
			// location before the corresponding record has been enumerated. Once the
			// record is present, however, the pair must be present and exact.
			if (matchCount > 1 || (matchCount != 0 && (range is null ||
				matchingEntry!.Offset != range.Offset || matchingEntry.Size != range.Size)))
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"The manifest configuration range does not match its entry.");
		}

		static BundleRange? ReadRange(BoundedBinaryReader reader, long fileLength) {
			long offset = reader.ReadInt64();
			long size = reader.ReadInt64();
			if (offset < 0 || size < 0)
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A manifest range is negative.", reader.Position - sizeof(long) * 2);
			long end;
			try {
				end = checked(offset + size);
			}
			catch (OverflowException ex) {
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A manifest range overflows the file offset space.", reader.Position - sizeof(long) * 2, ex);
			}
			if (end > fileLength)
				throw new BundleReadException(BundleReadErrorCode.InvalidEntryRange,
					"A manifest range exceeds the file.", reader.Position - sizeof(long) * 2);
			return offset == 0 && size == 0 ? null : new BundleRange(offset, size);
		}
	}
}
