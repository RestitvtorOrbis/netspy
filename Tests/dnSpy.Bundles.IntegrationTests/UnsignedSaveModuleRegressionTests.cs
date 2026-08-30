// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using dnlib.DotNet;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class UnsignedSaveModuleRegressionTests {
		[Fact]
		public void UnsignedManagedModuleSaveRemainsUnsigned() {
			using var fixture = ModuleSerializationTestFixture.Create();
			using var stream = new MemoryStream();
			Assert.True(ModuleSerializationTestFixture.WriteToStream(fixture.CreateOptions(), stream));
			using ModuleDefMD module = ModuleDefMD.Load(stream.ToArray());
			Assert.False(module.IsStrongNameSigned);
			Assert.False(module.Assembly!.HasPublicKey);
			Assert.Equal("SerializationFixture, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null",
				module.Assembly.FullName);
		}
	}
}
