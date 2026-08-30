// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using dnlib.DotNet;
using dnSpy.Bundles;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Bundles.Extension {
	/// <summary>Creates the bundle root, category, entry, and error tree nodes.</summary>
	[ExportDsDocumentNodeProvider(Order = BundleDocumentNodeProvider.ProviderOrder)]
	public sealed class BundleDocumentNodeProvider : IDsDocumentNodeProvider {
		// This extension must run before the default provider (which is deliberately last).
		public const double ProviderOrder = 1000d;

		/// <inheritdoc/>
		public DsDocumentNode? Create(IDocumentTreeView documentTreeView, DsDocumentNode? owner, IDsDocument document) =>
			CreateNode(owner, document);

		/// <summary>
		/// Creates one of this extension's nodes. The helper also gives focused tests a way to
		/// exercise the actual node contracts without constructing the WPF tree view.
		/// </summary>
		internal static DsDocumentNode? CreateNode(DsDocumentNode? owner, IDsDocument document) {
			if (document is DsDotNetDocument assemblyDocument &&
				assemblyDocument.Annotation<BundleAssemblyDocumentAnnotation>() is not null)
				return new BundleAssemblyDocumentNode(assemblyDocument);
			if (document is BundleEntryDocument managedEntry && managedEntry.IsManaged)
				return new BundleManagedEntryDocumentNode(managedEntry);
			if (document is BundleDsDocument bundle)
				return new BundleDsDocumentNode(bundle);
			if (document is BundleFolderDocument folder)
				return new BundleFolderDocumentNode(folder);
			if (document is BundleEntryDocument entry)
				return new BundleEntryDocumentNode(entry);
			if (document is BundleErrorDocument error)
				return new BundleErrorDocumentNode(error);
			if (document is BundleEntryErrorDocument entryError)
				return new BundleEntryErrorDocumentNode(entryError);
			return null;
		}

	}

	sealed class BundleManagedEntryDocumentNode : DsDocumentNode, IDecompileSelf {
		public static readonly Guid NodeGuid = new Guid("46A7C26C-8A3B-4ED2-B8FA-71AF8B0A4A42");

		readonly BundleEntryDocument document;

		public BundleManagedEntryDocumentNode(BundleEntryDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => NodeGuid;
		// Do not inspect AssemblyDef here. Icons and inventory rendering must not activate the entry.
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.ModulePublic;
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			if (!document.TryCreateManagedDocument(out BundleModuleDocument? module,
				out Exception? error)) {
				yield return CreateChildNode(this, new BundleEntryErrorDocument(document, error!));
				yield break;
			}

			// A valid netmodule has no AssemblyDef. Keep it on dnSpy's ordinary module path instead
			// of manufacturing an assembly wrapper whose normal node contract requires AssemblyDef.
			if (module!.ModuleDef!.Assembly is null)
				yield return CreateChildNode(this, module);
			else {
				DsDotNetDocument assemblyDocument = module.CreateAssemblyDocument();
				yield return CreateChildNode(this, assemblyDocument);
			}
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) {
			output.Write(BoxedTextColor.Text, document.Entry.RelativePath);
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				output.WriteFilename(Document.Filename);
			}
		}

		public override FilterType GetFilterType(IDocumentTreeNodeFilter filter) =>
			filter.GetResult(document).FilterType;

		public bool Decompile(IDecompileNodeContext context) {
			if (!document.TryCreateManagedDocument(out BundleModuleDocument? module,
				out Exception? error)) {
				context.ContentTypeString = ContentTypes.PlainText;
				context.Output.Write($"Unable to load managed bundle entry '{document.Entry.RelativePath}':\n{error!.Message}",
					BoxedTextColor.Error);
				return true;
			}

			if (module!.ModuleDef!.Assembly is AssemblyDef assembly)
				context.Decompiler.Decompile(assembly, context.Output, context.DecompilationContext);
			else
				context.Decompiler.Decompile(module.ModuleDef, context.Output,
					context.DecompilationContext);
			return true;
		}

		static DsDocumentNode CreateChildNode(DsDocumentNode owner, IDsDocument document) =>
			owner.Context is not null
				? owner.Context.DocumentTreeView.CreateNode(owner, document)
				: BundleDocumentNodeProvider.CreateNode(owner, document)!;
	}

	sealed class BundleAssemblyDocumentNode : AssemblyDocumentNode {
		public static readonly Guid NodeGuid = new Guid("A55E6BDA-DBD7-4F62-9CBF-5F3DFE18A39E");

		public BundleAssemblyDocumentNode(IDsDotNetDocument document)
			: base(document) {
		}

		public override Guid Guid => NodeGuid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) =>
			dnImgMgr.GetImageReference(Document.AssemblyDef!);
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (IDsDocument document in Document.Children) {
				if (Context is not null)
					yield return Context.DocumentTreeView.CreateNode(this, document);
				else {
					DsDocumentNode? node = BundleDocumentNodeProvider.CreateNode(this, document);
					if (node is not null)
						yield return node;
				}
			}
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) {
			if ((options & DocumentNodeWriteOptions.ToolTip) == 0) {
				new NodeFormatter().Write(output, decompiler, Document.AssemblyDef!, false,
					Context is not null && Context.ShowAssemblyVersion,
					Context is not null && Context.ShowAssemblyPublicKeyToken);
			}
			else {
				output.Write(Document.AssemblyDef!);
				output.WriteLine();
				output.WriteFilename(Document.Filename);
			}
		}

		public override FilterType GetFilterType(IDocumentTreeNodeFilter filter) =>
			filter.GetResult(Document.AssemblyDef!).FilterType;
	}

	sealed class BundleDsDocumentNode : DsDocumentNode {
		public static readonly Guid NodeGuid = new Guid("21AFC3F2-8A04-4F90-92EA-4F1C7D77A2BB");

		readonly BundleDsDocument document;

		public BundleDsDocumentNode(BundleDsDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => NodeGuid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.AssemblyExe;
		protected override ImageReference? GetExpandedIcon(IDotNetImageService dnImgMgr) => DsImages.AssemblyExe;
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (IDsDocument child in document.Children)
				yield return CreateChildNode(this, child);
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) {
			output.WriteFilename(Path.GetFileName(document.Filename));
			output.Write(BoxedTextColor.Text, " [.NET Bundle]");
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				output.Write(BoxedTextColor.Text, $"Entries: {document.Bundle.Entries.Count.ToString(CultureInfo.InvariantCulture)}");
			}
		}

		static DsDocumentNode CreateChildNode(DsDocumentNode owner, IDsDocument document) =>
			owner.Context is not null
				? owner.Context.DocumentTreeView.CreateNode(owner, document)
				: BundleDocumentNodeProvider.CreateNode(owner, document)!;
	}

	sealed class BundleFolderDocumentNode : DsDocumentNode {
		public static readonly Guid NodeGuid = new Guid("3E3A9CA7-80B7-4E67-AF5A-4F4558FD8F3A");

		readonly BundleFolderDocument document;

		public BundleFolderDocumentNode(BundleFolderDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => NodeGuid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.FolderClosed;
		protected override ImageReference? GetExpandedIcon(IDotNetImageService dnImgMgr) => DsImages.FolderOpened;
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (IDsDocument child in document.Children)
				yield return CreateChildNode(this, child);
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) => output.Write(BoxedTextColor.Text, document.DisplayName);

		static DsDocumentNode CreateChildNode(DsDocumentNode owner, IDsDocument document) =>
			owner.Context is not null
				? owner.Context.DocumentTreeView.CreateNode(owner, document)
				: BundleDocumentNodeProvider.CreateNode(owner, document)!;
	}

	sealed class BundleEntryDocumentNode : DsDocumentNode, IDecompileSelf {
		public static readonly Guid NodeGuid = new Guid("3D1A80B1-6644-46D4-93C3-E4DBD095E3C2");

		readonly BundleEntryDocument document;

		public BundleEntryDocumentNode(BundleEntryDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => NodeGuid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => GetEntryIcon(document.Entry.FileType);

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) {
			output.Write(BoxedTextColor.Text, document.Entry.RelativePath);
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				WriteMetadata(output, document.Entry);
			}
		}

		public bool Decompile(IDecompileNodeContext context) {
			if (document.Entry.FileType == BundleFileType.DepsJson ||
				document.Entry.FileType == BundleFileType.RuntimeConfigJson)
				return DecompileText(context);

			context.ContentTypeString = ContentTypes.PlainText;
			WriteMetadata(context.Output, document.Entry);
			return true;
		}

		bool DecompileText(IDecompileNodeContext context) {
			context.ContentTypeString = ContentTypes.PlainText;
			BundleTextViewOptions options = document.BundleDocument.TextViewOptions;
			int maximum = options.MaximumPreviewBytes;
			try {
				int requested = (int)Math.Min((long)maximum, document.Entry.Size);
				byte[] bytes = requested == 0 ? Array.Empty<byte>() : new byte[requested];
				int count = 0;
				bool hasMore;
				using (Stream stream = document.BundleDocument.OpenLogicalRead(document.Entry)) {
					while (count < bytes.Length) {
						int read = stream.Read(bytes, count, bytes.Length - count);
						if (read <= 0)
							throw new InvalidDataException("The bundle entry ended before its declared logical length.");
						count += read;
					}
					hasMore = document.Entry.Size > requested;
					if (!hasMore && requested == maximum && maximum > 0) {
						byte[] extra = new byte[1];
						hasMore = stream.Read(extra, 0, 1) != 0;
					}
				}

				string text;
				try {
					text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
						throwOnInvalidBytes: true).GetString(bytes, 0, count);
				}
				catch (System.Text.DecoderFallbackException) {
					context.Output.Write("The runtime configuration entry is not valid UTF-8.", BoxedTextColor.Text);
					return true;
				}
				context.Output.Write(text, BoxedTextColor.Text);
				if (hasMore) {
					context.Output.WriteLine();
					context.Output.Write($"[Preview truncated at {maximum.ToString(CultureInfo.InvariantCulture)} bytes.]",
						BoxedTextColor.Comment);
				}
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
				ex is InvalidOperationException || ex is ObjectDisposedException) {
				context.Output.Write($"Unable to preview entry: {ex.Message}", BoxedTextColor.Comment);
				return true;
			}
		}

		static ImageReference GetEntryIcon(BundleFileType type) => type switch {
			BundleFileType.Assembly => DsImages.ModulePublic,
			BundleFileType.DepsJson => DsImages.TextFile,
			BundleFileType.RuntimeConfigJson => DsImages.TextFile,
			BundleFileType.Symbols => DsImages.BinaryFile,
			BundleFileType.NativeBinary => DsImages.BinaryFile,
			_ => DsImages.Binary,
		};

		internal static void WriteMetadata(IDecompilerOutput output, BundleEntry entry) {
			output.WriteLine();
			output.Write("Path: ", BoxedTextColor.Keyword);
			output.WriteLine(entry.RelativePath, BoxedTextColor.Text);
			output.Write("Type: ", BoxedTextColor.Keyword);
			string typeName = entry.FileType == BundleFileType.Unknown
				? $"Unknown (0x{entry.RawFileType:X2})"
				: $"{entry.FileType} (0x{entry.RawFileType:X2})";
			output.WriteLine(typeName, BoxedTextColor.Text);
			output.Write("Logical size: ", BoxedTextColor.Keyword);
			output.WriteLine(entry.Size.ToString(CultureInfo.InvariantCulture), BoxedTextColor.Text);
			output.Write("Compressed size: ", BoxedTextColor.Keyword);
			string compressedSize = entry.IsCompressed
				? entry.CompressedSize.ToString(CultureInfo.InvariantCulture)
				: "not compressed";
			output.WriteLine(compressedSize, BoxedTextColor.Text);
		}

		static void WriteMetadata(ITextColorWriter output, BundleEntry entry) {
			WriteMetadataLine(output, "Path: ", entry.RelativePath);
			string typeName = entry.FileType == BundleFileType.Unknown
				? $"Unknown (0x{entry.RawFileType:X2})"
				: $"{entry.FileType} (0x{entry.RawFileType:X2})";
			WriteMetadataLine(output, "Type: ", typeName);
			WriteMetadataLine(output, "Logical size: ", entry.Size.ToString(CultureInfo.InvariantCulture));
			WriteMetadataLine(output, "Compressed size: ", entry.IsCompressed
				? entry.CompressedSize.ToString(CultureInfo.InvariantCulture)
				: "not compressed");
		}

		static void WriteMetadataLine(ITextColorWriter output, string label, string value) {
			output.Write(BoxedTextColor.Keyword, label);
			output.Write(BoxedTextColor.Text, value);
			output.Write(BoxedTextColor.Text, Environment.NewLine);
		}
	}

	sealed class BundleErrorDocumentNode : DsDocumentNode, IDecompileSelf {
		public static readonly Guid NodeGuid = new Guid("D93CD7EF-88C5-46A2-9708-BFA0AB76F0D5");

		readonly BundleErrorDocument document;

		public BundleErrorDocumentNode(BundleErrorDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => NodeGuid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.AssemblyError;

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) => output.Write(BoxedTextColor.Text,
			$"{Path.GetFileName(document.Filename)} [.NET Bundle error]");

		public bool Decompile(IDecompileNodeContext context) {
			context.ContentTypeString = ContentTypes.PlainText;
			context.Output.Write(document.ErrorMessage, BoxedTextColor.Error);
			return true;
		}
	}

	sealed class BundleEntryErrorDocumentNode : DsDocumentNode, IDecompileSelf {
		public static readonly Guid NodeGuid = new Guid("F8B5BE69-625A-45AB-BFCB-4B503B9E725B");

		readonly BundleEntryErrorDocument document;

		public BundleEntryErrorDocumentNode(BundleEntryErrorDocument document)
			: base(document) => this.document = document;

		public override Guid Guid => NodeGuid;
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.AssemblyError;

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
			DocumentNodeWriteOptions options) {
			output.Write(BoxedTextColor.Error, document.EntryDocument.Entry.RelativePath);
			if ((options & DocumentNodeWriteOptions.ToolTip) != 0) {
				output.WriteLine();
				output.Write(BoxedTextColor.Error, document.ErrorMessage);
			}
		}

		public bool Decompile(IDecompileNodeContext context) {
			context.ContentTypeString = ContentTypes.PlainText;
			context.Output.Write(document.ErrorMessage, BoxedTextColor.Error);
			return true;
		}
	}
}
