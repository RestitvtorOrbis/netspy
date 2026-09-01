// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class WindowsBundleEligibilityTests {
		readonly WindowsBundleEligibilityInspector inspector = new();

		[Fact]
		public void SupportedWindowsX64BundlePassesAndIsHashedWithoutMutation() {
			ModernBundleFixture fixture = GetFixture();
			string before = Hash(fixture.BundlePath);

			WindowsBundleEligibilityResult result = inspector.Inspect(fixture.BundlePath);

			Assert.True(result.IsEligible);
			Assert.Equal(WindowsBundleEligibilityStatus.Eligible, result.Status);
			Assert.Equal("The Windows x64 bundle is eligible for rebuilding.", result.Message);
			Assert.Equal(before, result.SourceSha256);
			Assert.False(result.HasAuthenticodeSignature);
			Assert.Equal(before, Hash(fixture.BundlePath));
		}

		[Fact]
		public void CertificateTableIsReportedWithoutClaimingPreservationOrMutatingSource() {
			using TempFile source = CopyFixture();
			byte[] bytes = File.ReadAllBytes(source.Path);
			int certificateDirectory = GetDataDirectoryOffset(bytes, 4);
			int certificateOffset = bytes.Length;
			Array.Resize(ref bytes, bytes.Length + 8);
			WriteUInt32(bytes, certificateDirectory, (uint)certificateOffset);
			WriteUInt32(bytes, certificateDirectory + 4, 8);
			File.WriteAllBytes(source.Path, bytes);
			string before = Hash(source.Path);

			WindowsBundleEligibilityResult result = inspector.Inspect(source.Path);

			Assert.True(result.IsEligible);
			Assert.True(result.HasAuthenticodeSignature);
			Assert.Equal("The Windows x64 bundle is eligible, but rebuilding it will invalidate its Authenticode signature.", result.Message);
			Assert.Equal(before, result.SourceSha256);
			Assert.Equal(before, Hash(source.Path));
		}

		[Fact]
		public void NonX64MachineHasPreciseUnsupportedArchitectureResult() {
			using TempFile source = CopyFixture();
			byte[] bytes = File.ReadAllBytes(source.Path);
			int pe = checked((int)ReadUInt32(bytes, 0x3C));
			WriteUInt16(bytes, pe + 4, 0x014C);
			File.WriteAllBytes(source.Path, bytes);

			WindowsBundleEligibilityResult result = inspector.Inspect(source.Path);

			Assert.False(result.IsEligible);
			Assert.Equal(WindowsBundleEligibilityStatus.UnsupportedArchitecture, result.Status);
			Assert.Equal("Save Bundle As currently supports only Windows x64 bundles; source machine is I386.", result.Message);
		}

		[Fact]
		public void UnknownRawTypeIsRejectedWithEntryDiagnostic() {
			using TempFile source = CopyFixture();
			SetManifestType(source.Path, "SingleFile.App.dll", 0xFE);

			WindowsBundleEligibilityResult result = inspector.Inspect(source.Path);

			Assert.Equal(WindowsBundleEligibilityStatus.UnknownFileType, result.Status);
			Assert.Equal("Bundle entry type 254 cannot be preserved by HostModel.", result.Message);
			Assert.Equal("SingleFile.App.dll", result.RelativePath);
			Assert.NotNull(result.EntryIndex);
		}

		[Fact]
		public void DirtyReadyToRunIsRejectedWhileOriginalReadyToRunRemainsInspectable() {
			using TempFile source = CopyFixture();
			BundleOpenResult open = new BundleReader().Open(source.Path);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			using var workspace = new BundleWorkspace(open.Bundle!);
			BundleEntry main = Assert.Single(workspace.Bundle.Entries,
				entry => entry.RelativePath == "SingleFile.App.dll");
			byte[] r2r = MakeReadyToRun(main.ReadAllBytes(main.Size));

			workspace.SetReplacement(main, r2r, new BundleReplacementInfo("r2r-test"));
			WindowsBundleEligibilityResult dirty = inspector.Inspect(workspace);
			Assert.Equal(WindowsBundleEligibilityStatus.DirtyReadyToRun, dirty.Status);
			Assert.Equal("A modified ReadyToRun entry cannot be rebuilt until ReadyToRun rewriting is supported.", dirty.Message);
			Assert.Equal(main.Index, dirty.EntryIndex);

			workspace.Revert(main);
			WindowsBundleEligibilityResult reverted = inspector.Inspect(workspace);
			Assert.Equal(WindowsBundleEligibilityStatus.Eligible, reverted.Status);
		}

		[Fact]
		public void DuplicateCurrentAssemblyIdentityIsRejectedDeterministically() {
			using TempFile source = CopyFixture();
			BundleOpenResult open = new BundleReader().Open(source.Path);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			using var workspace = new BundleWorkspace(open.Bundle!);
			BundleEntry main = Assert.Single(workspace.Bundle.Entries,
				entry => entry.RelativePath == "SingleFile.App.dll");
			BundleEntry dependency = Assert.Single(workspace.Bundle.Entries,
				entry => entry.RelativePath == "SingleFile.Dependency.dll");
			workspace.SetReplacement(dependency, main.ReadAllBytes(main.Size),
				new BundleReplacementInfo("duplicate-identity-test"));

			WindowsBundleEligibilityResult result = inspector.Inspect(workspace);

			Assert.Equal(WindowsBundleEligibilityStatus.AmbiguousAssemblyIdentity, result.Status);
			Assert.Equal("Duplicate managed assembly identity is ambiguous: SingleFile.App.dll, SingleFile.Dependency.dll.", result.Message);
			Assert.Equal(dependency.Index, result.EntryIndex);
		}

		[Fact]
		public void RetargetableFlagDoesNotChangeDuplicateAssemblyIdentity() {
			using TempFile source = CopyFixture();
			BundleOpenResult open = new BundleReader().Open(source.Path);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			using var workspace = new BundleWorkspace(open.Bundle!);
			BundleEntry main = Assert.Single(workspace.Bundle.Entries,
				entry => entry.RelativePath == "SingleFile.App.dll");
			BundleEntry dependency = Assert.Single(workspace.Bundle.Entries,
				entry => entry.RelativePath == "SingleFile.Dependency.dll");
			byte[] duplicate = AddRetargetableAssemblyFlag(main.ReadAllBytes(main.Size));

			workspace.SetReplacement(dependency, duplicate,
				new BundleReplacementInfo("duplicate-retargetable-test"));
			WindowsBundleEligibilityResult result = inspector.Inspect(workspace);

			Assert.Equal(WindowsBundleEligibilityStatus.AmbiguousAssemblyIdentity, result.Status);
			Assert.Equal("Duplicate managed assembly identity is ambiguous: SingleFile.App.dll, SingleFile.Dependency.dll.", result.Message);
			Assert.Equal(dependency.Index, result.EntryIndex);
		}

		[Fact]
		public void OversizedCompressedAssemblyFailsBeforeDecompression() {
			ModernBundleFixture fixture = GetFixture();
			BundleOpenResult open = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			BundleFile parsed = open.Bundle!;
			long oversized = checked(WindowsBundleEligibilityInspector.MaximumManagedAssemblyInspectionBytes + 1);
			var entry = new BundleEntry(0, parsed.FileLength, oversized, 1, 0,
				BundleFileType.Assembly, "oversized.dll");
			var synthetic = new BundleFile(parsed.Filename, parsed.FileLength, parsed.MarkerOffset,
				parsed.HeaderOffset, parsed.Manifest, new[] { entry });
			parsed.Dispose();
			using var workspace = new BundleWorkspace(synthetic);

			WindowsBundleEligibilityResult result = inspector.Inspect(workspace);

			Assert.Equal(WindowsBundleEligibilityStatus.InspectionLimitExceeded, result.Status);
			Assert.Equal("A managed bundle entry exceeds the safe eligibility inspection limit.", result.Message);
			Assert.Equal(entry.Index, result.EntryIndex);
			Assert.Equal(entry.RelativePath, result.RelativePath);
		}

		[Fact]
		public void TruncatedPeReturnsStableUnsupportedPlatformDiagnostic() {
			using TempFile source = new(new byte[] { 0x4D, 0x5A });
			var bundle = new BundleFile(source.Path, 2, 0, 0,
				new BundleManifest(6, 0, "truncated"), Array.Empty<BundleEntry>());
			using var workspace = new BundleWorkspace(bundle);

			WindowsBundleEligibilityResult result = inspector.Inspect(workspace);

			Assert.Equal(WindowsBundleEligibilityStatus.UnsupportedPlatform, result.Status);
			Assert.Equal("Save Bundle As currently supports only valid Windows PE bundles.", result.Message);
		}

		[Fact]
		public void NoManagedEntryFailsClosed() {
			ModernBundleFixture fixture = GetFixture();
			BundleOpenResult open = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			BundleFile parsed = open.Bundle!;
			var empty = new BundleFile(parsed.Filename, parsed.FileLength, parsed.MarkerOffset,
				parsed.HeaderOffset, parsed.Manifest, Array.Empty<BundleEntry>());
			parsed.Dispose();
			using var workspace = new BundleWorkspace(empty);
			WindowsBundleEligibilityResult missing = inspector.Inspect(workspace);
			Assert.Equal(WindowsBundleEligibilityStatus.NoManagedAssembly, missing.Status);
			Assert.Equal("The bundle contains no conventional managed assembly entry.", missing.Message);
		}

		[Fact]
		public void MalformedCurrentManagedEntryFailsClosed() {
			using TempFile malformed = CopyFixture();
			BundleOpenResult open = new BundleReader().Open(malformed.Path);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			using var workspace = new BundleWorkspace(open.Bundle!);
			BundleEntry main = Assert.Single(workspace.Bundle.Entries,
				entry => entry.RelativePath == "SingleFile.App.dll");
			workspace.SetReplacement(main, new byte[] { 1, 2, 3, 4 },
				new BundleReplacementInfo("malformed-test"));
			WindowsBundleEligibilityResult invalid = inspector.Inspect(workspace);
			Assert.Equal(WindowsBundleEligibilityStatus.MalformedManagedAssembly, invalid.Status);
			Assert.Equal(main.Index, invalid.EntryIndex);
		}

		[Fact]
		public void MalformedBundleAndCertificateDirectoryFailWithStableDiagnostics() {
			using TempFile malformedBundle = CopyFixture();
			BundleOpenResult parsed = new BundleReader().Open(malformedBundle.Path);
			Assert.Equal(BundleOpenStatus.Success, parsed.Status);
			long marker = parsed.Bundle!.MarkerOffset;
			parsed.Bundle.Dispose();
			byte[] bytes = File.ReadAllBytes(malformedBundle.Path);
			WriteUInt64(bytes, checked((int)marker - 8), unchecked((ulong)bytes.LongLength + 1));
			File.WriteAllBytes(malformedBundle.Path, bytes);
			WindowsBundleEligibilityResult malformed = inspector.Inspect(malformedBundle.Path);
			Assert.Equal(WindowsBundleEligibilityStatus.MalformedBundle, malformed.Status);
			Assert.Equal("The source bundle is malformed or unsupported (InvalidHeaderOffset).", malformed.Message);

			using TempFile malformedCertificate = CopyFixture();
			bytes = File.ReadAllBytes(malformedCertificate.Path);
			int directory = GetDataDirectoryOffset(bytes, 4);
			WriteUInt32(bytes, directory, (uint)bytes.Length);
			WriteUInt32(bytes, directory + 4, 0);
			File.WriteAllBytes(malformedCertificate.Path, bytes);
			WindowsBundleEligibilityResult certificate = inspector.Inspect(malformedCertificate.Path);
			Assert.Equal(WindowsBundleEligibilityStatus.UnsupportedPlatform, certificate.Status);
			Assert.Equal("The Windows PE certificate-table directory is malformed.", certificate.Message);
		}

		[Fact]
		public void HighConfidenceNativeAotExportGetsExplanatoryResult() {
			byte[] bytes = File.ReadAllBytes(GetFixture().BuildMainAssemblyPath);
			MakeNativeAot(bytes, "DotNetRuntimeDebugHeader");
			using var source = new TempFile(bytes);

			WindowsBundleEligibilityResult result = inspector.Inspect(source.Path);

			Assert.Equal(WindowsBundleEligibilityStatus.NativeAot, result.Status);
			Assert.Equal("This Windows NativeAOT executable does not contain conventional managed IL that can be rebuilt as a bundle.", result.Message);
		}

		static ModernBundleFixture GetFixture() => ModernFixtureLocator.FindRequired().Single(item =>
			item.Variant == "fdd-uncompressed");

		static TempFile CopyFixture() => new(File.ReadAllBytes(GetFixture().BundlePath));

		static void SetManifestType(string filename, string relativePath, byte type) {
			BundleOpenResult open = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, open.Status);
			int manifestStart = checked((int)open.Bundle!.HeaderOffset);
			open.Bundle.Dispose();
			byte[] bytes = File.ReadAllBytes(filename);
			byte[] path = System.Text.Encoding.UTF8.GetBytes(relativePath);
			int match = FindUnique(bytes, path, manifestStart);
			Assert.True(path.Length < 128);
			Assert.Equal(path.Length, bytes[match - 1]);
			bytes[match - 2] = type;
			File.WriteAllBytes(filename, bytes);
		}

		static int FindUnique(byte[] bytes, byte[] value, int start) {
			int relative = bytes.AsSpan(start).IndexOf(value);
			Assert.True(relative >= 0);
			int found = checked(start + relative);
			Assert.Equal(-1, bytes.AsSpan(found + 1).IndexOf(value));
			return found;
		}

		static byte[] MakeReadyToRun(byte[] input) {
			byte[] bytes = (byte[])input.Clone();
			int pe = checked((int)ReadUInt32(bytes, 0x3C));
			int optional = pe + 24;
			int corDirectory = GetDataDirectoryOffset(bytes, 14);
			uint corRva = ReadUInt32(bytes, corDirectory);
			int corOffset = RvaToOffset(bytes, pe, corRva);
			int firstSection = optional + ReadUInt16(bytes, pe + 20);
			uint nativeRva = ReadUInt32(bytes, firstSection + 12);
			uint nativeOffset = ReadUInt32(bytes, firstSection + 20);
			WriteUInt32(bytes, corOffset + 0x40, nativeRva);
			WriteUInt32(bytes, corOffset + 0x44, 4);
			WriteUInt32(bytes, checked((int)nativeOffset), 0x00525452);
			return bytes;
		}

		static void MakeNativeAot(byte[] bytes, string exportName) {
			int pe = checked((int)ReadUInt32(bytes, 0x3C));
			int optional = pe + 24;
			int firstSection = optional + ReadUInt16(bytes, pe + 20);
			uint sectionRva = ReadUInt32(bytes, firstSection + 12);
			uint sectionOffset = ReadUInt32(bytes, firstSection + 20);
			int exportOffset = checked((int)sectionOffset);
			uint functionsRva = sectionRva + 0x40;
			uint namesRva = sectionRva + 0x44;
			uint ordinalsRva = sectionRva + 0x48;
			uint nameRva = sectionRva + 0x4A;
			WriteUInt32(bytes, exportOffset + 20, 1);
			WriteUInt32(bytes, exportOffset + 24, 1);
			WriteUInt32(bytes, exportOffset + 28, functionsRva);
			WriteUInt32(bytes, exportOffset + 32, namesRva);
			WriteUInt32(bytes, exportOffset + 36, ordinalsRva);
			WriteUInt32(bytes, exportOffset + 0x40, sectionRva + 0x100);
			WriteUInt32(bytes, exportOffset + 0x44, nameRva);
			WriteUInt16(bytes, exportOffset + 0x48, 0);
			WriteAsciiZ(bytes, exportOffset + 0x4A, exportName);
			int exportDirectory = GetDataDirectoryOffset(bytes, 0);
			WriteUInt32(bytes, exportDirectory, sectionRva);
			WriteUInt32(bytes, exportDirectory + 4, 0x100);
			int corDirectory = GetDataDirectoryOffset(bytes, 14);
			WriteUInt32(bytes, corDirectory, 0);
			WriteUInt32(bytes, corDirectory + 4, 0);
		}

		static int RvaToOffset(byte[] bytes, int pe, uint rva) {
			ushort sections = ReadUInt16(bytes, pe + 6);
			int section = pe + 24 + ReadUInt16(bytes, pe + 20);
			for (int index = 0; index < sections; index++, section += 40) {
				uint virtualSize = ReadUInt32(bytes, section + 8);
				uint virtualAddress = ReadUInt32(bytes, section + 12);
				uint rawSize = ReadUInt32(bytes, section + 16);
				uint rawOffset = ReadUInt32(bytes, section + 20);
				uint span = Math.Max(virtualSize, rawSize);
				if (rva >= virtualAddress && rva - virtualAddress < span)
					return checked((int)(rawOffset + rva - virtualAddress));
			}
			throw new InvalidDataException("RVA is not mapped by the test image.");
		}

		static byte[] AddRetargetableAssemblyFlag(byte[] input) {
			byte[] bytes = (byte[])input.Clone();
			int pe = checked((int)ReadUInt32(bytes, 0x3C));
			using var reader = new PEReader(new MemoryStream(bytes, writable: false));
			MetadataReader metadata = reader.GetMetadataReader();
			AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
			int metadataOffset = RvaToOffset(bytes, pe,
				(uint)reader.PEHeaders.CorHeader!.MetadataDirectory.RelativeVirtualAddress);
			int tables = FindMetadataStream(bytes, metadataOffset, "#~");
			byte heapSizes = bytes[tables + 6];
			int[] rowCounts = new int[64];
			ulong valid = ReadUInt64(bytes, tables + 8);
			int rowCountOffset = tables + 24;
			for (int table = 0; table < rowCounts.Length; table++) {
				if ((valid & (1UL << table)) != 0) {
					rowCounts[table] = checked((int)ReadUInt32(bytes, rowCountOffset));
					rowCountOffset += 4;
				}
			}
			int data = rowCountOffset;
			for (int table = 0; table < 32; table++) {
				if ((valid & (1UL << table)) == 0)
					continue;
				int rowSize = MetadataRowSize(table, heapSizes, rowCounts);
				data = checked(data + rowSize * rowCounts[table]);
			}
			Assert.True((valid & (1UL << 32)) != 0);
			Assert.Equal(1, rowCounts[32]);
			int assemblyRowFlags = data + 12;
			Assert.Equal((uint)0, ReadUInt32(bytes, assemblyRowFlags) & 0x100U);
			WriteUInt32(bytes, assemblyRowFlags, ReadUInt32(bytes, assemblyRowFlags) | 0x100U);
			using var verify = new PEReader(new MemoryStream(bytes, writable: false));
			AssemblyFlags verifiedFlags = verify.GetMetadataReader().GetAssemblyDefinition().Flags;
			Assert.True((verifiedFlags & AssemblyFlags.Retargetable) != 0);
			return bytes;
		}

		static int MetadataRowSize(int table, byte heapSizes, int[] rows) {
			int stringIndex = (heapSizes & 1) == 0 ? 2 : 4;
			int guidIndex = (heapSizes & 2) == 0 ? 2 : 4;
			int blobIndex = (heapSizes & 4) == 0 ? 2 : 4;
			int String() => stringIndex;
			int Guid() => guidIndex;
			int Blob() => blobIndex;
			int Simple(int target) => rows[target] < 0x10000 ? 2 : 4;
			int Coded(int bits, params int[] targets) {
				int maximum = 0;
				foreach (int target in targets)
					maximum = Math.Max(maximum, rows[target]);
				return maximum < (1 << (16 - bits)) ? 2 : 4;
			}

			return table switch {
				0 => 2 + String() + Guid() * 3,
				1 => Coded(2, 0, 26, 35, 1) + String() * 2,
				2 => 4 + String() * 2 + Coded(2, 2, 1, 27) + Simple(4) + Simple(6),
				3 => Simple(4),
				4 => 2 + String() + Blob(),
				5 => Simple(6),
				6 => 4 + 2 + 2 + String() + Blob() + Simple(8),
				7 => Simple(8),
				8 => 2 + 2 + String(),
				9 => Simple(2) + Coded(2, 2, 1, 27),
				10 => Coded(3, 2, 1, 26, 6, 27) + String() + Blob(),
				11 => 2 + Coded(2, 4, 8, 23) + Blob(),
				12 => Coded(5, 6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 43, 44) +
					Coded(3, 6, 10) + Blob(),
				13 => Coded(1, 4, 8) + Blob(),
				14 => 2 + Coded(2, 2, 6, 32) + Blob(),
				15 => 2 + 4 + Simple(2),
				16 => 4 + Simple(4),
				17 => Blob(),
				18 => Simple(2) + Simple(20),
				19 => Simple(20),
				20 => 2 + String() + Coded(2, 2, 1, 27),
				21 => Simple(2) + Simple(23),
				22 => Simple(23),
				23 => 2 + String() + Blob(),
				24 => 2 + Simple(6) + Coded(1, 20, 23),
				25 => Simple(2) + Coded(1, 6, 10) * 2,
				26 => String(),
				27 => Blob(),
				28 => 2 + Coded(1, 4, 6) + String() + Simple(26),
				29 => 4 + Simple(4),
				30 => 8,
				31 => 4,
				32 => 4 + 4 + 8 + String() * 2 + Blob(),
				_ => throw new InvalidDataException("Unexpected metadata table in test fixture."),
			};
		}

		static int FindMetadataStream(byte[] bytes, int metadataOffset, string expectedName) {
			int versionLength = checked((int)ReadUInt32(bytes, metadataOffset + 12));
			int streamCountOffset = Align4(checked(metadataOffset + 16 + versionLength)) + 2;
			ushort streamCount = ReadUInt16(bytes, streamCountOffset);
			int header = streamCountOffset + 2;
			for (int index = 0; index < streamCount; index++) {
				int offset = checked(metadataOffset + (int)ReadUInt32(bytes, header));
				int name = header + 8;
				int length = 0;
				while (bytes[name + length] != 0)
					length++;
				string actual = System.Text.Encoding.ASCII.GetString(bytes, name, length);
				if (actual == expectedName)
					return offset;
				header = Align4(name + length + 1);
			}
			throw new InvalidDataException("The metadata stream is missing from the test fixture.");
		}

		static int Align4(int value) => checked((value + 3) & ~3);

		static int GetDataDirectoryOffset(byte[] bytes, int index) {
			int pe = checked((int)ReadUInt32(bytes, 0x3C));
			int optional = pe + 24;
			ushort magic = ReadUInt16(bytes, optional);
			return checked(optional + (magic == 0x20B ? 112 : 96) + index * 8);
		}

		static string Hash(string path) {
			using SHA256 sha = SHA256.Create();
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
		}

		static ushort ReadUInt16(byte[] bytes, int offset) =>
			(ushort)(bytes[offset] | bytes[offset + 1] << 8);
		static uint ReadUInt32(byte[] bytes, int offset) =>
			(uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
		static ulong ReadUInt64(byte[] bytes, int offset) =>
			ReadUInt32(bytes, offset) | (ulong)ReadUInt32(bytes, offset + 4) << 32;
		static void WriteUInt16(byte[] bytes, int offset, ushort value) {
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
		}
		static void WriteUInt32(byte[] bytes, int offset, uint value) {
			for (int index = 0; index < 4; index++)
				bytes[offset + index] = (byte)(value >> (index * 8));
		}
		static void WriteUInt64(byte[] bytes, int offset, ulong value) {
			for (int index = 0; index < 8; index++)
				bytes[offset + index] = (byte)(value >> (index * 8));
		}
		static void WriteAsciiZ(byte[] bytes, int offset, string value) {
			for (int index = 0; index < value.Length; index++)
				bytes[offset + index] = checked((byte)value[index]);
			bytes[offset + value.Length] = 0;
		}

		sealed class TempFile : IDisposable {
			public TempFile(byte[] bytes) {
				Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
					"dnspy-bundle-eligibility-" + Guid.NewGuid().ToString("N") + ".exe");
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
