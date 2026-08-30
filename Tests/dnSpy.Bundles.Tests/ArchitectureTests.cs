// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.Reflection;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class ArchitectureTests {
		[Fact]
		public void CoreAssemblyHasNoDnSpyDnlibOrWpfReferences() {
			Assembly coreAssembly = typeof(BundleReader).Assembly;
			foreach (AssemblyName reference in coreAssembly.GetReferencedAssemblies()) {
				string name = reference.Name ?? string.Empty;
				Assert.False(name.StartsWith("dnSpy", StringComparison.OrdinalIgnoreCase), name);
				Assert.False(name.StartsWith("dnlib", StringComparison.OrdinalIgnoreCase), name);
				Assert.False(name.Equals("PresentationCore", StringComparison.OrdinalIgnoreCase), name);
				Assert.False(name.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase), name);
				Assert.False(name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase), name);
				Assert.False(name.Equals("System.Windows", StringComparison.OrdinalIgnoreCase), name);
			}
		}
	}
}
