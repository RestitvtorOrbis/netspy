// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;

namespace dnSpy.Bundles {
	/// <summary>
	/// Reconstructs the unbundled prefix required by the official HostModel bundler.
	/// </summary>
	public sealed class WindowsAppHostReconstructor {
	const int HeaderPointerSize = sizeof(long);
	const int BundleSignatureSize = 32;
	const int HostModelPlaceholderSize = HeaderPointerSize + BundleSignatureSize;
	const int CopyBufferSize = 64 * 1024;
	const int PeDataDirectoryCountOffset32 = 92;
	const int PeDataDirectoryCountOffset64 = 108;
	const int PeDataDirectoryOffset32 = 96;
	const int PeDataDirectoryOffset64 = 112;
	const int CertificateDirectoryIndex = 4;

	/// <summary>
	/// Creates a private temporary host from a parsed bundle workspace. The returned object owns
	/// all temporary artifacts and must be disposed by the caller.
	/// </summary>
	public WindowsAppHostReconstruction Reconstruct(BundleWorkspace workspace) {
		if (workspace is null)
			throw new ArgumentNullException(nameof(workspace));
		return Reconstruct(workspace.Bundle);
	}

	/// <summary>
	/// Creates a private temporary host from parsed bundle metadata.
	/// </summary>
	public WindowsAppHostReconstruction Reconstruct(BundleFile bundle) {
		if (bundle is null)
			throw new ArgumentNullException(nameof(bundle));
		bundle.EnsureNotDisposed();

		SourceLayout layout = ValidateSource(bundle);
		string temporaryDirectory = CreatePrivateDirectory();
		string hostPath = System.IO.Path.Combine(temporaryDirectory, "apphost.exe");
		try {
			CopyPrefixAndResetPointer(bundle.Filename, hostPath, layout);
			ClearCertificateDirectory(hostPath, layout.CertificateDirectoryOffset,
				layout.HasAuthenticodeSignature);
			ValidateReconstructedHost(hostPath);
			return new WindowsAppHostReconstruction(hostPath, temporaryDirectory,
				layout.PayloadStart, layout.HeaderPointerOffset,
				layout.HasAuthenticodeSignature);
		}
		catch (WindowsAppHostReconstructionException) {
			WindowsAppHostReconstruction.TryDelete(hostPath);
			WindowsAppHostReconstruction.TryDeleteDirectory(temporaryDirectory);
			throw;
		}
		catch (Exception ex) when (IsTemporaryFailure(ex)) {
			WindowsAppHostReconstruction.TryDelete(hostPath);
			WindowsAppHostReconstruction.TryDeleteDirectory(temporaryDirectory);
			throw new WindowsAppHostReconstructionException(
				WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
				"The temporary apphost could not be created safely.", ex);
		}
		catch (Exception ex) {
			WindowsAppHostReconstruction.TryDelete(hostPath);
			WindowsAppHostReconstruction.TryDeleteDirectory(temporaryDirectory);
			throw new WindowsAppHostReconstructionException(
				WindowsAppHostReconstructionErrorCode.InvalidHostModelPlaceholder,
				"The reconstructed apphost could not be validated safely.", ex);
		}
	}

	SourceLayout ValidateSource(BundleFile bundle) {
		if (string.IsNullOrWhiteSpace(bundle.Filename))
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidArgument,
				"The bundle source filename is empty.");

