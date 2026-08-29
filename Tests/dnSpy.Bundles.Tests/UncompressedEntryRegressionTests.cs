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
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class UncompressedEntryRegressionTests {
		[Fact]
		public void UncompressedReadCannotReachNeighborEntry() {
			using (SyntheticFactory factory = CreateBundle()) {
				BundleEntry first = factory.Result.Bundle!.Entries[0];
				using Stream stream = first.OpenLogicalRead();
				byte[] actual = new byte[16];
				Assert.Equal(3, stream.Read(actual, 0, actual.Length));
				Assert.Equal(new byte[] { 1, 2, 3 }, actual.Take(3).ToArray());
				Assert.Equal(-1, stream.ReadByte());
				Assert.Equal(3, stream.Length);
			}
		}

		[Fact]
		public void UncompressedReadAllBytesReturnsOnlyTheEntry() {
			using (SyntheticFactory factory = CreateBundle()) {
				BundleFile bundle = factory.Result.Bundle!;
				BundleEntry entry = bundle.Entries[0];
				Assert.Equal(new byte[] { 1, 2, 3 }, entry.ReadAllBytes(3));
				Assert.Equal(new byte[] { 1, 2, 3 }, bundle.ReadAllBytes(entry, 3));
			}
		}

		[Fact]
		public void BundleAndEntryDisposalIsDeterministicAndIdempotent() {
			using (SyntheticFactory factory = CreateBundle()) {
				BundleFile bundle = factory.Result.Bundle!;
				Stream stream = bundle.Entries[0].OpenLogicalRead();
				bundle.Dispose();
				bundle.Dispose();
				Assert.Throws<ObjectDisposedException>(() => bundle.Entries[0].OpenLogicalRead());
				Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
				stream.Dispose();
				stream.Dispose();
			}
		}

		[Fact]
		public void SeekIsBoundedAtBeginCurrentAndEnd() {
			using (SyntheticFactory factory = CreateBundle()) {
				BundleEntry entry = factory.Result.Bundle!.Entries[0];
				using Stream stream = entry.OpenLogicalRead();
				Assert.Equal(0, stream.Position);
				Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
				Assert.Equal(1, stream.Seek(1, SeekOrigin.Current));
				Assert.Equal(1, stream.Position);
				Assert.Equal(2, stream.Seek(-1, SeekOrigin.End));
				Assert.Equal(3, stream.ReadByte());
				Assert.Equal(3, stream.Position);
				Assert.Equal(-1, stream.ReadByte());

				Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
				Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = stream.Length + 1);
				Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
				Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.End));
			}
		}

		[Fact]
		public async Task ConcurrentEntryStreamsHaveIndependentPositionsAndDisposal() {
			using (SyntheticFactory factory = CreateBundle()) {
				BundleFile bundle = factory.Result.Bundle!;
				BundleEntry first = bundle.Entries[0];
				BundleEntry second = bundle.Entries[1];
				using Stream firstStream = first.OpenLogicalRead();
				using Stream secondStream = first.OpenLogicalRead();
				Task<byte[]> firstTask = Task.Run(() => ReadBytes(firstStream));
				Task<byte[]> secondTask = Task.Run(() => ReadBytes(secondStream));
				byte[][] results = await Task.WhenAll(firstTask, secondTask);
				Assert.Equal(new byte[] { 1, 2, 3 }, results[0]);
				Assert.Equal(new byte[] { 1, 2, 3 }, results[1]);
				Assert.Equal(3, firstStream.Position);
				Assert.Equal(3, secondStream.Position);

				byte[] neighbor = ReadBytes(second);
				Assert.Equal(new byte[] { 9, 8, 7 }, neighbor);
			}
		}

		static byte[] ReadBytes(Stream stream) {
			using var output = new MemoryStream();
			stream.CopyTo(output);
			return output.ToArray();
		}

		static byte[] ReadBytes(BundleEntry entry) {
			using Stream stream = entry.OpenLogicalRead();
			return ReadBytes(stream);
		}

		[Fact]
		public void MalformedCompressedEntryFailsWhenRead() {
			var synthetic = SyntheticFactory.CreateV6Compressed();
			try {
				Assert.Equal(BundleOpenStatus.Success, synthetic.Result.Status);
				Assert.True(synthetic.Result.Bundle!.Entries[0].IsCompressed);
				using Stream stream = synthetic.Result.Bundle.Entries[0].OpenLogicalRead();
				Assert.Throws<InvalidDataException>(() => stream.CopyTo(Stream.Null));
			}
			finally {
				synthetic.Dispose();
			}
		}

		static SyntheticFactory CreateBundle() => SyntheticFactory.CreateV1(new[] {
			new SyntheticEntry(1, "first", new byte[] { 1, 2, 3 }, 64),
			new SyntheticEntry(2, "second", new byte[] { 9, 8, 7 }, 67),
		});
	}

	sealed class SyntheticFactory : IDisposable {
		static readonly byte[] Signature = Convert.FromBase64String(
			"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");
		public BundleOpenResult Result { get; }
		readonly string filename;

		SyntheticFactory(string filename, BundleOpenResult result) {
			this.filename = filename;
			Result = result;
		}

		public static SyntheticFactory CreateV1(SyntheticEntry[] entries) {
			const int markerOffset = 128;
			const int headerOffset = 512;
			using var manifest = new MemoryStream();
			WriteUInt32(manifest, 1);
			WriteUInt32(manifest, 0);
			WriteInt32(manifest, entries.Length);
			WriteString(manifest, "test");
			foreach (SyntheticEntry entry in entries) {
				WriteInt64(manifest, entry.Offset);
				WriteInt64(manifest, entry.Bytes.Length);
				manifest.WriteByte(entry.Type);
				WriteString(manifest, entry.Path);
			}
			byte[] bytes = new byte[checked(headerOffset + (int)manifest.Length)];
			WriteInt64(bytes, markerOffset - 8, headerOffset);
			Buffer.BlockCopy(Signature, 0, bytes, markerOffset, Signature.Length);
			foreach (SyntheticEntry entry in entries)
				Buffer.BlockCopy(entry.Bytes, 0, bytes, checked((int)entry.Offset), entry.Bytes.Length);
			Buffer.BlockCopy(manifest.GetBuffer(), 0, bytes, headerOffset, (int)manifest.Length);
			string filename = Path.Combine(Path.GetTempPath(), "dnspy-bundle-stream-" + Guid.NewGuid().ToString("N") + ".bin");
			File.WriteAllBytes(filename, bytes);
			var result = new BundleReader().Open(filename);
			return new SyntheticFactory(filename, result);
		}

		public static SyntheticFactory CreateV6Compressed() {
			const int markerOffset = 128;
			const int headerOffset = 512;
			using var manifest = new MemoryStream();
			WriteUInt32(manifest, 6);
			WriteUInt32(manifest, 0);
			WriteInt32(manifest, 1);
			WriteString(manifest, "test");
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteUInt64(manifest, 0);
			WriteInt64(manifest, 64);
			WriteInt64(manifest, 8);
			WriteInt64(manifest, 4);
			manifest.WriteByte(1);
			WriteString(manifest, "compressed");
			byte[] bytes = new byte[checked(headerOffset + (int)manifest.Length)];
			WriteInt64(bytes, markerOffset - 8, headerOffset);
			Buffer.BlockCopy(Signature, 0, bytes, markerOffset, Signature.Length);
			Buffer.BlockCopy(new byte[] { 1, 2, 3, 4 }, 0, bytes, 64, 4);
			Buffer.BlockCopy(manifest.GetBuffer(), 0, bytes, headerOffset, (int)manifest.Length);
			string filename = Path.Combine(Path.GetTempPath(), "dnspy-bundle-compressed-" + Guid.NewGuid().ToString("N") + ".bin");
			File.WriteAllBytes(filename, bytes);
			return new SyntheticFactory(filename, new BundleReader().Open(filename));
		}

		public void Dispose() {
			Result.Bundle?.Dispose();
			if (File.Exists(filename))
				File.Delete(filename);
		}

		static void WriteString(Stream stream, string value) {
			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
			stream.WriteByte((byte)bytes.Length);
			stream.Write(bytes, 0, bytes.Length);
		}
		static void WriteUInt32(Stream stream, uint value) => WriteInt64N(stream, value, 4);
		static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, unchecked((uint)value));
		static void WriteInt64(Stream stream, long value) => WriteInt64N(stream, unchecked((ulong)value), 8);
		static void WriteUInt64(Stream stream, ulong value) => WriteInt64N(stream, value, 8);
		static void WriteInt64N(Stream stream, ulong value, int count) {
			for (int i = 0; i < count; i++)
				stream.WriteByte((byte)(value >> (8 * i)));
		}
		static void WriteInt64(byte[] bytes, int offset, long value) {
			ulong raw = unchecked((ulong)value);
			for (int i = 0; i < 8; i++)
				bytes[offset + i] = (byte)(raw >> (8 * i));
		}
	}

	sealed class SyntheticEntry {
		public SyntheticEntry(byte type, string path, byte[] bytes, long offset) {
			Type = type;
			Path = path;
			Bytes = bytes;
			Offset = offset;
		}
		public byte Type { get; }
		public string Path { get; }
		public byte[] Bytes { get; }
		public long Offset { get; }
	}
}
