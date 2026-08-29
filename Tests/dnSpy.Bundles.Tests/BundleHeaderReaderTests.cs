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
using System.Text;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class BundleHeaderReaderTests {
		[Fact]
		public void EarliestLegalMarkerAndV1HeaderAreRead() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 8, bundleId: "core31");
			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.Success, result.Status);
			Assert.NotNull(result.Bundle);
			Assert.Equal(8, result.Bundle!.MarkerOffset);
			Assert.Equal("core31", result.Bundle.Manifest.BundleId);
			Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, result.Bundle.Manifest.Flags);
			Assert.Empty(result.Bundle.Entries);
		}

		[Fact]
		public void MarkerSplitAcrossScanBufferIsRead() {
			const int chunkSize = 64 * 1024;
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: chunkSize - 13, bundleId: "boundary");

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.Success, result.Status);
			Assert.Equal(chunkSize - 13, result.Bundle!.MarkerOffset);
		}

		[Fact]
		public void MultipleValidMarkersAreRejectedAsAmbiguous() {
			const int firstMarker = 128;
			const int secondMarker = 256;
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: secondMarker, bundleId: "ambiguous");
			long headerOffset = secondMarker + Signature.Length;
			WritePointer(bytes, firstMarker, headerOffset);
			WriteSignature(bytes, firstMarker);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.AmbiguousBundle, result.Error!.Code);
		}

		[Theory]
		[InlineData(0L)]
		[InlineData(-1L)]
		[InlineData(long.MaxValue)]
		public void InvalidHeaderPointersAreStable(long pointer) {
			const int markerOffset = 128;
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: markerOffset, bundleId: "bad-pointer");
			WritePointer(bytes, markerOffset, pointer);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidHeaderOffset, result.Error!.Code);
		}

		[Fact]
		public void HeaderPointerBeforeMarkerIsRejected() {
			const int markerOffset = 128;
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: markerOffset, bundleId: "pre-marker");
			WritePointer(bytes, markerOffset, markerOffset - 1);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidHeaderOffset, result.Error!.Code);
		}

		[Fact]
		public void MarkerWithoutPrecedingPointerIsRejected() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 0, bundleId: "no-pointer");

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidHeaderOffset, result.Error!.Code);
		}

		[Fact]
		public void TruncatedHeaderReturnsTruncatedManifest() {
			const int markerOffset = 128;
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: markerOffset, bundleId: "truncated");
			long headerOffset = markerOffset + Signature.Length;
			Array.Resize(ref bytes, checked((int)headerOffset + sizeof(uint)));
			WritePointer(bytes, markerOffset, headerOffset);
			WriteSignature(bytes, markerOffset);
			WriteUInt32(bytes, checked((int)headerOffset), 1);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.TruncatedManifest, result.Error!.Code);
		}

		[Theory]
		[InlineData(0u)]
		[InlineData(3u)]
		[InlineData(4u)]
		[InlineData(5u)]
		[InlineData(7u)]
		public void UnsupportedManifestVersionsHaveStableStatus(uint majorVersion) {
			byte[] bytes = CreateBundle(majorVersion: majorVersion, markerOffset: 128, bundleId: "version");

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.UnsupportedVersion, result.Status);
			Assert.Equal(BundleReadErrorCode.UnsupportedVersion, result.Error!.Code);
		}

		[Theory]
		[InlineData(2u)]
		[InlineData(6u)]
		public void KnownV2AndV6HeadersReadFlags(uint majorVersion) {
			byte[] bytes = CreateBundle(majorVersion: majorVersion, markerOffset: 128,
				bundleId: "modern", flags: (ulong)BundleManifestFlags.NetcoreApp3CompatMode,
				depsOffset: 16, depsSize: 3, runtimeConfigOffset: 32, runtimeConfigSize: 4);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.Success, result.Status);
			Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, result.Bundle!.Manifest.Flags);
			Assert.NotNull(result.Bundle.Manifest.DepsJson);
			Assert.Equal(16, result.Bundle.Manifest.DepsJson!.Offset);
			Assert.Equal(3, result.Bundle.Manifest.DepsJson.Size);
			Assert.NotNull(result.Bundle.Manifest.RuntimeConfigJson);
			Assert.Equal(32, result.Bundle.Manifest.RuntimeConfigJson!.Offset);
			Assert.Equal(4, result.Bundle.Manifest.RuntimeConfigJson.Size);
		}

		[Theory]
		[InlineData(2u)]
		[InlineData(6u)]
		public void UnknownV2AndV6FlagsAreRejected(uint majorVersion) {
			byte[] bytes = CreateBundle(majorVersion: majorVersion, markerOffset: 128,
				bundleId: "flags", flags: 2);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.UnknownManifestFlags, result.Error!.Code);
		}

		[Fact]
		public void ExcessiveFileCountIsRejectedBeforeAllocation() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 128, bundleId: "count", fileCount: int.MaxValue);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidFileCount, result.Error!.Code);
		}

		[Fact]
		public void NegativeFileCountIsRejectedWithStableError() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 128, bundleId: "negative", fileCount: -1);

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidFileCount, result.Error!.Code);
		}

		[Fact]
		public void ConfiguredFileCountLimitIsEnforced() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 128, bundleId: "count", fileCount: 2);
			BundleOpenResult result = Open(bytes, new BundleReaderOptions(maximumFileCount: 1));

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidFileCount, result.Error!.Code);
		}

		[Fact]
		public void ConfiguredStringLimitIsEnforced() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 128, bundleId: "four");
			BundleOpenResult result = Open(bytes, new BundleReaderOptions(maximumStringByteLength: 3));

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidString, result.Error!.Code);
		}

		[Fact]
		public void InvalidUtf8StringIsRejected() {
			byte[] bytes = CreateBundleWithRawBundleId(majorVersion: 1, markerOffset: 128,
				rawBundleId: new byte[] { 0xC3, 0x28 });

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidString, result.Error!.Code);
		}

		[Fact]
		public void NulBundleIdIsRejectedWithStableStringError() {
			byte[] bytes = CreateBundleWithRawBundleId(majorVersion: 1, markerOffset: 128,
				rawBundleId: new byte[] { (byte)'a', 0, (byte)'b' });

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidString, result.Error!.Code);
		}

		[Fact]
		public void MalformedSevenBitStringLengthIsRejected() {
			byte[] bytes = CreateBundle(majorVersion: 1, markerOffset: 128, bundleId: "length");
			int headerOffset = 128 + Signature.Length;
			for (int i = 0; i < 4; i++)
				bytes[headerOffset + 12 + i] = 0x80;
			bytes[headerOffset + 16] = 0x10;

			BundleOpenResult result = Open(bytes);

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidString, result.Error!.Code);
		}

		// Keep the marker out of this test assembly's static data. The normal-file
		// regression opens this same assembly and must not self-identify as a bundle.
		static readonly byte[] Signature = Convert.FromBase64String(
			"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");

		static BundleOpenResult Open(byte[] bytes, BundleReaderOptions? options = null) {
			string filename = Path.Combine(Path.GetTempPath(), "dnspy-bundle-header-" + Guid.NewGuid().ToString("N") + ".bin");
			try {
				File.WriteAllBytes(filename, bytes);
				return new BundleReader(options).Open(filename);
			}
			finally {
				if (File.Exists(filename))
					File.Delete(filename);
			}
		}

		static byte[] CreateBundle(uint majorVersion, int markerOffset, string bundleId,
			int fileCount = 0, ulong flags = 0, long depsOffset = 0, long depsSize = 0,
			long runtimeConfigOffset = 0, long runtimeConfigSize = 0) {
			byte[] rawBundleId = Encoding.UTF8.GetBytes(bundleId);
			return CreateBundleWithRawBundleId(majorVersion, markerOffset, rawBundleId, fileCount, flags,
				depsOffset, depsSize, runtimeConfigOffset, runtimeConfigSize);
		}

		static byte[] CreateBundleWithRawBundleId(uint majorVersion, int markerOffset,
			byte[] rawBundleId, int fileCount = 0, ulong flags = 0, long depsOffset = 0,
			long depsSize = 0, long runtimeConfigOffset = 0, long runtimeConfigSize = 0) {
			int headerOffset = checked(markerOffset + Signature.Length);
			using var header = new MemoryStream();
			WriteUInt32(header, majorVersion);
			WriteUInt32(header, 0);
			WriteInt32(header, fileCount);
			Write7BitEncodedInt(header, rawBundleId.Length);
			header.Write(rawBundleId, 0, rawBundleId.Length);
			if (majorVersion == 2 || majorVersion == 6) {
				WriteInt64(header, depsOffset);
				WriteInt64(header, depsSize);
				WriteInt64(header, runtimeConfigOffset);
				WriteInt64(header, runtimeConfigSize);
				WriteUInt64(header, flags);
			}

			int totalLength = checked(headerOffset + (int)header.Length);
			byte[] bytes = new byte[totalLength];
			if (markerOffset >= sizeof(long))
				WritePointer(bytes, markerOffset, headerOffset);
			WriteSignature(bytes, markerOffset);
			Buffer.BlockCopy(header.GetBuffer(), 0, bytes, headerOffset, checked((int)header.Length));
			return bytes;
		}

		static void WritePointer(byte[] bytes, int markerOffset, long value) =>
			WriteInt64(bytes, checked(markerOffset - sizeof(long)), value);

		static void WriteSignature(byte[] bytes, int markerOffset) =>
			Buffer.BlockCopy(Signature, 0, bytes, markerOffset, Signature.Length);

		static void WriteUInt32(Stream stream, uint value) {
			stream.WriteByte((byte)value);
			stream.WriteByte((byte)(value >> 8));
			stream.WriteByte((byte)(value >> 16));
			stream.WriteByte((byte)(value >> 24));
		}

		static void WriteUInt32(byte[] bytes, int offset, uint value) {
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
			bytes[offset + 2] = (byte)(value >> 16);
			bytes[offset + 3] = (byte)(value >> 24);
		}

		static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, unchecked((uint)value));

		static void WriteInt64(Stream stream, long value) {
			unchecked {
				ulong raw = (ulong)value;
				for (int i = 0; i < sizeof(long); i++)
					stream.WriteByte((byte)(raw >> (8 * i)));
			}
		}

		static void WriteInt64(byte[] bytes, int offset, long value) {
			unchecked {
				ulong raw = (ulong)value;
				for (int i = 0; i < sizeof(long); i++)
					bytes[offset + i] = (byte)(raw >> (8 * i));
			}
		}

		static void WriteUInt64(Stream stream, ulong value) {
			for (int i = 0; i < sizeof(ulong); i++)
				stream.WriteByte((byte)(value >> (8 * i)));
		}

		static void Write7BitEncodedInt(Stream stream, int value) {
			uint remaining = checked((uint)value);
			while (remaining >= 0x80) {
				stream.WriteByte((byte)(remaining | 0x80));
				remaining >>= 7;
			}
			stream.WriteByte((byte)remaining);
		}
	}
}
