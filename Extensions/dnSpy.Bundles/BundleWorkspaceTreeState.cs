// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Threading;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Bundles.Extension {
	/// <summary>Shared display and refresh helpers for bundle workspace state.</summary>
	static class BundleWorkspaceTreeState {
		public static string GetEntryStateSuffix(BundleWorkspace workspace, BundleEntry entry) {
			if (workspace is null)
				throw new ArgumentNullException(nameof(workspace));
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			BundleWorkspaceEntryState state = workspace.GetEntryState(entry);
			if (state == BundleWorkspaceEntryState.Error)
				return workspace.HasReplacement(entry) ? " [modified, error]" : " [error]";
			if (state == BundleWorkspaceEntryState.Modified)
				return " [modified]";
			return state == BundleWorkspaceEntryState.Reverted ? " [reverted]" : string.Empty;
		}

		public static string GetBundleStateSuffix(BundleDsDocument document) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (document.HasWorkspaceErrors)
				return document.HasPendingChanges ? " [modified, error]" : " [error]";
			if (document.HasPendingChanges)
				return " [modified]";
			return document.Workspace.HasRevertedEntries ? " [reverted]" : string.Empty;
		}

		/// <summary>
		/// Refreshes only the loaded nodes below this bundle. Ordinary top-level nodes are not
		/// touched, and lazy bundle children are still left unloaded.
		/// </summary>
		public static void RefreshBundleTree(DsDocumentNode root) =>
			RefreshBundleTree(root, dispatcher: null);

		/// <summary>
		/// Refreshes a bundle tree on the dispatcher that owns its WPF nodes. Workspace events may
		/// be raised by background document loading, so this method never calls tree APIs from an
		/// arbitrary worker thread.
		/// </summary>
		public static void RefreshBundleTree(DsDocumentNode root, Dispatcher? dispatcher) {
			if (root is null)
				throw new ArgumentNullException(nameof(root));
			if (root is BundleDsDocumentNode bundleRoot && bundleRoot.IsDisposed)
				return;
			dispatcher ??= Application.Current?.Dispatcher;
			if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
				return;
			if (!dispatcher.CheckAccess()) {
				try {
					dispatcher.BeginInvoke(DispatcherPriority.DataBind,
						new Action(() => RefreshBundleTreeCore(root)));
				}
				catch (InvalidOperationException) {
					// The UI dispatcher can shut down between the state event and this enqueue.
				}
				return;
			}
			RefreshBundleTreeCore(root);
		}

		static void RefreshBundleTreeCore(DsDocumentNode root) {
			if (root is BundleDsDocumentNode bundleRoot && bundleRoot.IsDisposed)
				return;
			if (root.TreeNode is null)
				return;
			foreach (ITreeNode node in root.TreeNode.DescendantsAndSelf())
				node.RefreshUI();
		}
	}
}
