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
	sealed class BundleManifestHeader {
		public BundleManifestHeader(uint majorVersion, uint minorVersion, int fileCount,
			string bundleId, BundleManifestFlags flags, BundleRange? depsJson,
			BundleRange? runtimeConfigJson) {
			MajorVersion = majorVersion;
			MinorVersion = minorVersion;
			FileCount = fileCount;
			BundleId = bundleId;
			Flags = flags;
			DepsJson = depsJson;
			RuntimeConfigJson = runtimeConfigJson;
		}

		public uint MajorVersion { get; }
		public uint MinorVersion { get; }
		public int FileCount { get; }
		public string BundleId { get; }
		public BundleManifestFlags Flags { get; }
		public BundleRange? DepsJson { get; }
		public BundleRange? RuntimeConfigJson { get; }
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

			// At least one byte is required per entry (its type byte). This inexpensive
			// check also prevents a crafted count from ever being used for an allocation.
			long remaining = fileLength - reader.Position;
			if (fileCount > remaining)
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

			return new BundleManifestHeader(majorVersion, minorVersion, fileCount,
				bundleId, flags, depsJson, runtimeConfigJson);
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
