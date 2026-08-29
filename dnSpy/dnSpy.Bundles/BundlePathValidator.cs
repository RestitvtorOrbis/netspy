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
	static class BundlePathValidator {
		public static string NormalizeAndValidate(string path, int entryIndex) {
			if (path is null)
				throw new ArgumentNullException(nameof(path));
			string normalized = path.Replace('\\', '/');
			if (normalized.Length == 0 || normalized.IndexOf('\0') >= 0)
				Invalid(entryIndex);
			// Path.IsPathRooted is platform-dependent (and does not recognize a Windows
			// drive path on Unix), so validate all portable rooted forms explicitly.
			if (normalized[0] == '/' ||
				(normalized.Length >= 2 && normalized[1] == ':' &&
					((normalized[0] >= 'A' && normalized[0] <= 'Z') ||
					 (normalized[0] >= 'a' && normalized[0] <= 'z'))))
				Invalid(entryIndex);

			string[] segments = normalized.Split('/');
			foreach (string segment in segments) {
				if (segment == "." || segment == "..")
					Invalid(entryIndex);
			}
			return normalized;
		}

		public static void AddUnique(HashSet<string> paths, string normalizedPath, int entryIndex) {
			if (paths is null)
				throw new ArgumentNullException(nameof(paths));
			if (!paths.Add(normalizedPath))
				throw new BundleReadException(BundleReadErrorCode.DuplicatePath,
					"The bundle contains duplicate normalized entry paths.", entryIndex);
		}

		static void Invalid(int entryIndex) => throw new BundleReadException(
			BundleReadErrorCode.InvalidPath,
			"A bundle entry path is empty, rooted, or contains traversal segments.", entryIndex);
	}
}
