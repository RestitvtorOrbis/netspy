// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class NormalFileRegressionTests {
		[Fact]
		public void OrdinaryFileReturnsNotBundle() {
			string filename = typeof(NormalFileRegressionTests).Assembly.Location;
			Assert.False(string.IsNullOrEmpty(filename));
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.NotBundle, result.Status);
			Assert.Null(result.Bundle);
			Assert.Null(result.Error);
		}
	}
}
