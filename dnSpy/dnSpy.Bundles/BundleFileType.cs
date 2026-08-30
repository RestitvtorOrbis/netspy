// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


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
