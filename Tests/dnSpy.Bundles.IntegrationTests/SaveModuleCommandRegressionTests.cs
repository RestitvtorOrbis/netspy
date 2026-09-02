// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnlib.DotNet;
using dnSpy.Bundles.Extension;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Ensures Save Module remains an independent ordinary-module operation.</summary>
	public sealed class SaveModuleCommandRegressionTests {
		[Fact]
		public void OrdinaryManagedModuleSaveAsRemainsReopenable() {
			using var fixture = ModuleSerializationTestFixture.Create();
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				Assert.True(ModuleSerializationTestFixture.WriteToFile(
					fixture.CreateOptions(), filename));
				using ModuleDefMD module = ModuleDefMD.Load(File.ReadAllBytes(filename));
				Assert.Equal("SerializationFixture.dll", module.Name.String);
				Assert.Equal("SerializationFixture, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null",
					module.Assembly?.FullName);
			}
			finally {
				try { File.Delete(filename); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}
}
