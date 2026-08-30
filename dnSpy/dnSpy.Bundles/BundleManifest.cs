// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;

namespace dnSpy.Bundles {
	/// <summary>
	/// Immutable metadata describing a bundle manifest.
	/// </summary>
	public sealed class BundleManifest {
		/// <summary>Creates manifest metadata from validated fields.</summary>
		public BundleManifest(uint majorVersion, uint minorVersion, string bundleId,
			BundleManifestFlags flags = BundleManifestFlags.None,
			BundleRange? depsJson = null, BundleRange? runtimeConfigJson = null) {
			if (bundleId is null)
				throw new ArgumentNullException(nameof(bundleId));
			MajorVersion = majorVersion;
			MinorVersion = minorVersion;
			BundleId = bundleId;
			Flags = flags;
			DepsJson = depsJson;
			RuntimeConfigJson = runtimeConfigJson;
		}

		/// <summary>Manifest major version.</summary>
		public uint MajorVersion { get; }

		/// <summary>Manifest minor version.</summary>
		public uint MinorVersion { get; }

		/// <summary>Manifest bundle identifier.</summary>
		public string BundleId { get; }

		/// <summary>Manifest compatibility flags.</summary>
		public BundleManifestFlags Flags { get; }

		/// <summary>Optional range containing the deps.json entry.</summary>
		public BundleRange? DepsJson { get; }

		/// <summary>Optional range containing the runtimeconfig.json entry.</summary>
		public BundleRange? RuntimeConfigJson { get; }
	}
}
