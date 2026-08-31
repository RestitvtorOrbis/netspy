// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles {
	/// <summary>
	/// Immutable metadata associated with a replacement entry.
	/// </summary>
	public sealed class BundleReplacementInfo {
		/// <summary>Creates replacement metadata without an optional description.</summary>
		public BundleReplacementInfo() { }

		/// <summary>Creates replacement metadata with a diagnostic description.</summary>
		public BundleReplacementInfo(string? description) => Description = description;

		/// <summary>Optional diagnostic description for the replacement.</summary>
		public string? Description { get; }
	}
}
