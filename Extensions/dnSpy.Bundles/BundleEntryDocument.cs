// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Metadata-only document for one bundle entry. Managed module creation is deliberately
	/// deferred to BND-009, so this document never exposes a ModuleDef or PEImage.
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

		/// <summary>Whether the entry is a managed assembly (still metadata-only in BND-008).</summary>
		public bool IsManaged => Entry.FileType == BundleFileType.Assembly;

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => null;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => new FilenameKey(Filename);
	}
}
