// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Presentation limits for bundle text previews. This type intentionally belongs to the
	/// dnSpy extension; the UI-independent bundle parser has no text-view policy.
	/// </summary>
	public sealed class BundleTextViewOptions {
		/// <summary>Default maximum number of UTF-8 bytes shown in a text preview.</summary>
		public const int DefaultMaximumPreviewBytes = 8 * 1024 * 1024;

		/// <summary>Default options used by bundle documents.</summary>
		public static BundleTextViewOptions Default { get; } = new BundleTextViewOptions();

		/// <summary>Creates options with the default eight-megabyte preview bound.</summary>
		public BundleTextViewOptions()
			: this(DefaultMaximumPreviewBytes) {
		}

		/// <summary>Creates options with a caller-specified preview bound.</summary>
		/// <param name="maximumPreviewBytes">Maximum UTF-8 bytes to materialize.</param>
		public BundleTextViewOptions(int maximumPreviewBytes) {
			if (maximumPreviewBytes < 0)
				throw new ArgumentOutOfRangeException(nameof(maximumPreviewBytes));
			MaximumPreviewBytes = maximumPreviewBytes;
		}

		/// <summary>Maximum UTF-8 bytes that a text node may materialize.</summary>
		public int MaximumPreviewBytes { get; }
	}
}
