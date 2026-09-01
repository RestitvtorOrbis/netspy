// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnSpy.Bundles;
using Microsoft.NET.HostModel.Bundle;
using Xunit;

namespace dnSpy.Bundles.Tests {
	[Collection("Bundle temporary directory")]
	public sealed class WindowsBundleGenerationTests {
		[Fact]
		public void NetCoreApp31UsesHostModelDefaultAllContent() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			string sourceRoot = Path.Combine(Path.GetTempPath(), "dnSpy-bnd024-v1-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(sourceRoot);
			try {
				string other = Path.Combine(sourceRoot, "content.dat");
				byte[] otherBytes = System.Text.Encoding.UTF8.GetBytes("v1 other content\n");
				File.WriteAllBytes(other, otherBytes);
				string original = CreateSyntheticBundle(appHost, fixture.BuildMainAssemblyPath,
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.deps.json"),
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.runtimeconfig.json"),
					sourceRoot, "V1Default.exe", new Version(3, 1), BundleOptions.BundleSymbolFiles, other);
				using BundleWorkspace workspace = OpenWorkspace(original);
				Assert.Equal(1u, workspace.Bundle.Manifest.MajorVersion);
				Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, workspace.Bundle.Manifest.Flags);
				Assert.All(workspace.Bundle.Entries, entry => {
					Assert.Equal((byte)0, entry.RawFileType);
					Assert.Equal(BundleFileType.Unknown, entry.FileType);
				});
				Assert.Contains(workspace.Bundle.Entries, entry => entry.RelativePath == "native-component.dll");
				Assert.Contains(workspace.Bundle.Entries, entry => entry.RelativePath == "Compat.App.pdb");
				Assert.Contains(workspace.Bundle.Entries, entry => entry.RelativePath == "content.dat");

				using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
					TestContext.Current.CancellationToken);
				using BundleFile output = Open(generated.BundlePath);
				Assert.Equal(1u, output.Manifest.MajorVersion);
				Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, output.Manifest.Flags);
				Assert.All(output.Entries, entry => {
					Assert.Equal((byte)0, entry.RawFileType);
					Assert.Equal(BundleFileType.Unknown, entry.FileType);
				});
				Assert.Equal(workspace.Bundle.Entries.Select(entry => entry.RelativePath).OrderBy(path => path),
					output.Entries.Select(entry => entry.RelativePath).OrderBy(path => path));
				foreach (BundleEntry entry in workspace.Bundle.Entries) {
					BundleEntry rebuilt = Assert.Single(output.Entries,
						candidate => candidate.RelativePath == entry.RelativePath);
					Assert.Equal(entry.ReadAllBytes(entry.Size), rebuilt.ReadAllBytes(rebuilt.Size));
				}
				BundleEntry rebuiltOther = Assert.Single(output.Entries,
					entry => entry.RelativePath == "content.dat");
				Assert.Equal(otherBytes, rebuiltOther.ReadAllBytes(rebuiltOther.Size));
			}
			finally {
				if (Directory.Exists(sourceRoot))
					Directory.Delete(sourceRoot, recursive: true);
			}
		}

		[Fact]
		public void GeneratesUncompressedBundleFromCurrentEntryBytes() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			BundleEntry runtime = Assert.Single(workspace.Bundle.Entries,
				entry => entry.FileType == BundleFileType.RuntimeConfigJson);
			byte[] replacement = System.Text.Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}\n");
			workspace.SetReplacement(runtime, replacement, new BundleReplacementInfo("test"));

			using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
				TestContext.Current.CancellationToken);
			Assert.True(File.Exists(generated.BundlePath));
			Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),
				Path.GetFullPath(generated.BundlePath), StringComparison.OrdinalIgnoreCase);
			Assert.Equal(new[] { "generated" }, Directory.EnumerateDirectories(
				generated.TemporaryDirectory).Select(Path.GetFileName));
			using BundleFile result = Open(generated.BundlePath);
			BundleEntry output = Assert.Single(result.Entries,
				entry => entry.RelativePath == runtime.RelativePath);
			Assert.Equal(replacement, output.ReadAllBytes(output.Size));
			Assert.DoesNotContain(result.Entries, entry => entry.IsCompressed);
		}

		[Fact]
		public void GeneratesCompressedBundleWhenOriginalHasCompressedEntries() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "scd-compressed-pdb");
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			Assert.Equal(BundleManifestFlags.None, workspace.Bundle.Manifest.Flags);
			Assert.Contains(workspace.Bundle.Entries, entry => entry.FileType == BundleFileType.Symbols);
			using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
				TestContext.Current.CancellationToken);
			using BundleFile result = Open(generated.BundlePath);
			Assert.Equal(workspace.Bundle.Manifest.MajorVersion, result.Manifest.MajorVersion);
			Assert.Equal(BundleManifestFlags.None, result.Manifest.Flags);
			Assert.Contains(result.Entries, entry => entry.IsCompressed);
			Assert.Contains(result.Entries, entry => entry.FileType == BundleFileType.Symbols);
		}

		[Fact]
		public void PreservesMultipleCurrentReplacements() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			BundleEntry[] configs = workspace.Bundle.Entries.Where(entry =>
				entry.FileType == BundleFileType.DepsJson || entry.FileType == BundleFileType.RuntimeConfigJson).ToArray();
			Assert.Equal(2, configs.Length);
			var replacements = new List<BundleWorkspaceReplacement>(configs.Length);
			for (int index = 0; index < configs.Length; index++)
				replacements.Add(new BundleWorkspaceReplacement(configs[index],
					System.Text.Encoding.UTF8.GetBytes("replacement-" + index), new BundleReplacementInfo("test")));
			workspace.SetReplacements(replacements);

			using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
				TestContext.Current.CancellationToken);
			using BundleFile result = Open(generated.BundlePath);
			for (int index = 0; index < configs.Length; index++) {
				BundleEntry output = Assert.Single(result.Entries,
					entry => entry.RelativePath == configs[index].RelativePath);
				Assert.Equal(System.Text.Encoding.UTF8.GetBytes("replacement-" + index),
					output.ReadAllBytes(output.Size));
			}
		}

		[Fact]
		public void GeneratedResultOwnsPrivateTemporaryDirectoryAndCleansItOnDispose() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
				TestContext.Current.CancellationToken);
			string directory = generated.TemporaryDirectory;
			string output = generated.BundlePath;
			Assert.True(Directory.Exists(directory));
			Assert.True(File.Exists(output));
			generated.Dispose();
			generated.Dispose();
			Assert.False(Directory.Exists(directory));
			Assert.False(File.Exists(output));
		}

		[Fact]
		public void CancellationBeforeGenerationLeavesNoPrivateArtifacts() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			HashSet<string> before = new HashSet<string>(
				Directory.EnumerateDirectories(Path.GetTempPath(), "dnSpy.Bundle.*"),
				StringComparer.OrdinalIgnoreCase);
			using var cancellation = new CancellationTokenSource();
			cancellation.Cancel();
			Assert.Throws<OperationCanceledException>(() =>
				new WindowsBundleRebuilder().Generate(workspace, cancellation.Token));
			foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), "dnSpy.Bundle.*"))
				Assert.Contains(directory, before);
		}

		[Fact]
		public void Net5CompatibilityFlagMapsToBundleAllContent() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			string sourceRoot = Path.Combine(Path.GetTempPath(), "dnSpy-bnd024-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(sourceRoot);
			try {
				string input = Path.Combine(sourceRoot, "Compat.App.dll");
				File.Copy(fixture.BuildMainAssemblyPath, input);
				File.Copy(Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.pdb"),
					Path.ChangeExtension(input, ".pdb"));
				string deps = Path.Combine(sourceRoot, "Compat.App.deps.json");
				string runtime = Path.Combine(sourceRoot, "Compat.App.runtimeconfig.json");
				File.Copy(Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.deps.json"), deps);
				File.Copy(Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.runtimeconfig.json"), runtime);
				string original = CreateSyntheticBundle(appHost, input, deps, runtime, sourceRoot,
					"Compat.exe", new Version(5, 0), BundleOptions.BundleAllContent);
				using BundleWorkspace workspace = OpenWorkspace(original);
				Assert.Equal(2u, workspace.Bundle.Manifest.MajorVersion);
				Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, workspace.Bundle.Manifest.Flags);
				using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
					TestContext.Current.CancellationToken);
				using BundleFile output = Open(generated.BundlePath);
				Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, output.Manifest.Flags);
			}
			finally {
				if (Directory.Exists(sourceRoot))
					Directory.Delete(sourceRoot, recursive: true);
			}
		}

		[Fact]
		public void Net5NonCompatibilityPreservesNativeAndSymbols() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			string sourceRoot = Path.Combine(Path.GetTempPath(), "dnSpy-bnd024-v2-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(sourceRoot);
			try {
				string original = CreateSyntheticBundle(appHost, fixture.BuildMainAssemblyPath,
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.deps.json"),
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.runtimeconfig.json"),
					sourceRoot, "NonCompat.exe", new Version(5, 0), BundleOptions.BundleNativeBinaries |
						BundleOptions.BundleSymbolFiles);
				using BundleWorkspace workspace = OpenWorkspace(original);
				Assert.Equal(2u, workspace.Bundle.Manifest.MajorVersion);
				Assert.Equal(BundleManifestFlags.None, workspace.Bundle.Manifest.Flags);
				Assert.Contains(workspace.Bundle.Entries, entry => entry.FileType == BundleFileType.NativeBinary);
				Assert.Contains(workspace.Bundle.Entries, entry => entry.FileType == BundleFileType.Symbols);
				using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
					TestContext.Current.CancellationToken);
				using BundleFile output = Open(generated.BundlePath);
				Assert.Equal(BundleManifestFlags.None, output.Manifest.Flags);
				Assert.Contains(output.Entries, entry => entry.FileType == BundleFileType.NativeBinary);
				Assert.Contains(output.Entries, entry => entry.FileType == BundleFileType.Symbols);
			}
			finally {
				if (Directory.Exists(sourceRoot))
					Directory.Delete(sourceRoot, recursive: true);
			}
		}

		[Fact]
		public void Net6NonCompatibilityPreservesNativeAndSymbols() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			string sourceRoot = Path.Combine(Path.GetTempPath(), "dnSpy-bnd024-v6-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(sourceRoot);
			try {
				string original = CreateSyntheticBundle(appHost, fixture.BuildMainAssemblyPath,
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.deps.json"),
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.runtimeconfig.json"),
					sourceRoot, "NonCompatV6.exe", new Version(6, 0),
					BundleOptions.BundleNativeBinaries | BundleOptions.BundleSymbolFiles);
				using BundleWorkspace workspace = OpenWorkspace(original);
				Assert.Equal(6u, workspace.Bundle.Manifest.MajorVersion);
				Assert.Equal(BundleManifestFlags.None, workspace.Bundle.Manifest.Flags);
				Assert.Contains(workspace.Bundle.Entries, entry => entry.FileType == BundleFileType.NativeBinary);
				Assert.Contains(workspace.Bundle.Entries, entry => entry.FileType == BundleFileType.Symbols);
				using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
					TestContext.Current.CancellationToken);
				using BundleFile output = Open(generated.BundlePath);
				Assert.Equal(BundleManifestFlags.None, output.Manifest.Flags);
				Assert.Contains(output.Entries, entry => entry.FileType == BundleFileType.NativeBinary);
				Assert.Contains(output.Entries, entry => entry.FileType == BundleFileType.Symbols);
			}
			finally {
				if (Directory.Exists(sourceRoot))
					Directory.Delete(sourceRoot, recursive: true);
			}
		}

		[Fact]
		public void Net6CompatibilityFlagMapsToBundleAllContent() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			string sourceRoot = Path.Combine(Path.GetTempPath(), "dnSpy-bnd024-v6-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(sourceRoot);
			try {
				string original = CreateSyntheticBundle(appHost, fixture.BuildMainAssemblyPath,
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.deps.json"),
					Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.runtimeconfig.json"),
					sourceRoot, "CompatV6.exe", new Version(6, 0),
					BundleOptions.BundleAllContent | BundleOptions.BundleSymbolFiles);
				using BundleWorkspace workspace = OpenWorkspace(original);
				Assert.Equal(6u, workspace.Bundle.Manifest.MajorVersion);
				Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, workspace.Bundle.Manifest.Flags);
				using WindowsBundleGeneration generated = new WindowsBundleRebuilder().Generate(workspace,
					TestContext.Current.CancellationToken);
				using BundleFile output = Open(generated.BundlePath);
				Assert.Equal(BundleManifestFlags.NetcoreApp3CompatMode, output.Manifest.Flags);
			}
			finally {
				if (Directory.Exists(sourceRoot))
					Directory.Delete(sourceRoot, recursive: true);
			}
		}

		static string CreateSyntheticBundle(string appHost, string mainAssembly, string deps,
			string runtime, string outputDirectory, string hostName, Version frameworkVersion,
			BundleOptions options) => CreateSyntheticBundle(appHost, mainAssembly, deps, runtime,
			outputDirectory, hostName, frameworkVersion, options, null);

		static string CreateSyntheticBundle(string appHost, string mainAssembly, string deps,
			string runtime, string outputDirectory, string hostName, Version frameworkVersion,
			BundleOptions options, string? otherPath) {
			var bundler = new Bundler(hostName, outputDirectory, options,
				System.Runtime.InteropServices.OSPlatform.Windows,
				System.Runtime.InteropServices.Architecture.X64, frameworkVersion,
				appAssemblyName: "Compat.App", macosCodesign: false);
			var inputs = new List<FileSpec> {
				new FileSpec(appHost, hostName),
				new FileSpec(mainAssembly, "Compat.App.dll"),
				new FileSpec(appHost, "native-component.dll"),
				new FileSpec(deps, "Compat.App.deps.json"),
				new FileSpec(runtime, "Compat.App.runtimeconfig.json"),
				new FileSpec(Path.ChangeExtension(mainAssembly, ".pdb"), "Compat.App.pdb"),
			};
			if (otherPath is not null)
				inputs.Add(new FileSpec(otherPath, "content.dat"));
			return bundler.GenerateBundle(inputs);
		}

		static BundleWorkspace OpenWorkspace(string filename) {
			BundleOpenResult opened = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			return new BundleWorkspace(opened.Bundle!);
		}

		static BundleFile Open(string filename) {
			BundleOpenResult opened = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			return opened.Bundle!;
		}
	}
}
