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
	/// Entry point for opening official .NET single-file bundles.
	/// </summary>
	public sealed class BundleReader {
		readonly BundleReaderOptions options;

		/// <summary>Creates a reader using the supplied or secure default limits.</summary>
		public BundleReader(BundleReaderOptions? options = null) => this.options = options ?? new BundleReaderOptions();

		/// <summary>
		/// Attempts to open a file as an official bundle.
		/// </summary>
		/// <remarks>
		/// Detection and parsing are introduced by subsequent parser tickets. BND-001
		/// intentionally preserves normal loading by returning <see cref="BundleOpenStatus.NotBundle"/>.
		/// </remarks>
		public BundleOpenResult Open(string filename) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			_ = options;
			return new BundleOpenResult(BundleOpenStatus.NotBundle);
		}
	}
}
