// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Narrow seam used by the close guard until the Save Bundle As implementation is available.
	/// PR-06 supplies the actual exporter; an empty composition deliberately treats Save as
	/// unavailable and leaves the workspace dirty.
	/// </summary>
	public interface IBundleWorkspaceSaveService {
		/// <summary>Writes the bundle to a new destination and returns whether it completed.</summary>
		bool SaveBundleAs(IDsBundleDocument document);
	}

	/// <summary>Protects dirty bundle workspaces from implicit document removal.</summary>
	[ExportDsDocumentCloseGuard("BundleWorkspace", DsDocumentCloseGuardConstants.ORDER_BUNDLE_WORKSPACE)]
	public sealed class BundleDocumentCloseGuard : IDsDocumentCloseGuard {
		readonly IMessageBoxService messageBoxService;
		readonly Lazy<IBundleWorkspaceSaveService>[] saveServices;

		sealed class BundleReferenceComparer : IEqualityComparer<IDsBundleDocument> {
			public bool Equals(IDsBundleDocument? x, IDsBundleDocument? y) => ReferenceEquals(x, y);
			public int GetHashCode(IDsBundleDocument obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
		}

		/// <summary>Creates a guard using the unavailable Save Bundle As seam.</summary>
		public BundleDocumentCloseGuard(IMessageBoxService messageBoxService)
			: this(messageBoxService, Array.Empty<Lazy<IBundleWorkspaceSaveService>>()) {
		}

		[ImportingConstructor]
		public BundleDocumentCloseGuard(IMessageBoxService messageBoxService,
			[ImportMany] IEnumerable<Lazy<IBundleWorkspaceSaveService>> saveServices) {
			this.messageBoxService = messageBoxService ?? throw new ArgumentNullException(nameof(messageBoxService));
			if (saveServices is null)
				throw new ArgumentNullException(nameof(saveServices));
			this.saveServices = saveServices.ToArray();
		}

		/// <inheritdoc/>
		public bool CanClose(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason) {
			if (documents is null)
				throw new ArgumentNullException(nameof(documents));
			var dirtyBundles = new List<IDsBundleDocument>();
			var seen = new HashSet<IDsBundleDocument>(new BundleReferenceComparer());
			foreach (IDsDocument document in documents) {
				if (document is IDsBundleDocument bundle && seen.Add(bundle) &&
					(bundle.HasPendingChanges || bundle.HasWorkspaceErrors))
					dirtyBundles.Add(bundle);
			}
			if (dirtyBundles.Count == 0)
				return true;

			string message = dirtyBundles.Count == 1
				? $"The bundle workspace for '{dirtyBundles[0].SourceBundleFilename}' has unapplied changes. Save Bundle As before closing?"
				: $"{dirtyBundles.Count} bundle workspaces have unapplied changes. Save Bundle As before closing?";
			MsgBoxButton result = messageBoxService.Show(message,
				MsgBoxButton.Yes | MsgBoxButton.No | MsgBoxButton.Cancel, Application.Current?.MainWindow);
			switch (result) {
			case MsgBoxButton.No:
				try {
					foreach (IDsBundleDocument bundle in dirtyBundles)
						bundle.RevertAllWorkspaceChanges();
					return true;
				}
				catch (Exception ex) {
					try { messageBoxService.Show(ex, "Unable to discard bundle workspace changes."); }
					catch { }
					return false;
				}
			case MsgBoxButton.Yes:
				foreach (IDsBundleDocument bundle in dirtyBundles) {
					if (!TrySave(bundle))
						return false;
				}
				return true;
			default:
				return false;
			}
		}

		bool TrySave(IDsBundleDocument bundle) {
			if (saveServices.Length == 0) {
				// PR-06 installs the saver exporter. Until then, keep the workspace dirty and
				// cancel without opening a second modal prompt.
				return false;
			}
			try {
				foreach (Lazy<IBundleWorkspaceSaveService> saver in saveServices)
					if (saver.Value.SaveBundleAs(bundle))
						return true;
			}
			catch (Exception ex) {
				try { messageBoxService.Show(ex, "Unable to save the bundle workspace."); }
				catch { }
			}
			return false;
		}
	}
}
