// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading;

namespace dnSpy.Bundles {
	/// <summary>
	/// Creates a complete bundle in a private temporary directory.
	/// </summary>
	/// <remarks>
	/// This narrow seam keeps validation/publication independent from HostModel and permits a
	/// caller to provide a generation implementation for a future platform adapter. The returned
	/// generation remains owned by the caller and must be disposed after publication or failure.
	/// </remarks>
	public interface IWindowsBundleGenerator {
		/// <summary>Generates a bundle from the current workspace state.</summary>
		WindowsBundleGeneration Generate(BundleWorkspace workspace,
			CancellationToken cancellationToken = default);
	}
}
