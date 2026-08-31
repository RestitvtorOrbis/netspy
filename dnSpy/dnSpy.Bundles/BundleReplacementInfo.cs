// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles {
	/// <summary>
	/// Describes how a strong-name requirement was handled for a replacement.
	/// </summary>
	public enum BundleStrongNameDisposition {
		/// <summary>The source module did not require a strong-name decision.</summary>
		NotRequired,
		/// <summary>The strong name was removed from the replacement output.</summary>
		Removed,
		/// <summary>The replacement output was signed with a selected key.</summary>
		ReSigned,
	}

	/// <summary>
	/// Immutable metadata associated with a replacement entry.
	/// </summary>
	public sealed class BundleReplacementInfo {
		/// <summary>Creates replacement metadata without an optional description.</summary>
		public BundleReplacementInfo()
			: this(null, BundleStrongNameDisposition.NotRequired, null) {
		}

		/// <summary>Creates replacement metadata with a diagnostic description.</summary>
		public BundleReplacementInfo(string? description)
			: this(description, BundleStrongNameDisposition.NotRequired, null) {
		}

		/// <summary>Creates replacement metadata including the strong-name decision.</summary>
		public BundleReplacementInfo(string? description,
			BundleStrongNameDisposition strongNameDisposition, string? strongNameKeyFileName = null) {
			if (!Enum.IsDefined(typeof(BundleStrongNameDisposition), strongNameDisposition))
				throw new ArgumentOutOfRangeException(nameof(strongNameDisposition));
			Description = description;
			StrongNameDisposition = strongNameDisposition;
			StrongNameKeyFileName = strongNameKeyFileName;
		}

		/// <summary>Optional diagnostic description for the replacement.</summary>
		public string? Description { get; }

		/// <summary>Strong-name handling selected for this replacement.</summary>
		public BundleStrongNameDisposition StrongNameDisposition { get; }

		/// <summary>
		/// Optional key filename used to create a re-signed replacement. This is diagnostic
		/// metadata only; rebuild consumes the already-signed bytes.
		/// </summary>
		public string? StrongNameKeyFileName { get; }
	}
}
