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
using System.Collections.Generic;

namespace dnSpy.Bundles {
	/// <summary>
	/// Immutable metadata shell for an opened bundle file.
	/// </summary>
	public sealed class BundleFile : IDisposable {
		/// <summary>Creates bundle metadata from validated fields.</summary>
		public BundleFile(string filename, long fileLength, long markerOffset, long headerOffset,
			BundleManifest manifest, IReadOnlyList<BundleEntry> entries) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			if (fileLength < 0)
				throw new ArgumentOutOfRangeException(nameof(fileLength));
			if (markerOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(markerOffset));
			if (headerOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(headerOffset));
			if (manifest is null)
				throw new ArgumentNullException(nameof(manifest));
			if (entries is null)
				throw new ArgumentNullException(nameof(entries));
			Filename = filename;
			FileLength = fileLength;
			MarkerOffset = markerOffset;
			HeaderOffset = headerOffset;
			Manifest = manifest;
			Entries = new List<BundleEntry>(entries).AsReadOnly();
		}

		/// <summary>Source filename.</summary>
		public string Filename { get; }
		/// <summary>Length of the source file.</summary>
		public long FileLength { get; }
		/// <summary>Offset of the official bundle marker.</summary>
		public long MarkerOffset { get; }
		/// <summary>Offset of the manifest header.</summary>
		public long HeaderOffset { get; }
		/// <summary>Manifest metadata.</summary>
		public BundleManifest Manifest { get; }
		/// <summary>Entries in manifest order.</summary>
		public IReadOnlyList<BundleEntry> Entries { get; }

		/// <summary>Releases resources held by a future entry reader.</summary>
		public void Dispose() {
			// The BND-001 shell owns no file handles yet.
		}
	}
}
