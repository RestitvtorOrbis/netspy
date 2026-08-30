// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>Visible diagnostic document for a managed entry that failed activation.</summary>
	public sealed class BundleEntryErrorDocument : DsDocument {
		readonly DsDocumentInfo serializedDocument;

		public BundleEntryErrorDocument(BundleEntryDocument entryDocument, Exception error) {
			EntryDocument = entryDocument ?? throw new ArgumentNullException(nameof(entryDocument));
			Error = error ?? throw new ArgumentNullException(nameof(error));
			Filename = entryDocument.Filename;
			serializedDocument = DsDocumentInfo.CreateDocument(Filename);
		}

		/// <summary>The managed entry whose activation failed.</summary>
		public BundleEntryDocument EntryDocument { get; }

		/// <summary>The safe diagnostic retained for the error node.</summary>
		public Exception Error { get; }

		/// <summary>Short diagnostic suitable for an editor/error view.</summary>
		public string ErrorMessage => Error.Message;

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => serializedDocument;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => BundleDocumentKey.Error(
			EntryDocument.BundleDocument.SourceBundleFilename, EntryDocument.Entry.RelativePath);
	}
}
