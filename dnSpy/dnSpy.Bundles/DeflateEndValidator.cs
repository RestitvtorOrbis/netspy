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
	/// <summary>
	/// Validates the raw Deflate block terminator without materializing the
	/// compressed range. DeflateStream does not expose its end-of-stream state,
	/// so this small structural pass distinguishes a valid final EOB from an
	/// input range ending after logical output but before the final block.
	/// </summary>
	static class DeflateEndValidator {
		static readonly int[] FixedLiteralLengths = CreateFixedLiteralLengths();
		static readonly int[] FixedDistanceLengths = CreateFixedDistanceLengths();
		static readonly int[] CodeLengthOrder = {
			16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
		};

		public static void Validate(Stream stream) {
			if (stream is null)
				throw new ArgumentNullException(nameof(stream));
			var bits = new BitReader(stream);
			var literalTable = new HuffmanTable();
			var distanceTable = new HuffmanTable();
			var codeLengthTable = new HuffmanTable();
			var codeLengthLengths = new int[19];
			var codeLengths = new int[286 + 32];
			bool final;
			do {
				final = bits.ReadBit() != 0;
				int blockType = bits.ReadBits(2);
				switch (blockType) {
					case 0:
						ReadStoredBlock(bits);
						break;
					case 1:
						literalTable.Set(FixedLiteralLengths, FixedLiteralLengths.Length);
						distanceTable.Set(FixedDistanceLengths, FixedDistanceLengths.Length);
						ReadHuffmanBlock(bits, literalTable, distanceTable);
						break;
					case 2:
						ReadDynamicTables(bits, literalTable, distanceTable, codeLengthTable,
							codeLengthLengths, codeLengths);
						ReadHuffmanBlock(bits, literalTable, distanceTable);
						break;
					default:
						throw InvalidDeflate();
				}
			} while (!final);
		}

		static void ReadStoredBlock(BitReader bits) {
			bits.AlignToByte();
			int length = bits.ReadBits(16);
			int inverseLength = bits.ReadBits(16);
			if ((length ^ 0xFFFF) != inverseLength)
				throw InvalidDeflate();
			for (int i = 0; i < length; i++)
				bits.ReadBits(8);
		}

		static void ReadDynamicTables(BitReader bits, HuffmanTable literalTable,
			HuffmanTable distanceTable, HuffmanTable codeLengthTable,
			int[] codeLengthLengths, int[] lengths) {
			int literalCount = bits.ReadBits(5) + 257;
			int distanceCount = bits.ReadBits(5) + 1;
			int codeLengthCount = bits.ReadBits(4) + 4;
			if (literalCount + distanceCount > lengths.Length)
				throw InvalidDeflate();
			Array.Clear(codeLengthLengths, 0, codeLengthLengths.Length);
			for (int i = 0; i < codeLengthCount; i++)
				codeLengthLengths[CodeLengthOrder[i]] = bits.ReadBits(3);

			codeLengthTable.Set(codeLengthLengths, codeLengthLengths.Length);
			int position = 0;
			int totalLengthCount = literalCount + distanceCount;
			while (position < totalLengthCount) {
				int symbol = codeLengthTable.Decode(bits);
				switch (symbol) {
					case >= 0 and <= 15:
						lengths[position++] = symbol;
						break;
					case 16:
						if (position == 0)
							throw InvalidDeflate();
						int repeatPrevious = bits.ReadBits(2) + 3;
						if (repeatPrevious > totalLengthCount - position)
							throw InvalidDeflate();
						int previous = lengths[position - 1];
						for (int i = 0; i < repeatPrevious; i++)
							lengths[position++] = previous;
						break;
					case 17:
						int repeatZeroShort = bits.ReadBits(3) + 3;
						if (repeatZeroShort > totalLengthCount - position)
							throw InvalidDeflate();
						for (int i = 0; i < repeatZeroShort; i++)
							lengths[position++] = 0;
						break;
					case 18:
						int repeatZeroLong = bits.ReadBits(7) + 11;
						if (repeatZeroLong > totalLengthCount - position)
							throw InvalidDeflate();
						for (int i = 0; i < repeatZeroLong; i++)
							lengths[position++] = 0;
						break;
					default:
						throw InvalidDeflate();
				}
			}

			literalTable.Set(lengths, literalCount);
			distanceTable.Set(lengths, literalCount, distanceCount);
		}

		static void ReadHuffmanBlock(BitReader bits, HuffmanTable literalTable,
			HuffmanTable distanceTable) {
			while (true) {
				int symbol = literalTable.Decode(bits);
				if (symbol < 256)
					continue;
				if (symbol == 256)
					return;
				if (symbol < 257 || symbol > 285)
					throw InvalidDeflate();
				int lengthIndex = symbol - 257;
				bits.ReadBits(LengthExtraBits[lengthIndex]);
				int distanceSymbol = distanceTable.Decode(bits);
				if (distanceSymbol < 0 || distanceSymbol > 29)
					throw InvalidDeflate();
				bits.ReadBits(DistanceExtraBits[distanceSymbol]);
			}
		}

		static readonly int[] LengthExtraBits = {
			0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
			3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
		};
		static readonly int[] DistanceExtraBits = {
			0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
			7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
		};

		static int[] CreateFixedLiteralLengths() {
			var lengths = new int[288];
			for (int i = 0; i <= 143; i++)
				lengths[i] = 8;
			for (int i = 144; i <= 255; i++)
				lengths[i] = 9;
			for (int i = 256; i <= 279; i++)
				lengths[i] = 7;
			for (int i = 280; i < lengths.Length; i++)
				lengths[i] = 8;
			return lengths;
		}

		static int[] CreateFixedDistanceLengths() {
			var lengths = new int[32];
			for (int i = 0; i < lengths.Length; i++)
				lengths[i] = 5;
			return lengths;
		}

		static InvalidDataException InvalidDeflate() =>
			new InvalidDataException("The bundle entry contains invalid Deflate data.");

		sealed class HuffmanTable {
			readonly int[][] symbols = CreateLevels();
			readonly int[][] generations = CreateLevels();
			readonly int[] codeCounts = new int[16];
			readonly int[] nextCodes = new int[16];
			int generation;

			public void Set(int[] lengths, int count) => Set(lengths, 0, count);

			public void Set(int[] lengths, int offset, int count) {
				if (lengths is null || offset < 0 || count < 0 || count > lengths.Length - offset)
					throw new ArgumentOutOfRangeException();
				generation = checked(generation + 1);
				if (generation == 0)
					throw InvalidDeflate();
				Array.Clear(codeCounts, 0, codeCounts.Length);
				for (int i = 0; i < count; i++) {
					int length = lengths[offset + i];
					if (length < 0 || length > 15)
						throw InvalidDeflate();
					if (length != 0)
						codeCounts[length]++;
				}
				int code = 0;
				Array.Clear(nextCodes, 0, nextCodes.Length);
				for (int length = 1; length <= 15; length++) {
					code = (code + codeCounts[length - 1]) << 1;
					if (code + codeCounts[length] > (1 << length))
						throw InvalidDeflate();
					nextCodes[length] = code;
				}
				for (int i = 0; i < count; i++) {
					int length = lengths[offset + i];
					if (length == 0)
						continue;
					int reversedCode = ReverseBits(nextCodes[length]++, length);
					if (generations[length][reversedCode] == generation)
						throw InvalidDeflate();
					symbols[length][reversedCode] = i + 1;
					generations[length][reversedCode] = generation;
				}
			}

			public int Decode(BitReader bits) {
				int code = 0;
				for (int length = 1; length <= 15; length++) {
					code |= bits.ReadBit() << (length - 1);
					if (generations[length][code] == generation)
						return symbols[length][code] - 1;
				}
				throw InvalidDeflate();
			}

			static int[][] CreateLevels() {
				var result = new int[16][];
				for (int i = 1; i < result.Length; i++)
					result[i] = new int[1 << i];
				return result;
			}

			static int ReverseBits(int value, int count) {
				int result = 0;
				for (int i = 0; i < count; i++) {
					result = (result << 1) | (value & 1);
					value >>= 1;
				}
				return result;
			}
		}

		sealed class BitReader {
			readonly Stream stream;
			int bits;
			int bitCount;

			public BitReader(Stream stream) => this.stream = stream;

			public int ReadBit() => ReadBits(1);

			public int ReadBits(int count) {
				if (count < 0 || count > 16)
					throw new ArgumentOutOfRangeException(nameof(count));
				while (bitCount < count) {
					int next = stream.ReadByte();
					if (next < 0)
						throw InvalidDeflate();
					bits |= next << bitCount;
					bitCount += 8;
				}
				int result = bits & ((1 << count) - 1);
				bits >>= count;
				bitCount -= count;
				return result;
			}

			public void AlignToByte() {
				int discard = bitCount & 7;
				if (discard != 0)
					ReadBits(discard);
			}
		}
	}
}
