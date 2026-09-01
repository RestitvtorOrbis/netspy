// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace dnSpy.Bundles {
	/// <summary>Stable failure categories for apphost reconstruction.</summary>
	public enum WindowsAppHostReconstructionErrorCode {
		/// <summary>The source argument or workspace was invalid.</summary>
		InvalidArgument,
		/// <summary>The source could not be read as a valid PE image.</summary>
		InvalidPeImage,
		/// <summary>The source PE is not Windows x64.</summary>
		UnsupportedArchitecture,
		/// <summary>The bundle metadata has an invalid physical boundary.</summary>
		InvalidBundleBoundary,
		/// <summary>The known marker or its preceding pointer is invalid.</summary>
		InvalidBundleMarker,
		/// <summary>The temporary reconstructed host is not valid HostModel input.</summary>
		InvalidHostModelPlaceholder,
		/// <summary>A temporary file could not be created or removed.</summary>
		TemporaryFileFailure,
	}
}
