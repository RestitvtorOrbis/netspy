// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using dnSpy.Properties;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Focused invariants for the visible netSpy identity and branding seams.</summary>
	public sealed class BrandingTests {
		static Assembly ProductAssembly => typeof(dnSpy_Resources).Assembly;

		[Fact]
		public void ConstantsKeepPresentationAndCompatibilityIdentitySeparate() {
			Type constants = ProductAssembly.GetType("dnSpy.MainApp.Constants", throwOnError: true)!;
			Assert.Equal("netSpy", GetConstant(constants, "AppName"));
			Assert.Equal("dnSpy", GetConstant(constants, "DnSpy"));
			Assert.Equal("dnSpy", GetConstant(constants, "DnSpyFile"));
			Assert.Equal("dnSpy", ProductAssembly.GetName().Name);
		}

		[Fact]
		public void ProductAssemblyHasTheApprovedVisibleMetadata() {
			Assert.Equal("dnSpy", ProductAssembly.GetName().Name);
			Assert.Equal("netSpy", ProductAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
			Assert.Equal("netSpy", ProductAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
			Assert.Equal("netSpy contributors", ProductAssembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
			Assert.Equal("netSpy .NET assembly editor, debugger, and single-file bundle explorer",
				ProductAssembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);
		}

		[Fact]
		public void NeutralResourcesKeepProductContextAndUpstreamProvenance() {
			var expected = new Dictionary<string, string> {
				["LoadingDnSpyPleaseWait"] = "Loading netSpy. Please wait...",
				["ExplorerOpenWithDnSpy"] = "Open with netSpy",
				["LanguageSwitchMessage"] = "You must restart netSpy for the language change to take effect.",
				["About_Menu"] = "_About netSpy",
				["About_TabTitle"] = "About netSpy",
				["AboutScreen_Description"] = "A dnSpyEx-based .NET assembly editor, debugger, and single-file bundle explorer.",
				["AboutScreen_LicenseInfo"] = "netSpy is free software licensed under GPLv3.",
				["AboutScreen_Attribution"] = "Based on dnSpyEx and dnSpy; original copyright and contributor credits follow.",
				["AboutScreen_CheckForUpdates"] = "Check for upstream dnSpyEx updates",
				["AboutScreen_NewUpdateAvailable"] = "A new upstream dnSpyEx release is available: {0}. Do you want to open its download page?",
				["AboutScreen_RunningLatestVersion"] = "You are running a version based on the latest upstream dnSpyEx release.",
				["InfoBar_NewUpdateAvailable"] = "A new upstream dnSpyEx release is available: {0}",
				["About_LatestRelease"] = "Latest _Upstream dnSpyEx Release",
				["About_Issues"] = "Upstream dnSpyEx _Issues",
				["About_Wiki"] = "Upstream dnSpyEx _Wiki",
				["About_SourceCode"] = "Upstream dnSpyEx _Source Code",
			};

			foreach (var item in expected)
				Assert.Equal(item.Value, dnSpy_Resources.ResourceManager.GetString(item.Key, CultureInfo.InvariantCulture));
		}

		[Fact]
		public void SingleInstanceSourceKeepsWireHeaderAndUsesVisibleTitleOnlyForDiscovery() {
			string constants = ReadSource("dnSpy", "dnSpy", "MainApp", "Constants.cs");
			string app = ReadSource("dnSpy", "dnSpy", "MainApp", "App.xaml.cs");

			Assert.Contains("public const string AppName = \"netSpy\";", constants, StringComparison.Ordinal);
			Assert.Contains("public const string DnSpy = \"dnSpy\";", constants, StringComparison.Ordinal);
			Assert.Contains("const string COPYDATASTRUCT_HEADER = Constants.DnSpy", app, StringComparison.Ordinal);
			Assert.Contains("sb.ToString().StartsWith(Constants.AppName + \" \", StringComparison.Ordinal)", app, StringComparison.Ordinal);
			Assert.Contains("if (args[0] != COPYDATASTRUCT_HEADER)", app, StringComparison.Ordinal);
			Assert.DoesNotContain("COPYDATASTRUCT_HEADER = Constants.AppName", app, StringComparison.Ordinal);
		}

		[Fact]
		public void VisualAndCaptionSourcesKeepTheApprovedStructuralSeams() {
			string mainWindow = ReadSource("dnSpy", "dnSpy", "MainApp", "MainWindow.xaml");
			string loader = ReadSource("dnSpy", "dnSpy", "MainApp", "DsLoaderControl.xaml");
			string about = ReadSource("dnSpy", "dnSpy", "MainApp", "AboutScreen.cs");
			string mark = ReadSource("dnSpy", "dnSpy", "MainApp", "BrandMark.xaml");
			string markCode = ReadSource("dnSpy", "dnSpy", "MainApp", "BrandMark.xaml.cs");
			string svg = ReadSource("dnSpy", "dnSpy", "Branding", "netSpy-logo.svg");
			string template = ReadSource("dnSpy", "dnSpy", "Themes", "wpf.styles.templates.xaml");
			string appProject = ReadSource("dnSpy", "dnSpy", "dnSpy.csproj");
			string x86Project = ReadSource("dnSpy", "dnSpy-x86", "dnSpy-x86.csproj");

			Assert.DoesNotContain("SystemMenuImage=", mainWindow, StringComparison.Ordinal);
			Assert.DoesNotContain("DsImages.Assembly", mainWindow, StringComparison.Ordinal);
			Assert.Contains("<ds:DecorativeAccent", loader, StringComparison.Ordinal);
			Assert.Contains("new DecorativeAccent", about, StringComparison.Ordinal);
			Assert.Contains("Focusable=\"False\"", loader, StringComparison.Ordinal);
			Assert.Contains("IsHitTestVisible=\"False\"", loader, StringComparison.Ordinal);
			Assert.Contains("Focusable = false", about, StringComparison.Ordinal);
			Assert.Contains("IsHitTestVisible = false", about, StringComparison.Ordinal);
			Assert.Contains("Viewbox", mark, StringComparison.Ordinal);
			foreach (string value in new[] { "#FF111827", "#FF22D3EE", "#FFA78BFA", "#FFF8FAFC", "14,45 14,25 32,14 50,25 50,45" })
				Assert.Contains(value, mark, StringComparison.Ordinal);
			foreach (string value in new[] { "SPDX-License-Identifier: GPL-3.0-or-later", "#111827", "#22D3EE", "#A78BFA", "#F8FAFC", "14,45 14,25 32,14 50,25 50,45" })
				Assert.Contains(value, svg, StringComparison.Ordinal);
			Assert.Contains("Images\\netSpy.ico", appProject, StringComparison.Ordinal);
			Assert.Contains("..\\dnSpy\\Images\\netSpy.ico", x86Project, StringComparison.Ordinal);
			Assert.DoesNotContain("Images\\dnSpy.ico", appProject, StringComparison.Ordinal);
			Assert.DoesNotContain("Images\\dnSpy-x86.ico", x86Project, StringComparison.Ordinal);

			Assert.Contains("<Setter Property=\"Icon\" Value=\"../Images/netSpy.ico\"/>", template, StringComparison.Ordinal);
			Assert.Contains("x:Name=\"nativeSystemMenuImage\"", template, StringComparison.Ordinal);
			Assert.Contains("Path=Icon", template, StringComparison.Ordinal);
			Assert.Contains("x:Name=\"referencedSystemMenuImage\"", template, StringComparison.Ordinal);
			Assert.Contains("Path=SystemMenuImage}", template, StringComparison.Ordinal);
			Assert.Contains("Path=SystemMenuImage.IsDefault}", template, StringComparison.Ordinal);
			Assert.Contains("TargetName=\"nativeSystemMenuImage\" Property=\"Visibility\" Value=\"Visible\"", template, StringComparison.Ordinal);
			Assert.Contains("TargetName=\"referencedSystemMenuImage\" Property=\"Visibility\" Value=\"Collapsed\"", template, StringComparison.Ordinal);

			Assert.Contains("sealed class DecorativeAccent : Border", markCode, StringComparison.Ordinal);
			Assert.Contains("OnCreateAutomationPeer()", markCode, StringComparison.Ordinal);
			Assert.Contains("sealed class DecorativeAccentAutomationPeer : FrameworkElementAutomationPeer", markCode, StringComparison.Ordinal);
			Assert.Contains("IsControlElementCore() => false", markCode, StringComparison.Ordinal);
			Assert.Contains("IsContentElementCore() => false", markCode, StringComparison.Ordinal);
			foreach (string source in new[] { loader, about, markCode }) {
				Assert.DoesNotContain("AutomationProperties.IsInAccessibleTree", source, StringComparison.Ordinal);
				Assert.DoesNotContain("AutomationProperties.AccessibilityView", source, StringComparison.Ordinal);
				Assert.DoesNotContain("SetAccessibilityView", source, StringComparison.Ordinal);
			}
		}

		[Fact]
		public void NativeIconContainsRequiredStable32BitFrames() {
			byte[] icon = File.ReadAllBytes(Path.Combine(RepoRoot, "dnSpy", "dnSpy", "Images", "netSpy.ico"));
			Assert.True(icon.Length >= 6);
			Assert.Equal(0, ReadUInt16(icon, 0));
			Assert.Equal(1, ReadUInt16(icon, 2));
			ushort count = ReadUInt16(icon, 4);
			Assert.True(count >= 6);
			var sizes = new HashSet<int>();
			for (int i = 0; i < count; i++) {
				int offset = 6 + i * 16;
				Assert.True(icon.Length >= offset + 16);
				int width = icon[offset] == 0 ? 256 : icon[offset];
				int height = icon[offset + 1] == 0 ? 256 : icon[offset + 1];
				Assert.Equal(width, height);
				sizes.Add(width);
				Assert.Equal(1, ReadUInt16(icon, offset + 4));
				Assert.Equal(32, ReadUInt16(icon, offset + 6));
				uint size = ReadUInt32(icon, offset + 8);
				uint imageOffset = ReadUInt32(icon, offset + 12);
				Assert.True(imageOffset <= icon.Length && size <= icon.Length - imageOffset);
				Assert.True(size >= 8 && icon[imageOffset] == 0x89 && icon[imageOffset + 1] == (byte)'P' &&
					icon[imageOffset + 2] == (byte)'N' && icon[imageOffset + 3] == (byte)'G');
			}

			Assert.True(new[] { 16, 24, 32, 48, 64, 256 }.All(sizes.Contains));
		}

		[Fact]
		public void ReadmeLeadsWithNetSpyAndExplicitUpstreamLineage() {
			string readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
			const string opening = "# netSpy\n\nnetSpy is a dnSpyEx-based debugger and .NET assembly editor with support for inspecting and editing official .NET single-file bundles. It preserves the dnSpy editing and debugging workflow while adding a dedicated bundle subsystem.\n\ndnSpyEx is the upstream base for netSpy: https://github.com/dnSpyEx/dnSpy.";
			Assert.StartsWith(opening, readme, StringComparison.Ordinal);
			Assert.Contains("https://github.com/dnSpyEx/dnSpy/releases", readme, StringComparison.Ordinal);
			Assert.Contains("./build.ps1 -NoMsbuild", readme, StringComparison.Ordinal);
		}

		static string GetConstant(Type type, string name) =>
			(string)type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetRawConstantValue()!;

		static ushort ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
		static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

		static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

		static string RepoRoot {
			get {
				DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
				while (directory is not null) {
					if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
						Directory.Exists(Path.Combine(directory.FullName, "dnSpy")))
						return directory.FullName;
					directory = directory.Parent;
				}
				throw new DirectoryNotFoundException("Could not locate the repository root from the test assembly.");
			}
		}
	}
}
