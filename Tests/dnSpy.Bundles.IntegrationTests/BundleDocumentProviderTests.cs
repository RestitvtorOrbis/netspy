// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleDocumentProviderTests {
		[Fact]
		public void ProviderRunsBeforeDefaultProvider() {
			var provider = new BundleDsDocumentProvider();
			Assert.True(provider.Order < DocumentConstants.ORDER_DEFAULT_DOCUMENT_PROVIDER);
		}

		[Fact]
		public void ValidOfficialBundleReturnsContainerAndPreservesIdentity() {
			string filename = CreateTemporaryFile(CreateBundle(majorVersion: 1));
			try {
				var provider = new BundleDsDocumentProvider();
				DsDocumentInfo info = DsDocumentInfo.CreateDocument(filename);
				IDsDocumentNameKey expectedKey = new FilenameKey(filename);

				Assert.Equal(expectedKey, provider.CreateKey(null!, info));
				var document = Assert.IsType<BundleDsDocument>(provider.Create(null!, info));
				try {
					Assert.Equal(info.Name, document.Filename);
					Assert.Equal(info.Name, document.SerializedDocument!.Value.Name);
					Assert.Equal(info.Type, document.SerializedDocument!.Value.Type);
					Assert.Equal(expectedKey, document.Key);
					Assert.Equal(filename, document.SourceFilename);
					Assert.NotNull(document.Bundle);
				}
				finally {
					document.Dispose();
				}
			}
			finally {
				DeleteTemporaryFile(filename);
			}
		}

		[Fact]
		public void MarkedMalformedExecutableReturnsVisibleErrorDocument() {
			string filename = CreateTemporaryFile(CreateBundle(majorVersion: 99));
			try {
				var provider = new BundleDsDocumentProvider();
				DsDocumentInfo info = DsDocumentInfo.CreateDocument(filename);
				var document = Assert.IsType<BundleErrorDocument>(provider.Create(null!, info));
				Assert.Equal(BundleOpenStatus.UnsupportedVersion, document.Status);
				Assert.Equal(BundleReadErrorCode.UnsupportedVersion, document.Error.Code);
				Assert.False(string.IsNullOrWhiteSpace(document.ErrorMessage));
				Assert.Equal(info.Name, document.SerializedDocument!.Value.Name);
				Assert.Equal(new FilenameKey(filename), document.Key);
			}
			finally {
				DeleteTemporaryFile(filename);
			}
		}

		[Fact]
		public void OrdinaryManagedDllAndExeAreLeftForDefaultProvider() {
			// Keep the fixture out of this test assembly: the synthetic marker bytes used
			// below are intentionally present in its metadata. The extension itself is
			// an ordinary managed PE and is therefore a better default-provider fixture.
			string source = typeof(BundleDsDocumentProvider).Assembly.Location;
			string dll = CreateTemporaryFile(File.ReadAllBytes(source), ".dll");
			string exe = CreateTemporaryFile(File.ReadAllBytes(source), ".exe");
			try {
				var provider = new BundleDsDocumentProvider();
				Assert.Null(provider.Create(null!, DsDocumentInfo.CreateDocument(dll)));
				Assert.Null(provider.Create(null!, DsDocumentInfo.CreateDocument(exe)));
			}
			finally {
				DeleteTemporaryFile(dll);
				DeleteTemporaryFile(exe);
			}
		}

		[Fact]
		public void NonExecutableFileIsNotProbedOrClaimed() {
			string filename = CreateTemporaryFile(Encoding.UTF8.GetBytes("not an executable"), ".bin");
			try {
				var provider = new BundleDsDocumentProvider();
				DsDocumentInfo info = DsDocumentInfo.CreateDocument(filename);
				Assert.Null(provider.CreateKey(null!, info));
				Assert.Null(provider.Create(null!, info));
			}
			finally {
				DeleteTemporaryFile(filename);
			}
		}

		[Theory]
		[MemberData(nameof(ExecutableMagicCases))]
		public void ExecutableMagicTableIsRecognizedWithoutParsing(byte[] magic, bool accepted) {
			string filename = CreateTemporaryFile(magic, ".candidate");
			try {
				var provider = new BundleDsDocumentProvider();
				DsDocumentInfo info = DsDocumentInfo.CreateDocument(filename);
				IDsDocumentNameKey? key = provider.CreateKey(null!, info);
				if (accepted)
					Assert.Equal(new FilenameKey(filename), key);
				else
					Assert.Null(key);
			}
			finally {
				DeleteTemporaryFile(filename);
			}
		}

		public static IEnumerable<object[]> ExecutableMagicCases() {
			yield return new object[] { new byte[] { 0x4D, 0x5A }, true }; // PE/DOS
			foreach (uint magic in new[] {
				0x464C457Fu, // ELF
				0xFEEDFACEu, 0xFEEDFACFu, 0xCEFAEDFEu, 0xCFFAEDFEu, // Mach-O
				0xCAFEBABEu, 0xBEBAFECAu, 0xCAFEBABFu, 0xBFBAFECAu, // fat Mach-O 32/64
			})
				yield return new object[] { Bytes(magic), true };
			yield return new object[] { new byte[] { 0x4C, 0x46, 0x45, 0x7F }, false };
			yield return new object[] { new byte[] { 0xCA, 0xFE, 0xBA, 0xBC }, false };
			yield return new object[] { new byte[] { 0x00, 0x00, 0x00, 0x00 }, false };
		}

		[Fact]
		public void ReaderFailureFallsThroughInsteadOfCreatingErrorDocument() {
			string filename = CreateTemporaryFile(new byte[] { 0x4D, 0x5A, 0x00, 0x00 }, ".exe");
			try {
				var provider = new BundleDsDocumentProvider(_ => throw new IOException("deterministic test failure"));
				DsDocumentInfo info = DsDocumentInfo.CreateDocument(filename);
				Assert.Null(provider.Create(null!, info));
			}
			finally {
				DeleteTemporaryFile(filename);
			}
		}

		static byte[] CreateBundle(uint majorVersion) {
			const int markerOffset = 128;
			const int headerOffset = markerOffset + 32;
			byte[] bytes = new byte[headerOffset + 32];
			bytes[0] = 0x4D;
			bytes[1] = 0x5A;
			WriteInt64(bytes, markerOffset - sizeof(long), headerOffset);
			Buffer.BlockCopy(Signature, 0, bytes, markerOffset, Signature.Length);
			WriteUInt32(bytes, headerOffset, majorVersion);
			WriteUInt32(bytes, headerOffset + sizeof(uint), 0);
			WriteInt32(bytes, headerOffset + sizeof(uint) * 2, 0);
			if (majorVersion == 1) {
				bytes[headerOffset + sizeof(uint) * 2 + sizeof(int)] = 1;
				bytes[headerOffset + sizeof(uint) * 2 + sizeof(int) + 1] = (byte)'x';
			}
			return bytes;
		}

		static string CreateTemporaryFile(byte[] bytes, string extension = ".exe") {
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
			File.WriteAllBytes(filename, bytes);
			return filename;
		}

		static void DeleteTemporaryFile(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		static void WriteInt32(byte[] bytes, int offset, int value) =>
			WriteUInt32(bytes, offset, unchecked((uint)value));

		static void WriteUInt32(byte[] bytes, int offset, uint value) {
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
			bytes[offset + 2] = (byte)(value >> 16);
			bytes[offset + 3] = (byte)(value >> 24);
		}

		static void WriteInt64(byte[] bytes, int offset, long value) {
			ulong unsigned = unchecked((ulong)value);
			for (int i = 0; i < sizeof(long); i++)
				bytes[offset + i] = (byte)(unsigned >> (8 * i));
		}

		static byte[] Bytes(uint value) => new[] {
			(byte)value,
			(byte)(value >> 8),
			(byte)(value >> 16),
			(byte)(value >> 24),
		};

		static readonly byte[] Signature = Convert.FromBase64String(
			"ixICuWphIDhye5MCFNegMhP1uebvrjMY7jstziSzaq4=");
	}
}
