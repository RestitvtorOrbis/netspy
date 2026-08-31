// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace dnSpy.Contracts.Documents.Bundles {
	/// <summary>Strong-name decision made for one bundle workspace replacement.</summary>
	public enum DsBundleStrongNameDisposition {
		/// <summary>The source module did not require a strong-name decision.</summary>
		NotRequired,
		/// <summary>The strong name was explicitly removed for the replacement output.</summary>
		Removed,
		/// <summary>The replacement output was explicitly re-signed with a selected key.</summary>
		ReSigned,
	}
}
