// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace dnSpy.Bundles.Tests {
	/// <summary>
	/// Locates the isolated historical fixture artifacts emitted by
	/// Generate-HistoricalFixtures.ps1. The environment variable is useful on
	/// CI, where downloaded artifacts are outside the source checkout.
	/// </summary>
	internal static class HistoricalFixtureLocator {
		const string FixtureEnvironmentVariable = "DNSPY_BUNDLE_FIXTURES";
		const string DefaultFixtureRelativePath = "Tests/TestAssets/SingleFile/artifacts/historical";
		static readonly JsonSerializerOptions JsonOptions = new() {
			PropertyNameCaseInsensitive = true,
		};

		public static IReadOnlyList<HistoricalBundleFixture> Find() {
			var sidecars = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string root in GetRoots()) {
				if (File.Exists(root)) {
					if (Path.GetFileName(root).Equals("fixture.json", StringComparison.OrdinalIgnoreCase))
						AddSidecar(root, sidecars, seen);
					continue;
				}
				if (!Directory.Exists(root))
					continue;
				foreach (string sidecar in Directory.EnumerateFiles(root, "fixture.json",
					SearchOption.AllDirectories))
					AddSidecar(sidecar, sidecars, seen);
			}

			var fixtures = new List<HistoricalBundleFixture>(sidecars.Count);
			foreach (string sidecar in sidecars) {
				HistoricalFixtureRecord? record;
				try {
					record = JsonSerializer.Deserialize<HistoricalFixtureRecord>(
						File.ReadAllText(sidecar), JsonOptions);
				}
				catch (Exception ex) when (ex is JsonException || ex is IOException ||
					ex is UnauthorizedAccessException) {
					throw new InvalidDataException("Unable to read historical fixture metadata '" +
						sidecar + "'.", ex);
				}
				if (record is null)
					throw new InvalidDataException("Historical fixture metadata is empty: " + sidecar);
				fixtures.Add(new HistoricalBundleFixture(Path.GetDirectoryName(sidecar)!, record));
			}
			fixtures.Sort((left, right) => {
				int result = StringComparer.Ordinal.Compare(left.Generation, right.Generation);
				return result != 0 ? result : StringComparer.Ordinal.Compare(left.Variant, right.Variant);
			});
			return fixtures;
		}

		public static IReadOnlyList<HistoricalBundleFixture> FindRequired() {
			IReadOnlyList<HistoricalBundleFixture> fixtures = Find();
			if (fixtures.Count == 0)
				throw new InvalidOperationException(
					"Historical single-file fixtures are missing. Run " +
					"Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1 with the " +
					"five pinned SDKs, or set DNSPY_BUNDLE_FIXTURES to the downloaded artifact root.");
			return fixtures;
		}

		static IEnumerable<string> GetRoots() {
			string? configured = Environment.GetEnvironmentVariable(FixtureEnvironmentVariable);
			if (!String.IsNullOrWhiteSpace(configured)) {
				char[] separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ';', ':' };
				foreach (string root in configured.Split(separators, StringSplitOptions.RemoveEmptyEntries))
					yield return Path.GetFullPath(root.Trim());
				yield break;
			}

			string baseDirectory = AppContext.BaseDirectory;
			yield return Path.GetFullPath(Path.Combine(baseDirectory, "../../../../",
				"TestAssets/SingleFile/artifacts/historical"));

			string currentDirectory = Directory.GetCurrentDirectory();
			foreach (string relative in new[] {
				DefaultFixtureRelativePath,
				Path.Combine("dnSpy", DefaultFixtureRelativePath),
				Path.Combine("..", DefaultFixtureRelativePath),
			})
				yield return Path.GetFullPath(Path.Combine(currentDirectory, relative));
		}

		static void AddSidecar(string path, List<string> sidecars, HashSet<string> seen) {
			string fullPath = Path.GetFullPath(path);
			if (seen.Add(fullPath))
				sidecars.Add(fullPath);
		}
	}

	internal sealed class HistoricalBundleFixture {
		readonly string root;
		readonly HistoricalFixtureRecord record;

		public HistoricalBundleFixture(string root, HistoricalFixtureRecord record) {
			this.root = root;
			this.record = record;
			if (record.SchemaVersion != 2)
				throw new InvalidDataException("Unsupported historical fixture metadata schema in '" +
					Path.Combine(root, "fixture.json") + "'.");
			if (String.IsNullOrWhiteSpace(record.Generation) || String.IsNullOrWhiteSpace(record.Variant) ||
				String.IsNullOrWhiteSpace(record.Bundle) || String.IsNullOrWhiteSpace(record.BuildMainAssembly) ||
				String.IsNullOrWhiteSpace(record.BuildDependencyAssembly) || record.PublishedFiles is null ||
				record.ExpectedEntries is null)
				throw new InvalidDataException("Historical fixture metadata has missing required fields in '" +
					Path.Combine(root, "fixture.json") + "'.");
			BundlePath = Resolve(record.Bundle);
			BuildMainAssemblyPath = Resolve(record.BuildMainAssembly);
			BuildDependencyAssemblyPath = Resolve(record.BuildDependencyAssembly);
			InventoryPath = Resolve(record.Inventory ?? "inventory.json");
			HashesPath = Resolve(record.Hashes ?? "hashes.json");
			if (!File.Exists(BundlePath) || !File.Exists(BuildMainAssemblyPath) ||
				!File.Exists(BuildDependencyAssemblyPath) || !File.Exists(InventoryPath) ||
				!File.Exists(HashesPath))
				throw new FileNotFoundException("A historical fixture references a missing artifact.",
					!File.Exists(BundlePath) ? BundlePath : !File.Exists(BuildMainAssemblyPath) ?
					BuildMainAssemblyPath : !File.Exists(BuildDependencyAssemblyPath) ?
					BuildDependencyAssemblyPath : !File.Exists(InventoryPath) ? InventoryPath : HashesPath);
		}

		string Resolve(string relativePath) {
			if (Path.IsPathRooted(relativePath))
				throw new InvalidDataException("Historical fixture paths must be relative: " + relativePath);
			string path = Path.GetFullPath(Path.Combine(root, relativePath));
			string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ||
				root.EndsWith(Path.AltDirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
			if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Historical fixture path escapes its artifact root: " + relativePath);
			return path;
		}

		public string Generation => record.Generation!;
		public string Variant => record.Variant!;
		public string SdkVersion => record.SdkVersion ?? String.Empty;
		public string TargetFramework => record.TargetFramework ?? String.Empty;
		public string RuntimeIdentifier => record.RuntimeIdentifier ?? String.Empty;
		public uint ManifestMajorVersion => record.ManifestMajorVersion;
		public ulong ManifestFlags => record.ManifestFlags;
		public bool SelfContained => record.SelfContained;
		public bool Compressed => record.Compressed;
		public bool IncludesSymbols => record.IncludesSymbols;
		public bool CompatibilityMode => record.CompatibilityMode;
		public string BundlePath { get; }
		public string BuildMainAssemblyPath { get; }
		public string BuildDependencyAssemblyPath { get; }
		public string InventoryPath { get; }
		public string HashesPath { get; }
		public IReadOnlyList<HistoricalPublishedFile> PublishedFiles => record.PublishedFiles!;
		public IReadOnlyList<HistoricalExpectedEntry> ExpectedEntries => record.ExpectedEntries!;
		public string ResolvePublishedPath(string relativePath) => Resolve(relativePath);
	}

	internal sealed class HistoricalFixtureRecord {
		public int SchemaVersion { get; set; }
		public string? Generation { get; set; }
		public string? SdkVersion { get; set; }
		public string? TargetFramework { get; set; }
		public string? RuntimeIdentifier { get; set; }
		public uint ManifestMajorVersion { get; set; }
		public ulong ManifestFlags { get; set; }
		public string? Variant { get; set; }
		public bool SelfContained { get; set; }
		public bool Compressed { get; set; }
		public bool IncludesSymbols { get; set; }
		public bool CompatibilityMode { get; set; }
		public string? Bundle { get; set; }
		public string? BuildMainAssembly { get; set; }
		public string? BuildDependencyAssembly { get; set; }
		public string? Inventory { get; set; }
		public string? Hashes { get; set; }
		public HistoricalPublishedFile[]? PublishedFiles { get; set; }
		public HistoricalExpectedEntry[]? ExpectedEntries { get; set; }
	}

	internal sealed class HistoricalPublishedFile {
		public string? Path { get; set; }
		public long Length { get; set; }
		public string? Sha256 { get; set; }
	}

	internal sealed class HistoricalExpectedEntry {
		public int Index { get; set; }
		public string? RelativePath { get; set; }
		public string? FileType { get; set; }
		public byte RawFileType { get; set; }
		public long Offset { get; set; }
		public long Size { get; set; }
		public long CompressedSize { get; set; }
		public bool IsCompressed { get; set; }
	}

	internal sealed class HistoricalInventoryRecord {
		public int SchemaVersion { get; set; }
		public string? Generation { get; set; }
		public string? Variant { get; set; }
		public uint ManifestMajorVersion { get; set; }
		public ulong ManifestFlags { get; set; }
		public bool SelfContained { get; set; }
		public bool Compressed { get; set; }
		public bool IncludesSymbols { get; set; }
		public HistoricalExpectedEntry[]? Entries { get; set; }
	}

	internal sealed class HistoricalHashesRecord {
		public int SchemaVersion { get; set; }
		public HistoricalPublishedFile[]? Files { get; set; }
	}
}
