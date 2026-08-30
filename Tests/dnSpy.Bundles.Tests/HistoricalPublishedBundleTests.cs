// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class HistoricalPublishedBundleTests {
		// This is the fixture contract from docs/specs/dotnet-single-file-bundles.md.
		// Compression, symbols, and compatibility mode are asserted from each
		// generated inventory as well as against this explicit matrix; they are
		// never inferred from a variant filename.
		static readonly ExpectedFixture[] Expected = {
			new("NetCoreApp31", "3.1.426", "netcoreapp3.1", 1, 1, true, false, false, false),
			new("Net5", "5.0.408", "net5.0", 2, 0, false, false, false, false),
			new("Net5", "5.0.408", "net5.0", 2, 0, true, false, false, false),
			new("Net5", "5.0.408", "net5.0", 2, 1, true, false, false, true),
			new("Net5", "5.0.408", "net5.0", 2, 0, true, false, true, false),
			new("Net6", "6.0.428", "net6.0", 6, 0, false, false, false, false),
			new("Net6", "6.0.428", "net6.0", 6, 0, true, false, false, false),
			new("Net6", "6.0.428", "net6.0", 6, 0, true, true, false, false),
			new("Net6", "6.0.428", "net6.0", 6, 0, true, false, true, false),
			new("Net6", "6.0.428", "net6.0", 6, 0, true, true, true, false),
			new("Net8", "8.0.419", "net8.0", 6, 0, true, false, false, false),
			new("Net8", "8.0.419", "net8.0", 6, 0, true, true, false, false),
			new("Net10", "10.0.111", "net10.0", 6, 0, false, false, false, false),
			new("Net10", "10.0.111", "net10.0", 6, 0, true, false, false, false),
			new("Net10", "10.0.111", "net10.0", 6, 0, true, true, false, false),
			new("Net10", "10.0.111", "net10.0", 6, 0, true, false, true, false),
			new("Net10", "10.0.111", "net10.0", 6, 0, true, true, true, false),
		};

		[Fact]
		public void PublishedHistoricalVariantsMatchRequiredInventoryMatrix() {
			IReadOnlyList<HistoricalBundleFixture> fixtures = GetFixtures();
			Assert.Equal(Expected.Length, fixtures.Count);
			foreach (ExpectedFixture expected in Expected) {
				HistoricalBundleFixture fixture = Assert.Single(fixtures, candidate =>
					candidate.Generation == expected.Generation && MatchesVariant(candidate, expected));
				Assert.Equal(expected.SdkVersion, fixture.SdkVersion);
				Assert.Equal(expected.TargetFramework, fixture.TargetFramework);
				Assert.Equal("win-x64", fixture.RuntimeIdentifier);
				Assert.Equal(expected.ManifestMajorVersion, fixture.ManifestMajorVersion);
				Assert.Equal(expected.ManifestFlags, fixture.ManifestFlags);
				Assert.Equal(expected.SelfContained, fixture.SelfContained);
				Assert.Equal(expected.Compressed, fixture.Compressed);
				Assert.Equal(expected.IncludesSymbols, fixture.IncludesSymbols);
				Assert.Equal(expected.CompatibilityMode, fixture.CompatibilityMode);
				ValidateSidecars(fixture);

				BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				using BundleFile bundle = result.Bundle!;
				Assert.Equal(expected.ManifestMajorVersion, bundle.Manifest.MajorVersion);
				Assert.Equal((BundleManifestFlags)expected.ManifestFlags, bundle.Manifest.Flags);
				Assert.Contains(bundle.Entries, entry => entry.RelativePath == "SingleFile.App.dll" &&
					entry.FileType == BundleFileType.Assembly);
				Assert.Contains(bundle.Entries, entry => entry.RelativePath == "SingleFile.Dependency.dll" &&
					entry.FileType == BundleFileType.Assembly);
				Assert.Contains(bundle.Entries, entry => entry.FileType == BundleFileType.DepsJson);
				Assert.Contains(bundle.Entries, entry => entry.FileType == BundleFileType.RuntimeConfigJson);
				Assert.Equal(expected.IncludesSymbols,
					bundle.Entries.Any(entry => entry.FileType == BundleFileType.Symbols));
				Assert.Equal(expected.Compressed, bundle.Entries.Any(entry => entry.IsCompressed));
				ValidateExpectedInventory(fixture, bundle);
			}
		}

		[Fact]
		public void Net5IncludeAllContentForSelfExtractPublishesCompatibilityFlag() {
			HistoricalBundleFixture fixture = Assert.Single(GetFixtures(), candidate =>
				candidate.Generation == "Net5" && candidate.CompatibilityMode);
			Assert.Equal(1UL, fixture.ManifestFlags);
			BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			Assert.Equal(2u, bundle.Manifest.MajorVersion);
			Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, bundle.Manifest.Flags);
		}

		[Fact]
		public void PublishedHistoricalAssembliesMatchBuildOutputExactly() {
			foreach (HistoricalBundleFixture fixture in GetFixtures()) {
				BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				using BundleFile bundle = result.Bundle!;
				BundleEntry main = Assert.Single(bundle.Entries,
					entry => entry.RelativePath == "SingleFile.App.dll");
				BundleEntry dependency = Assert.Single(bundle.Entries,
					entry => entry.RelativePath == "SingleFile.Dependency.dll");
				Assert.Equal(File.ReadAllBytes(fixture.BuildMainAssemblyPath), main.ReadAllBytes(main.Size));
				Assert.Equal(File.ReadAllBytes(fixture.BuildDependencyAssemblyPath),
					dependency.ReadAllBytes(dependency.Size));
			}
		}

		static bool MatchesVariant(HistoricalBundleFixture fixture, ExpectedFixture expected) =>
			fixture.SelfContained == expected.SelfContained && fixture.Compressed == expected.Compressed &&
			fixture.IncludesSymbols == expected.IncludesSymbols &&
			fixture.CompatibilityMode == expected.CompatibilityMode;

		static IReadOnlyList<HistoricalBundleFixture> GetFixtures() {
			try {
				return HistoricalFixtureLocator.FindRequired();
			}
			catch (Exception ex) when (ex is InvalidOperationException || ex is FileNotFoundException ||
				ex is InvalidDataException) {
				// Local Linux development commonly has only SDK 10. CI sets CI=true
				// and must fail loudly when any required fixture is absent.
				string? ci = Environment.GetEnvironmentVariable("CI");
				if (!String.IsNullOrWhiteSpace(ci) &&
					!String.Equals(ci, "false", StringComparison.OrdinalIgnoreCase))
					throw;
				Assert.Skip("Historical fixture prerequisite is unavailable: " + ex.Message);
				return Array.Empty<HistoricalBundleFixture>();
			}
		}

		static void ValidateExpectedInventory(HistoricalBundleFixture fixture, BundleFile bundle) {
			Assert.NotEmpty(fixture.ExpectedEntries);
			Assert.Equal(fixture.ExpectedEntries.Count, bundle.Entries.Count);
			for (int index = 0; index < fixture.ExpectedEntries.Count; index++) {
				HistoricalExpectedEntry expected = fixture.ExpectedEntries[index];
				Assert.NotNull(expected.RelativePath);
				BundleEntry entry = bundle.Entries[index];
				Assert.Equal(index, expected.Index);
				Assert.Equal(expected.RelativePath, entry.RelativePath);
				Assert.Equal(expected.RawFileType, entry.RawFileType);
				Assert.Equal(expected.FileType, entry.FileType.ToString());
				Assert.Equal(expected.Offset, entry.Offset);
				Assert.Equal(expected.Size, entry.Size);
				Assert.Equal(expected.CompressedSize, entry.CompressedSize);
				// This is the per-entry state recorded by the independent raw
				// manifest inventory. It catches HostModel ratio exceptions.
				Assert.Equal(expected.IsCompressed, entry.IsCompressed);
			}
		}

		static void ValidateSidecars(HistoricalBundleFixture fixture) {
			Assert.NotEmpty(fixture.PublishedFiles);
			foreach (HistoricalPublishedFile file in fixture.PublishedFiles) {
				Assert.False(String.IsNullOrWhiteSpace(file.Path));
				Assert.False(String.IsNullOrWhiteSpace(file.Sha256));
				string path = fixture.ResolvePublishedPath(file.Path!);
				Assert.True(File.Exists(path), path);
				Assert.Equal(file.Length, new FileInfo(path).Length);
				using SHA256 sha256 = SHA256.Create();
				using FileStream stream = File.OpenRead(path);
				Assert.Equal(file.Sha256!.ToLowerInvariant(),
					Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant());
			}

			JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
			HistoricalInventoryRecord inventory = JsonSerializer.Deserialize<HistoricalInventoryRecord>(
				File.ReadAllText(fixture.InventoryPath), options)!;
			Assert.Equal(1, inventory.SchemaVersion);
			Assert.Equal(fixture.Generation, inventory.Generation);
			Assert.Equal(fixture.Variant, inventory.Variant);
			Assert.Equal(fixture.ManifestMajorVersion, inventory.ManifestMajorVersion);
			Assert.Equal(fixture.ManifestFlags, inventory.ManifestFlags);
			Assert.Equal(fixture.SelfContained, inventory.SelfContained);
			Assert.Equal(fixture.Compressed, inventory.Compressed);
			Assert.Equal(fixture.IncludesSymbols, inventory.IncludesSymbols);
			Assert.NotNull(inventory.Entries);
			Assert.Equal(fixture.ExpectedEntries.Count, inventory.Entries!.Length);
			for (int index = 0; index < fixture.ExpectedEntries.Count; index++) {
				HistoricalExpectedEntry expected = fixture.ExpectedEntries[index];
				HistoricalExpectedEntry actual = inventory.Entries[index];
				Assert.Equal(expected.Index, actual.Index);
				Assert.Equal(expected.RelativePath, actual.RelativePath);
				Assert.Equal(expected.FileType, actual.FileType);
				Assert.Equal(expected.RawFileType, actual.RawFileType);
				Assert.Equal(expected.Offset, actual.Offset);
				Assert.Equal(expected.Size, actual.Size);
				Assert.Equal(expected.CompressedSize, actual.CompressedSize);
				Assert.Equal(expected.IsCompressed, actual.IsCompressed);
			}

			HistoricalHashesRecord hashes = JsonSerializer.Deserialize<HistoricalHashesRecord>(
				File.ReadAllText(fixture.HashesPath), options)!;
			Assert.Equal(1, hashes.SchemaVersion);
			Assert.NotNull(hashes.Files);
			Assert.Equal(fixture.PublishedFiles.Select(file => file.Path),
				hashes.Files!.Select(file => file.Path));
			Assert.Equal(fixture.PublishedFiles.Select(file => file.Sha256),
				hashes.Files!.Select(file => file.Sha256));
		}

		readonly record struct ExpectedFixture(string Generation, string SdkVersion,
			string TargetFramework, uint ManifestMajorVersion, ulong ManifestFlags,
			bool SelfContained, bool Compressed, bool IncludesSymbols, bool CompatibilityMode);
	}
}
