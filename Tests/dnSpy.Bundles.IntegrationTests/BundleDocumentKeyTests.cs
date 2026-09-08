// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Bundles.Extension;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleDocumentKeyTests {
		[Theory]
		[InlineData(BundleDocumentKeyKind.Root)]
		[InlineData(BundleDocumentKeyKind.Folder)]
		[InlineData(BundleDocumentKeyKind.Entry)]
		[InlineData(BundleDocumentKeyKind.Module)]
		[InlineData(BundleDocumentKeyKind.Error)]
		public void DeclaredKindsAreAccepted(BundleDocumentKeyKind kind) {
			string source = Path.Combine(Path.GetTempPath(), "BLC-001", "source.exe");
			string relativePath = kind == BundleDocumentKeyKind.Root ? string.Empty : "entry.dll";

			var key = new BundleDocumentKey(source, kind, relativePath);

			Assert.Equal(kind, key.Kind);
		}

		[Theory]
		[InlineData(-1)]
		[InlineData(int.MaxValue)]
		public void UndefinedKindsAreRejected(int value) {
			string source = Path.Combine(Path.GetTempPath(), "BLC-001", "source.exe");

			var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
				new BundleDocumentKey(source, (BundleDocumentKeyKind)value, "entry.dll"));

			Assert.Equal("kind", exception.ParamName);
		}
	}
}
