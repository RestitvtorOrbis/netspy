// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Locates the generated modern single-file fixtures used by integration tests.</summary>
	internal static class IntegrationFixtureLocator {
		const string FixtureEnvironmentVariable = "DNSPY_BUNDLE_FIXTURES";
		const string DefaultFixtureRelativePath = "Tests/TestAssets/SingleFile/Net10/artifacts/net10.0";

		public static readonly IReadOnlyList<string> ModernVariants = new[] {
			"fdd-uncompressed",
			"scd-compressed",
			"scd-compressed-pdb",
			"scd-uncompressed",
			"scd-uncompressed-pdb",
		};

		public static string FindBundle(string variant) {
			if (variant is null)
				throw new ArgumentNullException(nameof(variant));
			foreach (string root in GetRoots()) {
				string candidate = Path.Combine(root, variant, "publish", "SingleFile.App.exe");
				if (File.Exists(candidate))
					return Path.GetFullPath(candidate);

				// Also accept a configured variant directory. This is convenient when a single
				// fixture is downloaded from CI rather than the complete artifact root.
				candidate = Path.Combine(root, "publish", "SingleFile.App.exe");
				if (Path.GetFileName(root).Equals(variant, StringComparison.OrdinalIgnoreCase) &&
					File.Exists(candidate))
					return Path.GetFullPath(candidate);
			}
			throw new InvalidOperationException("The generated modern single-file fixture '" +
				variant + "' is missing. Run Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1 " +
				"with SDK 10.0.111, or set DNSPY_BUNDLE_FIXTURES to the generated artifact root.");
		}

		static IEnumerable<string> GetRoots() {
			string? configured = Environment.GetEnvironmentVariable(FixtureEnvironmentVariable);
			if (!string.IsNullOrWhiteSpace(configured)) {
				char[] separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ';', ':' };
				foreach (string root in configured.Split(separators, StringSplitOptions.RemoveEmptyEntries)) {
					string path = Path.GetFullPath(root.Trim());
					if (File.Exists(path) && Path.GetFileName(path).Equals("fixture.json",
						StringComparison.OrdinalIgnoreCase))
						path = Path.GetDirectoryName(path)!;
					yield return path;
				}
				yield break;
			}

			string baseDirectory = AppContext.BaseDirectory;
			yield return Path.GetFullPath(Path.Combine(baseDirectory, "../../../../",
				"TestAssets/SingleFile/Net10/artifacts/net10.0"));

			string currentDirectory = Directory.GetCurrentDirectory();
			foreach (string relative in new[] {
				DefaultFixtureRelativePath,
				Path.Combine("dnSpy", DefaultFixtureRelativePath),
				Path.Combine("..", DefaultFixtureRelativePath),
			})
				yield return Path.GetFullPath(Path.Combine(currentDirectory, relative));
		}
	}
}
