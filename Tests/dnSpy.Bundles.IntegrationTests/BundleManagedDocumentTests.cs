// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using Microsoft.VisualStudio.Utilities;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleManagedDocumentTests {
		[Fact]
		public void CompressedFixtureActivatesExactlyOneManagedEntryLazily() {
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

			BundleFolderDocument folder = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies);
			BundleEntryDocument[] entries = folder.Children.Cast<BundleEntryDocument>().ToArray();
			Assert.True(entries.Length >= 2);
			Assert.All(entries, entry => Assert.Null(entry.ManagedDocument));

			BundleEntryDocument selected = entries.Single(entry =>
				entry.Entry.RelativePath == "SingleFile.App.dll");
			Assert.True(selected.Entry.IsCompressed);
			BundleModuleDocument module = selected.CreateManagedDocument();
			Assert.Same(module, selected.ManagedDocument);
			Assert.Equal(1, reads[selected.Entry.Index]);
			Assert.All(entries.Where(entry => !ReferenceEquals(entry, selected)), entry =>
				Assert.False(reads.ContainsKey(entry.Entry.Index)));
			Assert.IsType<dnlib.DotNet.ModuleDefMD>(module.ModuleDef);
			Assert.Equal(string.Empty, module.ModuleDef!.Location);
			Assert.Equal(filename + "!/SingleFile.App.dll", module.Filename);
			Assert.Equal("SingleFile.App.dll", module.BundleRelativePath);
			Assert.Equal(BundleDocumentKey.Module(filename, "SingleFile.App.dll"), module.Key);
			Assert.Same(module.ModuleDef, selected.ModuleDef);
		}

		[Fact]
		public void ProductionProviderUsesLazyAssemblyNodeAndNormalModuleChildPath() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>()
				.Single(a => a.Entry.RelativePath == "SingleFile.App.dll");

			var provider = new BundleDocumentNodeProvider();
			DsDocumentNode? node = provider.Create(null!, null, entry);
			Assert.NotNull(node);
			Assert.IsNotAssignableFrom<AssemblyDocumentNode>(node);
			Assert.Equal("BundleManagedEntryDocumentNode", node.GetType().Name);
			Assert.Null(entry.ManagedDocument);

			// The actual metadata-node expansion invokes the lazy entry document and creates the
			// annotated assembly wrapper. In a real tree view that wrapper's child is passed to the
			// default provider as ModuleDocumentNodeImpl.
			DsDocumentNode[] children = node!.CreateChildren().Cast<DsDocumentNode>().ToArray();
			Assert.Single(children);
			Assert.IsAssignableFrom<AssemblyDocumentNode>(children[0]);
			Assert.Equal("BundleAssemblyDocumentNode", children[0].GetType().Name);
			Assert.NotNull(entry.ManagedDocument);
		}

		[Fact]
		public void ProductionTreeContextKeepsMetadataColdAndRoutesSelectedEntryToDefaultModuleNode() {
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
			ProductionTreeContext tree = ProductionTreeContext.Create();
			DsDocumentNode root = tree.View.CreateNode(null, document);
			DsDocumentNode[] entryNodes = root.CreateChildren().Cast<DsDocumentNode>()
				.SelectMany(a => a.CreateChildren()).Cast<DsDocumentNode>().ToArray();
			BundleEntryDocument[] entries = entryNodes.Select(a => (BundleEntryDocument)a.Document).ToArray();
			Assert.All(entries, entry => Assert.IsNotAssignableFrom<IDsDotNetDocument>(entry));
			Assert.All(entries, entry => Assert.Null(entry.ManagedDocument));
			Assert.All(entries, entry => Assert.False(reads.ContainsKey(entry.Entry.Index)));

			DsDocumentNode selectedNode = entryNodes.Single(a =>
				((BundleEntryDocument)a.Document).Entry.RelativePath == "SingleFile.App.dll");
			DsDocumentNode assemblyNode = Assert.Single(selectedNode.CreateChildren().Cast<DsDocumentNode>());
			Assert.IsAssignableFrom<AssemblyDocumentNode>(assemblyNode);
			Assert.Equal("BundleAssemblyDocumentNode", assemblyNode.GetType().Name);
			DsDocumentNode moduleNode = Assert.Single(assemblyNode.CreateChildren().Cast<DsDocumentNode>());
			Assert.IsAssignableFrom<ModuleDocumentNode>(moduleNode);
			Assert.Equal("ModuleDocumentNodeImpl", moduleNode.GetType().Name);
			Assert.NotNull(moduleNode.Document.ModuleDef);
			Assert.IsAssignableFrom<IDsBundleEntryDocument>(((BundleEntryDocument)entryNodes
				.Single(a => ((BundleEntryDocument)a.Document).Entry.RelativePath == "SingleFile.App.dll")
				.Document).CreateManagedDocument());
			Assert.Equal(1, reads[((BundleEntryDocument)selectedNode.Document).Entry.Index]);
			Assert.All(entries.Where(entry => !ReferenceEquals(entry, selectedNode.Document)), entry =>
				Assert.False(reads.ContainsKey(entry.Entry.Index)));
		}

		[Fact]
		public void ValidNetmoduleUsesOrdinaryModuleNodeAndDecompilesWithoutAssembly() {
			byte[] fixture = CreateNetmoduleFixture();
			var entry = new BundleEntry(0, 0, fixture.LongLength, 0, 1,
				BundleFileType.Assembly, "tools/Selected.netmodule");
			var bundle = new BundleFile("netmodule-bundle.exe", fixture.LongLength,
				128, 160, new BundleManifest(6, 0, "test"), new[] { entry });
			int reads = 0;
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(bundle.Filename), bundle,
				openLogicalRead: _ => {
					reads++;
					return new MemoryStream(fixture, writable: false);
				});
			ProductionTreeContext tree = ProductionTreeContext.Create();
			DsDocumentNode root = tree.View.CreateNode(null, document);
			DsDocumentNode selectedNode = Assert.Single(root.CreateChildren().Cast<DsDocumentNode>()
				.SelectMany(a => a.CreateChildren()).Cast<DsDocumentNode>());
			Assert.IsNotAssignableFrom<IDsDotNetDocument>(selectedNode.Document);

			DsDocumentNode moduleNode = Assert.Single(selectedNode.CreateChildren().Cast<DsDocumentNode>());
			Assert.IsAssignableFrom<ModuleDocumentNode>(moduleNode);
			Assert.Equal("ModuleDocumentNodeImpl", moduleNode.GetType().Name);
			Assert.NotNull(moduleNode.Document.ModuleDef);
			Assert.Null(moduleNode.Document.ModuleDef!.Assembly);
			Assert.Equal(1, reads);

			var context = new FailingDecompileNodeContext(CreateDummyDecompiler());
			Assert.True(((IDecompileSelf)selectedNode).Decompile(context));
			Assert.Equal(1, reads);
		}

		[Fact]
		public void InvalidManagedPayloadProducesVisibleActualTreeDiagnostic() {
			var entry = new BundleEntry(0, 0, 4, 0, 1, BundleFileType.Assembly, "broken.dll");
			var bundle = new BundleFile("broken-bundle.exe", 4,
				128, 160, new BundleManifest(6, 0, "test"), new[] { entry });
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(bundle.Filename), bundle,
				openLogicalRead: _ => new MemoryStream(new byte[4]));
			var node = new BundleDocumentNodeProvider().Create(null!, null, document.Children
				.Cast<BundleFolderDocument>().Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Single())!;
			DsDocumentNode[] errorChildren = node.CreateChildren().Cast<DsDocumentNode>().ToArray();
			Assert.Single(errorChildren);
			Assert.Equal("BundleEntryErrorDocumentNode", errorChildren[0].GetType().Name);
			var context = new FailingDecompileNodeContext();

			Assert.True(((IDecompileSelf)node).Decompile(context));
			Assert.Contains("Unable to load managed bundle entry", context.Output.GetText(), StringComparison.Ordinal);
			Assert.Contains("broken.dll", context.Output.GetText(), StringComparison.Ordinal);
		}

		[Fact]
		public void BundleKeysKeepKindsAndCaseDistinctEntryPathsSeparate() {
			string source = Path.Combine(Path.GetTempPath(), "bundle.exe");
			BundleDocumentKey folder = BundleDocumentKey.Folder(source, "Assemblies");
			BundleDocumentKey entry = BundleDocumentKey.Entry(source, "Assemblies");
			BundleDocumentKey upper = BundleDocumentKey.Entry(source, "App.dll");
			BundleDocumentKey lower = BundleDocumentKey.Entry(source, "app.dll");
			BundleDocumentKey slash = BundleDocumentKey.Entry(source, "dir\\app.dll");
			BundleDocumentKey slashNormalized = BundleDocumentKey.Entry(source, "dir/app.dll");

			Assert.NotEqual(folder, entry);
			Assert.NotEqual(upper, lower);
			Assert.Equal(slash, slashNormalized);
			Assert.NotEqual(BundleDocumentKey.Entry(source, "app.dll"),
				BundleDocumentKey.Module(source, "app.dll"));
		}

		[Fact]
		public void RelativeInputPreservesSerializedNameButCanonicalizesBundleSourceAndChildNames() {
			string absolute = Path.Combine(Path.GetTempPath(), "relative-bundle.exe");
			string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), absolute);
			var bundle = new BundleFile(relative, 0, 128, 160,
				new BundleManifest(6, 0, "test"), Array.Empty<BundleEntry>());
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(relative), bundle);
			Assert.Equal(Path.GetFullPath(relative), document.SourceFilename);
			Assert.Equal(relative, document.SerializedDocument!.Value.Name);
			Assert.StartsWith(document.SourceFilename + "!/Assemblies", document.Children
				.Cast<BundleFolderDocument>().Single(a => a.Kind == BundleFolderKind.Assemblies).Filename,
				StringComparison.Ordinal);
		}

		[Fact]
		public void RootOwnsModuleLifetimeAndRepeatedDisposalIsSafe() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>()
				.Single(a => a.Entry.RelativePath == "SingleFile.App.dll");
			BundleModuleDocument module = entry.CreateManagedDocument();
			DsDotNetDocument wrapper = module.CreateAssemblyDocument();
			wrapper.Dispose();
			Assert.NotNull(module.ModuleDef!.Assembly);
			document.Dispose();
			document.Dispose();
			Assert.Throws<ObjectDisposedException>(() => entry.CreateManagedDocument());
		}

		[Fact]
		public void VerifiedModuleGetsNormalAssemblyWrapperAndModuleNodeShape() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().Single(a => a.Entry.RelativePath == "SingleFile.App.dll");
			using BundleModuleDocument module = entry.CreateManagedDocument();
			DsDotNetDocument assemblyDocument = module.CreateAssemblyDocument();
			try {
				var bundleProvider = new BundleDocumentNodeProvider();
				DsDocumentNode? assemblyNode = bundleProvider.Create(null!, null, assemblyDocument);
				Assert.NotNull(assemblyNode);
				Assert.Equal("BundleAssemblyDocumentNode", assemblyNode.GetType().Name);
				Assert.IsAssignableFrom<AssemblyDocumentNode>(assemblyNode);

				// The product's default provider still receives the wrapper's child as a module
				// because the bundle provider only claims the annotated assembly wrapper.
				Assembly product = Assembly.Load("dnSpy");
				Type providerType = product.GetType("dnSpy.Documents.TreeView.DefaultDsDocumentNodeProvider",
					throwOnError: true)!;
				var defaultProvider = (IDsDocumentNodeProvider)Activator.CreateInstance(providerType,
					nonPublic: true)!;
				DsDocumentNode? moduleNode = defaultProvider.Create(null!, assemblyNode, module);
				Assert.NotNull(moduleNode);
				Assert.Equal("ModuleDocumentNodeImpl", moduleNode.GetType().Name);
				Assert.IsAssignableFrom<ModuleDocumentNode>(moduleNode);
			}
			finally {
				assemblyDocument.Dispose();
			}
		}

		static string FindCompressedFixture() {
			string? configured = Environment.GetEnvironmentVariable("DNSPY_BUNDLE_FIXTURES");
			var roots = new List<string>();
			if (!string.IsNullOrWhiteSpace(configured))
				roots.AddRange(configured.Split(new[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries));
			roots.Add(Path.Combine(AppContext.BaseDirectory, "../../../../TestAssets/SingleFile/Net10/artifacts/net10.0"));
			roots.Add(Path.Combine(Directory.GetCurrentDirectory(), "Tests/TestAssets/SingleFile/Net10/artifacts/net10.0"));
			foreach (string root in roots) {
				string candidate = Path.GetFullPath(Path.Combine(root, "scd-compressed/publish/SingleFile.App.exe"));
				if (File.Exists(candidate))
					return candidate;
			}
			throw new InvalidOperationException("The generated compressed net10 bundle fixture is missing.");
		}

		sealed class FailingDecompileNodeContext : IDecompileNodeContext {
			public FailingDecompileNodeContext(IDecompiler? decompiler = null) => Decompiler = decompiler!;

			public StringBuilderDecompilerOutput Output { get; } = new StringBuilderDecompilerOutput();
			IDecompilerOutput IDecompileNodeContext.Output => Output;
			public IDocumentWriterService DocumentWriterService => null!;
			public IDecompiler Decompiler { get; }
			public DecompilationContext DecompilationContext { get; } = new DecompilationContext();
			public IContentType? ContentType { get; set; }
			public string? ContentTypeString { get; set; }
			public T UIThread<T>(Func<T> func) => func();
		}

		sealed class ProductionTreeContext {
			public IDocumentTreeView View { get; }

			ProductionTreeContext(IDocumentTreeView view) => View = view;

			public static ProductionTreeContext Create() {
				IDocumentTreeView view = DispatchProxy.Create<IDocumentTreeView, TreeViewProxy>();
				var viewProxy = (TreeViewProxy)(object)view;
				IDocumentTreeNodeDataContext context =
					DispatchProxy.Create<IDocumentTreeNodeDataContext, NodeContextProxy>();
				((NodeContextProxy)(object)context).View = view;
				viewProxy.Initialize(context);
				return new ProductionTreeContext(view);
			}
		}

		public sealed class NodeContextProxy : DispatchProxy {
			public IDocumentTreeView View { get; set; } = null!;

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_DocumentTreeView")
					return View;
				return DefaultValue(targetMethod?.ReturnType);
			}
		}

		public sealed class TreeViewProxy : DispatchProxy {
			IDocumentTreeNodeDataContext context = null!;
			readonly BundleDocumentNodeProvider bundleProvider = new BundleDocumentNodeProvider();
			readonly IDsDocumentNodeProvider defaultProvider;

			public TreeViewProxy() {
				Assembly product = Assembly.Load("dnSpy");
				Type providerType = product.GetType("dnSpy.Documents.TreeView.DefaultDsDocumentNodeProvider",
					throwOnError: true)!;
				defaultProvider = (IDsDocumentNodeProvider)Activator.CreateInstance(providerType,
					nonPublic: true)!;
			}

			public void Initialize(IDocumentTreeNodeDataContext context) => this.context = context;

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "CreateNode") {
					var owner = (DsDocumentNode?)args![0];
					var document = (IDsDocument)args[1]!;
					DsDocumentNode? node = bundleProvider.Create((IDocumentTreeView)(object)this,
						owner, document) ?? defaultProvider.Create((IDocumentTreeView)(object)this,
						owner, document);
					if (node is not null)
						node.Context = context;
					return node;
				}
				return DefaultValue(targetMethod?.ReturnType);
			}
		}

		static object? DefaultValue(Type? type) => type is null || !type.IsValueType
			? null : Activator.CreateInstance(type);

		static IDecompiler CreateDummyDecompiler() {
			Assembly product = Assembly.Load("dnSpy");
			Type decompilerType = product.GetType("dnSpy.Decompiler.DummyDecompiler", throwOnError: true)!;
			return (IDecompiler)Activator.CreateInstance(decompilerType, nonPublic: true)!;
		}

		static byte[] CreateNetmoduleFixture() {
			var module = new ModuleDefUser("Selected.netmodule") {
				Kind = ModuleKind.NetModule,
				Characteristics = Characteristics.Bit32Machine | Characteristics.ExecutableImage |
					Characteristics.Dll,
			};
			using var stream = new MemoryStream();
			module.Write(stream);
			return stream.ToArray();
		}
	}
}
