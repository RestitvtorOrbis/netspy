// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleDecompilerAnalyzerTests {
		[Fact]
		public void CompressedBundleDecompilesAndNavigatesAcrossMultipleAssembliesLazily() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			var reads = new Dictionary<int, int>();
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle,
				openLogicalRead: entry => {
					reads.TryGetValue(entry.Index, out int count);
					reads[entry.Index] = count + 1;
					return entry.OpenLogicalRead();
				});

			BundleEntryDocument[] entries = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().ToArray();
			Assert.Contains(entries, a => a.Entry.RelativePath == "SingleFile.App.dll");
			Assert.Contains(entries, a => a.Entry.RelativePath == "SingleFile.Dependency.dll");
			Assert.All(entries, a => Assert.True(a.Entry.IsCompressed));
			Assert.All(entries, a => Assert.Null(a.ManagedDocument));
			Assert.Empty(reads);

			var provider = new BundleDocumentNodeProvider();
			DsDocumentNode appNode = provider.Create(null!, null, entries.Single(a =>
				a.Entry.RelativePath == "SingleFile.App.dll"))!;
			var decompileContext = new TestDecompileNodeContext(CreateCSharpDecompiler());
			Assert.True(((IDecompileSelf)appNode).Decompile(decompileContext));
			string decompiled = decompileContext.Output.GetText();
			Assert.Contains("SingleFile.App", decompiled, StringComparison.Ordinal);
			Assert.Contains("Console.WriteLine", decompiled, StringComparison.Ordinal);
			Assert.Contains("BUNDLE_VALUE=", decompiled, StringComparison.Ordinal);
			Assert.DoesNotContain("The decompiler extension wasn't built", decompiled,
				StringComparison.Ordinal);
			Assert.Equal(1, reads[entries.Single(a => a.Entry.RelativePath == "SingleFile.App.dll").Entry.Index]);
			Assert.DoesNotContain(entries, a => a.Entry.RelativePath != "SingleFile.App.dll" &&
				reads.ContainsKey(a.Entry.Index));

			BundleEntryDocument dependency = entries.Single(a =>
				a.Entry.RelativePath == "SingleFile.Dependency.dll");
			BundleModuleDocument dependencyModule = dependency.CreateManagedDocument();
			Assert.NotNull(dependencyModule.ModuleDef);
			Assert.Equal(1, reads[dependency.Entry.Index]);
			Assert.Equal("SingleFile.Dependency", dependencyModule.AssemblyDef!.Name.String);
		}

		[Fact]
		public void AnalyzerMethodNavigationResolvesAReferenceToTheLoadedBundleDependency() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle);
			BundleEntryDocument[] entries = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().ToArray();
			BundleModuleDocument source = entries.Single(a =>
				a.Entry.RelativePath == "SingleFile.App.dll").CreateManagedDocument();
			BundleModuleDocument dependency = entries.Single(a =>
				a.Entry.RelativePath == "SingleFile.Dependency.dll").CreateManagedDocument();
			ModuleDef sourceModule = source.ModuleDef!;

			TypeDef dependencyType = dependency.ModuleDef!.Types.Single(a => a.Name == "BundleValue");
			var target = new MethodDefUser("Navigate", MethodSig.CreateStatic(
				dependency.ModuleDef.CorLibTypes.String),
				dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
				dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) {
				Body = new CilBody(),
			};
			target.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "bundle"));
			target.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			dependencyType.Methods.Add(target);

			TypeDef callerType = new TypeDefUser("Bnd011", "NavigationCaller",
				sourceModule.CorLibTypes.Object.TypeDefOrRef);
			var caller = new MethodDefUser("Call", MethodSig.CreateStatic(
				sourceModule.CorLibTypes.String),
				dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
				dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) {
				Body = new CilBody(),
			};
			caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call,
				sourceModule.Import(target)));
			caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			callerType.Methods.Add(caller);
			sourceModule.Types.Add(callerType);

			Assembly analyzer = LoadExtensionAssembly("dnSpy.Analyzer.x");
			Type nodeType = analyzer.GetType("dnSpy.Analyzer.TreeNodes.MethodUsesNode", true)!;
			object node = Activator.CreateInstance(nodeType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, new object[] { caller }, null)!;
			MethodInfo fetch = nodeType.BaseType!.GetMethod("FetchChildrenInternal",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
			var children = ((IEnumerable)fetch.Invoke(node, new object[] { CancellationToken.None })!)
				.Cast<object>().ToArray();
			Assert.Single(children);
			PropertyInfo member = children[0].GetType().GetProperty("Member",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
			Assert.Same(target, member.GetValue(children[0]));
		}

		[Fact]
		public void EnumeratingCompressedBundleInventoryDoesNotMaterializeUnexpandedEntries() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			var reads = new Dictionary<int, int>();
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle,
				openLogicalRead: entry => {
					reads.TryGetValue(entry.Index, out int count);
					reads[entry.Index] = count + 1;
					return entry.OpenLogicalRead();
				});

			var root = new BundleDocumentNodeProvider().Create(null!, null, document)!;
			var nodes = root.CreateChildren().SelectMany(a => a.CreateChildren()).ToArray();
			foreach (DsDocumentNode node in nodes)
				_ = node.ToString();

			Assert.NotEmpty(nodes);
			Assert.Empty(reads);
			Assert.All(nodes.Select(a => (BundleEntryDocument)((DsDocumentNode)a).Document), a =>
				Assert.Null(a.ManagedDocument));
		}

		static IDecompiler CreateCSharpDecompiler() {
			Assembly assembly = LoadExtensionAssembly("dnSpy.Decompiler.ILSpy.x");
			Type providerType = assembly.GetType(
				"dnSpy.Decompiler.ILSpy.Core.CSharp.DecompilerProvider", true)!;
			object provider = Activator.CreateInstance(providerType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, Array.Empty<object>(), null)!;
			MethodInfo create = providerType.GetMethod("Create",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
			return ((IEnumerable)create.Invoke(provider, null)!).Cast<IDecompiler>()
				.Single(a => a.GenericNameUI == DecompilerConstants.GENERIC_NAMEUI_CSHARP);
		}

		static Assembly LoadExtensionAssembly(string simpleName) {
			try {
				return Assembly.Load(simpleName);
			}
			catch (FileNotFoundException) {
				string filename = simpleName + ".dll";
				DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
				while (directory is not null) {
					string candidate = Path.Combine(directory.FullName, "dnSpy", "dnSpy", "bin",
						"Release", "net10.0-windows", filename);
					if (File.Exists(candidate))
						return Assembly.LoadFrom(candidate);
					directory = directory.Parent;
				}
				throw;
			}
		}

		static string FindCompressedFixture() {
			string? configured = Environment.GetEnvironmentVariable("DNSPY_BUNDLE_FIXTURES");
			var roots = new List<string>();
			if (!string.IsNullOrWhiteSpace(configured))
				roots.AddRange(configured.Split(new[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries));
			roots.Add(Path.Combine(AppContext.BaseDirectory,
				"../../../../TestAssets/SingleFile/Net10/artifacts/net10.0"));
			roots.Add(Path.Combine(Directory.GetCurrentDirectory(),
				"Tests/TestAssets/SingleFile/Net10/artifacts/net10.0"));
			foreach (string root in roots) {
				string candidate = Path.GetFullPath(Path.Combine(root,
					"scd-compressed/publish/SingleFile.App.exe"));
				if (File.Exists(candidate))
					return candidate;
			}
			throw new InvalidOperationException("The generated compressed net10 bundle fixture is missing.");
		}

		sealed class TestDecompileNodeContext : IDecompileNodeContext {
			public TestDecompileNodeContext(IDecompiler decompiler) => Decompiler = decompiler;
			public StringBuilderDecompilerOutput Output { get; } = new StringBuilderDecompilerOutput();
			IDecompilerOutput IDecompileNodeContext.Output => Output;
			public IDocumentWriterService DocumentWriterService => null!;
			public IDecompiler Decompiler { get; }
			public DecompilationContext DecompilationContext { get; } = new DecompilationContext();
			public Microsoft.VisualStudio.Utilities.IContentType? ContentType { get; set; }
			public string? ContentTypeString { get; set; }
			public T UIThread<T>(Func<T> func) => func();
		}
	}
}
