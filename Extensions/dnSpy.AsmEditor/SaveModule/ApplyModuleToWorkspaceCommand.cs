// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using dnSpy.AsmEditor.Commands;
using dnSpy.AsmEditor.Properties;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;

namespace dnSpy.AsmEditor.SaveModule {
	/// <summary>
	/// Applies edited managed bundle modules to their in-memory bundle workspaces.
	/// </summary>
	/// <remarks>
	/// This command intentionally has no file output path. All selected modules are serialized and
	/// reopened before any replacement is installed, so a writer or validation failure leaves the
	/// workspace exactly as it was.
	/// </remarks>
	[DebuggerDisplay("{Description}")]
	sealed class ApplyModuleToWorkspaceCommand {
		sealed class WorkspaceLogger : ILogger {
			public static readonly WorkspaceLogger Instance = new WorkspaceLogger();

			void ILogger.Log(object? sender, LoggerEvent loggerEvent, string format, params object[] args) {
				// dnlib diagnostics are surfaced by the writer as exceptions. There is no save log
				// window for an in-memory operation, and swallowing informational diagnostics here
				// keeps the command independent of the file-save progress UI.
			}

			bool ILogger.IgnoresEvent(LoggerEvent loggerEvent) => false;
		}

		[ExportMenuItem(Header = "Apply Module Changes to Bundle", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_MISC, Order = 90)]
		sealed class DocumentsCommand : DocumentsContextMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly IAppService appService;
			readonly IMessageBoxService messageBoxService;

			[ImportingConstructor]
			DocumentsCommand(Lazy<IUndoCommandService> undoCommandService, IAppService appService,
				IMessageBoxService messageBoxService) {
				this.undoCommandService = undoCommandService;
				this.appService = appService;
				this.messageBoxService = messageBoxService;
			}

			public override bool IsVisible(AsmEditorContext context) =>
				GetEntryDocuments(context.Nodes).Length != 0;

			public override void Execute(AsmEditorContext context) => Apply(
				GetEntryDocuments(context.Nodes), appService.MainWindow, messageBoxService,
				undoCommandService.Value);

			public override string? GetHeader(AsmEditorContext context) =>
				"Apply Module Changes to Bundle";
		}

