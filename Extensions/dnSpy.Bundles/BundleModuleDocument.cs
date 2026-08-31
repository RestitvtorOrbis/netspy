// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// A dnSpy managed document backed by one selected bundle entry.
	/// </summary>
	/// <remarks>
	/// Construction is the activation boundary. Before this class is created, the corresponding
	/// <see cref="BundleEntryDocument"/> contains metadata only and no entry bytes are read.
	/// </remarks>
	public sealed class BundleModuleDocument : DsDotNetDocumentBase, IDsBundleEntryDocument, IDisposable {
		readonly BundleEntryDocument entryDocument;
		readonly DsDocumentInfo serializedDocument;
		int disposed;

		internal BundleModuleDocument(BundleEntryDocument entryDocument, ModuleDefMD module)
			: base(module ?? throw new ArgumentNullException(nameof(module)), loadSyms: false) {
			this.entryDocument = entryDocument ?? throw new ArgumentNullException(nameof(entryDocument));
			Filename = entryDocument.Filename;
			serializedDocument = DsDocumentInfo.CreateDocument(Filename);
		}

		/// <summary>The metadata document from which this selected module was created.</summary>
		public BundleEntryDocument EntryDocument => entryDocument;

		/// <summary>The validated parser entry represented by this document.</summary>
		public BundleEntry Entry => entryDocument.Entry;

		/// <inheritdoc/>
		public BundleDsDocument BundleDocument => entryDocument.BundleDocument;

		IDsBundleDocument IDsBundleEntryDocument.BundleDocument => BundleDocument;

		/// <inheritdoc/>
		public string BundleRelativePath => entryDocument.Entry.RelativePath;

		/// <inheritdoc/>
		public bool HasWorkspaceReplacement => BundleDocument.Workspace.HasReplacement(Entry);

		/// <summary>Current workspace state, including a failed operation.</summary>
		public BundleWorkspaceEntryState WorkspaceState => BundleDocument.Workspace.GetEntryState(Entry);

		/// <inheritdoc/>
		public Exception? WorkspaceError => BundleDocument.Workspace.GetError(Entry);

		/// <summary>Metadata for the current workspace replacement, if one is installed.</summary>
		public BundleReplacementInfo? WorkspaceReplacementInfo =>
			BundleDocument.Workspace.GetReplacementInfo(Entry);

		/// <inheritdoc/>
		public bool IsReadyToRun => BundleManagedEntryAdapter.IsReadyToRun(ModuleDef!);

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => serializedDocument;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => BundleDocumentKey.Module(
			BundleDocument.SourceBundleFilename, BundleRelativePath);

		/// <summary>
		/// Creates the assembly wrapper used by dnSpy's existing assembly/module tree.
		/// </summary>
		/// <remarks>
		/// The wrapper is deliberately created only after this module has been selected. The
		/// annotation lets the bundle node provider supply an assembly node for the wrapper while
		/// its child continues through the ordinary <c>ModuleDocumentNodeImpl</c> provider.
		/// </remarks>
		public DsDotNetDocument CreateAssemblyDocument() {
			if (ModuleDef!.Assembly is null)
				throw new InvalidOperationException("A netmodule does not have an assembly wrapper.");
			var wrapper = DsDotNetDocument.CreateAssembly(this, ownsModule: false);
			wrapper.Filename = Filename;
			wrapper.AddAnnotation(new BundleAssemblyDocumentAnnotation(this));
			return wrapper;
		}

		/// <inheritdoc/>
		public void SetWorkspaceReplacement(byte[] bytes) {
			// Validate completely before touching the workspace. A replacement is deliberately
			// reopened with dnlib at this boundary so malformed output cannot become dirty state.
			try {
				using ModuleDefMD replacement = ValidateWorkspaceReplacement(bytes);
				SetWorkspaceReplacementCore(bytes);
			}
			catch (Exception ex) {
				BundleDocument.Workspace.RecordError(Entry, ex);
				throw;
			}
		}

		void SetWorkspaceReplacementCore(byte[] bytes) {
			if (IsStrongNameRequired(ModuleDef!))
				throw new InvalidOperationException(
					"An explicit strong-name disposition is required before replacing a signed bundle entry.");
			const BundleStrongNameDisposition disposition = BundleStrongNameDisposition.NotRequired;
			var info = new BundleReplacementInfo(
				$"Applied managed module replacement for {BundleRelativePath}", disposition);
			BundleDocument.Workspace.SetReplacements(new[] {
				new BundleWorkspaceReplacement(Entry, bytes, info),
			});
		}

		internal BundleWorkspaceReplacement CreateWorkspaceReplacement(byte[] bytes, BundleReplacementInfo info) {
			if (info is null)
				throw new ArgumentNullException(nameof(info));
			try {
				using ModuleDefMD replacement = ValidateWorkspaceReplacement(bytes);
				ValidateStrongNameDisposition(ModuleDef!, replacement, info);
			}
			catch (Exception ex) {
				BundleDocument.Workspace.RecordError(Entry, ex);
				throw;
			}
			return new BundleWorkspaceReplacement(Entry, bytes, info);
		}

		ModuleDefMD ValidateWorkspaceReplacement(byte[] bytes) {
			if (bytes is null)
				throw new ArgumentNullException(nameof(bytes));
			if (IsReadyToRun)
				throw new NotSupportedException("ReadyToRun bundle entries cannot be rewritten.");
			if (bytes.LongLength > BundleReaderOptions.DefaultMaximumEntrySize)
				throw new InvalidOperationException("The replacement exceeds the configured bundle entry size limit.");
			return ModuleDefMD.Load(bytes);
		}

		/// <inheritdoc/>
		public void RevertWorkspaceReplacement() => BundleDocument.Workspace.Revert(Entry);

		/// <inheritdoc/>
		public void RecordWorkspaceError(Exception error) =>
			BundleDocument.Workspace.RecordError(Entry, error);

		static bool IsStrongNameRequired(ModuleDef module) {
			var publicKey = module.Assembly?.PublicKey;
			return (publicKey is not null && !publicKey.IsNullOrEmpty) || module.IsStrongNameSigned;
		}

		static void ValidateStrongNameDisposition(ModuleDef original, ModuleDef replacement,
			BundleReplacementInfo info) {
			bool originalRequiresStrongName = IsStrongNameRequired(original);
			switch (info.StrongNameDisposition) {
			case BundleStrongNameDisposition.NotRequired:
				if (originalRequiresStrongName)
					throw new InvalidOperationException(
						"A signed bundle entry requires an explicit remove or re-sign disposition.");
				break;
			case BundleStrongNameDisposition.Removed:
				if (IsStrongNameRequired(replacement) || HasStrongNameDirectory(replacement))
					throw new InvalidOperationException(
						"The replacement still has strong-name metadata after removal was selected.");
				break;
			case BundleStrongNameDisposition.ReSigned:
				if (!IsStrongNameRequired(replacement) || !HasStrongNameDirectory(replacement))
					throw new InvalidOperationException(
						"The replacement has no strong-name signature after re-signing was selected.");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(info.StrongNameDisposition));
			}
		}

		static bool HasStrongNameDirectory(ModuleDef module) {
			if (module is not ModuleDefMD moduleMD)
				return false;
			var directory = moduleMD.Metadata.ImageCor20Header.StrongNameSignature;
			return directory.VirtualAddress != 0 || directory.Size != 0;
		}

		/// <inheritdoc/>
		/// <summary>
		/// Does not release the module. The owning <see cref="BundleDsDocument"/> releases every
		/// activated module exactly once, after all wrappers and nodes have become unreachable.
		/// </summary>
		public void Dispose() {
			// IDisposable is exposed for consumers that use a uniform document lifetime pattern. A
			// contained module is shared by its entry, wrapper, and tree nodes, so disposal belongs to
			// the bundle root rather than any one view.
		}

		internal void DisposeOwnedResources() {
			if (Interlocked.Exchange(ref disposed, 1) == 0)
				ModuleDef!.Dispose();
		}
	}

	/// <summary>Marks the helper-created assembly wrapper as a bundle-origin document.</summary>
	sealed class BundleAssemblyDocumentAnnotation {
		public BundleAssemblyDocumentAnnotation(BundleModuleDocument moduleDocument) =>
			ModuleDocument = moduleDocument ?? throw new ArgumentNullException(nameof(moduleDocument));

		public BundleModuleDocument ModuleDocument { get; }
	}
}
