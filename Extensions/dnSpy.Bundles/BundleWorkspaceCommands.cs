// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Bundles.Extension {
	/// <summary>Tree selection helpers shared by bundle workspace commands.</summary>
	static class BundleWorkspaceCommandSelection {
		public static bool IsDocumentTreeContext(IMenuItemContext context) =>
			context.CreatorObject.Guid == new Guid(MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID);

		public static DocumentTreeNodeData[] GetContextNodes(IMenuItemContext context) =>
			context.Find<TreeNodeData[]>()?.OfType<DocumentTreeNodeData>().ToArray()
			?? Array.Empty<DocumentTreeNodeData>();

		public static BundleEntryDocument[] GetEntryDocuments(IEnumerable<DocumentTreeNodeData> nodes) {
			var entries = new List<BundleEntryDocument>();
			var seen = new HashSet<BundleEntryDocument>();
			foreach (DocumentTreeNodeData node in nodes) {
				if (node is null)
					continue;
				DsDocumentNode? documentNode = node.GetDocumentNode();
				if (documentNode?.Document is BundleEntryDocument entry && seen.Add(entry))
					entries.Add(entry);
				else if (documentNode?.Document is BundleEntryErrorDocument entryError &&
					seen.Add(entryError.EntryDocument))
					entries.Add(entryError.EntryDocument);
				else if (documentNode?.Document is BundleModuleDocument module) {
					BundleEntryDocument moduleEntry = module.EntryDocument;
					if (seen.Add(moduleEntry))
						entries.Add(moduleEntry);
				}
				else if (documentNode?.Document is DsDotNetDocument assembly) {
					foreach (IDsDocument child in assembly.Children) {
						if (child is BundleModuleDocument childModule && seen.Add(childModule.EntryDocument))
							entries.Add(childModule.EntryDocument);
					}
				}
			}
			return entries.ToArray();
		}

		public static BundleDsDocument[] GetBundleDocuments(IEnumerable<DocumentTreeNodeData> nodes) {
			var bundles = new List<BundleDsDocument>();
			var seen = new HashSet<BundleDsDocument>();
			foreach (DocumentTreeNodeData node in nodes) {
				if (node is null)
					continue;
				DsDocumentNode? documentNode = node.GetDocumentNode();
				BundleDsDocument? bundle = GetBundleDocument(documentNode);
				if (bundle is not null && seen.Add(bundle))
					bundles.Add(bundle);
			}
			return bundles.ToArray();
		}

		static BundleDsDocument? GetBundleDocument(DsDocumentNode? documentNode) {
			if (documentNode?.Document is BundleDsDocument bundle)
				return bundle;
			if (documentNode?.Document is BundleFolderDocument folder)
				return folder.BundleDocument;
			if (documentNode?.Document is BundleEntryDocument entry)
				return entry.BundleDocument;
			if (documentNode?.Document is BundleEntryErrorDocument entryError)
				return entryError.EntryDocument.BundleDocument;
			if (documentNode?.Document is BundleModuleDocument module)
				return module.BundleDocument;
			if (documentNode?.Document is DsDotNetDocument assembly) {
				foreach (IDsDocument child in assembly.Children) {
					if (child is BundleModuleDocument childModule)
						return childModule.BundleDocument;
				}
			}

			// A provider-created child can be selected through an ordinary module/assembly node. The
			// nearest document node above it is normally enough, but the top-node fallback handles
			// mixed selections where the child provider does not expose the bundle document directly.
			if (documentNode is not null && documentNode.TreeNode is not null) {
				DsDocumentNode? top = documentNode.GetTopNode();
				if (top?.Document is BundleDsDocument topBundle)
					return topBundle;
			}
			return null;
		}
	}

	abstract class BundleWorkspaceEntryMenuCommand : MenuItemBase {
		protected static BundleEntryDocument[] GetEntries(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.GetEntryDocuments(
				BundleWorkspaceCommandSelection.GetContextNodes(context));

		protected static bool IsTreeContext(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.IsDocumentTreeContext(context);

		internal static bool CanRevert(BundleEntryDocument entry) =>
			entry.BundleDocument.Workspace.HasReplacement(entry.Entry) ||
			entry.WorkspaceState == BundleWorkspaceEntryState.Modified ||
			entry.WorkspaceState == BundleWorkspaceEntryState.Error;
	}

	[ExportMenuItem(Header = "Revert Bundle Entry", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_MISC, Order = 100)]
	sealed class RevertBundleEntryContextMenuCommand : BundleWorkspaceEntryMenuCommand {
		public override bool IsVisible(IMenuItemContext context) =>
			IsTreeContext(context) && GetEntries(context).Length != 0;

		public override bool IsEnabled(IMenuItemContext context) =>
			GetEntries(context).Any(CanRevert);

		public override void Execute(IMenuItemContext context) {
			foreach (BundleEntryDocument entry in GetEntries(context))
				if (CanRevert(entry))
					entry.BundleDocument.Workspace.Revert(entry.Entry);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID,
		Header = "Revert Bundle Entry", Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_MISC, Order = 100)]
	sealed class RevertBundleEntryEditMenuCommand : MenuItemBase {
		readonly IDocumentTreeView documentTreeView;

		[ImportingConstructor]
		RevertBundleEntryEditMenuCommand(IDocumentTreeView documentTreeView) =>
			this.documentTreeView = documentTreeView;

		BundleEntryDocument[] GetEntries() => BundleWorkspaceCommandSelection.GetEntryDocuments(
			documentTreeView.TreeView.TopLevelSelection.OfType<DocumentTreeNodeData>());

		public override bool IsVisible(IMenuItemContext context) => GetEntries().Length != 0;
		public override bool IsEnabled(IMenuItemContext context) => GetEntries().Any(
			BundleWorkspaceEntryMenuCommand.CanRevert);
		public override void Execute(IMenuItemContext context) {
			foreach (BundleEntryDocument entry in GetEntries())
				if (BundleWorkspaceEntryMenuCommand.CanRevert(entry))
					entry.BundleDocument.Workspace.Revert(entry.Entry);
		}
	}

	abstract class RevertAllBundleChangesMenuCommand : MenuItemBase {
		internal static bool CanRevertAll(BundleDsDocument bundle) =>
			bundle.HasPendingChanges || bundle.HasWorkspaceErrors ||
			bundle.Workspace.HasSavedReplacements;

		protected static BundleDsDocument[] GetBundles(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.GetBundleDocuments(
				BundleWorkspaceCommandSelection.GetContextNodes(context));
	}

	[ExportMenuItem(Header = "Revert All Bundle Changes", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_MISC, Order = 110)]
	sealed class RevertAllBundleChangesContextMenuCommand : RevertAllBundleChangesMenuCommand {
		public override bool IsVisible(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.IsDocumentTreeContext(context) && GetBundles(context).Length != 0;
		public override bool IsEnabled(IMenuItemContext context) => GetBundles(context).Any(CanRevertAll);
		public override void Execute(IMenuItemContext context) {
			foreach (BundleDsDocument bundle in GetBundles(context))
				if (CanRevertAll(bundle))
					bundle.RevertAllWorkspaceChanges();
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID,
		Header = "Revert All Bundle Changes", Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_MISC, Order = 110)]
	sealed class RevertAllBundleChangesEditMenuCommand : MenuItemBase {
		readonly IDocumentTreeView documentTreeView;

		[ImportingConstructor]
		RevertAllBundleChangesEditMenuCommand(IDocumentTreeView documentTreeView) =>
			this.documentTreeView = documentTreeView;

		BundleDsDocument[] GetBundles() => BundleWorkspaceCommandSelection.GetBundleDocuments(
			documentTreeView.TreeView.TopLevelSelection.OfType<DocumentTreeNodeData>());

		public override bool IsVisible(IMenuItemContext context) => GetBundles().Length != 0;
		public override bool IsEnabled(IMenuItemContext context) => GetBundles().Any(
			RevertAllBundleChangesMenuCommand.CanRevertAll);
		public override void Execute(IMenuItemContext context) {
			foreach (BundleDsDocument bundle in GetBundles())
				if (RevertAllBundleChangesMenuCommand.CanRevertAll(bundle))
					bundle.RevertAllWorkspaceChanges();
		}
	}
}
