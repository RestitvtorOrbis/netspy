// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using dnSpy.Bundles;
using Microsoft.NET.HostModel.Bundle;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>
	/// Verifies Save Bundle As against generated framework-dependent and self-contained fixtures.
	/// Core HostModel tests own generation details; these tests exercise the publication boundary
	/// and reopen the resulting executable with the product parser.
	/// </summary>
	public sealed class BundleLogicalEquivalenceTests {
		[Fact]
		public void RebuiltVariantsPreserveOrderedLogicalInventoryAndReopenWithOwnParser() {
			IReadOnlyList<string> variants = IntegrationFixtureLocator.ModernVariants;
			Assert.Contains("fdd-uncompressed", variants);
			Assert.Contains("scd-compressed", variants);
			Assert.Contains("scd-compressed-pdb", variants);
			Assert.Contains("scd-uncompressed", variants);
			Assert.Contains("scd-uncompressed-pdb", variants);

			foreach (string variant in variants) {
				string source = IntegrationFixtureLocator.FindBundle(variant);
				string destination = TemporaryFilename("rebuilt");
				byte[] sourceHash = Hash(source);
				try {
					using BundleFile original = OpenBundle(source);
					using var workspace = new BundleWorkspace(original);
					BundleEntry[] managed = workspace.Bundle.Entries.Where(entry =>
						entry.FileType == BundleFileType.Assembly).ToArray();
					Assert.Contains(managed, entry => entry.RelativePath == "SingleFile.App.dll");
					Assert.Contains(managed, entry => entry.RelativePath == "SingleFile.Dependency.dll");

					// Install distinguishable, still-valid JSON replacements. Edit serialization is covered
					// by BND-015/BND-017; this test proves that current replacement bytes are not ignored
					// by Save Bundle As while both managed assemblies remain in the inventory.
					BundleEntry[] configs = workspace.Bundle.Entries.Where(entry =>
						entry.FileType == BundleFileType.DepsJson ||
						entry.FileType == BundleFileType.RuntimeConfigJson).ToArray();
					Assert.Equal(2, configs.Length);
					var replacements = configs
						.Select(entry => new BundleWorkspaceReplacement(entry,
							AppendJsonWhitespace(entry), new BundleReplacementInfo("BND-027")))
						.ToArray();
					workspace.SetReplacements(replacements);
					Assert.Equal(2, workspace.ModifiedEntries.Count);
					foreach (BundleWorkspaceReplacement replacement in replacements)
						Assert.NotEqual(replacement.Entry.ReadAllBytes(replacement.Entry.Size), replacement.Bytes);

					string published = new WindowsBundlePublicationService().Publish(workspace, destination,
						TestContext.Current.CancellationToken);
					Assert.Equal(Path.GetFullPath(destination), published);
					Assert.NotEqual(Path.GetFullPath(source), published,
						StringComparer.OrdinalIgnoreCase);
					Assert.Equal(sourceHash, Hash(source));

					// Reopening through BundleReader is part of the integration contract, not only an
					// assertion about HostModel's generated file.
					using BundleFile rebuilt = OpenBundle(published);
					Assert.Equal(original.Manifest.MajorVersion, rebuilt.Manifest.MajorVersion);
					Assert.Equal(original.Manifest.MinorVersion, rebuilt.Manifest.MinorVersion);
					Assert.Equal(original.Manifest.Flags, rebuilt.Manifest.Flags);
					Assert.Equal(original.Entries.Count, rebuilt.Entries.Count);
					Assert.Equal(original.Entries.Select(entry => entry.RelativePath),
						rebuilt.Entries.Select(entry => entry.RelativePath));

					for (int index = 0; index < original.Entries.Count; index++) {
						BundleEntry expected = original.Entries[index];
						BundleEntry actual = rebuilt.Entries[index];
						Assert.Equal(expected.RawFileType, actual.RawFileType);
						Assert.Equal(expected.FileType, actual.FileType);
						AssertCurrentContentEqual(workspace, expected, actual);
					}

					bool sourceCompressed = original.Entries.Any(entry => entry.IsCompressed);
					Assert.Equal(IsCompressedVariant(variant), sourceCompressed);
					Assert.Equal(sourceCompressed, rebuilt.Entries.Any(entry => entry.IsCompressed));
					if (sourceCompressed)
						Assert.Contains(rebuilt.Entries, entry => entry.IsCompressed);
					else
						Assert.DoesNotContain(rebuilt.Entries, entry => entry.IsCompressed);

					// Keep the preservation promises visible in the integration test: config, native,
					// and symbol entries are compared by type, ordered path, and logical bytes. The
					// modern SDK may place native runtime components in the apphost, in which case both
					// inventories correctly contain no native manifest entries.
					foreach (BundleFileType type in new[] {
						BundleFileType.DepsJson,
						BundleFileType.RuntimeConfigJson,
						BundleFileType.NativeBinary,
						BundleFileType.Symbols,
					}) {
						BundleEntry[] expected = original.Entries.Where(entry => entry.FileType == type).ToArray();
						BundleEntry[] actual = rebuilt.Entries.Where(entry => entry.FileType == type).ToArray();
						Assert.Equal(expected.Select(entry => entry.RelativePath),
							actual.Select(entry => entry.RelativePath));
						for (int index = 0; index < expected.Length; index++)
							AssertCurrentContentEqual(workspace, expected[index], actual[index]);
					}

					if (variant.EndsWith("-pdb", StringComparison.Ordinal))
						Assert.Contains(original.Entries, entry => entry.FileType == BundleFileType.Symbols);
					Assert.Contains(original.Entries, entry => entry.FileType == BundleFileType.DepsJson);
					Assert.Contains(original.Entries, entry => entry.FileType == BundleFileType.RuntimeConfigJson);
				}
				finally {
					TryDelete(destination);
				}
			}
		}

		[Fact]
		public void RebuiltBundlePreservesNativeManifestEntry() {
			string sourceDirectory = CreateNativeFixtureDirectory();
			string source = Path.Combine(sourceDirectory, "BND027-Native.exe");
			string destination = TemporaryFilename("native");
			try {
				byte[] sourceHash = Hash(source);
				using BundleFile original = OpenBundle(source);
				BundleEntry[] native = original.Entries.Where(entry =>
					entry.FileType == BundleFileType.NativeBinary).ToArray();
				Assert.Contains(native, entry => entry.RelativePath == "native-component.dll");
				using var workspace = new BundleWorkspace(original);

				string published = new WindowsBundlePublicationService().Publish(workspace, destination,
					TestContext.Current.CancellationToken);
				Assert.Equal(sourceHash, Hash(source));
				using BundleFile rebuilt = OpenBundle(published);
				BundleEntry[] rebuiltNative = rebuilt.Entries.Where(entry =>
					entry.FileType == BundleFileType.NativeBinary).ToArray();
				Assert.Equal(native.Select(entry => entry.RelativePath),
					rebuiltNative.Select(entry => entry.RelativePath));
				for (int index = 0; index < native.Length; index++)
					AssertLogicalContentEqual(native[index], rebuiltNative[index]);
			}
			finally {
				TryDelete(destination);
				TryDeleteDirectory(sourceDirectory);
			}
		}

		[Fact]
		public void CorruptSourceFailsWithPreciseDiagnosticWithoutPublication() {
			string source = IntegrationFixtureLocator.FindBundle("fdd-uncompressed");
			string corruptSource = TemporaryFilename("corrupt-source");
			string destination = TemporaryFilename("corrupt-output");
			try {
				File.Copy(source, corruptSource);
				long managedOffset;
				using (BundleFile bundle = OpenBundle(corruptSource))
					managedOffset = Assert.Single(bundle.Entries,
						entry => entry.RelativePath == "SingleFile.App.dll").Offset;
				// fdd-uncompressed is deliberately used because changing the first byte of the
				// bounded managed payload is enough to make the eligibility PE check fail.
				using (var stream = new FileStream(corruptSource, FileMode.Open, FileAccess.Write,
					FileShare.Read)) {
					stream.Position = managedOffset;
					stream.WriteByte(0);
					stream.Flush(flushToDisk: true);
				}

				using BundleFile corruptBundle = OpenBundle(corruptSource);
				using var workspace = new BundleWorkspace(corruptBundle);
				InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
					new WindowsBundlePublicationService().Publish(workspace, destination,
						TestContext.Current.CancellationToken));
				Assert.Equal("A managed bundle entry is not a valid managed PE assembly.", error.Message);
				Assert.False(File.Exists(destination));
			}
			finally {
				TryDelete(corruptSource);
				TryDelete(destination);
			}
		}

		static BundleFile OpenBundle(string filename) {
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			return result.Bundle!;
		}

		static void AssertLogicalContentEqual(BundleEntry expected, BundleEntry actual) {
			Assert.Equal(expected.Size, actual.Size);
			using Stream left = expected.OpenLogicalRead();
			using Stream right = actual.OpenLogicalRead();
			AssertStreamContentEqual(left, right);
		}

		static void AssertCurrentContentEqual(BundleWorkspace workspace, BundleEntry expected,
			BundleEntry actual) {
			using Stream left = workspace.OpenCurrentRead(expected);
			using Stream right = actual.OpenLogicalRead();
			AssertStreamContentEqual(left, right);
		}

		static void AssertStreamContentEqual(Stream left, Stream right) {
			byte[] leftBuffer = new byte[64 * 1024];
			byte[] rightBuffer = new byte[64 * 1024];
			while (true) {
				int leftCount = Fill(left, leftBuffer);
				int rightCount = Fill(right, rightBuffer);
				Assert.Equal(leftCount, rightCount);
				if (leftCount == 0)
					return;
				Assert.Equal(leftBuffer.AsSpan(0, leftCount).ToArray(),
					rightBuffer.AsSpan(0, rightCount).ToArray());
			}
		}

		static int Fill(Stream stream, byte[] buffer) {
			int count = 0;
			while (count < buffer.Length) {
				int read = stream.Read(buffer, count, buffer.Length - count);
				Assert.True(read >= 0);
				if (read == 0)
					break;
				count = checked(count + read);
			}
			return count;
		}

		static byte[] AppendJsonWhitespace(BundleEntry entry) {
			byte[] original = entry.ReadAllBytes(entry.Size);
			byte[] replacement = new byte[checked(original.Length + 1)];
			Buffer.BlockCopy(original, 0, replacement, 0, original.Length);
			replacement[replacement.Length - 1] = (byte)'\n';
			return replacement;
		}

		static bool IsCompressedVariant(string variant) =>
			variant == "scd-compressed" || variant == "scd-compressed-pdb";

		static string CreateNativeFixtureDirectory() {
			string source = IntegrationFixtureLocator.FindBundle("fdd-uncompressed");
			string variantRoot = Directory.GetParent(Directory.GetParent(source)!.FullName)!.FullName;
			string appHost = Path.Combine(variantRoot, "obj", "App", "Release", "net10.0",
				"win-x64", "apphost.exe");
			string buildRoot = Path.Combine(variantRoot, "build", "App", "Release", "net10.0", "win-x64");
			string dependency = Path.Combine(variantRoot, "build", "SingleFile.Dependency", "Release",
				"net10.0", "SingleFile.Dependency.dll");
			string outputDirectory = Path.Combine(Path.GetTempPath(),
				"dnspy-bnd027-native-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(outputDirectory);
			string deps = Path.Combine(buildRoot, "SingleFile.App.deps.json");
			string runtimeConfig = Path.Combine(buildRoot, "SingleFile.App.runtimeconfig.json");
			string main = Path.Combine(buildRoot, "SingleFile.App.dll");
			string mainPdb = Path.Combine(buildRoot, "SingleFile.App.pdb");
			string dependencyPdb = Path.Combine(buildRoot, "SingleFile.Dependency.pdb");
			var bundler = new Bundler("BND027-Native.exe", outputDirectory,
				BundleOptions.BundleNativeBinaries | BundleOptions.BundleSymbolFiles,
				OSPlatform.Windows, Architecture.X64, new Version(6, 0),
				appAssemblyName: "SingleFile.App", macosCodesign: false);
			bundler.GenerateBundle(new[] {
				new FileSpec(appHost, "BND027-Native.exe"),
				new FileSpec(main, "SingleFile.App.dll"),
				new FileSpec(dependency, "SingleFile.Dependency.dll"),
				new FileSpec(appHost, "native-component.dll"),
				new FileSpec(deps, "SingleFile.App.deps.json"),
				new FileSpec(runtimeConfig, "SingleFile.App.runtimeconfig.json"),
				new FileSpec(mainPdb, "SingleFile.App.pdb"),
				new FileSpec(dependencyPdb, "SingleFile.Dependency.pdb"),
			});
			return outputDirectory;
		}

		static string TemporaryFilename(string stem) => Path.Combine(Path.GetTempPath(),
			"dnspy-bnd027-" + stem + "-" + Guid.NewGuid().ToString("N") + ".exe");

		static byte[] Hash(string filename) {
			using SHA256 sha256 = SHA256.Create();
			using FileStream stream = File.OpenRead(filename);
			return sha256.ComputeHash(stream);
		}

		static void TryDelete(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		static void TryDeleteDirectory(string directory) {
			try {
				if (Directory.Exists(directory))
					Directory.Delete(directory, recursive: true);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}
}
