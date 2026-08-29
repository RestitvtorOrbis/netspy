/*
    Copyright (C) 2026 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

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
