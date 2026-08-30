// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Top-level document for a successfully opened official single-file bundle.
	/// </summary>
	public sealed class BundleDsDocument : DsDocument, IDisposable {
		readonly DsDocumentInfo serializedDocument;
		readonly Func<BundleEntry, Stream> openLogicalRead;

		public BundleDsDocument(DsDocumentInfo serializedDocument, BundleFile bundle,
			BundleTextViewOptions? textViewOptions = null,
			Func<BundleEntry, Stream>? openLogicalRead = null) {
			if (bundle is null)
				throw new ArgumentNullException(nameof(bundle));
			this.serializedDocument = serializedDocument;
			Bundle = bundle;
			TextViewOptions = textViewOptions ?? BundleTextViewOptions.Default;
			this.openLogicalRead = openLogicalRead ?? (static entry => entry.OpenLogicalRead());
			Filename = serializedDocument.Name;
		}

		/// <summary>Validated bundle metadata and lazy entry access.</summary>
		public BundleFile Bundle { get; }

		/// <summary>Options used by bounded text previews of entries in this bundle.</summary>
		public BundleTextViewOptions TextViewOptions { get; }

		/// <summary>
		/// Opens one entry through the bundle's bounded logical stream. The optional factory is a
		/// narrow stream seam used by extension tests; production callers use the parser stream.
		/// </summary>
		internal Stream OpenLogicalRead(BundleEntry entry) {
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			return openLogicalRead(entry);
		}

		/// <summary>The physical source filename retained for later workspace operations.</summary>
		public string SourceFilename => serializedDocument.Name;

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => serializedDocument;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => new FilenameKey(Filename);

		/// <inheritdoc/>
		protected override TList<IDsDocument> CreateChildren() {
			// Keep the four categories stable even when a bundle does not contain an entry in
			// one of them. Reading Bundle.Entries only touches validated manifest metadata; it
			// never opens an entry stream or materializes managed bytes.
			return new TList<IDsDocument> {
				new BundleFolderDocument(this, BundleFolderKind.Assemblies),
				new BundleFolderDocument(this, BundleFolderKind.Runtime),
				new BundleFolderDocument(this, BundleFolderKind.Native),
				new BundleFolderDocument(this, BundleFolderKind.SymbolsAndOther),
			};
		}

		/// <inheritdoc/>
		public void Dispose() => Bundle.Dispose();
	}
}
