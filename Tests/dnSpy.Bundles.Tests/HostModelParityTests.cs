// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using dnSpy.Bundles;
using Microsoft.NET.HostModel.Bundle;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class HostModelParityTests {
		const string SdkHostModelPath = "/usr/lib/dotnet/sdk/10.0.111/Microsoft.NET.HostModel.dll";
		const int AllInventoryOptions = (int)(BundleOptions.BundleNativeBinaries |
			BundleOptions.BundleOtherFiles | BundleOptions.BundleSymbolFiles);

		[Theory]
		[InlineData(3, 1, 1u, false)]
		[InlineData(5, 0, 2u, false)]
		[InlineData(6, 0, 6u, true)]
		public void VendoredAndSdkHostModelProduceEquivalentLogicalBundles(
			int frameworkMajor, int frameworkMinor, uint expectedManifestMajor, bool compress) {
			Assert.True(File.Exists(SdkHostModelPath),
				"The pinned .NET 10.0.111 SDK HostModel assembly is required: " + SdkHostModelPath);
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			Assert.True(File.Exists(appHost), appHost);

			string testRoot = Path.Combine(Path.GetTempPath(), "dnSpy-hostmodel-parity-" + Guid.NewGuid().ToString("N"));
			string sdkOutput = Path.Combine(testRoot, "sdk");
			string vendoredOutput = Path.Combine(testRoot, "vendored");
			Directory.CreateDirectory(sdkOutput);
			Directory.CreateDirectory(vendoredOutput);
			try {
				string otherFile = Path.Combine(testRoot, "content.dat");
				File.WriteAllText(otherFile, String.Concat(Enumerable.Repeat("HostModel parity content\n", 512)));
				ParityInput[] inputs = CreateInputs(fixture, appHost, otherFile);
				var frameworkVersion = new Version(frameworkMajor, frameworkMinor);
				int options = AllInventoryOptions | (compress ? (int)BundleOptions.EnableCompression : 0);
				string sdkBundle = GenerateWithSdkHostModel(sdkOutput, inputs, options, frameworkVersion);
				string vendoredBundle = GenerateWithVendoredHostModel(vendoredOutput, inputs,
					(BundleOptions)options, frameworkVersion);

				using BundleFile sdk = Open(sdkBundle);
				using BundleFile vendored = Open(vendoredBundle);
				Assert.Equal(expectedManifestMajor, sdk.Manifest.MajorVersion);
				Assert.Equal(expectedManifestMajor, vendored.Manifest.MajorVersion);
				Assert.Equal(sdk.Manifest.MinorVersion, vendored.Manifest.MinorVersion);
				Assert.Equal(sdk.Manifest.Flags, vendored.Manifest.Flags);
				Assert.Equal(sdk.Entries.Count, vendored.Entries.Count);
				for (int index = 0; index < sdk.Entries.Count; index++) {
					BundleEntry expected = sdk.Entries[index];
					BundleEntry actual = vendored.Entries[index];
					Assert.Equal(expected.RelativePath, actual.RelativePath);
					Assert.Equal(expected.RawFileType, actual.RawFileType);
					Assert.Equal(expected.FileType, actual.FileType);
					Assert.Equal(expected.Size, actual.Size);
					Assert.Equal(expected.IsCompressed, actual.IsCompressed);
					Assert.Equal(expected.ReadAllBytes(expected.Size), actual.ReadAllBytes(actual.Size));
				}
				if (compress)
					Assert.Contains(vendored.Entries, entry => entry.IsCompressed);
				else
					Assert.DoesNotContain(vendored.Entries, entry => entry.IsCompressed);
			}
			finally {
				Directory.Delete(testRoot, recursive: true);
			}
		}

		[Fact]
		public void ProductionProjectHasNoInstalledSdkDependency() {
			string project = File.ReadAllText(Path.Combine(GetVendorRoot(), "Microsoft.NET.HostModel.Bundle.csproj"));
			Assert.DoesNotContain("/usr/lib/dotnet", project, StringComparison.Ordinal);
			Assert.DoesNotContain("Microsoft.NET.HostModel.dll", project, StringComparison.Ordinal);
			Assert.DoesNotContain(typeof(Bundler).Assembly.GetReferencedAssemblies(), reference =>
				reference.Name == "Microsoft.NET.HostModel");
		}

		static ParityInput[] CreateInputs(ModernBundleFixture fixture, string appHost, string otherFile) => new[] {
			new ParityInput(appHost, "ParityHost.exe"),
			new ParityInput(fixture.BuildMainAssemblyPath, "Parity.App.dll"),
			new ParityInput(fixture.BuildDependencyAssemblyPath, "Parity.Dependency.dll"),
			new ParityInput(Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.deps.json"), "Parity.App.deps.json"),
			new ParityInput(Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.runtimeconfig.json"), "Parity.App.runtimeconfig.json"),
			new ParityInput(Path.Combine(fixture.Root, "build", "App", "Release", "net10.0", "win-x64", "SingleFile.App.pdb"), "Parity.App.pdb"),
			new ParityInput(appHost, "native-component.dll"),
			new ParityInput(otherFile, "data/content.dat"),
		};

		static string GenerateWithVendoredHostModel(string outputDirectory, ParityInput[] inputs,
			BundleOptions options, Version frameworkVersion) {
			var bundler = new Bundler("ParityHost.exe", outputDirectory, options,
				OSPlatform.Windows, Architecture.X64, frameworkVersion,
				appAssemblyName: "Parity.App", macosCodesign: false);
			return bundler.GenerateBundle(inputs.Select(input =>
				new FileSpec(input.SourcePath, input.RelativePath)).ToArray());
		}

		static string GenerateWithSdkHostModel(string outputDirectory, ParityInput[] inputs,
			int options, Version frameworkVersion) {
			var loadContext = new SdkHostModelLoadContext(SdkHostModelPath);
			try {
				Assembly assembly = loadContext.LoadFromAssemblyPath(SdkHostModelPath);
				Type bundlerType = assembly.GetType("Microsoft.NET.HostModel.Bundle.Bundler", throwOnError: true)!;
				Type optionsType = assembly.GetType("Microsoft.NET.HostModel.Bundle.BundleOptions", throwOnError: true)!;
				Type fileSpecType = assembly.GetType("Microsoft.NET.HostModel.Bundle.FileSpec", throwOnError: true)!;
				object bundler = Activator.CreateInstance(bundlerType, new object?[] {
					"ParityHost.exe", outputDirectory, Enum.ToObject(optionsType, options),
					OSPlatform.Windows, Architecture.X64, frameworkVersion, false, "Parity.App", false,
				})!;
				Array fileSpecs = Array.CreateInstance(fileSpecType, inputs.Length);
				for (int index = 0; index < inputs.Length; index++)
					fileSpecs.SetValue(Activator.CreateInstance(fileSpecType,
						inputs[index].SourcePath, inputs[index].RelativePath), index);
				MethodInfo generate = bundlerType.GetMethod("GenerateBundle", BindingFlags.Instance | BindingFlags.Public)!;
				return (string)generate.Invoke(bundler, new object[] { fileSpecs })!;
			}
			finally {
				loadContext.Unload();
			}
		}

		static BundleFile Open(string path) {
			BundleOpenResult result = new BundleReader().Open(path);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			return result.Bundle!;
		}

		static string GetVendorRoot() {
			DirectoryInfo? directory = new(AppContext.BaseDirectory);
			while (directory is not null) {
				string candidate = Path.Combine(directory.FullName, "Libraries", "Microsoft.NET.HostModel.Bundle");
				if (File.Exists(Path.Combine(candidate, "Microsoft.NET.HostModel.Bundle.csproj")))
					return candidate;
				directory = directory.Parent;
			}
			throw new DirectoryNotFoundException("Could not locate the vendored HostModel project.");
		}

		sealed record ParityInput(string SourcePath, string RelativePath);

		sealed class SdkHostModelLoadContext : AssemblyLoadContext {
			readonly AssemblyDependencyResolver resolver;

			public SdkHostModelLoadContext(string componentPath) : base("SDK HostModel parity", isCollectible: true) =>
				resolver = new AssemblyDependencyResolver(componentPath);

			protected override Assembly? Load(AssemblyName assemblyName) {
				string? path = resolver.ResolveAssemblyToPath(assemblyName);
				return path is null ? null : LoadFromAssemblyPath(path);
			}
		}
	}
}
