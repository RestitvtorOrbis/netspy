// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	public sealed class BundleWorkspaceTests {
		[Fact]
		public void ReplacementsAreCopiedAndCurrentAndOriginalReadsRemainDistinct() {
			using SyntheticFactory factory = SyntheticFactory.CreateV1(new[] {
				new SyntheticEntry(1, "first", new byte[] { 1, 2, 3 }, 64),
				new SyntheticEntry(2, "second", new byte[] { 9, 8, 7 }, 67),
			});
			using BundleWorkspace workspace = new BundleWorkspace(factory.Result.Bundle!);
			BundleEntry first = workspace.Bundle.Entries[0];
			BundleEntry second = workspace.Bundle.Entries[1];
			Assert.False(workspace.HasChanges);
			Assert.Empty(workspace.ModifiedEntries);
			Assert.True(workspace.OriginalReadAvailable);

			byte[] replacement = { 10, 11, 12 };
			var info = new BundleReplacementInfo("first replacement");
			workspace.SetReplacement(first, replacement, info);
			replacement[0] = 99;

			Assert.True(workspace.HasChanges);
			Assert.Equal(new[] { first }, workspace.ModifiedEntries);
			Assert.Equal(new byte[] { 10, 11, 12 }, Read(workspace.OpenCurrentRead(first)));
			Assert.Equal(new byte[] { 1, 2, 3 }, Read(workspace.OpenOriginalRead(first)));
			using (Stream current = workspace.OpenCurrentRead(first)) {
				Assert.False(current.CanWrite);
				Assert.Throws<UnauthorizedAccessException>(() => ((MemoryStream)current).GetBuffer());
			}
			workspace.SetReplacement(second, new byte[] { 20, 21 }, new BundleReplacementInfo());
			Assert.Equal(new[] { first, second }, workspace.ModifiedEntries);
			Assert.Equal(new byte[] { 9, 8, 7 }, Read(workspace.OpenOriginalRead(second)));
			Assert.Equal(new byte[] { 20, 21 }, Read(workspace.OpenCurrentRead(second)));
		}

		[Fact]
		public void ChangesIdentifyReplacementAndOneAndAllReverts() {
			using SyntheticFactory factory = SyntheticFactory.CreateV1(new[] {
				new SyntheticEntry(1, "first", new byte[] { 1, 2, 3 }, 64),
				new SyntheticEntry(2, "second", new byte[] { 9, 8, 7 }, 67),
			});
			using BundleWorkspace workspace = new BundleWorkspace(factory.Result.Bundle!);
			BundleEntry first = workspace.Bundle.Entries[0];
			BundleEntry second = workspace.Bundle.Entries[1];
			var changes = new List<BundleWorkspaceChangedEventArgs>();
			workspace.Changed += (_, e) => changes.Add(e);
			var firstInfo = new BundleReplacementInfo("first");
			var secondInfo = new BundleReplacementInfo("second");

			workspace.SetReplacement(first, new byte[] { 4 }, firstInfo);
			workspace.SetReplacement(second, new byte[] { 5 }, secondInfo);
			Assert.Equal(BundleWorkspaceChangeKind.ReplacementSet, changes[0].ChangeKind);
			Assert.Same(first, changes[0].Entry);
			Assert.Same(firstInfo, changes[0].ReplacementInfo);
			Assert.True(changes[0].IsReplacement);

			Assert.True(workspace.Revert(first));
			Assert.False(workspace.Revert(first));
			Assert.True(workspace.HasChanges);
			Assert.Equal(new[] { second }, workspace.ModifiedEntries);
			Assert.Equal(BundleWorkspaceChangeKind.Reverted, changes[2].ChangeKind);
			Assert.Same(firstInfo, changes[2].ReplacementInfo);
			Assert.True(changes[2].IsRevert);

			workspace.SetReplacement(first, new byte[] { 6 }, firstInfo);
			workspace.RevertAll();
			Assert.False(workspace.HasChanges);
			Assert.Empty(workspace.ModifiedEntries);
			Assert.Equal(new[] { first, second }, changes.Skip(4).Select(a => a.Entry));
			Assert.All(changes.Skip(4), change => Assert.Equal(
				BundleWorkspaceChangeKind.Reverted, change.ChangeKind));
			Assert.Equal(6, changes.Count);
		}

		[Fact]
		public void InvalidReplacementPreservesTheLastValidStateAndForeignEntriesAreRejected() {
			using SyntheticFactory factory = SyntheticFactory.CreateV1(new[] {
				new SyntheticEntry(1, "first", new byte[] { 1, 2, 3 }, 64),
			});
			using SyntheticFactory foreignFactory = SyntheticFactory.CreateV1(new[] {
				new SyntheticEntry(1, "foreign", new byte[] { 7 }, 64),
			});
			using BundleWorkspace workspace = new BundleWorkspace(factory.Result.Bundle!);
			BundleEntry entry = workspace.Bundle.Entries[0];
			BundleEntry foreignEntry = foreignFactory.Result.Bundle!.Entries[0];
			var info = new BundleReplacementInfo("valid");
			workspace.SetReplacement(entry, new byte[] { 30, 31 }, info);
			Assert.Throws<ArgumentException>(() => workspace.SetReplacements(new[] {
				new BundleWorkspaceReplacement(entry, new byte[] { 32 }, new BundleReplacementInfo("batch")),
				new BundleWorkspaceReplacement(foreignEntry, new byte[] { 33 }, new BundleReplacementInfo("foreign")),
			}));

			Assert.Throws<ArgumentException>(() => workspace.SetReplacement(foreignEntry,
				new byte[] { 40 }, new BundleReplacementInfo()));
			Assert.Throws<ArgumentNullException>(() => workspace.SetReplacement(entry,
				null!, new BundleReplacementInfo()));
			Assert.Throws<ArgumentNullException>(() => workspace.SetReplacement(entry,
				new byte[] { 41 }, null!));
			Assert.True(workspace.HasChanges);
			Assert.Same(entry, Assert.Single(workspace.ModifiedEntries));
			Assert.Equal(new byte[] { 30, 31 }, Read(workspace.OpenCurrentRead(entry)));
		}

		[Fact]
		public void DisposalOwnsTheBundleAndRejectsFurtherOperations() {
			SyntheticFactory factory = SyntheticFactory.CreateV1(new[] {
				new SyntheticEntry(1, "first", new byte[] { 1 }, 64),
			});
			var workspace = new BundleWorkspace(factory.Result.Bundle!);
			try {
				workspace.Dispose();
				workspace.Dispose();
				Assert.Throws<ObjectDisposedException>(() => _ = workspace.HasChanges);
				Assert.Throws<ObjectDisposedException>(() => _ = workspace.ModifiedEntries);
				Assert.Throws<ObjectDisposedException>(() => _ = workspace.OriginalReadAvailable);
				Assert.Throws<ObjectDisposedException>(() => workspace.OpenCurrentRead(workspace.Bundle.Entries[0]));
				Assert.Throws<ObjectDisposedException>(() => workspace.Revert(workspace.Bundle.Entries[0]));
				Assert.Throws<ObjectDisposedException>(() => workspace.RevertAll());
				Assert.Throws<ObjectDisposedException>(() => workspace.Bundle.Entries[0].OpenLogicalRead());
			}
			finally {
				factory.Dispose();
			}
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
