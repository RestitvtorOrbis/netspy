// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using dnlib.DotNet;
using dnlib.PE;
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
		public bool HasWorkspaceReplacement => false;

		/// <inheritdoc/>
		public bool IsReadyToRun => false;

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
		public void SetWorkspaceReplacement(byte[] bytes) =>
			throw new NotSupportedException("Bundle workspace editing is not available yet.");

		/// <inheritdoc/>
		public void RevertWorkspaceReplacement() =>
			throw new NotSupportedException("Bundle workspace editing is not available yet.");

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
