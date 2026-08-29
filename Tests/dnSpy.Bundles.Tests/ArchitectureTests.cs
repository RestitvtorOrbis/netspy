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
