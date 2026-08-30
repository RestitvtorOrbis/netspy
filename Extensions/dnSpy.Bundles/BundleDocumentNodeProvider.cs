// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
			if (document is BundleDsDocument bundle)
				return new BundleDsDocumentNode(bundle);
			if (document is BundleFolderDocument folder)
				return new BundleFolderDocumentNode(folder);
			if (document is BundleEntryDocument entry)
				return new BundleEntryDocumentNode(entry);
			if (document is BundleErrorDocument error)
				return new BundleErrorDocumentNode(error);
			return null;
		}
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
}