		[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID,
			Header = "Apply Module Changes to Bundle",
			Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_MISC, Order = 90)]
		sealed class EditMenuCommand : EditMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly IAppService appService;
			readonly IMessageBoxService messageBoxService;

			[ImportingConstructor]
			EditMenuCommand(Lazy<IUndoCommandService> undoCommandService, IAppService appService,
				IMessageBoxService messageBoxService)
				: base(appService.DocumentTreeView) {
				this.undoCommandService = undoCommandService;
				this.appService = appService;
				this.messageBoxService = messageBoxService;
			}

			public override bool IsVisible(AsmEditorContext context) =>
				GetEntryDocuments(context.Nodes).Length != 0;

			public override void Execute(AsmEditorContext context) => Apply(
				GetEntryDocuments(context.Nodes), appService.MainWindow, messageBoxService,
				undoCommandService.Value);

			public override string? GetHeader(AsmEditorContext context) =>
				"Apply Module Changes to Bundle";
		}

		public string Description => "Apply Module Changes to Bundle";

		/// <summary>
		/// Serializes and installs one or more managed bundle entries as one command operation.
		/// </summary>
		internal static bool Apply(IReadOnlyList<IDsBundleEntryDocument> documents,
			System.Windows.Window? ownerWindow, IMessageBoxService messageBoxService,
			IUndoCommandService? undoCommandService) {
			return ApplyCore(documents, ownerWindow, messageBoxService, undoCommandService,
				removeStrongName: null, reSignKeyFileName: null);
		}

		// Narrow test seam for exercising the explicit strong-name choices without opening WPF
		// dialogs. Production menu exports always use the overload above.
		internal static bool ApplyWithStrongNameChoices(IReadOnlyList<IDsBundleEntryDocument> documents,
			System.Windows.Window? ownerWindow, IMessageBoxService messageBoxService,
			IUndoCommandService? undoCommandService,
			Func<IDsBundleEntryDocument, bool>? removeStrongName,
			Func<IDsBundleEntryDocument, string?>? reSignKeyFileName) {
			return ApplyCore(documents, ownerWindow, messageBoxService, undoCommandService,
				removeStrongName, reSignKeyFileName);
		}

		static bool ApplyCore(IReadOnlyList<IDsBundleEntryDocument> documents,
			System.Windows.Window? ownerWindow, IMessageBoxService messageBoxService,
			IUndoCommandService? undoCommandService,
			Func<IDsBundleEntryDocument, bool>? removeStrongName,
			Func<IDsBundleEntryDocument, string?>? reSignKeyFileName) {
			if (documents is null)
				throw new ArgumentNullException(nameof(documents));
			if (messageBoxService is null)
				throw new ArgumentNullException(nameof(messageBoxService));
			if (documents.Count == 0)
				return false;

			// Remove duplicate selections while preserving selection order. A module can be
			// represented by both its assembly and module nodes in the tree.
			var uniqueDocuments = new List<IDsBundleEntryDocument>(documents.Count);
			var seen = new HashSet<IDsBundleEntryDocument>();
			foreach (IDsBundleEntryDocument document in documents) {
				if (document is null || !seen.Add(document))
					continue;
				uniqueDocuments.Add(document);
			}
			if (uniqueDocuments.Count == 0)
				return false;
			IDsBundleDocument bundleDocument = uniqueDocuments[0].BundleDocument;
			if (bundleDocument is null) {
				messageBoxService.Show(
					"The selected module does not have a valid bundle workspace.",
					MsgBoxButton.OK, ownerWindow);
				return false;
			}
			if (uniqueDocuments.Any(a => !ReferenceEquals(a.BundleDocument, bundleDocument))) {
				messageBoxService.Show(
					"Apply Module Changes to Bundle supports one bundle workspace at a time.",
					MsgBoxButton.OK, ownerWindow);
				return false;
			}

			// Build and validate every output before touching the workspace. The bundle contract then
			// installs all candidates in one atomic workspace transaction.
			var serialized = new List<SerializedReplacement>(uniqueDocuments.Count);
			try {
				foreach (IDsBundleEntryDocument document in uniqueDocuments) {
					if (document.IsReadyToRun) {
						messageBoxService.Show(
							$"The ReadyToRun bundle entry '{document.BundleRelativePath}' cannot be applied to the bundle workspace. ReadyToRun rewriting is not supported.",
							MsgBoxButton.OK, ownerWindow);
						return false;
					}
				}

				foreach (IDsBundleEntryDocument document in uniqueDocuments) {
					var options = new SaveModuleOptionsVM(document);
					if (StrongNameSaveGuard.IsRequired(options.Module) &&
						(removeStrongName is not null || reSignKeyFileName is not null)) {
						if (removeStrongName?.Invoke(document) == true)
							options.SetStrongNameSaveChoice(StrongNameSaveDisposition.Remove, null);
						else
							options.SetStrongNameSaveChoice(StrongNameSaveDisposition.ReSign,
								reSignKeyFileName?.Invoke(document));
					}
					else if (!options.PrepareStrongNameSave(ownerWindow))
						return false;

					byte[] bytes;
					using (var stream = new MemoryStream()) {
						if (!ModuleSerializationService.WriteToStream(options, stream,
							WorkspaceLogger.Instance, progressUpdated: null))
							throw new InvalidOperationException(
								$"The edited module '{document.BundleRelativePath}' could not be serialized with the selected strong-name disposition.");
						bytes = stream.ToArray();
					}

					// Validate the complete byte sequence independently of the document implementation.
					// The implementation then repeats this check before SetReplacement, protecting callers
					// that invoke the contract directly.
					using (ModuleDefMD reopened = ModuleDefMD.Load(bytes)) {
						// Loading the complete image is the validation contract. Bundle file type says
						// which managed entry was selected, but valid netmodules do not have an AssemblyDef.
						_ = reopened.Mvid;
					}
					serialized.Add(new SerializedReplacement(document, bytes,
						ToContractDisposition(options.StrongNameSaveDisposition),
						options.StrongNameKeyFileName));
				}

				var candidates = serialized.Select(a => new dnSpy.Contracts.Documents.Bundles.BundleWorkspaceReplacement(
					a.Document, a.Bytes, a.StrongNameDisposition, a.StrongNameKeyFileName)).ToArray();
				bundleDocument.SetWorkspaceReplacements(candidates);
			}
			catch (Exception ex) {
				// The batch contract validates and stages every candidate before swapping workspace state.
				// Any failure is reported without claiming success.
				messageBoxService.Show(ex,
					"Unable to apply the edited module to the bundle workspace. No source bundle file was changed.",
					ownerWindow);
				return false;
			}

			// Mark the module's undo state as saved to the workspace, not to disk. Undo remains
			// available; another edit will make the module dirty again and can be applied separately.
			foreach (IDsBundleEntryDocument document in uniqueDocuments) {
				if (undoCommandService is not null) {
					var undoObject = undoCommandService.GetUndoObject(document);
					if (undoObject is not null)
						undoCommandService.MarkAsSaved(undoObject);
				}
			}
			return true;
		}

		static DsBundleStrongNameDisposition ToContractDisposition(StrongNameSaveDisposition disposition) =>
			disposition switch {
			StrongNameSaveDisposition.Remove => DsBundleStrongNameDisposition.Removed,
			StrongNameSaveDisposition.ReSign => DsBundleStrongNameDisposition.ReSigned,
			// PrepareStrongNameSave returns true with Cancel only for an unsigned module.
			StrongNameSaveDisposition.Cancel => DsBundleStrongNameDisposition.NotRequired,
			_ => throw new ArgumentOutOfRangeException(nameof(disposition)),
		};

		sealed class SerializedReplacement {
			public SerializedReplacement(IDsBundleEntryDocument document, byte[] bytes,
				DsBundleStrongNameDisposition strongNameDisposition, string? strongNameKeyFileName) {
				Document = document;
				Bytes = bytes;
				StrongNameDisposition = strongNameDisposition;
				StrongNameKeyFileName = strongNameKeyFileName;
			}

			public IDsBundleEntryDocument Document { get; }
			public byte[] Bytes { get; }
			public DsBundleStrongNameDisposition StrongNameDisposition { get; }
			public string? StrongNameKeyFileName { get; }
		}

		internal static IDsBundleEntryDocument[] GetEntryDocuments(DocumentTreeNodeData[] nodes) {
			if (nodes is null || nodes.Length == 0)
				return Array.Empty<IDsBundleEntryDocument>();

			var result = new List<IDsBundleEntryDocument>();
			foreach (DocumentTreeNodeData node in nodes) {
				if (node is null)
					continue;
				DsDocumentNode? documentNode = node.GetDocumentNode();
				if (documentNode?.Document is IDsBundleEntryDocument direct) {
					result.Add(direct);
					continue;
				}

				// Selecting an assembly wrapper should apply its ordinary module child, matching the
				// existing Save Module command's assembly/module selection behavior.
				if (documentNode is AssemblyDocumentNode assemblyNode) {
					assemblyNode.TreeNode.EnsureChildrenLoaded();
					ModuleDocumentNode? moduleNode = assemblyNode.TreeNode.DataChildren
						.OfType<ModuleDocumentNode>().FirstOrDefault();
					if (moduleNode?.Document is IDsBundleEntryDocument child)
						result.Add(child);
				}
			}
			return result.ToArray();
		}
	}
}
