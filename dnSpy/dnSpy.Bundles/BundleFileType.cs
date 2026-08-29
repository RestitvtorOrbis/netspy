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
	/// <summary>
	/// Identifies the official bundle manifest type of an entry.
	/// </summary>
	public enum BundleFileType : byte {
		/// <summary>Unknown or not-yet-classified entry.</summary>
		Unknown = 0,
		/// <summary>Managed IL or ReadyToRun assembly.</summary>
		Assembly = 1,
		/// <summary>Native binary.</summary>
		NativeBinary = 2,
		/// <summary>.deps.json configuration file.</summary>
		DepsJson = 3,
		/// <summary>.runtimeconfig.json configuration file.</summary>
		RuntimeConfigJson = 4,
		/// <summary>Program database symbols.</summary>
		Symbols = 5,
	}
}
