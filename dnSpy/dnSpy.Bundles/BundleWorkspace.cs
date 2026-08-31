// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace dnSpy.Bundles {
	/// <summary>
	/// Owns the original bundle and transactionally tracks logical entry replacements.
	/// </summary>
	public sealed class BundleWorkspace : IDisposable {
		readonly object gate = new object();
		Dictionary<BundleEntry, Replacement> replacements =
			new Dictionary<BundleEntry, Replacement>(BundleEntryReferenceComparer.Instance);
		Dictionary<BundleEntry, Exception> errors =
			new Dictionary<BundleEntry, Exception>(BundleEntryReferenceComparer.Instance);
		HashSet<BundleEntry> reverted = new HashSet<BundleEntry>(BundleEntryReferenceComparer.Instance);
		int disposed;

		sealed class Replacement {
			public Replacement(byte[] bytes, BundleReplacementInfo info) {
				Bytes = bytes;
				Info = info;
			}

			public byte[] Bytes { get; }
			public BundleReplacementInfo Info { get; }
		}

		sealed class BundleEntryReferenceComparer : IEqualityComparer<BundleEntry> {
			public static readonly BundleEntryReferenceComparer Instance = new BundleEntryReferenceComparer();
			public bool Equals(BundleEntry? x, BundleEntry? y) => ReferenceEquals(x, y);
			public int GetHashCode(BundleEntry obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
		}

		/// <summary>Creates a workspace which takes ownership of <paramref name="bundle"/>.</summary>
		public BundleWorkspace(BundleFile bundle) {
			Bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
			bundle.EnsureNotDisposed();
		}

		/// <summary>The source bundle owned by this workspace.</summary>
		public BundleFile Bundle { get; }

		/// <summary>True when at least one entry has a replacement.</summary>
		public bool HasChanges {
			get {
				lock (gate) {
					EnsureNotDisposed();
					return replacements.Count != 0;
				}
			}
		}

		/// <summary>Returns whether one entry currently has replacement bytes.</summary>
		public bool HasReplacement(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				return replacements.ContainsKey(entry);
			}
		}

		/// <summary>Returns whether an entry currently has an operation error.</summary>
		public bool HasError(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				return errors.ContainsKey(entry);
			}
		}

		/// <summary>Gets the last operation error for an entry, if any.</summary>
		public Exception? GetError(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				return errors.TryGetValue(entry, out Exception? error) ? error : null;
			}
		}

		/// <summary>Gets the current logical state of an entry.</summary>
		public BundleWorkspaceEntryState GetEntryState(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				if (errors.ContainsKey(entry))
					return BundleWorkspaceEntryState.Error;
				if (replacements.ContainsKey(entry))
					return BundleWorkspaceEntryState.Modified;
				return reverted.Contains(entry)
					? BundleWorkspaceEntryState.Reverted : BundleWorkspaceEntryState.Unchanged;
			}
		}

		/// <summary>True when at least one entry has a visible operation error.</summary>
		public bool HasErrors {
			get {
				lock (gate) {
					EnsureNotDisposed();
					return errors.Count != 0;
				}
			}
		}

		/// <summary>True when an entry was restored by a revert operation.</summary>
		public bool HasRevertedEntries {
			get {
				lock (gate) {
					EnsureNotDisposed();
					return reverted.Count != 0;
				}
			}
		}

		/// <summary>Gets replacement metadata, or <see langword="null"/> for an original entry.</summary>
		public BundleReplacementInfo? GetReplacementInfo(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				return replacements.TryGetValue(entry, out Replacement? replacement)
					? replacement.Info : null;
			}
		}

		/// <summary>
		/// Gets a snapshot of entries with replacements, in manifest order.
		/// </summary>
		public IReadOnlyCollection<BundleEntry> ModifiedEntries {
			get {
				lock (gate) {
					EnsureNotDisposed();
					return Bundle.Entries.Where(replacements.ContainsKey).ToArray();
				}
			}
		}

		/// <summary>Raised after a replacement or revert has been installed.</summary>
		public event EventHandler<BundleWorkspaceChangedEventArgs>? Changed;

		/// <summary>
		/// True when the source bundle has a logical-read mapping for original entries.
		/// </summary>
		public bool OriginalReadAvailable {
			get {
				lock (gate) {
					EnsureNotDisposed();
					return Bundle.HasSourceMapping;
				}
			}
		}

		/// <summary>
		/// Opens the current logical content, using a replacement when one is installed.
		/// </summary>
		public Stream OpenCurrentRead(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				if (replacements.TryGetValue(entry, out Replacement? replacement))
					// Keep the replacement array private to this workspace. The overload
					// taking publiclyVisible:false prevents callers from recovering the
					// backing array through MemoryStream.GetBuffer/TryGetBuffer.
					return new MemoryStream(replacement.Bytes, 0, replacement.Bytes.Length,
						writable: false, publiclyVisible: false);
				return OpenOriginalReadCore(entry);
			}
		}

		/// <summary>Opens the original logical content of an entry.</summary>
		public Stream OpenOriginalRead(BundleEntry entry) {
			lock (gate) {
				EnsureEntry(entry);
				return OpenOriginalReadCore(entry);
			}
		}

		Stream OpenOriginalReadCore(BundleEntry entry) {
			if (!Bundle.HasSourceMapping)
				throw new InvalidOperationException("Original bundle entry reads are unavailable.");
			return entry.OpenLogicalRead();
		}

		/// <summary>
		/// Installs a replacement after validating all arguments. The input bytes are copied.
		/// </summary>
		public void SetReplacement(BundleEntry entry, byte[] bytes, BundleReplacementInfo info) {
			SetReplacements(new[] { new BundleWorkspaceReplacement(entry, bytes, info) });
		}

		/// <summary>
		/// Installs all candidates as one workspace transaction. Every argument is validated and
		/// copied before the replacement map is swapped, so a failed batch preserves prior state.
		/// </summary>
		public void SetReplacements(IReadOnlyList<BundleWorkspaceReplacement> candidates) {
			if (candidates is null)
				throw new ArgumentNullException(nameof(candidates));
			if (candidates.Count == 0)
				return;

			BundleWorkspaceChangedEventArgs[] changes;
			lock (gate) {
				EnsureNotDisposed();
				var pending = new Dictionary<BundleEntry, Replacement>(
					BundleEntryReferenceComparer.Instance);
				var pendingEntries = new List<KeyValuePair<BundleEntry, Replacement>>(candidates.Count);
				foreach (BundleWorkspaceReplacement candidate in candidates) {
					if (candidate is null)
						throw new ArgumentException("The replacement list contains a null candidate.", nameof(candidates));
					EnsureEntry(candidate.Entry);
					if (pending.ContainsKey(candidate.Entry))
						throw new ArgumentException("The replacement list contains a duplicate entry.", nameof(candidates));
					// Clone into a temporary map. No workspace state is changed until every clone succeeds.
					var replacement = new Replacement((byte[])candidate.Bytes.Clone(), candidate.Info);
					pending.Add(candidate.Entry, replacement);
					pendingEntries.Add(new KeyValuePair<BundleEntry, Replacement>(candidate.Entry, replacement));
				}

				var updated = new Dictionary<BundleEntry, Replacement>(replacements,
					BundleEntryReferenceComparer.Instance);
				foreach (KeyValuePair<BundleEntry, Replacement> candidate in pendingEntries)
					updated[candidate.Key] = candidate.Value;
				replacements = updated;
				foreach (KeyValuePair<BundleEntry, Replacement> candidate in pendingEntries)
					errors.Remove(candidate.Key);
				foreach (KeyValuePair<BundleEntry, Replacement> candidate in pendingEntries)
					reverted.Remove(candidate.Key);
				changes = pendingEntries.Select(a => new BundleWorkspaceChangedEventArgs(a.Key,
					BundleWorkspaceChangeKind.ReplacementSet, a.Value.Info)).ToArray();
			}
			foreach (BundleWorkspaceChangedEventArgs change in changes)
				Changed?.Invoke(this, change);
		}

		/// <summary>Reverts one replacement and reports whether one existed.</summary>
		public bool Revert(BundleEntry entry) {
			BundleWorkspaceChangedEventArgs? change = null;
			lock (gate) {
				EnsureEntry(entry);
				Replacement? replacement = null;
				Exception? error = null;
				bool hasReplacement = replacements.TryGetValue(entry, out replacement);
				bool hasError = errors.TryGetValue(entry, out error);
				if (hasReplacement || hasError) {
					replacements.Remove(entry);
					errors.Remove(entry);
					reverted.Add(entry);
					change = new BundleWorkspaceChangedEventArgs(entry,
						BundleWorkspaceChangeKind.Reverted, replacement?.Info, error);
				}
			}
			if (change is null)
				return false;
			Changed?.Invoke(this, change);
			return true;
		}

		/// <summary>Reverts every replacement in manifest order.</summary>
		public void RevertAll() {
			List<BundleWorkspaceChangedEventArgs>? changes = null;
			lock (gate) {
				EnsureNotDisposed();
				foreach (BundleEntry entry in Bundle.Entries) {
					if (replacements.TryGetValue(entry, out Replacement? replacement)) {
						replacements.Remove(entry);
						errors.Remove(entry);
						reverted.Add(entry);
						(changes ??= new List<BundleWorkspaceChangedEventArgs>()).Add(
							new BundleWorkspaceChangedEventArgs(entry,
								BundleWorkspaceChangeKind.Reverted, replacement.Info));
					}
					else if (errors.TryGetValue(entry, out Exception? error)) {
						errors.Remove(entry);
						reverted.Add(entry);
						(changes ??= new List<BundleWorkspaceChangedEventArgs>()).Add(
							new BundleWorkspaceChangedEventArgs(entry,
								BundleWorkspaceChangeKind.Reverted, null, error));
					}
				}
			}
			if (changes is null)
				return;
			foreach (BundleWorkspaceChangedEventArgs change in changes)
				Changed?.Invoke(this, change);
		}

		/// <summary>
		/// Records an operation failure without changing the current replacement or original bytes.
		/// A later successful replacement or revert clears the error.
		/// </summary>
		public void RecordError(BundleEntry entry, Exception error) {
			if (error is null)
				throw new ArgumentNullException(nameof(error));
			BundleWorkspaceChangedEventArgs change;
			lock (gate) {
				EnsureEntry(entry);
				errors[entry] = error;
				reverted.Remove(entry);
				change = new BundleWorkspaceChangedEventArgs(entry,
					BundleWorkspaceChangeKind.Error, GetReplacementInfoCore(entry), error);
			}
			Changed?.Invoke(this, change);
		}

		BundleReplacementInfo? GetReplacementInfoCore(BundleEntry entry) =>
			replacements.TryGetValue(entry, out Replacement? replacement) ? replacement.Info : null;

		void EnsureEntry(BundleEntry entry) {
			EnsureNotDisposed();
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			if (!ReferenceEquals(entry.Owner, Bundle))
				throw new ArgumentException("The entry does not belong to this workspace.", nameof(entry));
		}

		void EnsureNotDisposed() {
			if (disposed != 0)
				throw new ObjectDisposedException(nameof(BundleWorkspace));
			Bundle.EnsureNotDisposed();
		}

		/// <summary>Disposes this workspace and its owned source bundle.</summary>
		public void Dispose() {
			lock (gate) {
				if (disposed != 0)
					return;
				disposed = 1;
				replacements.Clear();
				errors.Clear();
				reverted.Clear();
				Changed = null;
				Bundle.Dispose();
			}
		}
	}
}
