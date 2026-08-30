// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Resolves references for modules belonging to one <see cref="BundleDsDocument"/>.
	/// </summary>
	/// <remarks>
	/// The resolver is intentionally owned by one bundle root. It never installs itself in the
	/// global document service and it never considers a child of another open bundle a candidate.
	/// </remarks>
	public sealed class BundleAssemblyResolver : IAssemblyResolver, IDisposable {
		const AssemblyNameComparerFlags IdentityFlags = AssemblyNameComparerFlags.Name |
			AssemblyNameComparerFlags.PublicKeyToken | AssemblyNameComparerFlags.Culture |
			AssemblyNameComparerFlags.ContentType;
		static readonly AssemblyNameComparer identityComparer = new AssemblyNameComparer(IdentityFlags);
		static readonly AssemblyNameComparer fullComparer = new AssemblyNameComparer(
			IdentityFlags | AssemblyNameComparerFlags.Version);

		readonly BundleDsDocument bundleDocument;
		readonly IDsDocumentService? documentService;
		readonly IAssemblyResolver? fallbackResolver;
		readonly BundleWorkspaceDocumentIndex index;
		readonly object diagnosticLock = new object();
		string? lastDiagnostic;
		int disposed;

		public BundleAssemblyResolver(BundleDsDocument bundleDocument,
			IDsDocumentService? documentService = null, IAssemblyResolver? fallbackResolver = null) {
			this.bundleDocument = bundleDocument ?? throw new ArgumentNullException(nameof(bundleDocument));
			this.documentService = documentService;
			this.fallbackResolver = fallbackResolver ?? documentService?.AssemblyResolver;
			index = new BundleWorkspaceDocumentIndex(bundleDocument);
		}

		/// <summary>Gets the most recent resolver diagnostic, if one was produced.</summary>
		/// <remarks>
		/// Ambiguity and activation failures are retained here for a visible caller. A successful
		/// resolve clears a previous diagnostic; the text is deterministic and candidates are sorted
		/// by normalized manifest path.
		/// </remarks>
		public string? LastDiagnostic {
			get {
				lock (diagnosticLock)
					return lastDiagnostic;
			}
		}

		/// <summary>Alias for callers that expose resolver diagnostics as a single value.</summary>
		public string? Diagnostic => LastDiagnostic;

		/// <summary>Gets the per-bundle registry used by this resolver.</summary>
		internal BundleWorkspaceDocumentIndex WorkspaceIndex => index;

		AssemblyDef? IAssemblyResolver.Resolve(IAssembly assembly, ModuleDef sourceModule) =>
			Resolve(assembly, sourceModule);

		/// <summary>
		/// Resolves an assembly reference in the required contextual order.
		/// </summary>
		public AssemblyDef? Resolve(IAssembly assembly, ModuleDef? sourceModule) {
			if (assembly is null)
				throw new ArgumentNullException(nameof(assembly));
			if (VolatileDisposed)
				return null;
			SetDiagnostic(null);

			if (IsRequestingBundleModule(sourceModule)) {
				// Already activated modules in this workspace win before opening another entry. This
				// is a separate phase so a module that was explicitly selected is stable even if a
				// sibling with the same filename appears later in the manifest.
				BundleModuleDocument[] loaded = index.GetLoaded();
				AssemblyDef? loadedAssembly = SelectLoaded(assembly, loaded, out bool loadedAmbiguous);
				if (loadedAmbiguous)
					return null;
				if (loadedAssembly is not null) {
					SetDiagnostic(null);
					return loadedAssembly;
				}

				// Candidate activation is intentionally lazy. The per-entry guard is acquired by
				// BundleDsDocument.CreateManagedDocument() so a resolver probe cannot acquire it
				// twice and reject every otherwise-valid candidate as recursive.
				AssemblyDef? candidate = ResolveCandidate(assembly, out bool candidateAmbiguous);
				if (candidateAmbiguous)
					return null;
				if (candidate is not null) {
					SetDiagnostic(null);
					return candidate;
				}

				// Only after same-workspace lookup has failed do we consult ordinary top-level
				// documents and then the existing runtime/GAC/disk resolver.
				AssemblyDef? topLevel = ResolveTopLevel(assembly, allowCurrentBundle: true);
				if (topLevel is not null) {
					SetDiagnostic(null);
					return topLevel;
				}
			}
			else {
				// This resolver is contextual, but callers can still invoke it directly with an
				// unrelated source module. Preserve the ordinary resolver order for that path.
				AssemblyDef? topLevel = ResolveTopLevel(assembly, allowCurrentBundle: false);
				if (topLevel is not null) {
					SetDiagnostic(null);
					return topLevel;
				}
			}

			AssemblyDef? fallback = ResolveFallback(assembly, sourceModule);
			if (fallback is not null)
				SetDiagnostic(null);
			return fallback;
		}

		bool VolatileDisposed => System.Threading.Volatile.Read(ref disposed) != 0 || index.IsDisposed;

		AssemblyDef? ResolveTopLevel(IAssembly assembly, bool allowCurrentBundle) {
			if (documentService is null)
				return null;
			IDsDocument? document;
			try {
				document = documentService.FindAssembly(assembly,
					FindAssemblyOptions.All & ~FindAssemblyOptions.Version);
			}
			catch (Exception ex) when (IsResolverFailure(ex)) {
				SetDiagnostic("The global document service failed while looking up '" +
					GetAssemblyName(assembly) + "': " + ex.Message);
				return null;
			}
			if (document is null)
				return null;
			if (document is IDsBundleEntryDocument bundleEntry &&
				(!allowCurrentBundle || !ReferenceEquals(bundleEntry.BundleDocument, bundleDocument))) {
				SetDiagnostic("An assembly from another bundle was excluded while resolving '" +
					GetAssemblyName(assembly) + "'.");
				return null;
			}
			AssemblyDef? result = document.AssemblyDef;
			return result is not null && !IsForeignBundleAssembly(result) ? result : null;
		}

		AssemblyDef? SelectLoaded(IAssembly request, IEnumerable<BundleModuleDocument> loaded,
			out bool ambiguous) {
			var matches = new List<LoadedAssembly>();
			foreach (BundleModuleDocument document in loaded) {
				AssemblyDef? assembly = document.AssemblyDef;
				if (assembly is not null && identityComparer.Equals(request, assembly))
					matches.Add(new LoadedAssembly(document.Entry.RelativePath, assembly));
			}
			if (matches.Count == 0)
				ambiguous = false;
			else
				return SelectBest(request, matches, out ambiguous);
			return null;
		}

		AssemblyDef? ResolveCandidate(IAssembly request, out bool ambiguous) {
			ambiguous = false;
			string? simpleName = request.Name.String;
			if (string.IsNullOrEmpty(simpleName))
				return null;
			BundleEntry[] entries = index.GetCandidates(simpleName);
			if (entries.Length == 0)
				return null;

			var matches = new List<LoadedAssembly>();
			foreach (BundleEntry entry in entries) {
				if (index.TryGetFailure(entry.Index, out _))
					continue;
				try {
					if (!index.TryGetLoaded(entry.Index, out BundleModuleDocument? document)) {
						document = bundleDocument.CreateManagedDocument(entry);
					}
					AssemblyDef? assembly = document!.AssemblyDef;
					if (assembly is not null && identityComparer.Equals(request, assembly))
						matches.Add(new LoadedAssembly(entry.RelativePath, assembly));
				}
				catch (Exception ex) when (IsResolverFailure(ex)) {
					index.RecordFailure(entry.Index, ex);
					SetDiagnostic("Unable to load same-bundle assembly candidate '" + entry.RelativePath + "': " + ex.Message);
				}
			}

			if (matches.Count == 0)
				return null;
			return SelectBest(request, matches, out ambiguous);
		}

		AssemblyDef? SelectBest(IAssembly request, List<LoadedAssembly> matches,
			out bool ambiguous) {
			ambiguous = false;
			matches.Sort(LoadedAssembly.Compare);
			var exact = matches.Where(a => fullComparer.Equals(request, a.Assembly)).ToArray();
			if (exact.Length > 1) {
				ambiguous = true;
				SetAmbiguity(request, exact);
				return null;
			}
			if (exact.Length == 1)
				return exact[0].Assembly;

			// A compatible candidate has the requested identity fields and the nearest version.
			// Prefer the highest version not greater than the request (the normal binding policy);
			// if every candidate is newer, use the lowest newer version. Equal versions are still
			// ambiguous, regardless of manifest/tree order.
			Version requestedVersion = request.Version ?? new Version(0, 0, 0, 0);
			Version[] versions = matches.Select(a => a.Assembly.Version ?? new Version(0, 0, 0, 0))
				.Distinct().OrderBy(a => a).ToArray();
			if (versions.Length == 0)
				return null;
			Version[] compatibleLower = versions.Where(a => a.CompareTo(requestedVersion) <= 0).ToArray();
			Version selectedVersion;
			if (compatibleLower.Length == 0)
				selectedVersion = versions.Min()!;
			else
				selectedVersion = compatibleLower.Max()!;
			LoadedAssembly[] selected = matches.Where(a =>
				(a.Assembly.Version ?? new Version(0, 0, 0, 0)).Equals(selectedVersion)).ToArray();
			if (selected.Length > 1) {
				ambiguous = true;
				SetAmbiguity(request, selected);
				return null;
			}
			return selected.Length == 0 ? null : selected[0].Assembly;
		}

		AssemblyDef? ResolveFallback(IAssembly assembly, ModuleDef? sourceModule) {
			if (fallbackResolver is null || VolatileDisposed)
				return null;
			AssemblyDef? resolved;
			try {
				resolved = fallbackResolver.Resolve(assembly, sourceModule);
			}
			catch (Exception ex) when (IsResolverFailure(ex)) {
				SetDiagnostic("The existing assembly resolver failed while looking up '" +
					GetAssemblyName(assembly) + "': " + ex.Message);
				return null;
			}
			if (resolved is not null && IsForeignBundleAssembly(resolved)) {
				SetDiagnostic("An assembly from another bundle was excluded while resolving '" +
					GetAssemblyName(assembly) + "'.");
				return null;
			}
			return resolved;
		}

		bool IsRequestingBundleModule(ModuleDef? sourceModule) {
			if (sourceModule is null)
				return false;
			if (sourceModule.Context?.AssemblyResolver is BundleAssemblyResolver resolver)
				return ReferenceEquals(resolver, this);
			foreach (BundleModuleDocument document in index.GetLoaded()) {
				if (ReferenceEquals(document.ModuleDef, sourceModule))
					return true;
			}
			return false;
		}

		bool IsForeignBundleAssembly(AssemblyDef assembly) {
			foreach (ModuleDef module in assembly.Modules) {
				if (module.Context?.AssemblyResolver is BundleAssemblyResolver resolver &&
					!ReferenceEquals(resolver, this))
					return true;
			}
			return false;
		}

		void SetAmbiguity(IAssembly request, IEnumerable<LoadedAssembly> matches) {
			string paths = string.Join(", ", matches.Select(a => a.Path).OrderBy(a => a, StringComparer.Ordinal));
			SetDiagnostic("Ambiguous same-bundle assembly '" + GetAssemblyName(request) +
				"'; matching entries: " + paths + ".");
		}

		void SetDiagnostic(string? diagnostic) {
			lock (diagnosticLock)
				lastDiagnostic = diagnostic;
		}

		static string GetAssemblyName(IAssembly assembly) => assembly.Name.String ?? string.Empty;

		static bool IsResolverFailure(Exception ex) => ex is not OutOfMemoryException &&
			ex is not StackOverflowException;

		public void Dispose() {
			if (System.Threading.Interlocked.Exchange(ref disposed, 1) == 0)
				index.Dispose();
		}

		readonly struct LoadedAssembly {
			public LoadedAssembly(string path, AssemblyDef assembly) {
				Path = path;
				Assembly = assembly;
			}
			public string Path { get; }
			public AssemblyDef Assembly { get; }
			public static int Compare(LoadedAssembly x, LoadedAssembly y) =>
				StringComparer.Ordinal.Compare(x.Path, y.Path);
		}
	}
}
