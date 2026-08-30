// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using dnSpy.Debugger.DbgUI;
using dnlib.PE;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class OrdinaryDebugPathRegressionTests {
		[Fact]
		public void OrdinaryPhysicalDocumentKeepsItsExistingDebugTarget() {
			string filename = typeof(OrdinaryDebugPathRegressionTests).Assembly.Location;
			using var peImage = new PEImage(filename, verify: false);
			using var document = new DsPEDocument(peImage);
			var provider = new OrdinaryDocumentNodeProvider();
			DsDocumentNode node = provider.Create(null!, null, document)!;

			Assert.Equal(filename, DebugTargetCompatibility.GetPhysicalFilename(node));
		}

		sealed class OrdinaryDocumentNodeProvider : IDsDocumentNodeProvider {
			public DsDocumentNode? Create(IDocumentTreeView documentTreeView, DsDocumentNode? owner,
				IDsDocument document) {
				return new TestDocumentNode(document);
			}
		}

		sealed class TestDocumentNode : DsDocumentNode {
			public TestDocumentNode(IDsDocument document) : base(document) { }
			public override Guid Guid => new Guid("B4B54C7D-8783-4F37-8C42-04FD6D8D6E2A");
			protected override dnSpy.Contracts.Images.ImageReference GetIcon(
				dnSpy.Contracts.Images.IDotNetImageService dnImgMgr) => default;
			protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
				DocumentNodeWriteOptions options) => output.Write(Document.Filename);
		}
	}
}
