// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace dnSpy.Bundles {
	/// <summary>Describes the current logical state of one bundle entry.</summary>
	public enum BundleWorkspaceEntryState {
		/// <summary>The entry is backed by its original bytes.</summary>
		Unchanged,
		/// <summary>The entry has replacement bytes pending in the workspace.</summary>
		Modified,
		/// <summary>The last workspace operation for the entry failed.</summary>
		Error,
		/// <summary>The entry was restored by a revert operation.</summary>
		Reverted,
	}
}
