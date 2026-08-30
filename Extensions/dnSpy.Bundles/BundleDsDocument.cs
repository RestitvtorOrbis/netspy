// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Bundles;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Top-level document for a successfully opened official single-file bundle.
	/// </summary>
	public sealed class BundleDsDocument : DsDocument, IDsBundleDocument, IDisposable {
		readonly DsDocumentInfo serializedDocument;
		readonly string sourceFilename;
		readonly Func<BundleEntry, Stream> openLogicalRead;
		readonly IAssemblyResolver? globalAssemblyResolver;
		readonly BundleAssemblyResolver bundleAssemblyResolver;
		readonly object managedDocumentsLock = new object();
		readonly Dictionary<int, BundleModuleDocument> managedDocuments = new Dictionary<int, BundleModuleDocument>();
		readonly Dictionary<int, Exception> managedDocumentErrors = new Dictionary<int, Exception>();
		int disposed;

		public BundleDsDocument(DsDocumentInfo serializedDocument, BundleFile bundle,
			BundleTextViewOptions? textViewOptions = null,
			Func<BundleEntry, Stream>? openLogicalRead = null,
			IAssemblyResolver? assemblyResolver = null,
			IDsDocumentService? documentService = null) {
			if (bundle is null)
				throw new ArgumentNullException(nameof(bundle));
			this.serializedDocument = serializedDocument;
			sourceFilename = GetFullPath(bundle.Filename);
			Bundle = bundle;
			TextViewOptions = textViewOptions ?? BundleTextViewOptions.Default;
			this.openLogicalRead = openLogicalRead ?? (static entry => entry.OpenLogicalRead());
			globalAssemblyResolver = assemblyResolver ?? documentService?.AssemblyResolver;
			bundleAssemblyResolver = new BundleAssemblyResolver(this, documentService, globalAssemblyResolver);
			Filename = serializedDocument.Name;
		}

		/// <summary>Validated bundle metadata and lazy entry access.</summary>
		public BundleFile Bundle { get; }

		/// <summary>Options used by bounded text previews of entries in this bundle.</summary>
		public BundleTextViewOptions TextViewOptions { get; }

		/// <summary>
		/// Opens one entry through the bundle's bounded logical stream. The optional factory is a
		/// narrow stream seam used by extension tests; production callers use the parser stream.
		/// </summary>
		internal Stream OpenLogicalRead(BundleEntry entry) {
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			return openLogicalRead(entry);
		}

		/// <summary>The physical source filename retained for later workspace operations.</summary>
		public string SourceFilename => sourceFilename;

		/// <summary>Resolver scoped to modules activated from this bundle only.</summary>
		public BundleAssemblyResolver AssemblyResolver => bundleAssemblyResolver;

		/// <inheritdoc/>
		public string SourceBundleFilename => SourceFilename;

		/// <inheritdoc/>
		public bool HasPendingChanges => false;

		/// <summary>
		/// Creates the module context for a selected managed entry. Every child receives this
		/// root's resolver; that resolver delegates to the existing document-service resolver only
		/// after same-bundle lookup has completed.
		/// </summary>
		internal ModuleContext CreateModuleContext() =>
			DsDotNetDocumentBase.CreateModuleContext(bundleAssemblyResolver);

		internal BundleModuleDocument? GetManagedDocument(BundleEntryDocument entryDocument) {
			if (entryDocument is null)
				throw new ArgumentNullException(nameof(entryDocument));
			if (!ReferenceEquals(entryDocument.BundleDocument, this))
				throw new ArgumentException("The entry does not belong to this bundle.", nameof(entryDocument));
			if (entryDocument.Entry.FileType != BundleFileType.Assembly || !OwnsEntry(entryDocument.Entry))
				throw new ArgumentException("The entry is not a managed entry in this bundle.", nameof(entryDocument));
			lock (managedDocumentsLock) {
				return managedDocuments.TryGetValue(entryDocument.Entry.Index, out BundleModuleDocument? document)
					? document : null;
			}
		}

		internal BundleModuleDocument CreateManagedDocument(BundleEntryDocument entryDocument) {
			if (entryDocument is null)
				throw new ArgumentNullException(nameof(entryDocument));
			if (!ReferenceEquals(entryDocument.BundleDocument, this))
				throw new ArgumentException("The entry does not belong to this bundle.", nameof(entryDocument));
			if (entryDocument.Entry.FileType != BundleFileType.Assembly || !OwnsEntry(entryDocument.Entry))
				throw new ArgumentException("The entry is not a managed entry in this bundle.", nameof(entryDocument));
			lock (managedDocumentsLock) {
				EnsureNotDisposed();
				if (managedDocuments.TryGetValue(entryDocument.Entry.Index, out BundleModuleDocument? document))
					return document;
				if (managedDocumentErrors.TryGetValue(entryDocument.Entry.Index, out Exception? error))
					throw new InvalidOperationException("The managed bundle entry could not be loaded.", error);
				if (!bundleAssemblyResolver.WorkspaceIndex.TryBeginLoad(entryDocument.Entry.Index))
					throw new InvalidOperationException("Recursive managed bundle entry loading was rejected.");

				try {
					document = new BundleModuleDocument(entryDocument,
						BundleManagedEntryAdapter.LoadModule(this, entryDocument.Entry));
					managedDocuments.Add(entryDocument.Entry.Index, document);
					bundleAssemblyResolver.WorkspaceIndex.RegisterLoaded(document);
					return document;
				}
				catch (Exception ex) {
					if (ex is not OutOfMemoryException && ex is not StackOverflowException)
						managedDocumentErrors[entryDocument.Entry.Index] = ex;
					throw;
				}
				finally {
					bundleAssemblyResolver.WorkspaceIndex.EndLoad(entryDocument.Entry.Index);
				}
			}
		}

		/// <summary>
		/// Activates a manifest assembly without requiring the tree to have created an entry node.
		/// Resolver probes use this overload so candidate lookup remains lazy and UI-independent.
		/// </summary>
		internal BundleModuleDocument CreateManagedDocument(BundleEntry entry) {
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			if (entry.FileType != BundleFileType.Assembly || !OwnsEntry(entry))
				throw new ArgumentException("The entry is not a managed entry in this bundle.", nameof(entry));
			lock (managedDocumentsLock) {
				EnsureNotDisposed();
				if (managedDocuments.TryGetValue(entry.Index, out BundleModuleDocument? document))
					return document;
				if (managedDocumentErrors.TryGetValue(entry.Index, out Exception? error))
					throw new InvalidOperationException("The managed bundle entry could not be loaded.", error);
				if (!bundleAssemblyResolver.WorkspaceIndex.TryBeginLoad(entry.Index))
					throw new InvalidOperationException("Recursive managed bundle entry loading was rejected.");

				try {
					var entryDocument = new BundleEntryDocument(
						new BundleFolderDocument(this, BundleFolderKind.Assemblies), entry);
					document = new BundleModuleDocument(entryDocument,
						BundleManagedEntryAdapter.LoadModule(this, entry));
					managedDocuments.Add(entry.Index, document);
					bundleAssemblyResolver.WorkspaceIndex.RegisterLoaded(document);
					return document;
				}
				catch (Exception ex) {
					if (ex is not OutOfMemoryException && ex is not StackOverflowException)
						managedDocumentErrors[entry.Index] = ex;
					throw;
				}
				finally {
					bundleAssemblyResolver.WorkspaceIndex.EndLoad(entry.Index);
				}
			}
		}

		bool OwnsEntry(BundleEntry entry) {
			foreach (BundleEntry candidate in Bundle.Entries) {
				if (ReferenceEquals(candidate, entry))
					return true;
			}
			return false;
		}

		internal bool TryCreateManagedDocument(BundleEntryDocument entryDocument,
			out BundleModuleDocument? document, out Exception? error) {
			document = null;
			error = null;
			try {
				document = CreateManagedDocument(entryDocument);
				return true;
			}
			catch (Exception ex) {
				error = ex;
				return false;
			}
		}

		void EnsureNotDisposed() {
			if (Volatile.Read(ref disposed) != 0)
				throw new ObjectDisposedException(nameof(BundleDsDocument));
		}

		/// <inheritdoc/>
		public override DsDocumentInfo? SerializedDocument => serializedDocument;

		/// <inheritdoc/>
		public override IDsDocumentNameKey Key => new FilenameKey(SourceFilename);

		/// <inheritdoc/>
		protected override TList<IDsDocument> CreateChildren() {
			// Keep the four categories stable even when a bundle does not contain an entry in
			// one of them. Reading Bundle.Entries only touches validated manifest metadata; it
			// never opens an entry stream or materializes managed bytes.
			return new TList<IDsDocument> {
				new BundleFolderDocument(this, BundleFolderKind.Assemblies),
				new BundleFolderDocument(this, BundleFolderKind.Runtime),
				new BundleFolderDocument(this, BundleFolderKind.Native),
				new BundleFolderDocument(this, BundleFolderKind.SymbolsAndOther),
			};
		}

		/// <inheritdoc/>
		public void Dispose() {
			if (Interlocked.Exchange(ref disposed, 1) != 0)
				return;
			BundleModuleDocument[] documents;
			lock (managedDocumentsLock) {
				documents = new BundleModuleDocument[managedDocuments.Count];
				managedDocuments.Values.CopyTo(documents, 0);
				managedDocuments.Clear();
				managedDocumentErrors.Clear();
			}
			try {
				foreach (BundleModuleDocument document in documents)
					document.DisposeOwnedResources();
			}
			finally {
				bundleAssemblyResolver.Dispose();
				Bundle.Dispose();
			}
		}

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
