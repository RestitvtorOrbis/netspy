// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using SingleFile.Dependency;

namespace SingleFile.App {
	internal static class Program {
		private static void Main() => Console.WriteLine("BUNDLE_VALUE=" + BundleValue.Value);
	}
}
