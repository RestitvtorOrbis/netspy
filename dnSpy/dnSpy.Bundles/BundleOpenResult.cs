// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


namespace dnSpy.Bundles {
	/// <summary>Outcome of attempting to open a file as an official bundle.</summary>
	public enum BundleOpenStatus {
		/// <summary>The file does not contain an official bundle.</summary>
		NotBundle,
		/// <summary>The bundle was opened successfully.</summary>
		Success,
		/// <summary>The file contains a malformed bundle.</summary>
		InvalidBundle,
		/// <summary>The bundle manifest version is not supported.</summary>
		UnsupportedVersion,
	}

	/// <summary>
	/// Immutable result returned at the bundle-reader/provider boundary.
	/// </summary>
	public sealed class BundleOpenResult {
		/// <summary>Creates a bundle-open result.</summary>
		public BundleOpenResult(BundleOpenStatus status, BundleFile? bundle = null,
			BundleReadError? error = null) {
			Status = status;
			Bundle = bundle;
			Error = error;
		}

		/// <summary>Open status.</summary>
		public BundleOpenStatus Status { get; }

		/// <summary>Opened bundle, present only for <see cref="BundleOpenStatus.Success"/>.</summary>
		public BundleFile? Bundle { get; }

		/// <summary>Safe failure diagnostic, when available.</summary>
		public BundleReadError? Error { get; }
	}
}
