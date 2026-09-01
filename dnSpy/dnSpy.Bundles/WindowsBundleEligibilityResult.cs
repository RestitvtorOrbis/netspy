// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles {
	/// <summary>Immutable, side-effect-free Windows bundle preflight result.</summary>
	public sealed class WindowsBundleEligibilityResult {
		internal WindowsBundleEligibilityResult(WindowsBundleEligibilityStatus status,
			string message, string? sourceSha256, bool hasAuthenticodeSignature,
			int? entryIndex = null, string? relativePath = null) {
			Status = status;
			Message = message ?? throw new ArgumentNullException(nameof(message));
			SourceSha256 = sourceSha256;
			HasAuthenticodeSignature = hasAuthenticodeSignature;
			EntryIndex = entryIndex;
			RelativePath = relativePath;
		}

		/// <summary>Stable result category.</summary>
		public WindowsBundleEligibilityStatus Status { get; }
		/// <summary>Stable safe diagnostic which contains no entry content.</summary>
		public string Message { get; }
		/// <summary>Lower-case SHA-256 of the inspected source, when it could be read.</summary>
		public string? SourceSha256 { get; }
		/// <summary>True when a non-empty, in-file PE certificate table is present.</summary>
		public bool HasAuthenticodeSignature { get; }
		/// <summary>Manifest index associated with the result, when applicable.</summary>
		public int? EntryIndex { get; }
		/// <summary>Validated relative path associated with the result, when applicable.</summary>
		public string? RelativePath { get; }
		/// <summary>True only when later rebuild stages may proceed.</summary>
		public bool IsEligible => Status == WindowsBundleEligibilityStatus.Eligible;
	}
}
