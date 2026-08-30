// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.IO;

namespace dnSpy.Bundles {
	/// <summary>One marker and its preceding pointer that passed scanner-level checks.</summary>
	sealed class BundleSignatureMatch {
		public BundleSignatureMatch(long markerOffset, long headerOffset) {
			MarkerOffset = markerOffset;
			HeaderOffset = headerOffset;
		}

		public long MarkerOffset { get; }
		public long HeaderOffset { get; }
	}

	sealed class BundleSignatureScanResult {
		public bool SignatureFound { get; set; }
		public BundleSignatureMatch? FirstValidMatch { get; set; }
		public bool MultipleValidMatches { get; set; }
		public BundleReadError? FirstInvalidPointer { get; set; }
}

	/// <summary>
	/// Finds official bundle markers without loading an executable into memory.
	/// </summary>
	static class BundleSignatureScanner {
		// This is SHA-256 of the string ".net core bundle", as used by HostModel.
		internal static readonly byte[] Signature = {
			0x8B, 0x12, 0x02, 0xB9, 0x6A, 0x61, 0x20, 0x38,
			0x72, 0x7B, 0x93, 0x02, 0x14, 0xD7, 0xA0, 0x32,
			0x13, 0xF5, 0xB9, 0xE6, 0xEF, 0xAE, 0x33, 0x18,
			0xEE, 0x3B, 0x2D, 0xCE, 0x24, 0xB3, 0x6A, 0xAE,
		};

		const int ScanChunkSize = 64 * 1024;

		public static BundleSignatureScanResult Scan(Stream stream, long fileLength,
			long maximumSearchBytes) {
			if (stream is null)
				throw new ArgumentNullException(nameof(stream));
			if (!stream.CanRead || !stream.CanSeek)
				throw new ArgumentException("The bundle source must be a readable, seekable stream.", nameof(stream));
			if (fileLength < 0)
				throw new ArgumentOutOfRangeException(nameof(fileLength));
			if (maximumSearchBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maximumSearchBytes));

			var result = new BundleSignatureScanResult();
			long searchLength = Math.Min(fileLength, maximumSearchBytes);
			if (searchLength < Signature.Length)
				return result;

			// Keep the signature-sized overlap so a marker split over two reads is examined once.
			byte[] buffer = new byte[ScanChunkSize + Signature.Length - 1];
			int carry = 0;
			long bufferStart = 0;
			long bytesRead = 0;
			stream.Position = 0;

			while (bytesRead < searchLength) {
				long remaining = searchLength - bytesRead;
				int requested = (int)Math.Min(ScanChunkSize, remaining);
				int read = stream.Read(buffer, carry, requested);
				if (read == 0)
					break;

				int available = carry + read;
				for (int index = 0; index <= available - Signature.Length; index++) {
					if (buffer[index] != Signature[0] || !Matches(buffer, index))
						continue;

					result.SignatureFound = true;
					long markerOffset = checked(bufferStart + index);
					if (markerOffset < sizeof(long)) {
						RememberInvalidPointer(result, markerOffset,
							"The bundle marker does not have a preceding header pointer.");
						continue;
					}

					// The complete preceding pointer is always in this buffer: the overlap is
					// larger than the pointer and every candidate is at least eight bytes in.
					int pointerIndex = checked(index - sizeof(long));
					long headerOffset = ReadInt64LittleEndian(buffer, pointerIndex);
					// The pointer must target the manifest appended after the apphost marker.
					// Use subtraction to avoid overflowing markerOffset + Signature.Length.
					bool pointerIsValid = headerOffset > markerOffset &&
						headerOffset - markerOffset >= Signature.Length &&
						headerOffset < fileLength;
					if (!pointerIsValid) {
						RememberInvalidPointer(result, markerOffset,
							"The bundle header pointer is outside the file or precedes the marker.");
						continue;
					}

					if (result.FirstValidMatch is null)
						result.FirstValidMatch = new BundleSignatureMatch(markerOffset, headerOffset);
					else
						result.MultipleValidMatches = true;
				}

				bytesRead = checked(bytesRead + read);
				int nextCarry = Math.Min(Signature.Length - 1, available);
				if (nextCarry != 0)
					Buffer.BlockCopy(buffer, available - nextCarry, buffer, 0, nextCarry);
				carry = nextCarry;
				bufferStart = checked(bufferStart + available - nextCarry);
			}

			return result;
		}

		static bool Matches(byte[] buffer, int index) {
			for (int i = 1; i < Signature.Length; i++) {
				if (buffer[index + i] != Signature[i])
					return false;
			}
			return true;
		}

		static long ReadInt64LittleEndian(byte[] buffer, int index) {
			unchecked {
				ulong value = 0;
				for (int i = 0; i < sizeof(long); i++)
					value |= (ulong)buffer[index + i] << (8 * i);
				return (long)value;
			}
		}

		static void RememberInvalidPointer(BundleSignatureScanResult result, long markerOffset, string message) {
			result.FirstInvalidPointer ??= new BundleReadError(
				BundleReadErrorCode.InvalidHeaderOffset, message, offset: markerOffset);
		}
	}
}
