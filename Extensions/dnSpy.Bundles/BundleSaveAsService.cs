// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using dnSpy.Bundles;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.MVVM.Dialogs;

namespace dnSpy.Bundles.Extension {
	/// <summary>Publishes a bundle workspace to a new executable selected by the user.</summary>
	[Export(typeof(IBundleWorkspaceSaveService))]
	public sealed class BundleSaveAsService : IBundleWorkspaceSaveService {
		const string BundleFilter = "Single-file executable (*.exe)|*.exe|All files (*.*)|*.*";
		readonly IAppWindow appWindow;
		readonly IMessageBoxService messageBoxService;
		readonly IPickSaveFilename pickSaveFilename;
		readonly Func<BundleWorkspace, string, CancellationToken, string> publish;
		readonly WindowsBundleEligibilityInspector eligibilityInspector;

		/// <summary>Creates the production Save Bundle As service.</summary>
		[ImportingConstructor]
		public BundleSaveAsService(IAppWindow appWindow, IMessageBoxService messageBoxService,
			IPickSaveFilename pickSaveFilename)
			: this(appWindow, messageBoxService, pickSaveFilename,
				static (workspace, destination, cancellationToken) =>
					new WindowsBundlePublicationService().Publish(workspace, destination, cancellationToken)) {
		}

		/// <summary>
		/// Creates a service with a publication delegate. The delegate is a narrow seam for tests and
		/// platform adapters; it must return only after the complete output has been validated.
		/// </summary>
		public BundleSaveAsService(IAppWindow appWindow, IMessageBoxService messageBoxService,
			IPickSaveFilename pickSaveFilename,
			Func<BundleWorkspace, string, CancellationToken, string> publish) {
			this.appWindow = appWindow ?? throw new ArgumentNullException(nameof(appWindow));
			this.messageBoxService = messageBoxService ?? throw new ArgumentNullException(nameof(messageBoxService));
			this.pickSaveFilename = pickSaveFilename ?? throw new ArgumentNullException(nameof(pickSaveFilename));
			this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
			eligibilityInspector = new WindowsBundleEligibilityInspector();
		}

		/// <inheritdoc/>
		public bool SaveBundleAs(IDsBundleDocument document) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (document is not BundleDsDocument bundle) {
				ShowFailure("The selected document is not a supported .NET single-file bundle.");
				return false;
			}

			WindowsBundleEligibilityResult eligibility;
			try {
				eligibility = eligibilityInspector.Inspect(bundle.Workspace);
			}
			catch (Exception ex) when (IsExpectedFailure(ex)) {
				ShowFailure(ex);
				return false;
			}
			if (!eligibility.IsEligible) {
				ShowFailure(eligibility.Message);
				return false;
			}

			if (eligibility.HasAuthenticodeSignature &&
				messageBoxService.Show(eligibility.Message, MsgBoxButton.Yes | MsgBoxButton.No,
					appWindow.MainWindow) != MsgBoxButton.Yes)
				return false;

			string? destination;
			try {
				destination = PickDestination(bundle.SourceBundleFilename);
			}
			catch (Exception ex) when (IsExpectedFailure(ex)) {
				ShowFailure(ex, "Unable to choose a bundle destination.");
				return false;
			}
			if (destination is null)
				return false;

			try {
				string published = PublishWithProgress(bundle.Workspace, destination);
				bundle.RecordSuccessfulBundleSave(published);
				return true;
			}
			catch (OperationCanceledException) {
				return false;
			}
			catch (Exception ex) when (IsExpectedFailure(ex)) {
				ShowFailure(ex, "Unable to save the bundle.");
				return false;
			}
		}

