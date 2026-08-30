// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.IO;
using System.Text;

namespace dnSpy.Bundles {
	/// <summary>Checked little-endian reads bounded by the source file.</summary>
	sealed class BoundedBinaryReader {
		static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		readonly Stream stream;
		readonly long endOffset;
		readonly int maximumStringByteLength;

		public BoundedBinaryReader(Stream stream, long endOffset, int maximumStringByteLength) {
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			if (!stream.CanRead || !stream.CanSeek)
				throw new ArgumentException("The bundle source must be a readable, seekable stream.", nameof(stream));
			if (endOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(endOffset));
			if (maximumStringByteLength <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumStringByteLength));
			this.endOffset = endOffset;
			this.maximumStringByteLength = maximumStringByteLength;
		}

		public long Position => stream.Position;

		public byte ReadByteValue() => ReadByte();

		public uint ReadUInt32() {
			byte[] bytes = ReadBytes(sizeof(uint));
			return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
		}

		public int ReadInt32() => unchecked((int)ReadUInt32());

		public long ReadInt64() {
			byte[] bytes = ReadBytes(sizeof(long));
			unchecked {
				ulong value = 0;
				for (int i = 0; i < bytes.Length; i++)
					value |= (ulong)bytes[i] << (8 * i);
				return (long)value;
			}
		}

		public ulong ReadUInt64() {
			byte[] bytes = ReadBytes(sizeof(ulong));
			unchecked {
				ulong value = 0;
				for (int i = 0; i < bytes.Length; i++)
					value |= (ulong)bytes[i] << (8 * i);
				return value;
			}
		}

		public string ReadUtf8String() {
			int length = Read7BitEncodedLength();
			if (length == 0)
				return string.Empty;

			byte[] bytes = ReadBytes(length);
			string value;
			try {
				value = StrictUtf8.GetString(bytes);
			}
			catch (DecoderFallbackException ex) {
				throw new BundleReadException(BundleReadErrorCode.InvalidString,
					"A manifest string is not valid UTF-8.", Position - length, ex);
			}
			if (value.IndexOf('\0') >= 0)
				throw new BundleReadException(BundleReadErrorCode.InvalidString,
					"A manifest string contains a NUL character.", Position - length);
			return value;
		}

		int Read7BitEncodedLength() {
			uint value = 0;
			for (int shift = 0; shift < 35; shift += 7) {
				byte current = ReadByte();
				uint payload = (uint)(current & 0x7F);
				if (shift == 28 && payload > 0x0F)
					throw new BundleReadException(BundleReadErrorCode.InvalidString,
						"A manifest string length overflows a 32-bit integer.", Position - 1);

				value |= payload << shift;
				if ((current & 0x80) == 0) {
					if (value > int.MaxValue || value > maximumStringByteLength)
						throw new BundleReadException(BundleReadErrorCode.InvalidString,
							"A manifest string exceeds the configured byte limit.", Position - 1);
					return (int)value;
				}
			}

			throw new BundleReadException(BundleReadErrorCode.InvalidString,
				"A manifest string length is not a valid 7-bit integer.", Position - 1);
		}

		byte ReadByte() {
			EnsureAvailable(1);
			int value = stream.ReadByte();
			if (value < 0)
				throw new BundleReadException(BundleReadErrorCode.TruncatedManifest,
					"The manifest ended unexpectedly.", Position);
			return (byte)value;
		}

		byte[] ReadBytes(int count) {
			if (count < 0)
				throw new ArgumentOutOfRangeException(nameof(count));
			EnsureAvailable(count);
			byte[] bytes = new byte[count];
			int readTotal = 0;
			while (readTotal < count) {
				int read = stream.Read(bytes, readTotal, count - readTotal);
				if (read == 0)
					throw new BundleReadException(BundleReadErrorCode.TruncatedManifest,
						"The manifest ended unexpectedly.", Position);
				readTotal += read;
			}
			return bytes;
		}

		void EnsureAvailable(int count) {
			long position = stream.Position;
			if (position < 0 || position > endOffset || count > endOffset - position)
				throw new BundleReadException(BundleReadErrorCode.TruncatedManifest,
					"The manifest ended unexpectedly.", position);
		}
	}
}
