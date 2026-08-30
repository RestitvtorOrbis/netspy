// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using Microsoft.VisualStudio.Utilities;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleTreeNodeTests {
		[Fact]
		public void RootExposesStableLazyCategoriesInManifestIndependentOrder() {
			using var document = CreateBundleDocument();
			Assert.False(document.ChildrenLoaded);

			var categories = document.Children.Cast<BundleFolderDocument>().ToArray();
			Assert.Equal(new[] { BundleFolderKind.Assemblies, BundleFolderKind.Runtime,
				BundleFolderKind.Native, BundleFolderKind.SymbolsAndOther }, categories.Select(a => a.Kind));
			Assert.Equal(new[] { "Assemblies", "Runtime", "Native", "Symbols/Other" },
				categories.Select(a => a.DisplayName));
			Assert.All(categories, category => Assert.False(category.ChildrenLoaded));
		}

		[Fact]
		public void TreeProviderCreatesBundleNodesAndPreservesEntryInventory() {
			using var document = CreateBundleDocument();
			var provider = new BundleDocumentNodeProvider();
			var root = provider.Create(null!, null, document);
			Assert.NotNull(root);
			Assert.Equal("BundleDsDocumentNode", root.GetType().Name);

			var categoryNodes = root.CreateChildren().ToArray();
			Assert.Equal(4, categoryNodes.Length);
			Assert.Equal(new[] { "BundleFolderDocumentNode", "BundleFolderDocumentNode",
				"BundleFolderDocumentNode", "BundleFolderDocumentNode" },
				categoryNodes.Select(a => a.GetType().Name));

			var entries = categoryNodes.SelectMany(a => a.CreateChildren()).ToArray();
			Assert.Equal(6, entries.Length);
			Assert.Equal(document.Bundle.Entries.Select(a => a.RelativePath),
				entries.Select(a => ((BundleEntryDocument)((DsDocumentNode)a).Document).Entry.RelativePath));
			Assert.All(entries, node => Assert.IsAssignableFrom<IDecompileSelf>(node));
		}

		[Fact]
		public void RenderingRootCategoriesAndManagedEntriesDoesNotMaterializeManagedBytes() {
			using var document = CreateBundleDocument();
			var provider = new BundleDocumentNodeProvider();
			var root = provider.Create(null!, null, document);
			Assert.NotNull(root);
			var nodes = root.CreateChildren().SelectMany(a => a.CreateChildren()).ToArray();

			// The BND-008 contract is metadata-only: even an assembly entry has no ModuleDef or
			// PEImage until BND-009's selected-entry adapter is introduced.
			Assert.All(nodes, node => {
				var documentNode = (DsDocumentNode)node;
				Assert.Null(documentNode.Document.ModuleDef);
				Assert.Null(documentNode.Document.PEImage);
				_ = node.ToString();
			});
		}

		[Fact]
		public void ExpandingNonEmptyManagedEntryInventoryPerformsZeroLogicalReads() {
			byte[] managedBytes = new byte[4096];
			using var document = CreateInstrumentedDocument(BundleFileType.Assembly, "app.dll",
				managedBytes, BundleTextViewOptions.Default, out var probe);
			var provider = new BundleDocumentNodeProvider();
			var root = provider.Create(null!, null, document)!;
			foreach (var category in root.CreateChildren()) {
				_ = category.ToString();
				foreach (var entry in category.CreateChildren())
					_ = entry.ToString();
			}

			Assert.Equal(0, probe.OpenCount);
			Assert.Equal(0, probe.MaximumRequestedBytes);
			var managedNode = root.CreateChildren().Cast<DsDocumentNode>()
				.SelectMany(a => a.CreateChildren()).Cast<DsDocumentNode>()
				.Single(a => ((BundleEntryDocument)a.Document).IsManaged);
			Assert.Null(managedNode.Document.ModuleDef);
			Assert.Null(managedNode.Document.PEImage);
		}

		[Fact]
		public void NonManagedEntryNodeRendersMetadataWithoutReadingContent() {
			using var document = CreateBundleDocument();
			var provider = new BundleDocumentNodeProvider();
			var root = provider.Create(null!, null, document);
			Assert.NotNull(root);
			var nativeNode = root.CreateChildren().ElementAt(2).CreateChildren().Single();

			string text = nativeNode.ToString();
			Assert.Contains("native/libnative.so", text, StringComparison.Ordinal);
			Assert.IsAssignableFrom<IDecompileSelf>(nativeNode);
		}

		[Fact]
		public void RuntimePreviewOptionsUseEightMiBDefaultAndCanBeBoundedByExtension() {
			Assert.Equal(8 * 1024 * 1024, BundleTextViewOptions.Default.MaximumPreviewBytes);
			Assert.Equal(32, new BundleTextViewOptions(32).MaximumPreviewBytes);
		}

		[Theory]
		[InlineData(BundleFileType.DepsJson)]
		[InlineData(BundleFileType.RuntimeConfigJson)]
		public void RuntimeJsonNodeUsesActualDecompileSelfContextAndBoundedStream(BundleFileType fileType) {
			byte[] bytes = Encoding.UTF8.GetBytes("{\"bundle\":true}\n");
			using var document = CreateInstrumentedDocument(fileType, "runtime.json", bytes,
				new BundleTextViewOptions(64), out var probe);
			var context = new TestDecompileNodeContext();

			Assert.True(((IDecompileSelf)GetEntryNode(document, fileType)).Decompile(context));
			Assert.Contains("{\"bundle\":true}\n", context.Output.GetText(), StringComparison.Ordinal);
			Assert.Equal(1, probe.OpenCount);
			Assert.True(probe.MaximumRequestedBytes <= 64);
			Assert.Equal(ContentTypes.PlainText, context.ContentTypeString);
		}

		[Fact]
		public void RuntimeJsonNodeReportsMalformedUtf8ThroughActualDecompileContext() {
			using var document = CreateInstrumentedDocument(BundleFileType.RuntimeConfigJson,
				"runtimeconfig.json", new byte[] { 0x7B, 0xFF, 0x7D },
				new BundleTextViewOptions(64), out var probe);
			var context = new TestDecompileNodeContext();

			Assert.True(((IDecompileSelf)GetEntryNode(document, BundleFileType.RuntimeConfigJson)).Decompile(context));
			Assert.Contains("not valid UTF-8", context.Output.GetText(), StringComparison.Ordinal);
			Assert.Equal(1, probe.OpenCount);
			Assert.True(probe.MaximumRequestedBytes <= 64);
		}

		[Fact]
		public void RuntimeJsonPreviewAtExactlyEightMiBIsNotTruncated() {
			const int maximum = BundleTextViewOptions.DefaultMaximumPreviewBytes;
			byte[] bytes = Enumerable.Repeat((byte)'a', maximum).ToArray();
			using var document = CreateInstrumentedDocument(BundleFileType.RuntimeConfigJson,
				"runtimeconfig.json", bytes, BundleTextViewOptions.Default, out var probe);
			var context = new TestDecompileNodeContext();

			Assert.True(((IDecompileSelf)GetEntryNode(document, BundleFileType.RuntimeConfigJson)).Decompile(context));
			Assert.Equal(maximum, context.Output.GetText().Count(a => a == 'a'));
			Assert.DoesNotContain("Preview truncated", context.Output.GetText(), StringComparison.Ordinal);
			Assert.Equal(1, probe.OpenCount);
			Assert.True(probe.MaximumRequestedBytes <= maximum);
		}

		[Fact]
		public void RuntimeJsonPreviewOverEightMiBIsTruncatedWithoutUnboundedRead() {
			const int maximum = BundleTextViewOptions.DefaultMaximumPreviewBytes;
			byte[] bytes = Enumerable.Repeat((byte)'b', maximum + 1).ToArray();
			using var document = CreateInstrumentedDocument(BundleFileType.RuntimeConfigJson,
				"runtimeconfig.json", bytes, BundleTextViewOptions.Default, out var probe);
			var context = new TestDecompileNodeContext();

			Assert.True(((IDecompileSelf)GetEntryNode(document, BundleFileType.RuntimeConfigJson)).Decompile(context));
			string text = context.Output.GetText();
			Assert.Equal(maximum, text.Count(a => a == 'b'));
			Assert.Contains("Preview truncated at 8388608 bytes", text, StringComparison.Ordinal);
			Assert.Equal(maximum + 1, bytes.Length);
			Assert.Equal(1, probe.OpenCount);
			Assert.True(probe.MaximumRequestedBytes <= maximum);
		}

		[Theory]
		[InlineData(BundleFileType.NativeBinary, "native.bin")]
		[InlineData(BundleFileType.Symbols, "symbols.pdb")]
		[InlineData(BundleFileType.Unknown, "other.bin")]
		public void NonManagedNodesUseActualDecompileSelfContextForMetadata(BundleFileType fileType,
			string path) {
			byte[] bytes = new byte[17];
			using var document = CreateInstrumentedDocument(fileType, path, bytes,
				BundleTextViewOptions.Default, out var probe);
			var context = new TestDecompileNodeContext();

			Assert.True(((IDecompileSelf)GetEntryNode(document, fileType)).Decompile(context));
			string text = context.Output.GetText();
			Assert.Contains($"Path: {path}", text, StringComparison.Ordinal);
			Assert.Contains("Logical size: 17", text, StringComparison.Ordinal);
			Assert.Contains("Compressed size: not compressed", text, StringComparison.Ordinal);
			Assert.Equal(0, probe.OpenCount);
		}

		static BundleDsDocument CreateBundleDocument() {
			var entries = new List<BundleEntry> {
				new BundleEntry(0, 0, 0, 0, 1, BundleFileType.Assembly, "app.dll"),
				new BundleEntry(1, 0, 0, 0, 3, BundleFileType.DepsJson, "app.deps.json"),
				new BundleEntry(2, 0, 0, 0, 4, BundleFileType.RuntimeConfigJson, "app.runtimeconfig.json"),
				new BundleEntry(3, 0, 0, 0, 2, BundleFileType.NativeBinary, "native/libnative.so"),
				new BundleEntry(4, 0, 0, 0, 5, BundleFileType.Symbols, "app.pdb"),
				new BundleEntry(5, 0, 0, 0, 0x7F, BundleFileType.Unknown, "content/data.bin"),
			};
			var bundle = new BundleFile("bundle.exe", 1024, 128, 160,
				new BundleManifest(6, 0, "test"), entries);
			return new BundleDsDocument(DsDocumentInfo.CreateDocument(bundle.Filename), bundle);
		}

		static BundleDsDocument CreateInstrumentedDocument(BundleFileType fileType, string path,
			byte[] bytes, BundleTextViewOptions options, out EntryReadProbe probe) {
			if (bytes is null)
				throw new ArgumentNullException(nameof(bytes));
			var readProbe = new EntryReadProbe(bytes);
			probe = readProbe;
			byte rawType = fileType switch {
				BundleFileType.Assembly => 1,
				BundleFileType.NativeBinary => 2,
				BundleFileType.DepsJson => 3,
				BundleFileType.RuntimeConfigJson => 4,
				BundleFileType.Symbols => 5,
				_ => 0x7F,
			};
			var entry = new BundleEntry(0, 0, bytes.LongLength, 0, rawType, fileType, path);
			var bundle = new BundleFile("instrumented-bundle.exe", bytes.LongLength + 256, 128, 160,
				new BundleManifest(6, 0, "test"), new[] { entry });
			return new BundleDsDocument(DsDocumentInfo.CreateDocument(bundle.Filename), bundle,
				options, _ => readProbe.Open());
		}

		static DsDocumentNode GetEntryNode(BundleDsDocument document, BundleFileType fileType) {
			var provider = new BundleDocumentNodeProvider();
			var root = provider.Create(null!, null, document)!;
			var folder = root.CreateChildren()
				.Cast<DsDocumentNode>()
				.Single(node => ((BundleFolderDocument)node.Document).Kind switch {
					BundleFolderKind.Assemblies => fileType == BundleFileType.Assembly,
					BundleFolderKind.Runtime => fileType == BundleFileType.DepsJson || fileType == BundleFileType.RuntimeConfigJson,
					BundleFolderKind.Native => fileType == BundleFileType.NativeBinary,
					BundleFolderKind.SymbolsAndOther => fileType == BundleFileType.Symbols || fileType == BundleFileType.Unknown,
					_ => false,
				});
			return folder.CreateChildren().Cast<DsDocumentNode>().Single();
		}

		sealed class EntryReadProbe {
			readonly byte[] bytes;

			public EntryReadProbe(byte[] bytes) => this.bytes = bytes;

			public int OpenCount { get; private set; }
			public int MaximumRequestedBytes { get; private set; }

			public Stream Open() {
				OpenCount++;
				return new ProbeStream(bytes, requested => {
					if (requested > MaximumRequestedBytes)
						MaximumRequestedBytes = requested;
				});
			}
		}

		sealed class ProbeStream : MemoryStream {
			readonly Action<int> onRead;

			public ProbeStream(byte[] bytes, Action<int> onRead)
				: base(bytes, writable: false) => this.onRead = onRead;

			public override int Read(byte[] buffer, int offset, int count) {
				onRead(count);
				return base.Read(buffer, offset, count);
			}

			public override int Read(Span<byte> buffer) {
				onRead(buffer.Length);
				return base.Read(buffer);
			}
		}

		sealed class TestDecompileNodeContext : IDecompileNodeContext {
			public StringBuilderDecompilerOutput Output { get; } = new StringBuilderDecompilerOutput();
			IDecompilerOutput IDecompileNodeContext.Output => Output;
			public IDocumentWriterService DocumentWriterService => null!;
			public IDecompiler Decompiler => null!;
			public DecompilationContext DecompilationContext { get; } = new DecompilationContext();
			public IContentType? ContentType { get; set; }
			public string? ContentTypeString { get; set; }
			public T UIThread<T>(Func<T> func) => func();
		}
	}
}
