// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles {
	/// <summary>One replacement candidate for an atomic workspace update.</summary>
	public sealed class BundleWorkspaceReplacement {
		/// <summary>Creates a replacement candidate for one bundle entry.</summary>
		public BundleWorkspaceReplacement(BundleEntry entry, byte[] bytes, BundleReplacementInfo info) {
			Entry = entry ?? throw new ArgumentNullException(nameof(entry));
			Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
			Info = info ?? throw new ArgumentNullException(nameof(info));
		}

		/// <summary>Entry receiving the replacement.</summary>
		public BundleEntry Entry { get; }
		/// <summary>Complete replacement bytes.</summary>
		public byte[] Bytes { get; }
		/// <summary>Replacement disposition metadata.</summary>
		public BundleReplacementInfo Info { get; }
	}
}
