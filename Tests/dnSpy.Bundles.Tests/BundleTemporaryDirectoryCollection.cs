// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace dnSpy.Bundles.Tests {
	/// <summary>Serializes tests that assert the process-wide private bundle temp-directory set.</summary>
	[CollectionDefinition("Bundle temporary directory", DisableParallelization = true)]
	public sealed class BundleTemporaryDirectoryCollectionDefinition {
	}
}
