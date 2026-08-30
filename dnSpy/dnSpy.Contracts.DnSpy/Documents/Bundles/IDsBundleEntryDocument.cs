// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using dnSpy.Contracts.Documents;

namespace dnSpy.Contracts.Documents.Bundles {
	/// <summary>
	/// A managed document originating from an official .NET single-file bundle.
	/// </summary>
	public interface IDsBundleEntryDocument : IDsDotNetDocument {
		/// <summary>The bundle containing this entry.</summary>
		IDsBundleDocument BundleDocument { get; }

		/// <summary>The validated relative path of this entry in the bundle.</summary>
		string BundleRelativePath { get; }

		/// <summary>Whether the workspace currently has replacement bytes for this entry.</summary>
		bool HasWorkspaceReplacement { get; }

		/// <summary>Whether this entry has been identified as ReadyToRun.</summary>
		bool IsReadyToRun { get; }

		/// <summary>Installs replacement bytes in the bundle workspace.</summary>
		void SetWorkspaceReplacement(byte[] bytes);

		/// <summary>Restores the original logical entry bytes.</summary>
		void RevertWorkspaceReplacement();
	}
}
