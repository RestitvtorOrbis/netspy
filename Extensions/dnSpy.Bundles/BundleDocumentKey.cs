// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Contracts.Documents;

namespace dnSpy.Bundles.Extension {
	/// <summary>Kind discriminator for a document identity below a bundle root.</summary>
	public enum BundleDocumentKeyKind {
		Root,
		Folder,
		Entry,
		Module,
		Error,
	}

	/// <summary>
	/// Identity for a document below one bundle root.
	/// </summary>
	/// <remarks>
	/// A synthetic filename is useful for display, but is not a sufficient document identity:
	/// path canonicalization can vary by platform and a relative entry path is not a physical
	/// filename. This key keeps the source identity and normalized child path as separate values.
	/// </remarks>
	public sealed class BundleDocumentKey : IDsDocumentNameKey, IEquatable<BundleDocumentKey?> {
		/// <summary>Creates a kind-discriminated key from a source bundle and child path.</summary>
		public BundleDocumentKey(string sourceBundleFilename, BundleDocumentKeyKind kind,
			string? relativePath = null) {
			if (sourceBundleFilename is null)
				throw new ArgumentNullException(nameof(sourceBundleFilename));
			if (!Enum.IsDefined(kind))
				throw new ArgumentOutOfRangeException(nameof(kind));
			if (kind == BundleDocumentKeyKind.Root && !string.IsNullOrEmpty(relativePath))
				throw new ArgumentException("A root key cannot have a child path.", nameof(relativePath));
			if (kind != BundleDocumentKeyKind.Root && relativePath is null)
				throw new ArgumentNullException(nameof(relativePath));
			SourceBundleFilename = GetFullPath(sourceBundleFilename);
			Kind = kind;
			RelativePath = Normalize(relativePath ?? string.Empty);
		}

		/// <summary>Creates a root identity.</summary>
		public static BundleDocumentKey Root(string sourceBundleFilename) =>
			new BundleDocumentKey(sourceBundleFilename, BundleDocumentKeyKind.Root);

		/// <summary>Creates a category identity.</summary>
		public static BundleDocumentKey Folder(string sourceBundleFilename, string folderName) =>
			new BundleDocumentKey(sourceBundleFilename, BundleDocumentKeyKind.Folder, folderName);

		/// <summary>Creates a metadata-entry identity.</summary>
		public static BundleDocumentKey Entry(string sourceBundleFilename, string relativePath) =>
			new BundleDocumentKey(sourceBundleFilename, BundleDocumentKeyKind.Entry, relativePath);

		/// <summary>Creates a loaded-module identity.</summary>
		public static BundleDocumentKey Module(string sourceBundleFilename, string relativePath) =>
			new BundleDocumentKey(sourceBundleFilename, BundleDocumentKeyKind.Module, relativePath);

		/// <summary>Creates a managed-entry activation-error identity.</summary>
		public static BundleDocumentKey Error(string sourceBundleFilename, string relativePath) =>
			new BundleDocumentKey(sourceBundleFilename, BundleDocumentKeyKind.Error, relativePath);

		/// <summary>Creates a metadata-entry key for compatibility with earlier callers.</summary>
		public BundleDocumentKey(string sourceBundleFilename, string relativePath)
			: this(sourceBundleFilename, BundleDocumentKeyKind.Entry, relativePath) {
		}

		/// <summary>The source bundle's canonical filename.</summary>
		public string SourceBundleFilename { get; }

		/// <summary>The identity kind.</summary>
		public BundleDocumentKeyKind Kind { get; }

		/// <summary>The normalized relative child path.</summary>
		public string RelativePath { get; }

		/// <inheritdoc/>
		public bool Equals(BundleDocumentKey? other) => other is not null &&
			StringComparer.OrdinalIgnoreCase.Equals(SourceBundleFilename, other.SourceBundleFilename) &&
			Kind == other.Kind &&
			StringComparer.Ordinal.Equals(RelativePath, other.RelativePath);

		/// <inheritdoc/>
		public override bool Equals(object? obj) => Equals(obj as BundleDocumentKey);

		/// <inheritdoc/>
		public override int GetHashCode() {
			unchecked {
				int hash = 17;
				hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(SourceBundleFilename);
				hash = hash * 31 + Kind.GetHashCode();
				hash = hash * 31 + StringComparer.Ordinal.GetHashCode(RelativePath);
				return hash;
			}
		}

		/// <inheritdoc/>
		public override string ToString() => SourceBundleFilename + "!/" + Kind + ":" + RelativePath;

		static string Normalize(string path) => path.Replace('\\', '/');

		static string GetFullPath(string filename) {
			try {
				if (!string.IsNullOrEmpty(filename))
					return Path.GetFullPath(filename);
			}
			catch (ArgumentException) {
			}
			return filename;
		}
	}
}
