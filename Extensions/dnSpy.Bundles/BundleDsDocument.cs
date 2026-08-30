// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Top-level document for a successfully opened official single-file bundle.
	/// </summary>
	public sealed class BundleDsDocument : DsDocument, IDisposable {
		readonly DsDocumentInfo serializedDocument;

		public BundleDsDocument(DsDocumentInfo serializedDocument, BundleFile bundle) {
			if (bundle is null)
				throw new ArgumentNullException(nameof(bundle));
			this.serializedDocument = serializedDocument;
			Bundle = bundle;
			Filename = serializedDocument.Name;
		}

		/// <summary>Validated bundle metadata and lazy entry access.</summary>
		public BundleFile Bundle { get; }

		/// <summary>The physical source filename retained for later workspace operations.</summary>
		public string SourceFilename => serializedDocument.Name;

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => serializedDocument;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => new FilenameKey(Filename);

		/// <inheritdoc/>
		public void Dispose() => Bundle.Dispose();
	}
}
