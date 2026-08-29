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
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	/// <summary>
	/// Named regression gate for the validation matrix. Existing header and
	/// entry tests own their detailed adversarial cases; this test covers the
	/// v6 uncompressed record shape that real modern bundles use alongside
	/// compressed entries.
	/// </summary>
	public sealed class SyntheticManifestRegressionTests {
		static readonly byte[] Signature = Convert.FromBase64String(
			"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");

		[Fact]
		public void V6UncompressedEntriesRemainIndependentlyBounded() {
			const int markerOffset = 128;
			const int headerOffset = 512;
			const int firstOffset = 64;
			const int secondOffset = 67;
			using var manifest = new MemoryStream();
			WriteUInt32(manifest, 6);
			WriteUInt32(manifest, 0);
			WriteInt32(manifest, 2);
			WriteString(manifest, "synthetic-v6");
			// v6 header: deps range, runtimeconfig range, and flags.
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteInt64(manifest, 0);
			WriteUInt64(manifest, 0);
			WriteEntry(manifest, firstOffset, 3, "first.dll");
			WriteEntry(manifest, secondOffset, 3, "second.dll");

			byte[] bytes = new byte[checked(headerOffset + (int)manifest.Length)];
			WriteInt64(bytes, markerOffset - sizeof(long), headerOffset);
			Buffer.BlockCopy(Signature, 0, bytes, markerOffset, Signature.Length);
			bytes[firstOffset] = 1;
			bytes[firstOffset + 1] = 2;
			bytes[firstOffset + 2] = 3;
			bytes[secondOffset] = 4;
			bytes[secondOffset + 1] = 5;
			bytes[secondOffset + 2] = 6;
			Buffer.BlockCopy(manifest.GetBuffer(), 0, bytes, headerOffset, (int)manifest.Length);

			string filename = Path.Combine(Path.GetTempPath(),
				"dnspy-bundle-v6-regression-" + Guid.NewGuid().ToString("N") + ".bin");
			File.WriteAllBytes(filename, bytes);
			try {
				BundleOpenResult result = new BundleReader().Open(filename);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				using BundleFile bundle = result.Bundle!;
				Assert.Equal(2, bundle.Entries.Count);
				Assert.All(bundle.Entries, entry => Assert.False(entry.IsCompressed));
				Assert.Equal(new byte[] { 1, 2, 3 }, bundle.Entries[0].ReadAllBytes(3));
				Assert.Equal(new byte[] { 4, 5, 6 }, bundle.Entries[1].ReadAllBytes(3));
			}
			finally {
				if (File.Exists(filename))
					File.Delete(filename);
			}
		}

		static void WriteEntry(Stream stream, long offset, long size, string path) {
			WriteInt64(stream, offset);
			WriteInt64(stream, size);
			WriteInt64(stream, 0);
			stream.WriteByte((byte)BundleFileType.Assembly);
			WriteString(stream, path);
		}

		static void WriteString(Stream stream, string value) {
			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
			stream.WriteByte((byte)bytes.Length);
			stream.Write(bytes, 0, bytes.Length);
		}

		static void WriteUInt32(Stream stream, uint value) => WriteInteger(stream, value, 4);
		static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, unchecked((uint)value));
		static void WriteUInt64(Stream stream, ulong value) => WriteInteger(stream, value, 8);
		static void WriteInt64(Stream stream, long value) => WriteUInt64(stream, unchecked((ulong)value));

		static void WriteInteger(Stream stream, ulong value, int byteCount) {
			for (int i = 0; i < byteCount; i++)
				stream.WriteByte((byte)(value >> (8 * i)));
		}

		static void WriteInt64(byte[] bytes, int offset, long value) {
			WriteInteger(bytes, offset, unchecked((ulong)value), sizeof(long));
		}

		static void WriteInteger(byte[] bytes, int offset, ulong value, int byteCount) {
			for (int i = 0; i < byteCount; i++)
				bytes[offset + i] = (byte)(value >> (8 * i));
		}
	}
}
