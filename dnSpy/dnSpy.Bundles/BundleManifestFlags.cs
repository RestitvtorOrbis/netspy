// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;

namespace dnSpy.Bundles {
	/// <summary>
	/// Flags serialized by v2 and v6 bundle manifests.
	/// </summary>
	[Flags]
	public enum BundleManifestFlags : ulong {
		/// <summary>No manifest compatibility flags.</summary>
		None = 0,
		/// <summary>Use .NET Core 3.1 compatibility extraction behavior.</summary>
		NetcoreApp3CompatMode = 1,
	}
}
