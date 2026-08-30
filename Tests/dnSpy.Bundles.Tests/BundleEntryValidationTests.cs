// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class BundleEntryValidationTests {
		[Theory]
		[InlineData(1u)]
		[InlineData(2u)]
		[InlineData(6u)]
		public void AllOfficialTypesAndUnknownTypeAreRetained(uint version) {
			var entries = new List<SyntheticBundleEntry>();
			for (byte type = 0; type <= 5; type++)
				entries.Add(new SyntheticBundleEntry((BundleFileType)type, type, "dir/type-" + type,
					new byte[] { type }));
			entries.Add(new SyntheticBundleEntry(BundleFileType.Unknown, 0xFE, "unknown.bin",
				new byte[] { 0xFE }));
			using (SyntheticBundle bundle = SyntheticBundle.Create(version, entries)) {
				BundleOpenResult result = bundle.Open();
			Assert.True(result.Status == BundleOpenStatus.Success,
				result.Error is null ? result.Status.ToString() : result.Error.Code + ": " + result.Error.Message);
				Assert.NotNull(result.Bundle);
				Assert.Equal(entries.Count, result.Bundle!.Entries.Count);
				for (int i = 0; i < entries.Count; i++) {
					Assert.Equal(entries[i].RawType, result.Bundle.Entries[i].RawFileType);
					Assert.Equal(entries[i].RawType <= 5 ? entries[i].FileType : BundleFileType.Unknown,
						result.Bundle.Entries[i].FileType);
				}
			}
		}

		[Fact]
		public void BackslashesNormalizeAndDuplicateComparisonIsOrdinal() {
			using (SyntheticBundle bundle = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "a\\b.dll", new byte[] { 1 }),
				new SyntheticBundleEntry(BundleFileType.NativeBinary, 2, "a/b.dll", new byte[] { 2 }),
			})) {
			BundleOpenResult result = bundle.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.DuplicatePath, result.Error!.Code);
		}
		}

		[Theory]
		[InlineData("")]
		[InlineData("/")]
		[InlineData("\\rooted")]
		[InlineData("C:/rooted")]
		[InlineData("C:\\rooted")]
		[InlineData(".")]
		[InlineData("..")]
		[InlineData("a/../b")]
		[InlineData("a\\..\\b")]
		[InlineData("a/./b")]
		public void InvalidRelativePathsAreRejected(string path) {
			using (SyntheticBundle bundle = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, path, new byte[] { 1 }),
			})) {
			BundleOpenResult result = bundle.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidPath, result.Error!.Code);
			}
		}

		[Fact]
		public void NulPathIsRejected() {
			using (SyntheticBundle bundle = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "a\0b", new byte[] { 1 }),
			})) {
			BundleOpenResult result = bundle.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidString, result.Error!.Code);
			}
		}

		[Fact]
		public void AggregateLogicalSizeLimitIsCheckedBeforeSuccess() {
			using (SyntheticBundle bundle = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "one", new byte[] { 1, 2 }),
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "two", new byte[] { 3, 4 }),
			})) {
			BundleOpenResult result = bundle.Open(new BundleReaderOptions(maximumTotalLogicalSize: 3));
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.LogicalSizeLimitExceeded, result.Error!.Code);
			}
		}

		[Fact]
		public void EntryRangesCannotOverlapEachOtherOrManifest() {
			using (SyntheticBundle overlap = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "one", new byte[] { 1, 2 }, offset: 64),
				new SyntheticBundleEntry(BundleFileType.NativeBinary, 2, "two", new byte[] { 3, 4 }, offset: 65),
			})) {
			BundleOpenResult result = overlap.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.EntryOverlap, result.Error!.Code);
			}

			using (SyntheticBundle manifestOverlap = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "entry", new byte[] { 1 }, offset: 512),
			})) {
			BundleOpenResult result = manifestOverlap.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.EntryOverlap, result.Error!.Code);
			}
		}

		[Fact]
		public void V2ConfigRangesMustMatchPresentConfigEntries() {
			var entry = new SyntheticBundleEntry(BundleFileType.DepsJson, 3, "app.deps.json",
				new byte[] { 1, 2, 3 });
			using (SyntheticBundle bundle = SyntheticBundle.Create(2, new[] { entry },
				depsOffsetOverride: 777)) {
			BundleOpenResult result = bundle.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidEntryRange, result.Error!.Code);
			}
		}

		[Fact]
		public void RangeOverflowAndUnexpectedEofAreRejected() {
			using (SyntheticBundle overflow = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "overflow", new byte[] { 1 },
					offset: long.MaxValue),
			})) {
			BundleOpenResult result = overflow.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidEntryRange, result.Error!.Code);
			}

			using (SyntheticBundle eof = SyntheticBundle.Create(1, new[] {
				new SyntheticBundleEntry(BundleFileType.Assembly, 1, "eof", new byte[] { 1 },
					offset: 1023, declaredSize: 2),
			})) {
			BundleOpenResult result = eof.Open();
			Assert.Equal(BundleOpenStatus.InvalidBundle, result.Status);
			Assert.Equal(BundleReadErrorCode.InvalidEntryRange, result.Error!.Code);
			}
		}

		[Fact]
		public void FuzzSeedCorpusReturnsStableFailuresWithoutThrowing() {
			var seeds = new[] {
				new FuzzSeed("empty-path", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, string.Empty, new byte[] { 1 }),
				}), BundleReadErrorCode.InvalidPath),
				new FuzzSeed("slash-traversal", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "a\\..\\b", new byte[] { 1 }),
				}), BundleReadErrorCode.InvalidPath),
				new FuzzSeed("rooted-drive", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "C:/outside", new byte[] { 1 }),
				}), BundleReadErrorCode.InvalidPath),
				new FuzzSeed("duplicate-normalized", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "same\\name", new byte[] { 1 }),
					new SyntheticBundleEntry(BundleFileType.NativeBinary, 2, "same/name", new byte[] { 2 }),
				}), BundleReadErrorCode.DuplicatePath),
				new FuzzSeed("negative-offset", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "negative", new byte[] { 1 }, offset: -1),
				}), BundleReadErrorCode.InvalidEntryRange),
				new FuzzSeed("negative-size", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "negative-size", new byte[] { 1 }, declaredSize: -1),
				}), BundleReadErrorCode.InvalidEntryRange),
				new FuzzSeed("negative-compressed-size", () => SyntheticBundle.Create(6, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "negative-compressed-size", new byte[] { 1 },
						declaredSize: 2, compressedSize: -1),
				}), BundleReadErrorCode.InvalidEntryRange),
				new FuzzSeed("offset-overflow", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "overflow", new byte[] { 1 }, offset: long.MaxValue),
				}), BundleReadErrorCode.InvalidEntryRange),
				new FuzzSeed("physical-eof", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "eof", new byte[] { 1 }, offset: 1023, declaredSize: 2),
				}), BundleReadErrorCode.InvalidEntryRange),
				new FuzzSeed("inconsistent-compressed-size", () => SyntheticBundle.Create(6, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "compressed", new byte[] { 1 }, declaredSize: 2, compressedSize: 2),
				}), BundleReadErrorCode.InvalidEntryRange),
				new FuzzSeed("aggregate-limit", () => SyntheticBundle.Create(1, new[] {
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "one", new byte[] { 1, 2 }),
					new SyntheticBundleEntry(BundleFileType.Assembly, 1, "two", new byte[] { 3, 4 }),
				}), BundleReadErrorCode.LogicalSizeLimitExceeded,
					new BundleReaderOptions(maximumTotalLogicalSize: 3)),
			};

			foreach (FuzzSeed seed in seeds) {
				using SyntheticBundle bundle = seed.Create();
				BundleOpenResult first = bundle.Open(seed.Options);
				BundleOpenResult second = bundle.Open(seed.Options);
				Assert.Equal(BundleOpenStatus.InvalidBundle, first.Status);
				Assert.Equal(BundleOpenStatus.InvalidBundle, second.Status);
				Assert.NotNull(first.Error);
				Assert.NotNull(second.Error);
				Assert.Equal(seed.ErrorCode, first.Error!.Code);
				Assert.Equal(first.Error.Code, second.Error!.Code);
			}
		}
	}

	sealed class FuzzSeed {
		public FuzzSeed(string name, Func<SyntheticBundle> create, BundleReadErrorCode errorCode,
			BundleReaderOptions? options = null) {
			Name = name;
			Create = create;
			ErrorCode = errorCode;
			Options = options;
		}
		public string Name { get; }
		public Func<SyntheticBundle> Create { get; }
		public BundleReadErrorCode ErrorCode { get; }
		public BundleReaderOptions? Options { get; }
	}

	sealed class SyntheticBundle : IDisposable {
		static readonly byte[] Signature = Convert.FromBase64String(
			"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");
		readonly string filename;
		BundleOpenResult? openResult;

		SyntheticBundle(string filename) => this.filename = filename;

		public static SyntheticBundle Create(uint version, IReadOnlyList<SyntheticBundleEntry> entries,
			long? depsOffsetOverride = null) {
			const int markerOffset = 128;
			const int headerOffset = 512;
			long nextOffset = 64;
			long totalLength = headerOffset;
			foreach (SyntheticBundleEntry entry in entries) {
				if (entry.Offset is null)
					entry.Offset = nextOffset;
				if (entry.DeclaredSize is null)
					entry.DeclaredSize = entry.Bytes.Length;
				try {
					long end = checked(entry.Offset.Value + entry.Bytes.Length);
					if (end > totalLength)
						totalLength = end;
					if (entry.Offset.Value == nextOffset)
						nextOffset = end;
				}
				catch (OverflowException) {
					// Leave the impossible range for the reader to reject without trying
					// to materialize it in this test fixture.
				}
			}
			using var stream = new MemoryStream();
			WriteUInt32(stream, version);
			WriteUInt32(stream, 0);
			WriteInt32(stream, entries.Count);
			WriteString(stream, "synthetic");
			if (version >= 2) {
				SyntheticBundleEntry? deps = Find(entries, BundleFileType.DepsJson);
				SyntheticBundleEntry? runtime = Find(entries, BundleFileType.RuntimeConfigJson);
				WriteInt64(stream, depsOffsetOverride ?? (deps?.Offset ?? 0));
				WriteInt64(stream, deps is null ? 0 : deps.DeclaredSize!.Value);
				WriteInt64(stream, runtime?.Offset ?? 0);
				WriteInt64(stream, runtime?.DeclaredSize ?? 0);
				WriteUInt64(stream, 0);
			}
			foreach (SyntheticBundleEntry entry in entries) {
				WriteInt64(stream, entry.Offset!.Value);
				WriteInt64(stream, entry.DeclaredSize!.Value);
				if (version >= 6)
					WriteInt64(stream, entry.CompressedSize);
				stream.WriteByte(entry.RawType);
				WriteString(stream, entry.Path);
			}
			totalLength = Math.Max(totalLength, checked(headerOffset + stream.Length));
			byte[] bytes = new byte[checked((int)totalLength)];
			WriteInt64(bytes, markerOffset - 8, headerOffset);
			Buffer.BlockCopy(Signature, 0, bytes, markerOffset, Signature.Length);
			foreach (SyntheticBundleEntry entry in entries)
				if (entry.Bytes.Length != 0 && entry.Offset >= 0 && entry.Offset <= bytes.Length - entry.Bytes.Length)
					Buffer.BlockCopy(entry.Bytes, 0, bytes, checked((int)entry.Offset!.Value), entry.Bytes.Length);
			Buffer.BlockCopy(stream.GetBuffer(), 0, bytes, headerOffset, checked((int)stream.Length));
			string filename = Path.Combine(Path.GetTempPath(), "dnspy-bundle-entry-" + Guid.NewGuid().ToString("N") + ".bin");
			File.WriteAllBytes(filename, bytes);
			return new SyntheticBundle(filename);
		}

		static SyntheticBundleEntry? Find(IReadOnlyList<SyntheticBundleEntry> entries, BundleFileType type) {
			foreach (SyntheticBundleEntry entry in entries)
				if (entry.FileType == type)
					return entry;
			return null;
		}

		public BundleOpenResult Open(BundleReaderOptions? options = null) =>
			openResult = new BundleReader(options).Open(filename);

		public void Dispose() {
			// BundleFile owns a Windows file mapping while entries are present.
			// Close it before deleting this temporary fixture.
			openResult?.Bundle?.Dispose();
			if (File.Exists(filename))
				File.Delete(filename);
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

	sealed class SyntheticBundleEntry {
		public SyntheticBundleEntry(BundleFileType fileType, byte rawType, string path, byte[] bytes,
			long? offset = null, long? declaredSize = null, long compressedSize = 0) {
			FileType = fileType;
			RawType = rawType;
			Path = path;
			Bytes = bytes;
			Offset = offset;
			DeclaredSize = declaredSize;
			CompressedSize = compressedSize;
		}
		public BundleFileType FileType { get; }
		public byte RawType { get; }
		public string Path { get; }
		public byte[] Bytes { get; }
		public long? Offset { get; set; }
		public long? DeclaredSize { get; set; }
		public long CompressedSize { get; }
	}
}
