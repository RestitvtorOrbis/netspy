// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Metadata-only document for one bundle entry. Managed materialization is explicit and cached,
	/// so generic document traversal never exposes or loads a ModuleDef or PEImage accidentally.
	/// </summary>
	public sealed class BundleEntryDocument : DsDocument {
		public BundleEntryDocument(BundleFolderDocument folderDocument, BundleEntry entry) {
			FolderDocument = folderDocument ?? throw new ArgumentNullException(nameof(folderDocument));
			Entry = entry ?? throw new ArgumentNullException(nameof(entry));
			BundleDocument = folderDocument.BundleDocument;
			Filename = BundleFolderDocument.GetSyntheticFilename(BundleDocument, entry.RelativePath);
		}

		/// <summary>Category containing this entry.</summary>
		public BundleFolderDocument FolderDocument { get; }

		/// <summary>Owning bundle document.</summary>
		public BundleDsDocument BundleDocument { get; }

		/// <summary>Validated parser metadata for the entry.</summary>
		public BundleEntry Entry { get; }

		/// <summary>Whether the entry is a managed assembly.</summary>
		public bool IsManaged => Entry.FileType == BundleFileType.Assembly;

		/// <summary>Current logical state tracked by the owning workspace.</summary>
		public BundleWorkspaceEntryState WorkspaceState => BundleDocument.Workspace.GetEntryState(Entry);

		/// <summary>Last workspace operation error, if any.</summary>
		public Exception? WorkspaceError => BundleDocument.Workspace.GetError(Entry);

		/// <summary>
		/// Gets the activated module, if this entry has already been selected. This property never
		/// activates an entry and therefore remains null while the bundle tree is being rendered.
		/// </summary>
		public BundleModuleDocument? ManagedDocument {
			get => BundleDocument.GetManagedDocument(this);
		}

		/// <summary>
		/// Activates this managed entry and caches the resulting module document. Only this entry's
		/// bounded logical stream is opened; sibling entries remain metadata-only.
		/// </summary>
		public BundleModuleDocument CreateManagedDocument() {
			if (!IsManaged)
				throw new InvalidOperationException("The bundle entry is not a managed assembly.");
			return BundleDocument.CreateManagedDocument(this);
		}

		/// <summary>
		/// Attempts to activate this entry and returns a safe exception for a visible error view.
		/// </summary>
		public bool TryCreateManagedDocument(out BundleModuleDocument? document, out Exception? error) {
			return BundleDocument.TryCreateManagedDocument(this, out document, out error);
		}

		public override DsDocumentInfo? SerializedDocument => null;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => BundleDocumentKey.Entry(
			BundleDocument.SourceBundleFilename, Entry.RelativePath);

		/// <inheritdoc/>
		protected override TList<IDsDocument> CreateChildren() {
			// The metadata document has no generic document children. The bundle tree node performs
			// activation only for an explicitly expanded/selected managed entry, then creates the
			// annotated assembly wrapper that feeds the normal module-node provider.
			return new TList<IDsDocument>();
		}
	}
}
