// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleWorkspaceTreeStateTests {
		[Fact]
		public void UnchangedEntryAndRootHaveNoDirtyState() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			(DsDocumentNode root, DsDocumentNode child) = CreateNodes(document, entry);

			Assert.False(document.HasPendingChanges);
			Assert.False(document.HasWorkspaceErrors);
			Assert.Equal(BundleWorkspaceEntryState.Unchanged, document.Workspace.GetEntryState(entry));
			Assert.DoesNotContain("[modified]", Render(root), StringComparison.Ordinal);
			Assert.DoesNotContain("[modified]", Render(child), StringComparison.Ordinal);
		}

		[Fact]
		public void ModifiedEntryAndRootRefreshStateWithoutChangingSource() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			(DsDocumentNode root, DsDocumentNode child) = CreateNodes(document, entry);
			var changes = new List<BundleWorkspaceChangedEventArgs>();
			document.Workspace.Changed += (_, e) => changes.Add(e);

			document.Workspace.SetReplacement(entry, new byte[] { 9, 8, 7 },
				new BundleReplacementInfo("test replacement"));

			Assert.True(document.HasPendingChanges);
			Assert.True(document.Workspace.HasReplacement(entry));
			Assert.Equal(BundleWorkspaceEntryState.Modified,
				document.Workspace.GetEntryState(entry));
			Assert.Contains("[modified]", Render(root), StringComparison.Ordinal);
			Assert.Contains("[modified]", Render(child), StringComparison.Ordinal);
			Assert.Single(changes);
			Assert.True(changes[0].IsReplacement);
		}

		[Fact]
		public void WorkspaceChangeRefreshesLoadedBundleNodesThroughTheRealProviderEvent() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			(DsDocumentNode root, DsDocumentNode child) = CreateNodes(document, entry,
				out RecordingTreeNode rootTree, out RecordingTreeNode childTree);
			int rootRefreshes = rootTree.RefreshCount;
			int childRefreshes = childTree.RefreshCount;

			document.Workspace.SetReplacement(entry, new byte[] { 0x2A },
				new BundleReplacementInfo("event replacement"));

			Assert.True(rootTree.RefreshCount > rootRefreshes);
			Assert.True(childTree.RefreshCount > childRefreshes);
			Assert.Contains("[modified]", Render(root), StringComparison.Ordinal);
			Assert.Contains("[modified]", Render(child), StringComparison.Ordinal);
		}

		[Fact]
		public async Task WorkspaceChangeFromWorkerIsMarshalledToTheTreeDispatcher() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			(DsDocumentNode root, DsDocumentNode child) = CreateNodes(document, entry,
				out RecordingTreeNode rootTree, out RecordingTreeNode childTree);
			using var ready = new ManualResetEventSlim();
			Dispatcher? dispatcher = null;
			int uiThreadId = 0;
			int workerThreadId = 0;
			var uiThread = new Thread(() => {
				uiThreadId = Environment.CurrentManagedThreadId;
				dispatcher = Dispatcher.CurrentDispatcher;
				ready.Set();
				Dispatcher.Run();
			});
			uiThread.SetApartmentState(ApartmentState.STA);
			uiThread.Start();
			try {
				Assert.True(ready.Wait(TimeSpan.FromSeconds(5),
					TestContext.Current.CancellationToken));
				Assert.NotNull(dispatcher);
				// Rebind the actual bundle node to the dedicated UI dispatcher before raising the event.
				dispatcher!.Invoke(root.Initialize);
				await Task.Run(() => {
					workerThreadId = Environment.CurrentManagedThreadId;
					document.Workspace.SetReplacement(entry, new byte[] { 0x2B },
						new BundleReplacementInfo("worker replacement"));
				}, TestContext.Current.CancellationToken);

				Assert.True(SpinWait.SpinUntil(() => rootTree.RefreshCount != 0,
					TimeSpan.FromSeconds(5)));
				Assert.True(childTree.RefreshCount != 0);
				Assert.NotEqual(uiThreadId, workerThreadId);
				Assert.NotEmpty(rootTree.Refreshes);
				Assert.All(rootTree.Refreshes, refresh => {
					Assert.Equal(uiThreadId, refresh.ThreadId);
					Assert.Same(dispatcher, refresh.Dispatcher);
					Assert.NotEqual(workerThreadId, refresh.ThreadId);
				});
				Assert.All(childTree.Refreshes, refresh => {
					Assert.Equal(uiThreadId, refresh.ThreadId);
					Assert.Same(dispatcher, refresh.Dispatcher);
					Assert.NotEqual(workerThreadId, refresh.ThreadId);
				});
			}
			finally {
				if (dispatcher is not null)
					dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
				uiThread.Join(TimeSpan.FromSeconds(5));
			}
		}

		[Fact]
		public void BundleRootTearsDownWorkspaceSubscriptionWhenDocumentIsDisposed() {
			var document = CreateBundleDocument(out BundleEntry entry);
			DsDocumentNode root = new BundleDocumentNodeProvider().Create(null!, null, document)!;
			Assert.IsAssignableFrom<IDisposable>(root);

			document.Dispose();
			// Disposal raises the document teardown event, and explicit disposal remains idempotent.
			((IDisposable)root).Dispose();
			((IDisposable)root).Dispose();
			Assert.Throws<ObjectDisposedException>(() => document.Workspace.GetEntryState(entry));
		}

		[Fact]
		public void RevertEntryRestoresOriginalLogicalStateAndRaisesRefreshEvent() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			(DsDocumentNode root, DsDocumentNode child) = CreateNodes(document, entry);
			document.Workspace.SetReplacement(entry, new byte[] { 1, 2, 3 },
				new BundleReplacementInfo("test replacement"));
			BundleWorkspaceChangedEventArgs? reverted = null;
			document.Workspace.Changed += (_, e) => reverted = e.IsRevert ? e : reverted;

			Assert.True(document.Workspace.Revert(entry));
			Assert.False(document.HasPendingChanges);
			Assert.False(document.Workspace.HasReplacement(entry));
			Assert.Equal(BundleWorkspaceEntryState.Reverted,
				document.Workspace.GetEntryState(entry));
			Assert.DoesNotContain("[modified]", Render(root), StringComparison.Ordinal);
			Assert.DoesNotContain("[modified]", Render(child), StringComparison.Ordinal);
			Assert.Contains("[reverted]", Render(root), StringComparison.Ordinal);
			Assert.Contains("[reverted]", Render(child), StringComparison.Ordinal);
			Assert.NotNull(reverted);
			Assert.Same(entry, reverted!.Entry);
		}

		[Fact]
		public void RevertAllKeepsPriorValidReplacementAndClearsErrorsTransactionally() {
			using var document = CreateBundleDocument(out BundleEntry first);
			BundleEntry second = document.Bundle.Entries[1];
			BundleWorkspace workspace = document.Workspace;
			workspace.SetReplacement(first, new byte[] { 4, 5 }, new BundleReplacementInfo("first"));
			workspace.SetReplacement(second, new byte[] { 6, 7 }, new BundleReplacementInfo("second"));
			workspace.RecordError(second, new InvalidDataException("failed second operation"));

			Assert.Equal(BundleWorkspaceEntryState.Modified, workspace.GetEntryState(first));
			Assert.Equal(BundleWorkspaceEntryState.Error, workspace.GetEntryState(second));
			Assert.Equal(new[] { first, second }, workspace.ModifiedEntries);
			Assert.Equal(new byte[] { 4, 5 }, Read(workspace.OpenCurrentRead(first)));

			Assert.True(workspace.Revert(first));
			Assert.False(workspace.HasReplacement(first));
			Assert.Equal(BundleWorkspaceEntryState.Error, workspace.GetEntryState(second));
			Assert.True(workspace.HasErrors);

			workspace.RevertAll();
			Assert.False(workspace.HasChanges);
			Assert.False(workspace.HasErrors);
			Assert.Empty(workspace.ModifiedEntries);
			Assert.Equal(BundleWorkspaceEntryState.Reverted, workspace.GetEntryState(first));
			Assert.Equal(BundleWorkspaceEntryState.Reverted, workspace.GetEntryState(second));
		}

		[Fact]
		public void OperationErrorIsVisibleAndDoesNotDestroyPriorReplacement() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			(DsDocumentNode root, DsDocumentNode child) = CreateNodes(document, entry);
			document.Workspace.SetReplacement(entry, new byte[] { 0xAA },
				new BundleReplacementInfo("valid replacement"));
			document.Workspace.RecordError(entry, new InvalidDataException("serialization failed"));

			Assert.Equal(BundleWorkspaceEntryState.Error,
				document.Workspace.GetEntryState(entry));
			Assert.Equal(new byte[] { 0xAA }, Read(document.Workspace.OpenCurrentRead(entry)));
			Assert.Contains("[error]", Render(root), StringComparison.Ordinal);
			Assert.Contains("[error]", Render(child), StringComparison.Ordinal);
			Assert.True(document.Workspace.Revert(entry));
			Assert.False(document.Workspace.HasReplacement(entry));
			Assert.False(document.Workspace.HasError(entry));
			Assert.Equal(Read(document.Workspace.OpenOriginalRead(entry)),
				Read(document.Workspace.OpenCurrentRead(entry)));
		}

		[Fact]
		public void RevertAllContextCommandFindsRootsEntriesAndAssemblyWrappersAcrossBundles() {
			using var first = CreateBundleDocument(out BundleEntry firstEntry);
			using var second = CreateBundleDocument(out BundleEntry secondEntry);
			byte[] firstOriginal = Read(first.Workspace.OpenOriginalRead(firstEntry));
			byte[] secondOriginal = Read(second.Workspace.OpenOriginalRead(secondEntry));
			DsDocumentNode firstRoot = new BundleDocumentNodeProvider().Create(null!, null, first)!;
			DsDocumentNode firstEntryNode = GetEntryNode(firstRoot, firstEntry);
			DsDocumentNode secondRoot = new BundleDocumentNodeProvider().Create(null!, null, second)!;
			BundleEntryDocument secondEntryDocument = GetEntryDocument(second, secondEntry);
			DsDocumentNode secondAssemblyNode = CreateAssemblySelectionNode(secondEntryDocument);
			DsDocumentNode secondModuleNode = CreateModuleSelectionNode(secondEntryDocument);

			first.Workspace.SetReplacement(firstEntry, new byte[] { 0x10 },
				new BundleReplacementInfo("first"));
			second.Workspace.SetReplacement(secondEntry, new byte[] { 0x20 },
				new BundleReplacementInfo("second"));
			var context = new TestMenuContext(new DocumentTreeNodeData[] {
				firstRoot, firstEntryNode, secondAssemblyNode, secondModuleNode, secondRoot,
			});
			IMenuItem command = CreateMenuCommand("RevertAllBundleChangesContextMenuCommand");

			Assert.True(command.IsVisible(context));
			Assert.True(command.IsEnabled(context));
			command.Execute(context);

			Assert.False(first.HasPendingChanges);
			Assert.False(second.HasPendingChanges);
			Assert.Equal(BundleWorkspaceEntryState.Reverted,
				first.Workspace.GetEntryState(firstEntry));
			Assert.Equal(BundleWorkspaceEntryState.Reverted,
				second.Workspace.GetEntryState(secondEntry));
			Assert.Equal(firstOriginal, Read(first.Workspace.OpenOriginalRead(firstEntry)));
			Assert.Equal(secondOriginal, Read(second.Workspace.OpenOriginalRead(secondEntry)));
		}

		[Fact]
		public void RevertEntryContextCommandUsesSelectedEntryAndRestoresOriginalBytes() {
			using var document = CreateBundleDocument(out BundleEntry entry);
			DsDocumentNode root = new BundleDocumentNodeProvider().Create(null!, null, document)!;
			DsDocumentNode entryNode = GetEntryNode(root, entry);
			var context = new TestMenuContext(new[] { entryNode });
			IMenuItem command = CreateMenuCommand("RevertBundleEntryContextMenuCommand");
			byte[] original = Read(document.Workspace.OpenOriginalRead(entry));

			Assert.True(command.IsVisible(context));
			Assert.False(command.IsEnabled(context));
			document.Workspace.SetReplacement(entry, new byte[] { 0x7F },
				new BundleReplacementInfo("command replacement"));
			Assert.True(command.IsEnabled(context));

			command.Execute(context);
			Assert.Equal(BundleWorkspaceEntryState.Reverted,
				document.Workspace.GetEntryState(entry));
			Assert.Equal(original, Read(document.Workspace.OpenCurrentRead(entry)));
			Assert.False(command.IsEnabled(context));
		}

		static (DsDocumentNode Root, DsDocumentNode Entry) CreateNodes(BundleDsDocument document,
			BundleEntry entry) => CreateNodes(document, entry, out _, out _);

		static (DsDocumentNode Root, DsDocumentNode Entry) CreateNodes(BundleDsDocument document,
			BundleEntry entry, out RecordingTreeNode rootTree, out RecordingTreeNode entryTree) {
			DsDocumentNode root = new BundleDocumentNodeProvider().Create(null!, null, document)!;
			rootTree = new RecordingTreeNode(root);
			root.TreeNode = rootTree;
			DsDocumentNode entryNode = null!;
			foreach (DsDocumentNode category in root.CreateChildren().Cast<DsDocumentNode>()) {
				var categoryTree = new RecordingTreeNode(category);
				category.TreeNode = categoryTree;
				rootTree.AddChild(categoryTree);
				foreach (DsDocumentNode child in category.CreateChildren().Cast<DsDocumentNode>()) {
					var childTree = new RecordingTreeNode(child);
					child.TreeNode = childTree;
					categoryTree.AddChild(childTree);
					if (ReferenceEquals(((BundleEntryDocument)child.Document).Entry, entry))
						entryNode = child;
				}
			}
			Assert.NotNull(entryNode);
			entryTree = (RecordingTreeNode)entryNode!.TreeNode;
			root.Initialize();
			return (root, entryNode);
		}

		static BundleEntryDocument GetEntryDocument(BundleDsDocument document, BundleEntry entry) =>
			document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().Single(a => ReferenceEquals(a.Entry, entry));

		static DsDocumentNode GetEntryNode(DsDocumentNode root, BundleEntry entry) =>
			root.CreateChildren().Cast<DsDocumentNode>()
				.Single(a => ((BundleFolderDocument)a.Document).Kind == BundleFolderKind.Assemblies)
				.CreateChildren().Cast<DsDocumentNode>()
				.Single(a => ReferenceEquals(((BundleEntryDocument)a.Document).Entry, entry));

		static DsDocumentNode CreateAssemblySelectionNode(BundleEntryDocument entry) {
			using var module = entry.CreateManagedDocument();
			return new BundleDocumentNodeProvider().Create(null!, null,
				module.CreateAssemblyDocument())!;
		}

		static DsDocumentNode CreateModuleSelectionNode(BundleEntryDocument entry) {
			BundleModuleDocument module = entry.CreateManagedDocument();
			Assembly product = Assembly.Load("dnSpy");
			Type nodeType = product.GetType("dnSpy.Documents.TreeView.ModuleDocumentNodeImpl",
				throwOnError: true)!;
			return (DsDocumentNode)Activator.CreateInstance(nodeType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, new object[] { module }, null)!;
		}

		static IMenuItem CreateMenuCommand(string name) {
			Type type = typeof(BundleDocumentNodeProvider).Assembly.GetType(
				"dnSpy.Bundles.Extension." + name, throwOnError: true)!;
			return (IMenuItem)Activator.CreateInstance(type, nonPublic: true)!;
		}

		static string Render(DsDocumentNode node) {
			var output = new StringBuilderTextColorOutput();
			node.Write(output, null!, DocumentNodeWriteOptions.None);
			return output.Text;
		}

		static BundleDsDocument CreateBundleDocument(out BundleEntry firstEntry) {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			BundleDsDocument document = new BundleDsDocument(
				DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			firstEntry = document.Bundle.Entries.First(a => a.FileType == BundleFileType.Assembly);
			return document;
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

		static byte[] Read(Stream stream) {
			using (stream)
			using (var output = new MemoryStream()) {
				stream.CopyTo(output);
				return output.ToArray();
			}
		}

		sealed class TestMenuContext : IMenuItemContext {
			readonly DocumentTreeNodeData[] nodes;
			readonly Dictionary<object, object> state = new Dictionary<object, object>();

			public TestMenuContext(DocumentTreeNodeData[] nodes) => this.nodes = nodes;
			public Guid MenuGuid => Guid.Empty;
			public bool OpenedFromKeyboard => true;
			public GuidObject CreatorObject => new GuidObject(
				MenuConstants.GUIDOBJ_DOCUMENTS_TREEVIEW_GUID, null);
			public IEnumerable<GuidObject> GuidObjects {
				get {
					yield return CreatorObject;
					yield return new GuidObject(MenuConstants.GUIDOBJ_TREEVIEW_NODES_ARRAY_GUID, nodes);
				}
			}

			public T? GetOrCreateState<T>(object key, Func<T> createState) where T : class {
			if (state.TryGetValue(key, out object? value))
				return (T)value;
			T created = createState();
			state.Add(key, created);
			return created;
		}

			public T? Find<T>() {
				foreach (GuidObject item in GuidObjects)
					if (item.Object is T value)
						return value;
				return default;
			}
		}

		sealed class RecordingTreeNode : ITreeNode {
			readonly List<ITreeNode> children = new List<ITreeNode>();
			readonly List<RefreshObservation> refreshes = new List<RefreshObservation>();
			int refreshCount;
			ITreeNode? parent;

			public RecordingTreeNode(TreeNodeData data) => Data = data;
			public int RefreshCount => Volatile.Read(ref refreshCount);
			public RefreshObservation[] Refreshes {
				get { lock (refreshes) return refreshes.ToArray(); }
			}
			public ITreeView TreeView => null!;
			public ITreeNode? Parent => parent;
			public IList<ITreeNode> Children => children;
			public IEnumerable<TreeNodeData> DataChildren => children.Select(a => a.Data);
			public TreeNodeData Data { get; }
			public bool LazyLoading { get; set; }
			public bool IsExpanded { get; set; }
			public bool IsHidden { get; set; }
			public bool IsVisible => !IsHidden && (parent is null || parent.IsVisible);
			public void EnsureChildrenLoaded() { }
			public void AddChild(ITreeNode node) {
				children.Add(node);
				if (node is RecordingTreeNode recording)
					recording.parent = this;
			}
			public IEnumerable<ITreeNode> Descendants() => children.SelectMany(a => a.DescendantsAndSelf());
			public IEnumerable<ITreeNode> DescendantsAndSelf() =>
				new[] { (ITreeNode)this }.Concat(Descendants());
			public void RefreshUI() {
				Interlocked.Increment(ref refreshCount);
				lock (refreshes)
					refreshes.Add(new RefreshObservation(Environment.CurrentManagedThreadId,
						Dispatcher.CurrentDispatcher));
				Data.OnRefreshUI();
			}
		}

		public readonly struct RefreshObservation {
			public RefreshObservation(int threadId, Dispatcher dispatcher) {
				ThreadId = threadId;
				Dispatcher = dispatcher;
			}

			public int ThreadId { get; }
			public Dispatcher Dispatcher { get; }
		}
	}
}
