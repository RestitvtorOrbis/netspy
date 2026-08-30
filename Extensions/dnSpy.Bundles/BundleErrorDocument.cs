// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Document retained in the document list when an executable has an official
	/// bundle marker but its manifest cannot be opened safely.
	/// </summary>
	public sealed class BundleErrorDocument : DsDocument {
		readonly DsDocumentInfo serializedDocument;

		public BundleErrorDocument(DsDocumentInfo serializedDocument, BundleOpenStatus status,
			BundleReadError error) {
			if (error is null)
				throw new ArgumentNullException(nameof(error));
			if (status != BundleOpenStatus.InvalidBundle && status != BundleOpenStatus.UnsupportedVersion)
				throw new ArgumentOutOfRangeException(nameof(status));
			this.serializedDocument = serializedDocument;
			Status = status;
			Error = error;
			Filename = serializedDocument.Name;
		}

		/// <summary>Reader status that caused this document to be created.</summary>
		public BundleOpenStatus Status { get; }

		/// <summary>Safe diagnostic suitable for a document/error view.</summary>
		public BundleReadError Error { get; }

		/// <summary>Convenience display message for a future error node.</summary>
		public string ErrorMessage => Error.Message;

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => serializedDocument;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => new FilenameKey(Filename);
	}
}
