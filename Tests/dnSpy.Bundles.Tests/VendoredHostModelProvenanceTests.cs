// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using dnSpy.Bundles;
using Microsoft.NET.HostModel.AppHost;
using Microsoft.NET.HostModel.Bundle;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class VendoredHostModelProvenanceTests {
		const string LicenseHash = "cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310";
		static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);

		static readonly SourceContract[] Sources = {
			new("AppHost/BinaryUtils.cs", "be076a56df428e620eff8057541cfa4b1a529a63ed4232b38eb01e43aa9712ba"),
			new("AppHost/PEUtils.cs", "623ff627b7b50b6c588e10135bd6e874ff573a4a7eff2bd1aa3f7f2d1fbd00f9", "PEUtils.is-pe-only.patch", "e7eaa2b309b94b37d2c953c4ee6c78467ceeb8f34b2489be8fb341a5bb8ccc2a", "359d146291f35fb2d5ee008dfb9733b27ee3c66903d7eaeeac849307863f4c74", new[] { "55c53154a4d690f359bbb1846cb11ef2d0b7da8215c5b5359a67c9a5b8c2e74c", "cd4422d9b4666f91ae7bf2a912b84eff9829a6d5d02d0994bcd1b7f70182e2c8", "e752471c4e7364f04399bbdd84edca6805d0959cd9d706002ed31ca89a60eb44" }),
			new("AppHost/PlaceHolderNotFoundInAppHostException.cs", "95fe8b7f721ab7b48080755924647e81fc1543e2ff32dea90eaafe9bac23d7a6", "PlaceHolderException.standalone.patch", "a01f5864eaf51a75d1189cb114b77ba772874f9ba13dc5483c8d98f574c05f09", "b0345e7db623c415bb89f7890c5d984a00cbf8809bfb34420635900033f69478", new[] { "819c8da601b221766f22607f0fe145beaa1069a641a5a1eca1fd3c0d4cc71124" }),
			new("AppHost/RetryUtil.cs", "85469a72f407d9c1cc5282ab4b60888885e6090380022f18ec225e8235ab1a3c"),
			new("Bundle/BundleOptions.cs", "53b5a78b5d0d4c80e18739904501c0b0ec8614ea43784979d7ab1f085336ac00"),
			new("Bundle/Bundler.cs", "611d3da3eb4d25ef9ae411fb5d626f8d55852bedd759d9e0ec3c8ad7b4713b77", "Bundler.windows-x64.patch", "06f6571e02c6fe305e7730c5678bd72366f86ba46d7124afc8155afa2506452c", "3150fc1a2598ffb05c0d10e72e07d7a088e1ddfd38e762ea8d7582378bc8acc4", new[] { "39360ffd03a1c9e0621cc25bdfd655f637d1cdb3b80b669b01bef28ae9fdce19", "9f55e68464dc573ab3cd7ca504a0c952fe5c2c495a03966405d1ccd44efdf48d", "200801f2b8a769ad47a1f3a1a32015cd73e8ab1ff193f22611b97fb60a03692e", "d823b960cd30b8116647826164d6fff3387601b2885034d2394d5fced69e051e", "1bc809a9105e50182f16869d457c735371aad2809818e8bb7f791d5524cacd43", "35b1fbcca0bdcd9383a5acd3e465e1506a9baebd51a4414d8ab3e1bae2e4b969", "f812bee891125cd229286dbacfd15b437c3a41789329e99415525366126fef83" }),
			new("Bundle/FileEntry.cs", "02419b4d1f18ccd389c8a2145a9e660a002c17294596837b77a280d34de4c848"),
			new("Bundle/FileSpec.cs", "1a9d96b1d6b3b78c02a31a5ac545ebb8f326f631fab1af7c1a2824abd158379d"),
			new("Bundle/FileType.cs", "bb378074145d183576a47169c3651d605eed70f454b18e1c8263b69e83419fae"),
			new("Bundle/Manifest.cs", "b45defad7f4c3545995b7f66af234eab1edbf524f900df8b0784fb35ab6f364f"),
			new("Bundle/TargetInfo.cs", "83f5f6a56a41926295e082400fcb099296b45658ddd7918b48b6cee7f90c72e2", "TargetInfo.windows-x64.patch", "148d9edaf4b7f7dc86be18d2a6df4e5b639a07e7f3c2018934ed45f239d072bf", "36c60c4ae9e50262fe4ee4d3e9caf12b70f206c9fd3db2fa94c9f1794943dd57", new[] { "47f6785d3a377a17e316e8c235660a6bcc78f40246ce35055315d79472b959cb", "107ed3d3a5cc9f33e39f08e1ed745182e2d01b348877cc2d726b999dbd1823f1", "cb21846803fb35cfa366a18fc9f904021b977fe1e982246ecbcbb41584502db1" }),
			new("Bundle/Trace.cs", "a0c2d63a9edc24c792c1b35ca6eaa5e2a607a8f9b4f3eefae18dde13a0e5e4f0"),
			new("HostModelUtils.cs", "9f60efce1dfc43ea1f1fb96c8cdf537dcac66cc16e7a35e96a6a55f558584d60", "HostModelUtils.windows-x64.patch", "055a07b710ed9f4311b15ed240e22869f2a22822ed4c16ef92c0a6b65a02ab99", "ed5c5fd4b8a29828b7c1f6021fbc1e7cad3d090461474ae860781f4cb952e377", new[] { "669b505e13d0d8a56ffd9cf566539442196e6bba0ec6f35ff644e62ac898262d" }),
			new("PEOffsets.cs", "b45684bf36c07823b480df864660694065b518be2cc34008027f83cdfebf4497"),
			new("Utils/Base64Url.cs", "729a814ffcff8bb97b6ada9408be15687300839b7ce27256e5bbdac737b3372b"),
		};

		[Fact]
		public void PinnedSourcesPatchesHunksResultsAndLicenseMatch() {
			string vendorRoot = GetVendorRoot();
			Assert.Equal(LicenseHash, HashFile(Path.Combine(vendorRoot, "LICENSE.TXT")));
			Assert.Equal(15, Sources.Length);

			foreach (SourceContract source in Sources) {
				string sourcePath = Path.Combine(vendorRoot, source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
				if (source.PatchName is null) {
					Assert.Equal(source.UpstreamHash, HashFile(sourcePath));
					continue;
				}

				string patchPath = Path.Combine(vendorRoot, "UPSTREAM-PATCHES", source.PatchName);
				byte[] patchBytes = File.ReadAllBytes(patchPath);
				Assert.DoesNotContain((byte)'\r', patchBytes);
				Assert.Equal(source.PatchHash, Hash(patchBytes));
				Assert.Equal(source.ResultHash, HashFile(sourcePath));
				Assert.Equal(source.HunkHashes, ReadHunks(patchBytes).Select(Hash));
				Assert.Equal(source.UpstreamHash, Hash(ReversePatch(File.ReadAllBytes(sourcePath), patchBytes)));
			}
		}

		[Fact]
		public void ProjectCompilesExactlyTheApprovedClosureAndNoOmittedException() {
			string vendorRoot = GetVendorRoot();
			XDocument project = XDocument.Load(Path.Combine(vendorRoot, "Microsoft.NET.HostModel.Bundle.csproj"));
			string[] compileItems = project.Descendants("Compile")
				.Select(element => element.Attribute("Include")?.Value.Replace('\\', '/'))
				.Where(value => value is not null).Cast<string>().ToArray();
			Assert.Equal(Sources.Select(source => source.RelativePath), compileItems);
			Assert.Equal("false", project.Descendants("EnableDefaultCompileItems").Single().Value);
			Assert.NotNull(project.Descendants("Target").Single(element =>
				element.Attribute("Name")?.Value == "AssertHostModelCompileItems"));

			Assembly assembly = typeof(Bundler).Assembly;
			Assert.NotNull(assembly.GetType("Microsoft.NET.HostModel.PEOffsets", throwOnError: false));
			Assert.Equal(typeof(Exception), typeof(PlaceHolderNotFoundInAppHostException).BaseType);
			string allSources = String.Join("\n", Sources.Select(source => File.ReadAllText(
				Path.Combine(vendorRoot, source.RelativePath.Replace('/', Path.DirectorySeparatorChar)))));
			Assert.DoesNotContain("AppHostUpdateException", allSources, StringComparison.Ordinal);
			Assert.DoesNotContain("AppHostNotCUIException", allSources, StringComparison.Ordinal);
			Assert.DoesNotContain("AppHostNotPEFileException", allSources, StringComparison.Ordinal);
		}

		[Fact]
		public void LockedDependencyGraphIsExact() {
			using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(GetVendorRoot(), "packages.lock.json")));
			JsonElement dependencies = document.RootElement.GetProperty("dependencies");
			Assert.Empty(dependencies.GetProperty("net10.0").EnumerateObject());
			JsonElement net48 = dependencies.GetProperty(".NETFramework,Version=v4.8");
			string[] expected = { "System.Buffers", "System.Collections.Immutable", "System.Memory", "System.Numerics.Vectors", "System.Reflection.Metadata", "System.Runtime.CompilerServices.Unsafe" };
			Assert.Equal(expected, net48.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
			AssertPackage(net48, "System.Memory", "Direct", "4.6.3");
			AssertPackage(net48, "System.Reflection.Metadata", "Direct", "10.0.0");
			AssertPackage(net48, "System.Collections.Immutable", "Transitive", "10.0.0");
			foreach (JsonProperty package in net48.EnumerateObject())
				Assert.False(String.IsNullOrWhiteSpace(package.Value.GetProperty("contentHash").GetString()));
		}

		[Fact]
		public void VendoredSubsetProducesV6BundleThatCoreParserReopens() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item => item.Variant == "fdd-uncompressed");
			string appHost = Path.Combine(fixture.Root, "obj", "App", "Release", "net10.0", "win-x64", "apphost.exe");
			Assert.True(File.Exists(appHost), appHost);
			string outputDirectory = Path.Combine(Path.GetTempPath(), "dnSpy-hostmodel-provenance-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(outputDirectory);
			try {
				const string hostName = "ProvenanceHost.exe";
				var bundler = new Bundler(hostName, outputDirectory, BundleOptions.EnableCompression,
					System.Runtime.InteropServices.OSPlatform.Windows,
					System.Runtime.InteropServices.Architecture.X64, new Version(10, 0),
					appAssemblyName: "SingleFile.App", macosCodesign: false);
				string output = bundler.GenerateBundle(new[] {
					new FileSpec(appHost, hostName),
					new FileSpec(fixture.BuildMainAssemblyPath, "SingleFile.App.dll"),
					new FileSpec(fixture.BuildDependencyAssemblyPath, "SingleFile.Dependency.dll"),
				});
				BundleOpenResult result = new BundleReader().Open(output);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				using BundleFile bundle = result.Bundle!;
				Assert.Equal(6u, bundle.Manifest.MajorVersion);
				Assert.Equal(new[] { "SingleFile.App.dll", "SingleFile.Dependency.dll" }, bundle.Entries.Select(entry => entry.RelativePath));
				Assert.Contains(bundle.Entries, entry => entry.IsCompressed);
			}
			finally {
				Directory.Delete(outputDirectory, recursive: true);
			}
		}

		static void AssertPackage(JsonElement framework, string name, string type, string version) {
			JsonElement package = framework.GetProperty(name);
			Assert.Equal(type, package.GetProperty("type").GetString());
			Assert.Equal(version, package.GetProperty("resolved").GetString());
		}

		static IReadOnlyList<byte[]> ReadHunks(byte[] patchBytes) {
			string patch = Encoding.UTF8.GetString(patchBytes);
			int first = patch.IndexOf("@@ ", StringComparison.Ordinal);
			Assert.True(first >= 0);
			var hunks = new List<byte[]>();
			while (first >= 0) {
				int next = patch.IndexOf("@@ ", first + 3, StringComparison.Ordinal);
				string hunk = next < 0 ? patch[first..] : patch[first..next];
				hunks.Add(Encoding.UTF8.GetBytes(hunk));
				first = next;
			}
			return hunks;
		}

		static byte[] ReversePatch(byte[] resultBytes, byte[] patchBytes) {
			string[] result = SplitLines(resultBytes);
			string[] patch = SplitLines(patchBytes);
			var original = new List<string>();
			int resultIndex = 0;
			for (int patchIndex = 2; patchIndex < patch.Length;) {
				Match match = HunkHeader.Match(patch[patchIndex]);
				Assert.True(match.Success, patch[patchIndex]);
				int newStart = Int32.Parse(match.Groups[3].Value);
				while (resultIndex < newStart - 1)
					original.Add(result[resultIndex++]);
				patchIndex++;
				while (patchIndex < patch.Length && !patch[patchIndex].StartsWith("@@ ", StringComparison.Ordinal)) {
					string line = patch[patchIndex++];
					Assert.NotEmpty(line);
					switch (line[0]) {
					case ' ':
						Assert.Equal(line[1..], result[resultIndex]);
						original.Add(line[1..]);
						resultIndex++;
						break;
					case '+':
						Assert.Equal(line[1..], result[resultIndex++]);
						break;
					case '-':
						original.Add(line[1..]);
						break;
					default:
						throw new InvalidDataException("Unsupported unified-diff line: " + line);
					}
				}
			}
			while (resultIndex < result.Length)
				original.Add(result[resultIndex++]);
			return Encoding.UTF8.GetBytes(String.Join("\n", original) + "\n");
		}

		static string[] SplitLines(byte[] bytes) {
			string text = Encoding.UTF8.GetString(bytes);
			Assert.DoesNotContain('\r', text);
			Assert.EndsWith("\n", text, StringComparison.Ordinal);
			return text[..^1].Split('\n');
		}

		static string HashFile(string path) => Hash(File.ReadAllBytes(path));
		static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

		sealed record SourceContract(string RelativePath, string UpstreamHash, string? PatchName = null,
			string? PatchHash = null, string? ResultHash = null, string[]? HunkHashes = null);
	}
}