		/// <summary>
		/// Publishes to an already-selected destination without opening the picker or progress UI.
		/// This overload is useful to callers that already own a modal save workflow.
		/// </summary>
		public bool SaveBundleAs(IDsBundleDocument document, string destination,
			CancellationToken cancellationToken = default) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (destination is null)
				throw new ArgumentNullException(nameof(destination));
			if (document is not BundleDsDocument bundle) {
				ShowFailure("The selected document is not a supported .NET single-file bundle.");
				return false;
			}
			try {
				WindowsBundleEligibilityResult eligibility = eligibilityInspector.Inspect(bundle.Workspace);
				if (!eligibility.IsEligible) {
					ShowFailure(eligibility.Message);
					return false;
				}
				if (eligibility.HasAuthenticodeSignature &&
					messageBoxService.Show(eligibility.Message, MsgBoxButton.Yes | MsgBoxButton.No,
						appWindow.MainWindow) != MsgBoxButton.Yes)
					return false;
				if (PathsEqual(bundle.SourceBundleFilename, destination)) {
					ShowFailure("The bundle destination must differ from the source bundle.");
					return false;
				}
				string published = publish(bundle.Workspace, destination, cancellationToken);
				bundle.RecordSuccessfulBundleSave(published);
				return true;
			}
			catch (OperationCanceledException) {
				return false;
			}
			catch (Exception ex) when (IsExpectedFailure(ex)) {
				ShowFailure(ex, "Unable to save the bundle.");
				return false;
			}
		}

		string? PickDestination(string sourceFilename) {
			while (true) {
				string? destination = pickSaveFilename.GetFilename(sourceFilename, "exe", BundleFilter);
				if (destination is null)
					return null;
				if (!PathsEqual(sourceFilename, destination))
					return destination;
				ShowFailure("The bundle destination must differ from the source bundle.");
			}
		}

		string PublishWithProgress(BundleWorkspace workspace, string destination) {
			var task = new BundlePublishProgressTask(workspace, destination, publish);
			var vm = new ProgressVM(System.Windows.Threading.Dispatcher.CurrentDispatcher, task);
			var window = new ProgressDlg {
				Owner = appWindow.MainWindow,
				Title = "Save Bundle As",
				DataContext = vm,
			};
			window.ShowDialog();
			if (vm.WasCanceled || task.Error is OperationCanceledException)
				throw new OperationCanceledException();
			if (task.Error is not null)
				throw task.Error;
			if (vm.WasError)
				throw new InvalidOperationException(vm.ErrorMessage ?? "The bundle save failed.");
			return task.PublishedPath ?? throw new InvalidDataException(
				"The bundle publisher returned no destination path.");
		}

		void ShowFailure(string message) {
			try {
				messageBoxService.Show(message, MsgBoxButton.OK, appWindow.MainWindow);
			}
			catch {
			}
		}

		void ShowFailure(Exception exception, string? message = null) {
			try {
				messageBoxService.Show(exception, message, appWindow.MainWindow);
			}
			catch {
			}
		}

		static bool PathsEqual(string left, string right) {
			try {
				string canonicalLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar);
				string canonicalRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar);
				return StringComparer.OrdinalIgnoreCase.Equals(canonicalLeft, canonicalRight);
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
				ex is PathTooLongException) {
				return false;
			}
		}

		static bool IsExpectedFailure(Exception ex) => ex is IOException ||
			ex is UnauthorizedAccessException || ex is ArgumentException ||
			ex is InvalidDataException || ex is InvalidOperationException ||
			ex is NotSupportedException || ex is PlatformNotSupportedException ||
			ex is PathTooLongException;
	}

	sealed class BundlePublishProgressTask : IProgressTask {
		readonly BundleWorkspace workspace;
		readonly string destination;
		readonly Func<BundleWorkspace, string, CancellationToken, string> publish;

		public BundlePublishProgressTask(BundleWorkspace workspace, string destination,
			Func<BundleWorkspace, string, CancellationToken, string> publish) {
			this.workspace = workspace;
			this.destination = destination;
			this.publish = publish;
		}

		public bool IsIndeterminate => true;
		public double ProgressMaximum => 1;
		public double ProgressMinimum => 0;
		public string? PublishedPath { get; private set; }
		public Exception? Error { get; private set; }

		public void Execute(IProgress progress) {
			try {
				progress.SetDescription("Rebuilding and validating the single-file executable...");
				progress.SetTotalProgress(0);
				progress.ThrowIfCancellationRequested();
				PublishedPath = publish(workspace, destination, progress.Token);
				progress.SetTotalProgress(1);
			}
			catch (Exception ex) {
				Error = ex;
				throw;
			}
		}
	}

	/// <summary>Shared command helpers for File and document-tree menu entries.</summary>
	static class BundleSaveAsCommands {
		public static BundleDsDocument[] GetBundles(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.GetBundleDocuments(
				BundleWorkspaceCommandSelection.GetContextNodes(context));
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "Save Bundle As...",
		Group = MenuConstants.GROUP_APP_MENU_FILE_SAVE, Order = 25)]
	sealed class SaveBundleAsFileCommand : MenuItemBase {
		readonly IDocumentTreeView documentTreeView;
		readonly IBundleWorkspaceSaveService saveService;

		[ImportingConstructor]
		SaveBundleAsFileCommand(IDocumentTreeView documentTreeView,
			IBundleWorkspaceSaveService saveService) {
			this.documentTreeView = documentTreeView;
			this.saveService = saveService;
		}

		BundleDsDocument[] GetBundles() => BundleWorkspaceCommandSelection.GetBundleDocuments(
			documentTreeView.TreeView.TopLevelSelection.OfType<DocumentTreeNodeData>());

		public override bool IsVisible(IMenuItemContext context) => GetBundles().Length != 0;
		public override bool IsEnabled(IMenuItemContext context) => GetBundles().Length != 0;
		public override void Execute(IMenuItemContext context) {
			foreach (BundleDsDocument bundle in GetBundles())
				saveService.SaveBundleAs(bundle);
		}
	}

	[ExportMenuItem(Header = "Save Bundle As...", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_MISC,
		Order = 120)]
	sealed class SaveBundleAsContextMenuCommand : MenuItemBase {
		readonly IBundleWorkspaceSaveService saveService;

		[ImportingConstructor]
		SaveBundleAsContextMenuCommand(IBundleWorkspaceSaveService saveService) => this.saveService = saveService;

		public override bool IsVisible(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.IsDocumentTreeContext(context) &&
			BundleSaveAsCommands.GetBundles(context).Length != 0;
		public override bool IsEnabled(IMenuItemContext context) =>
			BundleWorkspaceCommandSelection.IsDocumentTreeContext(context) &&
			BundleSaveAsCommands.GetBundles(context).Length != 0;
		public override void Execute(IMenuItemContext context) {
			foreach (BundleDsDocument bundle in BundleSaveAsCommands.GetBundles(context))
				saveService.SaveBundleAs(bundle);
		}
	}
}
