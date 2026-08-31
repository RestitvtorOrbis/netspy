// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class OrdinaryTreeRefreshRegressionTests {
		[Fact]
		public void OrdinaryUnknownDocumentsRemainOutsideBundleTreeProvider() {
			var provider = new BundleDocumentNodeProvider();
			var ordinary = new DsUnknownDocument("ordinary.bin");

			Assert.Null(provider.Create(null!, null, ordinary));
			Assert.Null(ordinary.ModuleDef);
			Assert.Null(ordinary.PEImage);
		}

		[Fact]
		public void OrdinaryManagedDocumentsRemainOutsideBundleWorkspaceState() {
			var module = new dnlib.DotNet.ModuleDefUser("ordinary.dll");
			var document = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary.dll"), module, loadSyms: false);
			try {
				Assert.NotNull(document.ModuleDef);
				Assert.Null(new BundleDocumentNodeProvider().Create(null!, null, document));
			}
			finally {
				document.Dispose();
			}
		}

		[Fact]
		public void BundleWorkspaceEventDoesNotRefreshAnOrdinaryTreeNode() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntry entry = document.Bundle.Entries.First(a => a.FileType == BundleFileType.Assembly);
			DsDocumentNode bundleRoot = new BundleDocumentNodeProvider().Create(null!, null, document)!;
			var bundleTree = new RecordingTreeNode(bundleRoot);
			bundleRoot.TreeNode = bundleTree;
			bundleRoot.Initialize();

			var ordinaryModule = new dnlib.DotNet.ModuleDefUser("ordinary.dll");
			DsDotNetDocument ordinaryDocument = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary.dll"), ordinaryModule, loadSyms: false);
			try {
				Assembly product = Assembly.Load("dnSpy");
				Type providerType = product.GetType(
					"dnSpy.Documents.TreeView.DefaultDsDocumentNodeProvider", throwOnError: true)!;
				var provider = (IDsDocumentNodeProvider)Activator.CreateInstance(
					providerType, nonPublic: true)!;
				DsDocumentNode ordinaryNode = provider.Create(null!, null, ordinaryDocument)!;
				var ordinaryTree = new RecordingTreeNode(ordinaryNode);
				ordinaryNode.TreeNode = ordinaryTree;

				document.Workspace.SetReplacement(entry, new byte[] { 1 },
					new BundleReplacementInfo("refresh test"));

				Assert.True(bundleTree.RefreshCount > 0);
				Assert.Equal(0, ordinaryTree.RefreshCount);
			}
			finally {
				ordinaryDocument.Dispose();
			}
		}

		static string FindCompressedFixture() {
			string? configured = Environment.GetEnvironmentVariable("DNSPY_BUNDLE_FIXTURES");
			var roots = new List<string>();
			if (!string.IsNullOrWhiteSpace(configured))
				roots.AddRange(configured.Split(new[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries));
			roots.Add(Path.Combine(AppContext.BaseDirectory,
				"../../../../TestAssets/SingleFile/Net10/artifacts/net10.0"));
			roots.Add(Path.Combine(Directory.GetCurrentDirectory(),
				"Tests/TestAssets/SingleFile/Net10/artifacts/net10.0"));
			foreach (string root in roots) {
				string candidate = Path.GetFullPath(Path.Combine(root,
					"scd-compressed/publish/SingleFile.App.exe"));
				if (File.Exists(candidate))
					return candidate;
			}
			throw new InvalidOperationException("The generated compressed net10 bundle fixture is missing.");
		}

		sealed class RecordingTreeNode : ITreeNode {
			readonly IList<ITreeNode> children = new List<ITreeNode>();

			public RecordingTreeNode(TreeNodeData data) => Data = data;
			public int RefreshCount { get; private set; }
			public ITreeView TreeView => null!;
			public ITreeNode? Parent => null;
			public IList<ITreeNode> Children => children;
			public IEnumerable<TreeNodeData> DataChildren => children.Select(a => a.Data);
			public TreeNodeData Data { get; }
			public bool LazyLoading { get; set; }
			public bool IsExpanded { get; set; }
			public bool IsHidden { get; set; }
			public bool IsVisible => !IsHidden;
			public void EnsureChildrenLoaded() { }
			public void AddChild(ITreeNode node) => children.Add(node);
			public IEnumerable<ITreeNode> Descendants() => children.SelectMany(a => a.DescendantsAndSelf());
			public IEnumerable<ITreeNode> DescendantsAndSelf() =>
				new[] { (ITreeNode)this }.Concat(Descendants());
			public void RefreshUI() {
				RefreshCount++;
				Data.OnRefreshUI();
			}
		}
	}
}
