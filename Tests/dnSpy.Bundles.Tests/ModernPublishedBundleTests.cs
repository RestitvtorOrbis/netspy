// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class ModernPublishedBundleTests {
		static readonly string[] ExpectedVariants = {
			"fdd-uncompressed",
			"scd-compressed",
			"scd-compressed-pdb",
			"scd-uncompressed",
			"scd-uncompressed-pdb",
		};

		[Fact]
		public void PublishedNet10VariantsHaveExpectedInventoryAndCompression() {
			IReadOnlyList<ModernBundleFixture> fixtures = GetFixtures();
			Assert.Equal(ExpectedVariants, fixtures.Select(fixture => fixture.Variant));

			foreach (ModernBundleFixture fixture in fixtures) {
				Assert.Equal("10.0.111", fixture.SdkVersion);
				Assert.Equal("net10.0", fixture.TargetFramework);
				Assert.Equal("win-x64", fixture.RuntimeIdentifier);
				ValidateSidecar(fixture);

				BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				Assert.NotNull(result.Bundle);
				using BundleFile bundle = result.Bundle!;
				Assert.Equal(6u, bundle.Manifest.MajorVersion);
				Assert.Contains(bundle.Entries, entry => entry.RelativePath == "SingleFile.App.dll" &&
					entry.FileType == BundleFileType.Assembly);
				Assert.Contains(bundle.Entries, entry => entry.RelativePath == "SingleFile.Dependency.dll" &&
					entry.FileType == BundleFileType.Assembly);
				Assert.Contains(bundle.Entries, entry => entry.FileType == BundleFileType.DepsJson);
				Assert.Contains(bundle.Entries, entry => entry.FileType == BundleFileType.RuntimeConfigJson);
				// The SDK may keep native runtime components in the apphost rather than
				// the manifest. The portable inventory contract is the managed pair,
				// both JSON files, and any symbols requested below.
				Assert.Equal(fixture.Compressed,
					bundle.Entries.Any(entry => entry.IsCompressed));
				if (fixture.IncludesSymbols)
					Assert.Contains(bundle.Entries, entry => entry.FileType == BundleFileType.Symbols);
				else
					Assert.DoesNotContain(bundle.Entries, entry => entry.FileType == BundleFileType.Symbols);
			}
		}

		[Fact]
		public void PublishedNet10AssembliesMatchBuildOutputExactly() {
			foreach (ModernBundleFixture fixture in GetFixtures()) {
				BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				using BundleFile bundle = result.Bundle!;
				BundleEntry main = Assert.Single(bundle.Entries,
					entry => entry.RelativePath == "SingleFile.App.dll");
				BundleEntry dependency = Assert.Single(bundle.Entries,
					entry => entry.RelativePath == "SingleFile.Dependency.dll");
				Assert.Equal(File.ReadAllBytes(fixture.BuildMainAssemblyPath),
					main.ReadAllBytes(main.Size));
				Assert.Equal(File.ReadAllBytes(fixture.BuildDependencyAssemblyPath),
					dependency.ReadAllBytes(dependency.Size));
			}
		}

		[Fact]
		public void PublishedNet10PdbVariantsContainBundledPortableSymbols() {
			foreach (ModernBundleFixture fixture in GetFixtures().Where(fixture => fixture.IncludesSymbols)) {
				BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				using BundleFile bundle = result.Bundle!;
				BundleEntry[] symbols = bundle.Entries.Where(entry => entry.FileType == BundleFileType.Symbols).ToArray();
				Assert.NotEmpty(symbols);
				Assert.Contains(symbols, entry => entry.RelativePath == "SingleFile.App.pdb");
				Assert.Contains(symbols, entry => entry.RelativePath == "SingleFile.Dependency.pdb");
			}
		}

		static IReadOnlyList<ModernBundleFixture> GetFixtures() {
			return ModernFixtureLocator.FindRequired();
		}

		static void ValidateSidecar(ModernBundleFixture fixture) {
			Assert.NotEmpty(fixture.PublishedFiles);
			foreach (ModernPublishedFile file in fixture.PublishedFiles) {
				Assert.False(String.IsNullOrWhiteSpace(file.Path));
				Assert.False(String.IsNullOrWhiteSpace(file.Sha256));
				string path = fixture.ResolvePublishedPath(file.Path!);
				Assert.True(File.Exists(path), path);
				Assert.Equal(file.Length, new FileInfo(path).Length);
				using SHA256 sha256 = SHA256.Create();
				using FileStream stream = File.OpenRead(path);
				string actual = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
				Assert.Equal(file.Sha256!.ToLowerInvariant(), actual);
			}
		}
	}
}
