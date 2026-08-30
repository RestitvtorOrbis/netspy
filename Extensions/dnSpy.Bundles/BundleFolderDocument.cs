// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>Stable categories shown below a bundle root.</summary>
	public enum BundleFolderKind {
		/// <summary>Managed IL and ReadyToRun entries.</summary>
		Assemblies,
		/// <summary>deps.json and runtimeconfig.json entries.</summary>
		Runtime,
		/// <summary>Native binary entries.</summary>
		Native,
		/// <summary>Symbols and entries with an unknown official type.</summary>
		SymbolsAndOther,
	}

	/// <summary>
	/// Lazy category document below a bundle root. Its children are created from manifest
	/// metadata in original order; no entry content is opened while categories are rendered.
	/// </summary>
	public sealed class BundleFolderDocument : DsDocument {
		public BundleFolderDocument(BundleDsDocument bundleDocument, BundleFolderKind kind) {
			BundleDocument = bundleDocument ?? throw new ArgumentNullException(nameof(bundleDocument));
			Kind = kind;
			Filename = GetSyntheticFilename(bundleDocument, DisplayName);
		}

		/// <summary>Owning bundle document.</summary>
		public BundleDsDocument BundleDocument { get; }

		/// <summary>Category represented by this document.</summary>
		public BundleFolderKind Kind { get; }

		/// <summary>Display name used by the category node.</summary>
		public string DisplayName => GetDisplayName(Kind);

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => null;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => new FilenameKey(Filename);

		/// <inheritdoc/>
		protected override TList<IDsDocument> CreateChildren() {
			var children = new TList<IDsDocument>();
			foreach (BundleEntry entry in BundleDocument.Bundle.Entries) {
				if (BelongsToFolder(entry.FileType, Kind))
					children.Add(new BundleEntryDocument(this, entry));
			}
			return children;
		}

		internal static string GetSyntheticFilename(BundleDsDocument bundleDocument, string childName) =>
			bundleDocument.SourceFilename + "!/" + childName;

		internal static string GetDisplayName(BundleFolderKind kind) => kind switch {
			BundleFolderKind.Assemblies => "Assemblies",
			BundleFolderKind.Runtime => "Runtime",
			BundleFolderKind.Native => "Native",
			BundleFolderKind.SymbolsAndOther => "Symbols/Other",
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		};

		internal static bool BelongsToFolder(BundleFileType type, BundleFolderKind kind) => kind switch {
			BundleFolderKind.Assemblies => type == BundleFileType.Assembly,
			BundleFolderKind.Runtime => type == BundleFileType.DepsJson || type == BundleFileType.RuntimeConfigJson,
			BundleFolderKind.Native => type == BundleFileType.NativeBinary,
			BundleFolderKind.SymbolsAndOther => type == BundleFileType.Symbols || type == BundleFileType.Unknown,
			_ => false,
		};
	}
}
