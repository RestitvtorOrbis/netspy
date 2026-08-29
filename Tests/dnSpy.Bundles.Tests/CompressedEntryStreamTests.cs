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
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class CompressedEntryStreamTests {
		[Fact]
		public void ValidCompressedContentMatchesLogicalBytes() {
			byte[] expected = RepeatedBytes(4096, 0x41);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);
			BundleEntry entry = GetEntry(bundle);

			Assert.True(entry.IsCompressed);
			Assert.True(entry.CompressedSize < entry.Size);
			Assert.Equal(expected, ReadEntry(entry));
			Assert.Equal(expected, entry.ReadAllBytes(expected.Length));
			Assert.Equal(expected, bundle.Result.Bundle!.ReadAllBytes(entry, expected.Length));
		}

		[Fact]
		public void ValidSmallCompressedContentMatchesLogicalBytes() {
			byte[] expected = Encoding.UTF8.GetBytes("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);

			Assert.Equal(expected, ReadEntry(GetEntry(bundle)));
		}

		[Fact]
		public void ExplicitRawDeflateFixturesCoverStoredFixedAndDynamicBlocks() {
			var fixtures = new[] {
				new RawDeflateFixture(
					new byte[] { 0x72, 0x74, 0xA4, 0x3D, 0x00, 0x00, 0x00, 0x00,
						0xFF, 0xFF, 0x01, 0x01, 0x00, 0xFE, 0xFF, 0x42 },
					RepeatedBytes(100, 0x41).Concat(new byte[] { 0x42 }).ToArray()),
				new RawDeflateFixture(
					new byte[] { 0x73, 0x74, 0x44, 0x05, 0x00 },
					RepeatedBytes(16, 0x41)),
				new RawDeflateFixture(
					new byte[] { 0xED, 0xC1, 0x01, 0x0D, 0x00, 0x00, 0x00, 0xC2,
						0xA0, 0x6C, 0xEF, 0x5F, 0xCA, 0x1E, 0x0E, 0x28,
						0x00, 0x00, 0x00, 0xE0, 0xDD, 0x00 },
					RepeatedBytes(4096, 0x41)),
			};

			foreach (RawDeflateFixture fixture in fixtures) {
				Assert.Equal(fixture.Expected, InflateIndependently(fixture.Raw));
				using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(
					fixture.Expected, physical: fixture.Raw);
				Assert.Equal(fixture.Expected, ReadEntry(GetEntry(bundle)));
			}
		}

		[Fact]
		public void InvalidReservedBlockCodeAndMissingFinalEobAreRejected() {
			var fixtures = new[] {
				new RawDeflateFixture(new byte[] { 0x07 }, RepeatedBytes(16, 0x41)),
				new RawDeflateFixture(new byte[] { 0x1B, 0x03 }, RepeatedBytes(16, 0x41)),
				new RawDeflateFixture(new byte[] { 0x73, 0x74, 0x44, 0x05 }, RepeatedBytes(16, 0x41)),
			};

			foreach (RawDeflateFixture fixture in fixtures) {
				using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(
					fixture.Expected, physical: fixture.Raw);
				Assert.Throws<InvalidDataException>(() => ReadEntry(GetEntry(bundle)));
			}
		}

		[Fact]
		public void ReadAllBytesChecksCallerLimitBeforeMaterialization() {
			byte[] expected = RepeatedBytes(4096, 0x42);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);
			BundleEntry entry = GetEntry(bundle);

			Assert.Throws<InvalidOperationException>(() => entry.ReadAllBytes(expected.Length - 1));
			Assert.Throws<InvalidOperationException>(() => bundle.Result.Bundle!.ReadAllBytes(entry, expected.Length - 1));
		}

		[Fact]
		public void ReaderEntryLimitRejectsBombBeforeOpeningStream() {
			byte[] expected = RepeatedBytes(4096, 0x43);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);
			BundleOpenResult result = bundle.Open(new BundleReaderOptions(maximumEntrySize: 1024));

			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.LogicalSizeLimitExceeded, result.Error!.Code);
		}

		[Fact]
		public void TruncatedDeflateFailsWithoutBorrowingNeighborBytes() {
			byte[] expected = RepeatedBytes(4096, 0x44);
			byte[] compressed = Compress(expected);
			Assert.True(compressed.Length > 2);
			byte[] truncated = compressed.Take(Math.Max(1, compressed.Length / 2)).ToArray();
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				physical: truncated, neighbor: new byte[] { compressed[compressed.Length - 1], 0x11, 0x22 });

			Assert.Throws<InvalidDataException>(() => ReadEntry(GetEntry(bundle)));
		}

		[Fact]
		public void TruncatedFinalDeflateByteAfterFullLogicalOutputIsRejected() {
			byte[] expected = RepeatedBytes(4096, 0x4D);
			byte[] compressed = Compress(expected);
			Assert.True(compressed.Length > 2);
			byte[] truncated = compressed.Take(compressed.Length - 1).ToArray();
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				physical: truncated, neighbor: new byte[] { compressed[compressed.Length - 1] });

			// The current DeflateStream wrapper can produce every logical byte and
			// report EOF even though the final physical Deflate byte is missing.
			Assert.Throws<InvalidDataException>(() => ReadEntry(GetEntry(bundle)));
		}


		[Fact]
		public void CorruptDeflateFailsPredictably() {
			byte[] expected = RepeatedBytes(4096, 0x45);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				physical: new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

			Assert.Throws<InvalidDataException>(() => ReadEntry(GetEntry(bundle)));
		}

		[Fact]
		public void DeclaredLogicalLengthLongerThanOutputFails() {
			byte[] expected = RepeatedBytes(4096, 0x46);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				declaredSize: expected.LongLength + 1);

			Assert.Throws<InvalidDataException>(() => ReadEntry(GetEntry(bundle)));
		}

		[Fact]
		public void DeclaredLogicalLengthShorterThanOutputFailsOnExtraByteProbe() {
			byte[] expected = RepeatedBytes(4096, 0x47);
			const long declaredSize = 1024;
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				declaredSize: declaredSize);

			Assert.True(GetEntry(bundle).CompressedSize < declaredSize);
			using Stream stream = GetEntry(bundle).OpenLogicalRead();
			Assert.Throws<InvalidDataException>(() => ReadStream(stream));
			Assert.Throws<InvalidDataException>(() => stream.ReadByte());
		}

		[Fact]
		public void NeighboringPhysicalDataIsNotReturnedAsCompressedContent() {
			byte[] expected = RepeatedBytes(4096, 0x48);
			byte[] neighbor = Encoding.UTF8.GetBytes("neighbor-entry-data");
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				neighbor: neighbor);

			Assert.Equal(expected, ReadEntry(GetEntry(bundle)));
		}

		[Fact]
		public void TrailingPhysicalBytesInsideRangeDoNotBecomeLogicalOutput() {
			byte[] expected = RepeatedBytes(4096, 0x48);
			byte[] compressed = Compress(expected);
			byte[] trailing = compressed.Concat(new byte[] { 0x7F, 0x7E, 0x7D }).ToArray();
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
				physical: trailing);

			Assert.Equal(expected, ReadEntry(GetEntry(bundle)));
		}

		[Fact]
		public async Task IndependentCompressedStreamsCanBeReadConcurrently() {
			byte[] expected = RepeatedBytes(8192, 0x49);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);
			BundleEntry entry = GetEntry(bundle);
			using Stream first = entry.OpenLogicalRead();
			using Stream second = entry.OpenLogicalRead();

			Task<byte[]> firstTask = Task.Run(() => ReadStream(first));
			Task<byte[]> secondTask = Task.Run(() => ReadStream(second));
			byte[][] actual = await Task.WhenAll(firstTask, secondTask);

			Assert.Equal(expected, actual[0]);
			Assert.Equal(expected, actual[1]);
			Assert.Equal(expected.Length, first.Position);
			Assert.Equal(expected.Length, second.Position);
		}

		[Fact]
		public void EarlyCompressedStreamDisposalIsDeterministicAndIdempotent() {
			byte[] expected = RepeatedBytes(4096, 0x4A);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);
			Stream stream = GetEntry(bundle).OpenLogicalRead();
			Assert.Equal(0x4A, stream.ReadByte());

			bundle.Result.Bundle!.Dispose();
			stream.Dispose();
			stream.Dispose();
			Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
		}

		[Fact]
		public void FuzzSeedCompressedPayloadsFailWithoutEscapingTheEntry() {
			byte[] expected = RepeatedBytes(4096, 0x4B);
			byte[] valid = Compress(expected);
			var seeds = new List<byte[]> {
				new byte[] { 0 },
				new byte[] { 0xFF },
				new byte[] { 0xFF, 0xFF },
				valid.Take(1).ToArray(),
				valid.Take(Math.Max(1, valid.Length / 2)).ToArray(),
			};

			foreach (byte[] seed in seeds) {
				using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected,
					physical: seed, neighbor: valid);
				BundleEntry entry = GetEntry(bundle);
				Assert.Throws<InvalidDataException>(() => ReadEntry(entry));
			}
		}

		[Fact]
		public void CompressedStreamIsReadOnlyAndNonSeekable() {
			byte[] expected = RepeatedBytes(4096, 0x4C);
			using SyntheticCompressedBundle bundle = SyntheticCompressedBundle.Create(expected);
			using Stream stream = GetEntry(bundle).OpenLogicalRead();

			Assert.False(stream.CanSeek);
			Assert.False(stream.CanWrite);
			Assert.Equal(expected.Length, stream.Length);
			Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
			Assert.Throws<NotSupportedException>(() => stream.Position = 0);
		}

		static BundleEntry GetEntry(SyntheticCompressedBundle bundle) {
			Assert.Equal(BundleOpenStatus.Success, bundle.Result.Status);
			return bundle.Result.Bundle!.Entries[0];
		}

		static byte[] ReadEntry(BundleEntry entry) {
			using Stream stream = entry.OpenLogicalRead();
			return ReadStream(stream);
		}

		static byte[] ReadStream(Stream stream) {
			using var output = new MemoryStream();
			stream.CopyTo(output);
			return output.ToArray();
		}

		static byte[] InflateIndependently(byte[] raw) {
			using var input = new MemoryStream(raw, writable: false);
			using var deflate = new DeflateStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);
			return output.ToArray();
		}

		static byte[] RepeatedBytes(int length, byte value) {
			byte[] result = new byte[length];
			for (int i = 0; i < result.Length; i++)
				result[i] = value;
			return result;
		}

		static byte[] Compress(byte[] data) {
			using var output = new MemoryStream();
			using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
				deflate.Write(data, 0, data.Length);
			return output.ToArray();
		}

		sealed class RawDeflateFixture {
			public RawDeflateFixture(byte[] raw, byte[] expected) {
				Raw = raw;
				Expected = expected;
			}
			public byte[] Raw { get; }
			public byte[] Expected { get; }
		}
	}

	sealed class SyntheticCompressedBundle : IDisposable {
		static readonly byte[] Signature = Convert.FromBase64String(
			"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");
		const int MarkerOffset = 1024;
		const int HeaderOffset = 2048;
		const int DataOffset = 64;
		readonly string filename;

		SyntheticCompressedBundle(string filename, BundleOpenResult result) {
			this.filename = filename;
			Result = result;
		}

		public BundleOpenResult Result { get; }

		public static SyntheticCompressedBundle Create(byte[] logical,
			byte[]? physical = null, long? declaredSize = null, byte[]? neighbor = null) {
			if (logical is null)
				throw new ArgumentNullException(nameof(logical));
			physical ??= Compress(logical);
			neighbor ??= Array.Empty<byte>();
			long logicalSize = declaredSize ?? logical.LongLength;
			if (logicalSize <= physical.LongLength)
				throw new ArgumentException("The synthetic compressed range must be shorter than its logical range.", nameof(declaredSize));

			using var manifest = new MemoryStream();
			WriteUInt32(manifest, 6);
			WriteUInt32(manifest, 0);
			WriteInt32(manifest, 1);
			WriteString(manifest, "compressed-test");
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteUInt64(manifest, 0);
			WriteInt64(manifest, DataOffset);
			WriteInt64(manifest, logicalSize);
			WriteInt64(manifest, physical.LongLength);
			manifest.WriteByte((byte)BundleFileType.Assembly);
			WriteString(manifest, "compressed/app.dll");

			long physicalEnd = checked((long)DataOffset + physical.LongLength);
			long neighborEnd = checked(physicalEnd + neighbor.LongLength);
			long manifestEnd = checked((long)HeaderOffset + manifest.Length);
			long fileLength = Math.Max(manifestEnd, neighborEnd);
			byte[] bytes = new byte[checked((int)fileLength)];
			WriteInt64(bytes, MarkerOffset - sizeof(long), HeaderOffset);
			Buffer.BlockCopy(Signature, 0, bytes, MarkerOffset, Signature.Length);
			Buffer.BlockCopy(physical, 0, bytes, DataOffset, physical.Length);
			if (neighbor.Length != 0)
				Buffer.BlockCopy(neighbor, 0, bytes, checked((int)physicalEnd), neighbor.Length);
			Buffer.BlockCopy(manifest.GetBuffer(), 0, bytes, HeaderOffset, checked((int)manifest.Length));

			string filename = Path.Combine(Path.GetTempPath(),
				"dnspy-bundle-compressed-" + Guid.NewGuid().ToString("N") + ".bin");
			File.WriteAllBytes(filename, bytes);
			return new SyntheticCompressedBundle(filename, new BundleReader().Open(filename));
		}

		public BundleOpenResult Open(BundleReaderOptions options) =>
			new BundleReader(options).Open(filename);

		public void Dispose() {
			Result.Bundle?.Dispose();
			if (File.Exists(filename))
				File.Delete(filename);
		}

		static byte[] Compress(byte[] data) {
			using var output = new MemoryStream();
			using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
				deflate.Write(data, 0, data.Length);
			return output.ToArray();
		}

		static void WriteString(Stream stream, string value) {
			byte[] bytes = Encoding.UTF8.GetBytes(value);
			Write7BitInt(stream, bytes.Length);
			stream.Write(bytes, 0, bytes.Length);
		}

		static void Write7BitInt(Stream stream, int value) {
			uint remaining = checked((uint)value);
			while (remaining >= 0x80) {
				stream.WriteByte((byte)(remaining | 0x80));
				remaining >>= 7;
			}
			stream.WriteByte((byte)remaining);
		}

		static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, unchecked((uint)value));

		static void WriteUInt32(Stream stream, uint value) {
			for (int i = 0; i < 4; i++)
				stream.WriteByte((byte)(value >> (8 * i)));
		}

		static void WriteInt64(Stream stream, long value) {
			unchecked {
				ulong raw = (ulong)value;
				for (int i = 0; i < 8; i++)
					stream.WriteByte((byte)(raw >> (8 * i)));
			}
		}

		static void WriteUInt64(Stream stream, ulong value) => WriteInt64(stream, unchecked((long)value));

		static void WriteInt64(byte[] bytes, int offset, long value) {
			unchecked {
				ulong raw = (ulong)value;
				for (int i = 0; i < 8; i++)
					bytes[offset + i] = (byte)(raw >> (8 * i));
			}
		}
	}
}
