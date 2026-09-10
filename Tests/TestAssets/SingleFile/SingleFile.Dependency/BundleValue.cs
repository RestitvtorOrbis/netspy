// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace SingleFile.Dependency {
	public static class BundleValue {
		// A constant is inlined by the compiler and removes the app's assembly reference.
		public static string Value => "v1";
	}
}
