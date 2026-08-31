// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles {
	/// <summary>Describes the mutation that raised a bundle workspace change.</summary>
	public enum BundleWorkspaceChangeKind {
		/// <summary>A replacement was installed or updated.</summary>
		ReplacementSet,
		/// <summary>A replacement was reverted.</summary>
		Reverted,
		/// <summary>Alias for <see cref="ReplacementSet"/>.</summary>
		Replacement = ReplacementSet,
		/// <summary>Alias for <see cref="Reverted"/>.</summary>
		Revert = Reverted,
		/// <summary>A workspace operation failed for this entry.</summary>
		Error,
	}

	/// <summary>Event data for a <see cref="BundleWorkspace"/> mutation.</summary>
	public sealed class BundleWorkspaceChangedEventArgs : EventArgs {
		/// <summary>Creates event data for an entry mutation.</summary>
		public BundleWorkspaceChangedEventArgs(BundleEntry entry, BundleWorkspaceChangeKind changeKind,
			BundleReplacementInfo? replacementInfo)
			: this(entry, changeKind, replacementInfo, null) {
		}

		/// <summary>Creates event data for an entry mutation or operation failure.</summary>
		public BundleWorkspaceChangedEventArgs(BundleEntry entry, BundleWorkspaceChangeKind changeKind,
			BundleReplacementInfo? replacementInfo, Exception? error) {
			Entry = entry ?? throw new ArgumentNullException(nameof(entry));
			ChangeKind = changeKind;
			ReplacementInfo = replacementInfo;
			Error = error;
		}

		/// <summary>Entry whose current content changed.</summary>
		public BundleEntry Entry { get; }
		/// <summary>Kind of mutation that occurred.</summary>
		public BundleWorkspaceChangeKind ChangeKind { get; }
		/// <summary>Metadata installed by a replacement, or reverted metadata.</summary>
		public BundleReplacementInfo? ReplacementInfo { get; }
		/// <summary>The operation failure, or <see langword="null"/> for successful changes.</summary>
		public Exception? Error { get; }
		/// <summary>True when this event represents a replacement installation.</summary>
		public bool IsReplacement => ChangeKind == BundleWorkspaceChangeKind.ReplacementSet;
		/// <summary>True when this event represents a revert.</summary>
		public bool IsRevert => ChangeKind == BundleWorkspaceChangeKind.Reverted;
		/// <summary>True when this event reports a failed workspace operation.</summary>
		public bool IsError => ChangeKind == BundleWorkspaceChangeKind.Error;
	}
}
