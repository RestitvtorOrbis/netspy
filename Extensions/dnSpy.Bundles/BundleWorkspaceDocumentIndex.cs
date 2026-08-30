// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// The per-bundle registry used by <see cref="BundleAssemblyResolver"/>.
	/// </summary>
	/// <remarks>
	/// This index deliberately has no process-wide state. It contains manifest candidates and
	/// modules activated from one <see cref="BundleDsDocument"/> only. Manifest candidates are
	/// indexed by filename and are not opened until a resolver asks for the corresponding name.
	/// </remarks>
	sealed class BundleWorkspaceDocumentIndex : IDisposable {
		readonly object sync = new object();
		readonly Dictionary<string, List<BundleEntry>> candidates =
			new Dictionary<string, List<BundleEntry>>(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<int, BundleModuleDocument> loaded =
			new Dictionary<int, BundleModuleDocument>();
		readonly Dictionary<int, Exception> failures = new Dictionary<int, Exception>();
		readonly HashSet<int> loading = new HashSet<int>();
		bool disposed;

		public BundleWorkspaceDocumentIndex(BundleDsDocument bundleDocument) {
			if (bundleDocument is null)
				throw new ArgumentNullException(nameof(bundleDocument));
			foreach (BundleEntry entry in bundleDocument.Bundle.Entries) {
				if (entry.FileType != BundleFileType.Assembly)
					continue;
				foreach (string name in GetCandidateNames(entry.RelativePath)) {
					if (!candidates.TryGetValue(name, out List<BundleEntry>? list)) {
						list = new List<BundleEntry>();
						candidates.Add(name, list);
					}
					list.Add(entry);
				}
			}
			foreach (List<BundleEntry> list in candidates.Values)
				list.Sort(CompareEntries);
		}

		public bool IsDisposed {
			get {
				lock (sync)
					return disposed;
			}
		}

		public BundleModuleDocument[] GetLoaded() {
			lock (sync) {
				if (disposed)
					return Array.Empty<BundleModuleDocument>();
				var result = new BundleModuleDocument[loaded.Count];
				loaded.Values.CopyTo(result, 0);
				return result;
			}
		}

		public void RegisterLoaded(BundleModuleDocument document) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			lock (sync) {
				if (!disposed)
					loaded[document.Entry.Index] = document;
			}
		}

		public bool TryGetLoaded(int entryIndex, out BundleModuleDocument? document) {
			lock (sync) {
				if (!disposed && loaded.TryGetValue(entryIndex, out document))
					return true;
				document = null;
				return false;
			}
		}

		public bool TryGetFailure(int entryIndex, out Exception? error) {
			lock (sync) {
				if (!disposed && failures.TryGetValue(entryIndex, out error))
					return true;
				error = null;
				return false;
			}
		}

		public void RecordFailure(int entryIndex, Exception error) {
			if (error is null)
				throw new ArgumentNullException(nameof(error));
			lock (sync) {
				if (!disposed && !failures.ContainsKey(entryIndex))
					failures.Add(entryIndex, error);
			}
		}

		/// <summary>Gets a stable manifest-order-independent candidate list for one simple name.</summary>
		public BundleEntry[] GetCandidates(string simpleName) {
			if (simpleName is null)
				throw new ArgumentNullException(nameof(simpleName));
			lock (sync) {
				if (disposed || !candidates.TryGetValue(simpleName, out List<BundleEntry>? list))
					return Array.Empty<BundleEntry>();
				return list.ToArray();
			}
		}

		public bool TryBeginLoad(int entryIndex) {
			lock (sync) {
				if (disposed || loading.Contains(entryIndex))
					return false;
				loading.Add(entryIndex);
				return true;
			}
		}

		public void EndLoad(int entryIndex) {
			lock (sync)
				loading.Remove(entryIndex);
		}

		public void Dispose() {
			lock (sync) {
				if (disposed)
					return;
				disposed = true;
				candidates.Clear();
				loaded.Clear();
				failures.Clear();
				loading.Clear();
			}
		}

		static IEnumerable<string> GetCandidateNames(string path) {
			int separator = path.LastIndexOf('/');
			string filename = separator < 0 ? path : path.Substring(separator + 1);
			if (filename.Length == 0)
				yield break;
			int extension = filename.LastIndexOf('.');
			if (extension > 0)
				yield return filename.Substring(0, extension);
			yield return filename;
		}

		static int CompareEntries(BundleEntry x, BundleEntry y) =>
			StringComparer.Ordinal.Compare(x.RelativePath, y.RelativePath);
	}
}
