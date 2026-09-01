// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using dnSpy.Bundles;
using Microsoft.NET.HostModel.AppHost;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class WindowsAppHostReconstructorTests {
		[Fact]
		public void ReconstructsExactPayloadPrefixAndHostModelPlaceholder() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			byte[] source = File.ReadAllBytes(fixture.BundlePath);
			string sourceHash = Hash(source);
			BundleOpenResult opened = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile bundle = opened.Bundle!;
			using var workspace = new BundleWorkspace(bundle);
			using WindowsAppHostReconstruction reconstructed =
				new WindowsAppHostReconstructor().Reconstruct(workspace);

			Assert.Equal(bundle.Entries.Min(entry => entry.Offset), reconstructed.PayloadStart);
			Assert.Equal(reconstructed.PayloadStart, new FileInfo(reconstructed.HostPath).Length);
			byte[] host = File.ReadAllBytes(reconstructed.HostPath);
			byte[] expected = source.AsSpan(0, checked((int)reconstructed.PayloadStart)).ToArray();
			Array.Clear(expected, checked((int)reconstructed.HeaderPointerOffset), 8);
			Assert.Equal(expected, host);
			Assert.Equal(0L, ReadInt64(host, checked((int)reconstructed.HeaderPointerOffset)));
			Assert.Equal(1, CountPlaceholders(host));
			Assert.True(PEUtils.IsPEImage(reconstructed.HostPath));
			using (var stream = File.OpenRead(reconstructed.HostPath))
			using (var reader = new PEReader(stream))
				Assert.Equal(Machine.Amd64, reader.PEHeaders.CoffHeader.Machine);
			Assert.Equal(sourceHash, Hash(File.ReadAllBytes(fixture.BundlePath)));
		}

		[Fact]
		public void DisposeRemovesPrivateTemporaryHostAndDirectory() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			BundleOpenResult opened = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile bundle = opened.Bundle!;
			using var workspace = new BundleWorkspace(bundle);
			WindowsAppHostReconstruction reconstructed =
				new WindowsAppHostReconstructor().Reconstruct(workspace);
			string hostPath = reconstructed.HostPath;
			string directory = reconstructed.TemporaryDirectory;
			Assert.True(File.Exists(hostPath));
			Assert.True(Directory.Exists(directory));

			reconstructed.Dispose();
			reconstructed.Dispose();

			Assert.False(File.Exists(hostPath));
			Assert.False(Directory.Exists(directory));
		}

		[Fact]
		public void CertificateDirectoryIsClearedFromTemporaryHostOnly() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			byte[] source = File.ReadAllBytes(fixture.BundlePath);
			int certificateDirectory = GetDataDirectoryOffset(source, 4);
			int certificateOffset = source.Length;
			Array.Resize(ref source, source.Length + 8);
			WriteUInt32(source, certificateDirectory, (uint)certificateOffset);
			WriteUInt32(source, certificateDirectory + 4, 8);
			using var sourceFile = new TemporaryFile(source);
			string sourceHash = Hash(source);
			BundleOpenResult opened = new BundleReader().Open(sourceFile.Path);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile bundle = opened.Bundle!;
			using var workspace = new BundleWorkspace(bundle);
			using WindowsAppHostReconstruction reconstructed =
				new WindowsAppHostReconstructor().Reconstruct(workspace);

			using var stream = File.OpenRead(reconstructed.HostPath);
			using var reader = new PEReader(stream);
			Assert.Equal(0, reader.PEHeaders.PEHeader!.CertificateTableDirectory.RelativeVirtualAddress);
			Assert.Equal(0, reader.PEHeaders.PEHeader.CertificateTableDirectory.Size);
			Assert.Equal(bundle.Entries.Min(entry => entry.Offset), stream.Length);
			Assert.Equal(sourceHash, Hash(File.ReadAllBytes(sourceFile.Path)));
		}

		[Fact]
		public void CertificateTableInsideManifestFailsBeforeCreatingTemporaryArtifacts() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			using var sourceFile = new TemporaryFile(File.ReadAllBytes(fixture.BundlePath));
			BundleOpenResult initial = new BundleReader().Open(sourceFile.Path);
			Assert.Equal(BundleOpenStatus.Success, initial.Status);
			long manifestEnd = initial.Bundle!.HeaderEndOffset;
			initial.Bundle.Dispose();
			byte[] source = File.ReadAllBytes(sourceFile.Path);
			int certificateDirectory = GetDataDirectoryOffset(source, 4);
			WriteUInt32(source, certificateDirectory, checked((uint)(manifestEnd - 1)));
			WriteUInt32(source, certificateDirectory + 4, 1);
			File.WriteAllBytes(sourceFile.Path, source);
			HashSet<string> temporaryDirectoriesBefore = new HashSet<string>(
				Directory.EnumerateDirectories(Path.GetTempPath(), "dnSpy.Bundle.*"),
				StringComparer.OrdinalIgnoreCase);
			BundleOpenResult opened = new BundleReader().Open(sourceFile.Path);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile bundle = opened.Bundle!;
			using var workspace = new BundleWorkspace(bundle);

			WindowsAppHostReconstructionException error = Assert.Throws<WindowsAppHostReconstructionException>(
				() => new WindowsAppHostReconstructor().Reconstruct(workspace));

			Assert.Equal(WindowsAppHostReconstructionErrorCode.InvalidBundleBoundary, error.Code);
			foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), "dnSpy.Bundle.*"))
				Assert.Contains(directory, temporaryDirectoriesBefore);
		}

		[Fact]
		public void InvalidMarkerBoundaryFailsBeforeCreatingTemporaryArtifacts() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			BundleOpenResult opened = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile parsed = opened.Bundle!;
			BundleEntry[] entries = parsed.Entries.Select(entry => new BundleEntry(entry.Index,
				entry.Offset, entry.Size, entry.CompressedSize, entry.RawFileType, entry.FileType,
				entry.RelativePath)).ToArray();
			// Put the marker at the beginning of the first payload. The existing source remains
			// untouched; only the metadata shell is malformed.
			using var malformed = new BundleFile(parsed.Filename, parsed.FileLength,
				entries.Min(entry => entry.Offset), parsed.HeaderOffset, parsed.Manifest, entries);
			using var workspace = new BundleWorkspace(malformed);

			WindowsAppHostReconstructionException error = Assert.Throws<WindowsAppHostReconstructionException>(
				() => new WindowsAppHostReconstructor().Reconstruct(workspace));

			Assert.Equal(WindowsAppHostReconstructionErrorCode.InvalidBundleMarker, error.Code);
			Assert.Contains("marker", error.Message, StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void InvalidPayloadBoundaryCleansTemporaryHostAfterPrefixCopyFailure() {
			HashSet<string> temporaryDirectoriesBefore = new HashSet<string>(
				Directory.EnumerateDirectories(Path.GetTempPath(), "dnSpy.Bundle.*"),
				StringComparer.OrdinalIgnoreCase);
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			BundleOpenResult opened = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile parsed = opened.Bundle!;
			BundleEntry[] entries = parsed.Entries.Select(entry => new BundleEntry(entry.Index,
				entry.Offset, entry.Size, entry.CompressedSize, entry.RawFileType, entry.FileType,
				entry.RelativePath)).ToArray();
			entries[0] = new BundleEntry(entries[0].Index, parsed.MarkerOffset + 32, 0,
				0, entries[0].RawFileType, entries[0].FileType, entries[0].RelativePath);
			using var malformed = new BundleFile(parsed.Filename, parsed.FileLength,
				parsed.MarkerOffset, parsed.HeaderOffset, parsed.Manifest, entries);
			using var workspace = new BundleWorkspace(malformed);

			WindowsAppHostReconstructionException error = Assert.Throws<WindowsAppHostReconstructionException>(
				() => new WindowsAppHostReconstructor().Reconstruct(workspace));

			Assert.Equal(WindowsAppHostReconstructionErrorCode.InvalidPeImage, error.Code);
			foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), "dnSpy.Bundle.*"))
				Assert.Contains(directory, temporaryDirectoriesBefore);
		}

		static int CountPlaceholders(byte[] bytes) {
			byte[] pattern = new byte[40];
			Buffer.BlockCopy(BundleSignatureScannerForTests.Signature, 0, pattern, 8, 32);
			int count = 0;
			for (int offset = 0; offset <= bytes.Length - pattern.Length; offset++) {
				if (bytes.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
					count++;
			}
			return count;
		}

		static long ReadInt64(byte[] bytes, int offset) {
			unchecked {
				ulong value = 0;
				for (int index = 0; index < 8; index++)
					value |= (ulong)bytes[offset + index] << (8 * index);
				return (long)value;
			}
		}

		static string Hash(byte[] bytes) {
			using SHA256 sha256 = SHA256.Create();
			return Convert.ToHexString(sha256.ComputeHash(bytes));
		}

		static int GetDataDirectoryOffset(byte[] bytes, int index) {
			int pe = checked((int)ReadUInt32(bytes, 0x3C));
			int optional = pe + 24;
			ushort magic = ReadUInt16(bytes, optional);
			return checked(optional + (magic == 0x20B ? 112 : 96) + index * 8);
		}

		static ushort ReadUInt16(byte[] bytes, int offset) =>
			(ushort)(bytes[offset] | bytes[offset + 1] << 8);

		static uint ReadUInt32(byte[] bytes, int offset) =>
			(uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 |
			bytes[offset + 3] << 24);

		static void WriteUInt32(byte[] bytes, int offset, uint value) {
			for (int index = 0; index < 4; index++)
				bytes[offset + index] = (byte)(value >> (index * 8));
		}

		static class BundleSignatureScannerForTests {
			public static readonly byte[] Signature = Convert.FromBase64String(
				"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");
		}

		sealed class TemporaryFile : IDisposable {
			public TemporaryFile(byte[] bytes) {
				Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
					"dnspy-bundle-reconstruction-" + Guid.NewGuid().ToString("N") + ".exe");
				File.WriteAllBytes(Path, bytes);
			}
			public string Path { get; }
			public void Dispose() {
				if (File.Exists(Path))
					File.Delete(Path);
			}
		}
	}
}
