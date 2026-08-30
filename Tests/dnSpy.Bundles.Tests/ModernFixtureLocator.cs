// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace dnSpy.Bundles.Tests {
	/// <summary>
	/// Locates generated net10 fixture metadata without making the test depend on
	/// the current working directory. DNSPY_BUNDLE_FIXTURES may contain one or
	/// more artifact roots (semicolon-separated on every platform; ':' is also
	/// accepted on Unix).
	/// </summary>
	internal static class ModernFixtureLocator {
		const string FixtureEnvironmentVariable = "DNSPY_BUNDLE_FIXTURES";
		const string DefaultFixtureRelativePath = "Tests/TestAssets/SingleFile/Net10/artifacts/net10.0";
		static readonly JsonSerializerOptions JsonOptions = new() {
			PropertyNameCaseInsensitive = true,
		};

		public static IReadOnlyList<ModernBundleFixture> Find() {
			var roots = GetRoots().ToArray();
			var sidecars = new List<string>();
			var seenSidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string root in roots) {
				if (File.Exists(root)) {
					if (Path.GetFileName(root).Equals("fixture.json", StringComparison.OrdinalIgnoreCase))
						AddSidecar(root, sidecars, seenSidecars);
					continue;
				}
				if (!Directory.Exists(root))
					continue;
				foreach (string sidecar in Directory.EnumerateFiles(root, "fixture.json",
					SearchOption.AllDirectories))
					AddSidecar(sidecar, sidecars, seenSidecars);
			}

			var fixtures = new List<ModernBundleFixture>(sidecars.Count);
			foreach (string sidecar in sidecars) {
				ModernFixtureRecord? record;
				try {
					record = JsonSerializer.Deserialize<ModernFixtureRecord>(
						File.ReadAllText(sidecar), JsonOptions);
				}
				catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) {
					throw new InvalidDataException("Unable to read single-file fixture metadata '" +
						sidecar + "'.", ex);
				}
				if (record is null)
					throw new InvalidDataException("Single-file fixture metadata is empty: " + sidecar);
				fixtures.Add(new ModernBundleFixture(Path.GetDirectoryName(sidecar)!, record));
			}
			fixtures.Sort((left, right) => StringComparer.Ordinal.Compare(left.Variant, right.Variant));
			return fixtures;
		}

		public static IReadOnlyList<ModernBundleFixture> FindRequired() {
			IReadOnlyList<ModernBundleFixture> fixtures = Find();
			if (fixtures.Count == 0)
				throw new InvalidOperationException(
					"Modern net10 single-file fixtures are missing. Run " +
					"Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1 (or " +
					"Generate-ModernFixtures.sh) with SDK 10.0.111, or set " +
					"DNSPY_BUNDLE_FIXTURES to the generated artifact root.");
			return fixtures;
		}

		static IEnumerable<string> GetRoots() {
			string? configured = Environment.GetEnvironmentVariable(FixtureEnvironmentVariable);
			if (!string.IsNullOrWhiteSpace(configured)) {
				char[] separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ';', ':' };
				foreach (string root in configured.Split(separators, StringSplitOptions.RemoveEmptyEntries))
					yield return Path.GetFullPath(root.Trim());
				yield break;
			}

			string baseDirectory = AppContext.BaseDirectory;
			string candidate = Path.GetFullPath(Path.Combine(baseDirectory, "../../../../",
				"TestAssets/SingleFile/Net10/artifacts/net10.0"));
			yield return candidate;

			// This fallback is useful when running `dotnet test` from the repository
			// root or from the dnSpy project directory rather than the test output dir.
			string currentDirectory = Directory.GetCurrentDirectory();
			foreach (string relative in new[] {
				DefaultFixtureRelativePath,
				Path.Combine("dnSpy", DefaultFixtureRelativePath),
				Path.Combine("..", DefaultFixtureRelativePath),
			}) {
				string path = Path.GetFullPath(Path.Combine(currentDirectory, relative));
				if (!StringComparer.OrdinalIgnoreCase.Equals(path, candidate))
					yield return path;
			}
		}

		static void AddSidecar(string path, List<string> sidecars, HashSet<string> seen) {
			string fullPath = Path.GetFullPath(path);
			if (seen.Add(fullPath))
				sidecars.Add(fullPath);
		}
	}

	internal sealed class ModernBundleFixture {
		readonly string root;
		readonly ModernFixtureRecord record;

		public ModernBundleFixture(string root, ModernFixtureRecord record) {
			this.root = root;
			this.record = record;
			if (record.SchemaVersion != 1)
				throw new InvalidDataException("Unsupported single-file fixture metadata schema in '" +
					Path.Combine(root, "fixture.json") + "'.");
			if (String.IsNullOrWhiteSpace(record.Variant) || String.IsNullOrWhiteSpace(record.Bundle) ||
				String.IsNullOrWhiteSpace(record.BuildMainAssembly) ||
				String.IsNullOrWhiteSpace(record.BuildDependencyAssembly))
				throw new InvalidDataException("Single-file fixture metadata has missing required paths in '" +
					Path.Combine(root, "fixture.json") + "'.");
			BundlePath = Resolve(record.Bundle);
			BuildMainAssemblyPath = Resolve(record.BuildMainAssembly);
			BuildDependencyAssemblyPath = Resolve(record.BuildDependencyAssembly);
			if (!File.Exists(BundlePath) || !File.Exists(BuildMainAssemblyPath) ||
				!File.Exists(BuildDependencyAssemblyPath))
				throw new FileNotFoundException("A generated single-file fixture references a missing file.",
					!File.Exists(BundlePath) ? BundlePath :
					!File.Exists(BuildMainAssemblyPath) ? BuildMainAssemblyPath : BuildDependencyAssemblyPath);
		}

		string Resolve(string relativePath) {
			if (Path.IsPathRooted(relativePath))
				throw new InvalidDataException("Fixture paths must be relative: " + relativePath);
			string path = Path.GetFullPath(Path.Combine(root, relativePath));
			string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ||
				root.EndsWith(Path.AltDirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
			if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Fixture path escapes its artifact root: " + relativePath);
			return path;
		}

		public string Variant => record.Variant!;
		public string SdkVersion => record.SdkVersion ?? string.Empty;
		public string TargetFramework => record.TargetFramework ?? string.Empty;
		public string RuntimeIdentifier => record.RuntimeIdentifier ?? string.Empty;
		public bool SelfContained => record.SelfContained;
		public bool Compressed => record.Compressed;
		public bool IncludesSymbols => record.IncludesSymbols;
		public string BundlePath { get; }
		public string BuildMainAssemblyPath { get; }
		public string BuildDependencyAssemblyPath { get; }
		public IReadOnlyList<ModernPublishedFile> PublishedFiles =>
			record.PublishedFiles ?? Array.Empty<ModernPublishedFile>();
		public string Root => root;
		public string ResolvePublishedPath(string relativePath) => Resolve(relativePath);
	}

	internal sealed class ModernFixtureRecord {
		public int SchemaVersion { get; set; }
		public string? SdkVersion { get; set; }
		public string? TargetFramework { get; set; }
		public string? RuntimeIdentifier { get; set; }
		public string? Variant { get; set; }
		public bool SelfContained { get; set; }
		public bool Compressed { get; set; }
		public bool IncludesSymbols { get; set; }
		public string? Bundle { get; set; }
		public string? BuildMainAssembly { get; set; }
		public string? BuildDependencyAssembly { get; set; }
		public ModernPublishedFile[]? PublishedFiles { get; set; }
	}

	internal sealed class ModernPublishedFile {
		public string? Path { get; set; }
		public long Length { get; set; }
		public string? Sha256 { get; set; }
	}
}
