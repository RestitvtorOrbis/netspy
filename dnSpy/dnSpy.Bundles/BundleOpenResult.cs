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

namespace dnSpy.Bundles {
	/// <summary>Outcome of attempting to open a file as an official bundle.</summary>
	public enum BundleOpenStatus {
		/// <summary>The file does not contain an official bundle.</summary>
		NotBundle,
		/// <summary>The bundle was opened successfully.</summary>
		Success,
		/// <summary>The file contains a malformed bundle.</summary>
		InvalidBundle,
		/// <summary>The bundle manifest version is not supported.</summary>
		UnsupportedVersion,
	}

	/// <summary>
	/// Immutable result returned at the bundle-reader/provider boundary.
	/// </summary>
	public sealed class BundleOpenResult {
		/// <summary>Creates a bundle-open result.</summary>
		public BundleOpenResult(BundleOpenStatus status, BundleFile? bundle = null,
			BundleReadError? error = null) {
			Status = status;
			Bundle = bundle;
			Error = error;
		}

		/// <summary>Open status.</summary>
		public BundleOpenStatus Status { get; }

		/// <summary>Opened bundle, present only for <see cref="BundleOpenStatus.Success"/>.</summary>
		public BundleFile? Bundle { get; }

		/// <summary>Safe failure diagnostic, when available.</summary>
		public BundleReadError? Error { get; }
	}
}
