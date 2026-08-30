// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


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
