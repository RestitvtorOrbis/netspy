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
		readonly Dictionary<BundleEntry, Replacement> replacements =
			new Dictionary<BundleEntry, Replacement>(BundleEntryReferenceComparer.Instance);
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
			if (bytes is null)
				throw new ArgumentNullException(nameof(bytes));
			if (info is null)
				throw new ArgumentNullException(nameof(info));
			BundleWorkspaceChangedEventArgs change;
			lock (gate) {
				EnsureEntry(entry);
				// Copy before changing the dictionary so validation/copy failures leave the
				// previously installed replacement untouched.
				byte[] copiedBytes = (byte[])bytes.Clone();
				replacements[entry] = new Replacement(copiedBytes, info);
				change = new BundleWorkspaceChangedEventArgs(entry,
					BundleWorkspaceChangeKind.ReplacementSet, info);
			}
			Changed?.Invoke(this, change);
		}

		/// <summary>Reverts one replacement and reports whether one existed.</summary>
		public bool Revert(BundleEntry entry) {
			BundleWorkspaceChangedEventArgs? change = null;
			lock (gate) {
				EnsureEntry(entry);
				if (replacements.TryGetValue(entry, out Replacement? replacement)) {
					replacements.Remove(entry);
					change = new BundleWorkspaceChangedEventArgs(entry,
						BundleWorkspaceChangeKind.Reverted, replacement.Info);
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
						(changes ??= new List<BundleWorkspaceChangedEventArgs>()).Add(
							new BundleWorkspaceChangedEventArgs(entry,
								BundleWorkspaceChangeKind.Reverted, replacement.Info));
					}
				}
			}
			if (changes is null)
				return;
			foreach (BundleWorkspaceChangedEventArgs change in changes)
				Changed?.Invoke(this, change);
		}

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
				Bundle.Dispose();
			}
		}
	}
}
