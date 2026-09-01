// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class BundleReaderRegressionTests {
		[Fact]
		public void ReaderEntriesRemainOriginalAfterWorkspaceReplacement() {
			using SyntheticFactory factory = SyntheticFactory.CreateV1(new[] {
				new SyntheticEntry(1, "first", new byte[] { 1, 2, 3 }, 64),
			});
			BundleOpenResult result = factory.Result;
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleWorkspace workspace = new BundleWorkspace(result.Bundle!);
			BundleEntry entry = result.Bundle!.Entries[0];
			workspace.SetReplacement(entry, new byte[] { 8, 9 }, new BundleReplacementInfo());

			Assert.Equal(new byte[] { 8, 9 }, Read(workspace.OpenCurrentRead(entry)));
			Assert.Equal(new byte[] { 1, 2, 3 }, Read(workspace.OpenOriginalRead(entry)));
			Assert.Equal(new byte[] { 1, 2, 3 }, entry.ReadAllBytes(3));
		}

		[Fact]
		public void WorkspaceReportsWhenOriginalReadsAreUnavailable() {
			var entry = new BundleEntry(0, 0, 0, 0, (byte)BundleFileType.Assembly,
				BundleFileType.Assembly, "entry");
			var bundle = new BundleFile("synthetic", 0, 0, 0,
				new BundleManifest(1, 0, "synthetic"), new[] { entry });
			using var workspace = new BundleWorkspace(bundle);
			Assert.False(workspace.OriginalReadAvailable);
			Assert.Throws<InvalidOperationException>(() => workspace.OpenOriginalRead(entry));
		}

		[Fact]
		public void EligibilityInspectionDoesNotDisposeOrMutateReaderState() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			BundleOpenResult result = new BundleReader().Open(fixture.BundlePath);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using var workspace = new BundleWorkspace(result.Bundle!);
			BundleEntry entry = Assert.Single(workspace.Bundle.Entries,
				candidate => candidate.RelativePath == "SingleFile.App.dll");
			byte[] before = entry.ReadAllBytes(entry.Size);

			WindowsBundleEligibilityResult eligibility =
				new WindowsBundleEligibilityInspector().Inspect(workspace);

			Assert.Equal(WindowsBundleEligibilityStatus.Eligible, eligibility.Status);
			Assert.Equal(before, entry.ReadAllBytes(entry.Size));
			Assert.False(workspace.HasChanges);
		}

		static byte[] Read(Stream stream) {
			using (stream) {
				using var output = new MemoryStream();
				stream.CopyTo(output);
				return output.ToArray();
			}
		}
	}
}
