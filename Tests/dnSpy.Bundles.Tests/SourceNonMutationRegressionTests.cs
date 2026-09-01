// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class SourceNonMutationRegressionTests {
		[Fact]
		public void ReconstructingBundleNeverChangesSourceBytes() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			byte[] before = File.ReadAllBytes(fixture.BundlePath);
			string beforeHash = Hash(before);
			BundleOpenResult opened = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using BundleFile bundle = opened.Bundle!;
			using var workspace = new BundleWorkspace(bundle);
			using (new WindowsAppHostReconstructor().Reconstruct(workspace)) {
			}

			Assert.Equal(beforeHash, Hash(File.ReadAllBytes(fixture.BundlePath)));
			Assert.Equal(before, File.ReadAllBytes(fixture.BundlePath));
		}

		static string Hash(byte[] bytes) {
			using SHA256 sha256 = SHA256.Create();
			return Convert.ToHexString(sha256.ComputeHash(bytes));
		}
	}
}
