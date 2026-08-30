// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace dnSpy.Contracts.Documents.Bundles {
	/// <summary>
	/// Identifies a document that owns an official .NET single-file bundle.
	/// </summary>
	public interface IDsBundleDocument : IDsDocument {
		/// <summary>The physical source bundle filename.</summary>
		string SourceBundleFilename { get; }

		/// <summary>Whether an entry replacement is pending in the bundle workspace.</summary>
		bool HasPendingChanges { get; }
	}
}