		long sourceLength;
		try {
			using FileStream source = File.Open(bundle.Filename, FileMode.Open,
				FileAccess.Read, FileShare.Read);
			sourceLength = source.Length;
		}
		catch (Exception ex) when (IsSourceFailure(ex)) {
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
				"The bundle source could not be read as a Windows PE image.", ex);
		}
		if (sourceLength != bundle.FileLength)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
				"The bundle source length changed while it was being inspected.");

		PeLayout pe = ReadPeLayout(bundle.Filename, sourceLength);
		if (pe.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
			throw Failure(WindowsAppHostReconstructionErrorCode.UnsupportedArchitecture,
				"Temporary apphost reconstruction supports only Windows x64 bundles.");

		if (bundle.MarkerOffset < HeaderPointerSize ||
			bundle.MarkerOffset > sourceLength - BundleSignatureSize ||
			bundle.HeaderOffset < 0 || bundle.HeaderOffset >= sourceLength)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleMarker,
				"The bundle marker or manifest header is outside the source file.");

		if (bundle.Entries.Count == 0)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
				"The bundle has no payload entries from which to determine the apphost boundary.");

		long payloadStart = sourceLength;
		long maximumEntryEnd = 0;
		var ranges = new List<PhysicalRange>(bundle.Entries.Count);
		foreach (BundleEntry entry in bundle.Entries) {
			if (entry is null || entry.Offset < 0 || entry.Size < 0 || entry.CompressedSize < 0)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"A bundle entry has a negative physical boundary.");
			long physicalSize = entry.IsCompressed ? entry.CompressedSize : entry.Size;
			if (entry.IsCompressed && (entry.Size == 0 || entry.CompressedSize >= entry.Size))
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"A compressed bundle entry has an inconsistent physical boundary.");
			long end;
			try {
				end = checked(entry.Offset + physicalSize);
			}
			catch (OverflowException ex) {
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"A bundle entry physical boundary overflows the file offset space.", ex);
			}
			if (end > sourceLength)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"A bundle entry physical range exceeds the source file.");
			payloadStart = Math.Min(payloadStart, entry.Offset);
			maximumEntryEnd = Math.Max(maximumEntryEnd, end);
			if (physicalSize != 0)
				ranges.Add(new PhysicalRange(entry.Offset, end));
		}

		ValidateNonOverlappingRanges(ranges);
		if (maximumEntryEnd > bundle.HeaderOffset ||
			(bundle.HeaderEndOffset != 0 &&
				(bundle.HeaderEndOffset < bundle.HeaderOffset || bundle.HeaderEndOffset > sourceLength)))
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
				"The manifest header precedes or intersects a bundle entry range.");
		if (pe.HasAuthenticodeSignature) {
			long manifestEnd = bundle.HeaderEndOffset != 0 ? bundle.HeaderEndOffset : bundle.HeaderOffset;
			long certificateEnd;
			try {
				certificateEnd = checked(pe.CertificateTableOffset + pe.CertificateTableSize);
			}
			catch (OverflowException ex) {
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"The PE certificate-table range overflows the source file.", ex);
			}
			if (pe.CertificateTableOffset < manifestEnd || certificateEnd > sourceLength)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"The PE certificate-table range must follow the complete bundle manifest.");
		}
		if (payloadStart < 8 || pe.CertificateDirectoryOffset > payloadStart - 8)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
				"The PE certificate-table directory is not wholly inside the apphost prefix.");

		long pointerOffset = bundle.MarkerOffset - HeaderPointerSize;
		if (pointerOffset < 0 || bundle.MarkerOffset + BundleSignatureSize > payloadStart ||
			pointerOffset + HeaderPointerSize > payloadStart)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleMarker,
				"The bundle marker and preceding pointer are not wholly inside the apphost prefix.");
		long pointerValue;
		try {
			using FileStream source = File.Open(bundle.Filename, FileMode.Open, FileAccess.Read, FileShare.Read);
			SeekExactly(source, pointerOffset);
			pointerValue = ReadInt64LittleEndian(source);
			if (pointerValue != bundle.HeaderOffset || pointerValue < bundle.MarkerOffset + BundleSignatureSize ||
				pointerValue >= sourceLength)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleMarker,
					"The bundle header pointer does not identify the parsed manifest.");
			SeekExactly(source, bundle.MarkerOffset);
			if (!MatchesSignature(source))
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleMarker,
					"The parsed bundle marker does not match the official HostModel signature.");
		}
		catch (WindowsAppHostReconstructionException) {
			throw;
		}
		catch (Exception ex) when (IsSourceFailure(ex) || ex is EndOfStreamException) {
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleMarker,
				"The bundle marker or preceding pointer could not be read safely.", ex);
		}

		return new SourceLayout(payloadStart, pointerOffset,
			pe.CertificateDirectoryOffset, pe.HasAuthenticodeSignature);
	}

	static void ValidateNonOverlappingRanges(List<PhysicalRange> ranges) {
		ranges.Sort((left, right) => {
			int compare = left.Start.CompareTo(right.Start);
			return compare != 0 ? compare : left.End.CompareTo(right.End);
		});
		for (int i = 1; i < ranges.Count; i++) {
			if (ranges[i].Start < ranges[i - 1].End)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"Bundle entry physical ranges overlap.");
		}
	}

	static void CopyPrefixAndResetPointer(string sourceFilename, string hostPath,
		SourceLayout layout) {
		try {
			using FileStream source = File.Open(sourceFilename, FileMode.Open, FileAccess.Read, FileShare.Read);
			using FileStream destination = new FileStream(hostPath, FileMode.CreateNew,
				FileAccess.ReadWrite, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
			CopyExactly(source, destination, layout.PayloadStart);
			if (source.Position != layout.PayloadStart || destination.Length != layout.PayloadStart)
				throw new EndOfStreamException();
			destination.Position = layout.HeaderPointerOffset;
			byte[] zeros = new byte[HeaderPointerSize];
			destination.Write(zeros, 0, zeros.Length);
			destination.Flush(flushToDisk: true);
		}
		catch (EndOfStreamException ex) {
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
				"The source ended before the validated apphost prefix was copied.", ex);
		}
		catch (WindowsAppHostReconstructionException) {
			throw;
		}
		catch (Exception ex) when (IsSourceFailure(ex) || ex is NotSupportedException) {
			throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
				"The temporary apphost could not be written safely.", ex);
		}
	}

	static void CopyExactly(Stream source, Stream destination, long count) {
		byte[] buffer = new byte[CopyBufferSize];
		long remaining = count;
		while (remaining != 0) {
			int requested = (int)Math.Min(buffer.Length, remaining);
			int read = source.Read(buffer, 0, requested);
			if (read <= 0)
				throw new EndOfStreamException();
			destination.Write(buffer, 0, read);
			remaining -= read;
		}
	}

	static void ClearCertificateDirectory(string hostPath, long certificateDirectoryOffset,
		bool hasAuthenticodeSignature) {
		if (!hasAuthenticodeSignature)
			return;
		if (certificateDirectoryOffset < 0 || certificateDirectoryOffset > int.MaxValue)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
				"The PE certificate-table directory is outside the apphost prefix.");
		try {
			using FileStream host = File.Open(hostPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
			if (certificateDirectoryOffset + 8 > host.Length)
				throw new EndOfStreamException();
			host.Position = certificateDirectoryOffset;
			byte[] zeros = new byte[8];
			host.Write(zeros, 0, zeros.Length);
			host.Flush(flushToDisk: true);
		}
		catch (WindowsAppHostReconstructionException) {
			throw;
		}
		catch (Exception ex) when (IsSourceFailure(ex) || ex is EndOfStreamException) {
			throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
				"The temporary PE certificate-table directory could not be cleared.", ex);
		}
	}

	static void ValidateReconstructedHost(string hostPath) {
		try {
			using FileStream stream = File.Open(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var reader = new PEReader(stream);
			if (reader.PEHeaders.PEHeader is null ||
				reader.PEHeaders.CoffHeader.Machine !=
					System.Reflection.PortableExecutable.Machine.Amd64)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The reconstructed apphost is not a valid Windows x64 PE image.");
			foreach (SectionHeader section in reader.PEHeaders.SectionHeaders) {
				long sectionEnd = checked((long)section.PointerToRawData + section.SizeOfRawData);
				if (sectionEnd > stream.Length)
					throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
						"The reconstructed apphost truncates a PE section.");
			}
		}
		catch (WindowsAppHostReconstructionException) {
			throw;
		}
		catch (Exception ex) when (IsSourceFailure(ex) || ex is EndOfStreamException ||
			ex is BadImageFormatException ||
			ex is ArgumentException || ex is InvalidOperationException) {
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
				"The reconstructed apphost is not a valid Windows x64 PE image.", ex);
		}

		long signatureCount = CountPatternOccurrences(hostPath, BundleSignatureScanner.Signature);
		long placeholderCount = CountPlaceholders(hostPath);
		if (signatureCount != 1 || placeholderCount != 1)
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidHostModelPlaceholder,
				"The reconstructed apphost must contain exactly one HostModel bundle placeholder.");
	}

	static long CountPatternOccurrences(string filename, byte[] pattern) {
		if (pattern is null)
			throw new ArgumentNullException(nameof(pattern));
		if (pattern.Length == 0)
			return 0;
		long count = 0;
		byte[] buffer = new byte[CopyBufferSize + pattern.Length - 1];
		int carry = 0;
		try {
			using FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
			while (true) {
				int read = stream.Read(buffer, carry, CopyBufferSize);
				if (read == 0)
					break;
				int available = carry + read;
				for (int index = 0; index <= available - pattern.Length; index++) {
					if (Matches(pattern, buffer, index))
						count++;
				}
				if (count > 1)
					return count;
				carry = Math.Min(pattern.Length - 1, available);
				if (carry != 0)
					Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
			}
			return count;
		}
		catch (Exception ex) when (IsSourceFailure(ex)) {
			throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
				"The reconstructed apphost could not be scanned safely.", ex);
		}
	}

	static long CountPlaceholders(string filename) {
		byte[] pattern = new byte[HostModelPlaceholderSize];
		Buffer.BlockCopy(BundleSignatureScanner.Signature, 0, pattern, HeaderPointerSize,
			BundleSignatureSize);
		long count = 0;
		byte[] buffer = new byte[CopyBufferSize + pattern.Length - 1];
		int carry = 0;
		try {
			using FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
			while (true) {
				int read = stream.Read(buffer, carry, CopyBufferSize);
				if (read == 0)
					break;
				int available = carry + read;
				for (int index = 0; index <= available - pattern.Length; index++) {
					if (Matches(pattern, buffer, index))
						count++;
				}
				if (count > 1)
					return count;
				carry = Math.Min(pattern.Length - 1, available);
				if (carry != 0)
					Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
			}
			return count;
		}
		catch (Exception ex) when (IsSourceFailure(ex)) {
			throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
				"The reconstructed apphost could not be scanned safely.", ex);
		}
	}

	static bool Matches(byte[] pattern, byte[] buffer, int offset) {
		for (int i = 0; i < pattern.Length; i++)
			if (pattern[i] != buffer[offset + i])
				return false;
		return true;
	}

	static bool MatchesSignature(Stream stream) {
		for (int index = 0; index < BundleSignatureSize; index++)
			if (stream.ReadByte() != BundleSignatureScanner.Signature[index])
				return false;
		return true;
	}

	static PeLayout ReadPeLayout(string filename, long sourceLength) {
		try {
			using FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var reader = new PEReader(stream);
			PEHeaders headers = reader.PEHeaders;
			if (headers.PEHeader is null)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source is not a valid Windows PE image.");
			stream.Position = 0x3C;
			uint peOffsetValue = ReadUInt32(stream);
			if (peOffsetValue > int.MaxValue)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source has an invalid PE header offset.");
			int peOffset = (int)peOffsetValue;
			int optionalHeaderSize = headers.CoffHeader.SizeOfOptionalHeader;
			long optionalEnd = checked((long)peOffset + 24 + optionalHeaderSize);
			if (optionalEnd > sourceLength || optionalHeaderSize < 2)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source has a truncated PE optional header.");
			stream.Position = peOffset + 24;
			ushort magic = ReadUInt16(stream);
			int countOffset;
			int directoriesOffset;
			if (magic == 0x10B) {
				countOffset = PeDataDirectoryCountOffset32;
				directoriesOffset = PeDataDirectoryOffset32;
			}
			else if (magic == 0x20B) {
				countOffset = PeDataDirectoryCountOffset64;
				directoriesOffset = PeDataDirectoryOffset64;
			}
			else
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source has an unsupported PE optional-header format.");
			if (optionalHeaderSize < directoriesOffset + (CertificateDirectoryIndex + 1) * 8) {
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source has no complete PE certificate-table directory.");
			}
			long countPosition = checked((long)peOffset + 24 + countOffset);
			long directoryPosition = checked((long)peOffset + 24 + directoriesOffset +
				CertificateDirectoryIndex * 8);
			if (countPosition + sizeof(uint) > sourceLength || directoryPosition + 8 > sourceLength)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source has truncated PE data directories.");
			stream.Position = countPosition;
			uint directoryCount = ReadUInt32(stream);
			if (directoryCount <= CertificateDirectoryIndex)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
					"The bundle source has no certificate-table directory.");
			stream.Position = directoryPosition;
			uint certificateOffset = ReadUInt32(stream);
			uint certificateSize = ReadUInt32(stream);
			bool hasOffset = certificateOffset != 0;
			bool hasSize = certificateSize != 0;
			if (hasOffset != hasSize)
				throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
					"The PE certificate-table directory is malformed.");
			if (hasOffset) {
				long certificateEnd = checked((long)certificateOffset + certificateSize);
				if (certificateEnd > sourceLength)
					throw Failure(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary,
						"The PE certificate-table directory exceeds the source file.");
			}
			return new PeLayout(headers.CoffHeader.Machine, directoryPosition,
				certificateOffset, certificateSize, hasOffset && hasSize);
		}
		catch (WindowsAppHostReconstructionException) {
			throw;
		}
		catch (Exception ex) when (IsSourceFailure(ex) || ex is EndOfStreamException ||
			ex is BadImageFormatException ||
			ex is ArgumentException || ex is OverflowException || ex is InvalidOperationException) {
			throw Failure(WindowsAppHostReconstructionErrorCode.InvalidPeImage,
				"The bundle source is not a valid Windows PE image.", ex);
		}
	}

	static ushort ReadUInt16(Stream stream) {
		int first = stream.ReadByte();
		int second = stream.ReadByte();
		if (first < 0 || second < 0)
			throw new EndOfStreamException();
		return (ushort)(first | second << 8);
	}

	static uint ReadUInt32(Stream stream) {
		uint result = 0;
		for (int index = 0; index < sizeof(uint); index++) {
			int value = stream.ReadByte();
			if (value < 0)
				throw new EndOfStreamException();
			result |= (uint)value << (8 * index);
		}
		return result;
	}

	static long ReadInt64LittleEndian(Stream stream) {
		unchecked {
			ulong result = 0;
			for (int index = 0; index < HeaderPointerSize; index++) {
				int value = stream.ReadByte();
				if (value < 0)
					throw new EndOfStreamException();
				result |= (ulong)value << (8 * index);
			}
			return (long)result;
		}
	}

	static void SeekExactly(Stream stream, long offset) {
		if (offset < 0 || offset > stream.Length)
			throw new EndOfStreamException();
		stream.Position = offset;
	}

	static string CreatePrivateDirectory() {
		string tempRoot;
		try {
			tempRoot = System.IO.Path.GetTempPath();
		}
		catch (Exception ex) when (IsSourceFailure(ex)) {
			throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
				"A private temporary directory could not be selected.", ex);
		}
		for (int attempt = 0; attempt < 10; attempt++) {
			string directory = System.IO.Path.Combine(tempRoot,
				"dnSpy.Bundle." + Guid.NewGuid().ToString("N"));
			try {
				Directory.CreateDirectory(directory);
				return directory;
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException ex) {
				throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
					"A private temporary directory could not be created.", ex);
			}
		}
		throw Failure(WindowsAppHostReconstructionErrorCode.TemporaryFileFailure,
			"A unique private temporary directory could not be created.");
	}

	static WindowsAppHostReconstructionException Failure(
		WindowsAppHostReconstructionErrorCode code, string message, Exception? inner = null) =>
		inner is null ? new WindowsAppHostReconstructionException(code, message) :
		new WindowsAppHostReconstructionException(code, message, inner);

	static bool IsSourceFailure(Exception ex) => ex is IOException ||
		ex is UnauthorizedAccessException || ex is NotSupportedException ||
		ex is System.Security.SecurityException;

	static bool IsTemporaryFailure(Exception ex) => IsSourceFailure(ex) ||
		ex is ObjectDisposedException || ex is ArgumentException;

	readonly struct PhysicalRange {
		public PhysicalRange(long start, long end) {
			Start = start;
			End = end;
		}
		public long Start { get; }
		public long End { get; }
	}

	readonly struct SourceLayout {
		public SourceLayout(long payloadStart, long headerPointerOffset,
			long certificateDirectoryOffset, bool hasAuthenticodeSignature) {
			PayloadStart = payloadStart;
			HeaderPointerOffset = headerPointerOffset;
			CertificateDirectoryOffset = certificateDirectoryOffset;
			HasAuthenticodeSignature = hasAuthenticodeSignature;
		}
		public long PayloadStart { get; }
		public long HeaderPointerOffset { get; }
		public long CertificateDirectoryOffset { get; }
		public bool HasAuthenticodeSignature { get; }
	}

	readonly struct PeLayout {
		public PeLayout(System.Reflection.PortableExecutable.Machine machine,
			long certificateDirectoryOffset, long certificateTableOffset,
			long certificateTableSize, bool hasAuthenticodeSignature) {
			Machine = machine;
			CertificateDirectoryOffset = certificateDirectoryOffset;
			CertificateTableOffset = certificateTableOffset;
			CertificateTableSize = certificateTableSize;
			HasAuthenticodeSignature = hasAuthenticodeSignature;
		}
		public System.Reflection.PortableExecutable.Machine Machine { get; }
		public long CertificateDirectoryOffset { get; }
		public long CertificateTableOffset { get; }
		public long CertificateTableSize { get; }
		public bool HasAuthenticodeSignature { get; }
	}
}
}
