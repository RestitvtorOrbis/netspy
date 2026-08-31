// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Contracts.Documents.Bundles {
	/// <summary>One candidate for an atomic bundle workspace update.</summary>
	public sealed class BundleWorkspaceReplacement {
		/// <summary>Creates a replacement candidate for one bundle entry.</summary>
		public BundleWorkspaceReplacement(IDsBundleEntryDocument document, byte[] bytes,
			DsBundleStrongNameDisposition strongNameDisposition,
			string? strongNameKeyFileName = null) {
			Document = document ?? throw new ArgumentNullException(nameof(document));
			Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
			if (!Enum.IsDefined(typeof(DsBundleStrongNameDisposition), strongNameDisposition))
				throw new ArgumentOutOfRangeException(nameof(strongNameDisposition));
			StrongNameDisposition = strongNameDisposition;
			StrongNameKeyFileName = strongNameKeyFileName;
		}

		/// <summary>Managed bundle entry receiving the replacement.</summary>
		public IDsBundleEntryDocument Document { get; }
		/// <summary>Complete standalone managed-module bytes.</summary>
		public byte[] Bytes { get; }
		/// <summary>Explicit strong-name decision selected for this output.</summary>
		public DsBundleStrongNameDisposition StrongNameDisposition { get; }
		/// <summary>Selected signing-key filename, when re-signing was chosen.</summary>
		public string? StrongNameKeyFileName { get; }
	}
}
