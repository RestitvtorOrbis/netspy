// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using dnSpy.Bundles;
using dnSpy.Contracts.App;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnSpy.Contracts.TreeView;
using dnSpy.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Contract and bundle-policy tests for the centralized close guard.</summary>
	public sealed class DocumentCloseGuardContractTests {
		[Fact]
		public void ExportMetadataHasStableNameAndOrderConstants() {
			var attribute = new ExportDsDocumentCloseGuardAttribute(
				"BundleWorkspace", DsDocumentCloseGuardConstants.ORDER_BUNDLE_WORKSPACE);
			Assert.Equal(typeof(IDsDocumentCloseGuard), attribute.ContractType);
			Assert.Equal("BundleWorkspace", attribute.Name);
			Assert.Equal(1000d, attribute.Order);
			Assert.Equal(double.MaxValue, DsDocumentCloseGuardConstants.ORDER_DEFAULT);
		}

		[Fact]
		public void CancelLeavesDirtyWorkspaceUntouched() {
			var bundle = DispatchProxy.Create<IDsBundleDocument, BundleProxy>();
			((BundleProxy)(object)bundle).Dirty = true;
			var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Cancel };
			var guard = new BundleDocumentCloseGuard(messageBox);

			Assert.False(guard.CanClose(new IDsDocument[] { bundle }, DsDocumentCloseReason.Remove));
			Assert.Equal(0, ((BundleProxy)(object)bundle).RevertCount);
			Assert.Single(messageBox.Messages);
		}

		[Fact]
		public void DiscardRevertsDirtyWorkspace() {
			var bundle = DispatchProxy.Create<IDsBundleDocument, BundleProxy>();
			((BundleProxy)(object)bundle).Dirty = true;
			var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.No };
			var guard = new BundleDocumentCloseGuard(messageBox);

			Assert.True(guard.CanClose(new IDsDocument[] { bundle }, DsDocumentCloseReason.Remove));
			Assert.Equal(1, ((BundleProxy)(object)bundle).RevertCount);
		}

		[Fact]
		public void SaveWithoutRebuildServiceCancelsWithoutDiscarding() {
			var bundle = DispatchProxy.Create<IDsBundleDocument, BundleProxy>();
			((BundleProxy)(object)bundle).Dirty = true;
			var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Yes };
			var guard = new BundleDocumentCloseGuard(messageBox);

			Assert.False(guard.CanClose(new IDsDocument[] { bundle }, DsDocumentCloseReason.Remove));
			Assert.Equal(0, ((BundleProxy)(object)bundle).RevertCount);
			Assert.Single(messageBox.Messages);
		}

		[Fact]
		public void GuardsAreOrderedByNumberThenOrdinalName() {
			RunOnSta(window => {
				var calls = new List<string>();
				var guards = new[] {
					CreateGuard("Zulu", 2, calls),
					CreateGuard("Alpha", 2, calls),
					CreateGuard("Earlier", 1, calls),
				};
				var service = new DsDocumentCloseGuardService(CreateAppWindow(window), guards);
				var document = DispatchProxy.Create<IDsDocument, DocumentProxy>();

				Assert.True(service.TryExecute(new[] { document }, DsDocumentCloseReason.Remove,
					static () => true));
				Assert.Equal(new[] { "Earlier", "Alpha", "Zulu" }, calls);
			});
		}

		[Fact]
		public void GuardReentrancyFailsClosedWithoutRunningNestedMutation() {
			RunOnSta(window => {
				var document = DispatchProxy.Create<IDsDocument, DocumentProxy>();
				DsDocumentCloseGuardService? service = null;
				bool nestedMutationRan = false;
				bool nestedResult = true;
				var guard = new CallbackGuard((documents, reason) => {
					nestedResult = service!.TryExecute(documents, reason, () => {
						nestedMutationRan = true;
						return true;
					});
					return true;
				});
				service = new DsDocumentCloseGuardService(CreateAppWindow(window),
					new[] { CreateLazyGuard("Reentrant", 1, guard) });

				Assert.True(service.TryExecute(new[] { document }, DsDocumentCloseReason.Remove,
					static () => true));
				Assert.False(nestedResult);
				Assert.False(nestedMutationRan);
			});
		}

		[Fact]
		public void DuplicateGuardNamesAreRejectedAtComposition() {
			RunOnSta(window => {
				var guards = new[] {
					CreateLazyGuard("Duplicate", 1, new CallbackGuard((_, _) => true)),
					CreateLazyGuard("Duplicate", 2, new CallbackGuard((_, _) => true)),
				};
				Assert.Throws<InvalidOperationException>(() =>
					new DsDocumentCloseGuardService(CreateAppWindow(window), guards));
			});
		}

		[Fact]
		public void GuardExceptionCancelsWithoutRunningMutation() {
			RunOnSta(window => {
				bool mutationRan = false;
				var service = new DsDocumentCloseGuardService(CreateAppWindow(window),
					new[] { CreateLazyGuard("Throws", 1, new CallbackGuard((_, _) =>
						throw new InvalidOperationException("test"))) });
				var document = DispatchProxy.Create<IDsDocument, DocumentProxy>();

				Assert.False(service.TryExecute(new[] { document }, DsDocumentCloseReason.Remove, () => {
					mutationRan = true;
					return true;
				}));
				Assert.False(mutationRan);
			});
		}

		[Fact]
		public void RealDocumentServiceCancellationPrecedesKeyBatchAndClearMutation() {
			string[] files = Enumerable.Range(0, 3).Select(_ => CopyAssembly()).ToArray();
			try {
				RunOnSta(window => {
					DsDocumentCloseGuardService? coordinator = null;
					IDsDocumentService? service = null;
					var reasons = new List<DsDocumentCloseReason>();
					var guard = new CallbackGuard((_, reason) => {
						reasons.Add(reason);
						// A read during CanClose would throw LockRecursionException if the
						// document-service read lock were still held by the caller.
						_ = service!.GetDocuments();
						return false;
					});
					coordinator = new DsDocumentCloseGuardService(CreateAppWindow(window),
						new[] { CreateLazyGuard("Deny", 1, guard) });
					using var composition = ActualDocumentServiceComposition.Create(coordinator);
					service = composition.Service;
					IDsDocument[] documents = files.Select(a => service.TryGetOrCreate(
						DsDocumentInfo.CreateDocument(a))).Where(a => a is not null).Cast<IDsDocument>().ToArray();
					Assert.Equal(3, documents.Length);
					var changes = new List<NotifyDocumentCollectionChangedEventArgs>();
					service.CollectionChanged += (_, e) => changes.Add(e);
					service.Remove(documents[0].Key);
					service.Remove(new[] { documents[1], documents[1] });
					service.Clear();

					Assert.Equal(documents, service.GetDocuments());
					Assert.Empty(changes);
					Assert.Equal(new[] { DsDocumentCloseReason.Remove, DsDocumentCloseReason.Remove,
						DsDocumentCloseReason.Remove }, reasons);
				});
			}
			finally {
				DeleteFiles(files);
			}
		}

		[Fact]
		public void RealDocumentListLoaderCancellationPrecedesLoadReloadAndCloseAll() {
			string file = CopyAssembly();
			try {
				RunOnSta(window => {
					var reasons = new List<DsDocumentCloseReason>();
					var guard = new CallbackGuard((_, reason) => {
						reasons.Add(reason);
						return false;
					});
					var coordinator = new DsDocumentCloseGuardService(CreateAppWindow(window),
						new[] { CreateLazyGuard("Deny", 1, guard) });
					using var composition = ActualDocumentServiceComposition.Create(coordinator);
					IDsDocumentService service = composition.Service;
					IDsDocument? document = service.TryGetOrCreate(DsDocumentInfo.CreateDocument(file));
					Assert.NotNull(document);
					object documentList = CreateDocumentList();
					object loader = CreateDocumentListLoader(CreateAppWindow(window), service, coordinator,
						documentList);
					object documentLoader = CreateDefaultDocumentLoader(service);
					Type loaderType = loader.GetType();
					MethodInfo load = loaderType.GetMethod("Load", BindingFlags.Instance |
						BindingFlags.Public | BindingFlags.NonPublic, null,
						new[] { documentList.GetType(), documentLoader.GetType().GetInterfaces().Single(a =>
							a.Name == "IDsDocumentLoader") }, null)!;
					// Reflection selects the two internal product contracts while keeping this test
					// independent of their visibility.
					Assert.False((bool)load.Invoke(loader, new[] { documentList, documentLoader })!);
					MethodInfo reload = loaderType.GetMethod("Reload", BindingFlags.Instance |
						BindingFlags.Public | BindingFlags.NonPublic)!;
					Assert.False((bool)reload.Invoke(loader, new[] { documentLoader })!);
					MethodInfo closeAll = loaderType.GetMethod("CloseAll", BindingFlags.Instance |
						BindingFlags.Public | BindingFlags.NonPublic)!;
					closeAll.Invoke(loader, null);

					Assert.Single(service.GetDocuments());
					Assert.Equal(new[] { DsDocumentCloseReason.LoadList, DsDocumentCloseReason.ReloadList,
						DsDocumentCloseReason.Remove }, reasons);
				});
			}
			finally {
				DeleteFiles(new[] { file });
			}
		}

		[Fact]
		public void RealDocumentListLoaderAuthorizedClearConsumesOneAuthorization() {
			string file = CopyAssembly();
			try {
				RunOnSta(window => {
					IAppWindow appWindow = CreateAppWindow(window);
					var reasons = new List<DsDocumentCloseReason>();
					var guard = new CallbackGuard((_, reason) => {
						reasons.Add(reason);
						return true;
					});
					var coordinator = new DsDocumentCloseGuardService(appWindow,
						new[] { CreateLazyGuard("Counting", 1, guard) });
					using var composition = ActualDocumentServiceComposition.Create(coordinator);
					IDsDocumentService service = composition.Service;
					IDsDocument document = service.TryGetOrCreate(DsDocumentInfo.CreateDocument(file))!;
					Assert.NotNull(document);
					var changes = new List<NotifyDocumentCollectionChangedEventArgs>();
					service.CollectionChanged += (_, e) => changes.Add(e);

					object documentList = CreateDocumentList();
					object loader = CreateDocumentListLoader(appWindow, service, coordinator, documentList);
					object documentLoader = CreateDefaultDocumentLoader(service);
					Type loaderType = loader.GetType();
					Type loaderContract = documentLoader.GetType().GetInterfaces().Single(a =>
						a.Name == "IDsDocumentLoader");
					MethodInfo load = loaderType.GetMethod("Load", BindingFlags.Instance |
						BindingFlags.Public | BindingFlags.NonPublic, null,
						new[] { documentList.GetType(), loaderContract }, null)!;
					Assert.True(RunWorkerWithDispatcher(window.Dispatcher, () =>
						(bool)load.Invoke(loader, new[] { documentList, documentLoader })!));

					Assert.Equal(new[] { DsDocumentCloseReason.LoadList }, reasons);
					Assert.Single(changes);
					Assert.Equal(NotifyDocumentCollectionType.Clear, changes[0].Type);
					Assert.Equal(new[] { document }, changes[0].Documents);
					Assert.Empty(service.GetDocuments());
				});
			}
			finally {
				DeleteFiles(new[] { file });
			}
		}

		[Fact]
		public void RealDirtyBundleGuardCancelsWorkerDocumentRemovalBeforeMutation() {
			RunDirtyBundleCase((appWindow, dispatcher, service, coordinator, bundle, entry,
				messageBox, uiThreadId) => {
				List<Exception> promptFailures = InstallBundlePromptProbe(messageBox, service, bundle,
					entry, uiThreadId);
				RunWorkerWithDispatcher(dispatcher, () => service.Remove(bundle.Key));
				RunWorkerWithDispatcher(dispatcher,
					() => service.Remove(new IDsDocument[] { bundle, bundle }));
				RunWorkerWithDispatcher(dispatcher, service.Clear);

				Assert.Empty(promptFailures);
				Assert.Equal(3, messageBox.Messages.Count);
				Assert.Single(service.GetDocuments());
				Assert.Same(bundle, service.GetDocuments()[0]);
				Assert.True(bundle.HasPendingChanges);
			});
		}

		[Fact]
		public void RealDirtyBundleGuardCancelsWorkerDocumentListOperationsBeforeMutation() {
			RunDirtyBundleCase((appWindow, dispatcher, service, coordinator, bundle, entry,
				messageBox, uiThreadId) => {
				List<Exception> promptFailures = InstallBundlePromptProbe(messageBox, service, bundle,
					entry, uiThreadId);
				object documentList = CreateDocumentList();
				object loader = CreateDocumentListLoader(appWindow, service, coordinator, documentList);
				object documentLoader = CreateDefaultDocumentLoader(service);
				Type loaderType = loader.GetType();
				Type loaderContract = documentLoader.GetType().GetInterfaces().Single(a =>
					a.Name == "IDsDocumentLoader");
				MethodInfo load = loaderType.GetMethod("Load", BindingFlags.Instance |
					BindingFlags.Public | BindingFlags.NonPublic, null,
					new[] { documentList.GetType(), loaderContract }, null)!;
				Assert.False(RunWorkerWithDispatcher(dispatcher, () => (bool)load.Invoke(loader,
					new[] { documentList, documentLoader })!));
				MethodInfo reload = loaderType.GetMethod("Reload", BindingFlags.Instance |
					BindingFlags.Public | BindingFlags.NonPublic)!;
				Assert.False(RunWorkerWithDispatcher(dispatcher, () => (bool)reload.Invoke(loader,
					new[] { documentLoader })!));
				MethodInfo closeAll = loaderType.GetMethod("CloseAll", BindingFlags.Instance |
					BindingFlags.Public | BindingFlags.NonPublic)!;
				RunWorkerWithDispatcher(dispatcher, () => closeAll.Invoke(loader, null));

				Assert.Empty(promptFailures);
				Assert.Equal(3, messageBox.Messages.Count);
				Assert.Single(service.GetDocuments());
				Assert.Same(bundle, service.GetDocuments()[0]);
				Assert.True(bundle.HasPendingChanges);
			});
		}

		[Fact]
		public void RealDirtyBundleGuardProtectsAppCloseComposition() {
			RunDirtyBundleCase((appWindow, dispatcher, service, coordinator, bundle, entry,
				messageBox, uiThreadId) => {
				List<Exception> promptFailures = InstallBundlePromptProbe(messageBox, service, bundle,
					entry, uiThreadId);
				CreateAppCloseLoader(appWindow, service, coordinator);
				var args = new CancelEventArgs();
				RaiseClosing(appWindow, args);

				Assert.True(args.Cancel);
				Assert.Empty(promptFailures);
				Assert.Single(messageBox.Messages);
				Assert.Single(service.GetDocuments());
				Assert.Same(bundle, service.GetDocuments()[0]);
			});
		}

		[Fact]
		public void AppCloseLoaderUsesOneBundlePromptAndCancelsClosing() {
			RunOnSta(window => {
				foreach (bool unrelatedListenerFirst in new[] { true, false }) {
					var appWindow = CreateAppWindow(window);
					var appProxy = (AppWindowProxy)(object)appWindow;
					var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Cancel };
					var bundle = DispatchProxy.Create<IDsBundleDocument, BundleProxy>();
					((BundleProxy)(object)bundle).Dirty = true;
					var guard = new BundleDocumentCloseGuard(messageBox);
					var coordinator = new DsDocumentCloseGuardService(appWindow,
						new[] { CreateLazyGuard("BundleWorkspace", 1000, guard) });
					using (var composition = ActualDocumentServiceComposition.Create(coordinator)) {
						IDsDocumentService service = composition.Service;
						messageBox.OnShow = () => _ = service.GetDocuments();
						service.ForceAdd(bundle, false, null);
						EventHandler<CancelEventArgs> unrelated = static (_, _) => { };
						if (unrelatedListenerFirst)
							appProxy.AddClosing(unrelated);
						CreateAppCloseLoader(appWindow, service, coordinator);
						if (!unrelatedListenerFirst)
							appProxy.AddClosing(unrelated);

						var args = new CancelEventArgs();
						appProxy.RaiseClosing(args);
						Assert.True(args.Cancel);
						Assert.Single(messageBox.Messages);
					}
				}
			});
		}

		[Fact]
		public void ExactNestedClearRequiresMatchingReasonSetAndSingleConsumption() {
			RunOnSta(window => {
				var document = DispatchProxy.Create<IDsDocument, DocumentProxy>();
				var coordinator = new DsDocumentCloseGuardService(CreateAppWindow(window),
					Array.Empty<Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata>>());
				MethodInfo clear = typeof(DsDocumentCloseGuardService).GetMethod("TryExecuteClear",
					BindingFlags.Instance | BindingFlags.NonPublic)!;
				bool authorized = false;
				bool wrongReason = true;
				bool wrongSet = true;
				bool second = true;
				Assert.True(coordinator.TryExecute(new[] { document }, DsDocumentCloseReason.LoadList, () => {
					authorized = InvokeClear(clear, coordinator, new[] { document },
						DsDocumentCloseReason.LoadList);
					wrongReason = InvokeClear(clear, coordinator, new[] { document },
						DsDocumentCloseReason.Remove);
					var other = DispatchProxy.Create<IDsDocument, DocumentProxy>();
					wrongSet = InvokeClear(clear, coordinator, new[] { other },
						DsDocumentCloseReason.LoadList);
					second = InvokeClear(clear, coordinator, new[] { document },
						DsDocumentCloseReason.LoadList);
					return true;
				}));
				Assert.True(authorized);
				Assert.False(wrongReason);
				Assert.False(wrongSet);
				Assert.False(second);
			});
		}

		[Fact]
		public async Task WorkerCallMarshalsGuardsAndMutationSynchronouslyToUiDispatcher() {
			using var ready = new ManualResetEventSlim();
			using var completed = new ManualResetEventSlim();
			Exception? failure = null;
			Dispatcher? dispatcher = null;
			DsDocumentCloseGuardService? service = null;
			int uiThreadId = 0;
			int guardThreadId = 0;
			int mutationThreadId = 0;
			var uiThread = new Thread(() => {
				try {
					uiThreadId = Environment.CurrentManagedThreadId;
					var window = new Window();
					dispatcher = window.Dispatcher;
					service = new DsDocumentCloseGuardService(CreateAppWindow(window),
						new[] { CreateLazyGuard("UI", 1, new CallbackGuard((_, _) => {
							guardThreadId = Environment.CurrentManagedThreadId;
							return true;
						})) });
					ready.Set();
					Dispatcher.Run();
					window.Close();
				}
				catch (Exception ex) {
					failure = ex;
					ready.Set();
				}
			});
			uiThread.SetApartmentState(ApartmentState.STA);
			uiThread.Start();
			try {
				Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
				if (failure is not null)
					throw new Xunit.Sdk.XunitException(failure.ToString());
				Assert.NotNull(service);
				var document = DispatchProxy.Create<IDsDocument, DocumentProxy>();
				Task worker = Task.Run(() => {
					Assert.True(service!.TryExecute(new[] { document }, DsDocumentCloseReason.Remove,
						() => {
							mutationThreadId = Environment.CurrentManagedThreadId;
							completed.Set();
							return true;
						}));
				}, TestContext.Current.CancellationToken);
				Assert.True(completed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
				await worker;
				Assert.Equal(uiThreadId, guardThreadId);
				Assert.Equal(uiThreadId, mutationThreadId);
			}
			finally {
				if (dispatcher is not null)
					dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
				uiThread.Join(TimeSpan.FromSeconds(5));
			}
		}

		static Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata> CreateLazyGuard(
			string name, double order, IDsDocumentCloseGuard guard) =>
			new Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata>(() => guard,
				new CloseGuardMetadata(name, order));

		static bool InvokeClear(MethodInfo method, DsDocumentCloseGuardService coordinator,
			IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason) =>
			(bool)method.Invoke(coordinator, new object[] { documents, reason,
				(Func<bool>)(() => true) })!;

		internal static object CreateDocumentListLoader(IAppWindow appWindow, IDsDocumentService service,
			IDsDocumentCloseGuardService coordinator, object documentList) {
			Assembly product = Assembly.Load("dnSpy");
			Type loaderType = product.GetType("dnSpy.Documents.Tabs.DocumentListLoader", true)!;
			object documentTabService = CreateUninitializedDocumentTabService(product, service,
				out object documentTabSerializer);
			object documentListService = CreateConfiguredDocumentListService(product, documentList);
			var constructor = loaderType.GetConstructors(BindingFlags.Instance |
				BindingFlags.Public | BindingFlags.NonPublic).Single();
			Type listenerType = constructor.GetParameters()[^1].ParameterType.GetGenericArguments()[0];
			Array listeners = Array.CreateInstance(listenerType, 0);
			return constructor.Invoke(new object?[] { appWindow, documentListService, documentTabService,
				documentTabSerializer,
				service, coordinator, listeners });
		}

		static object CreateUninitializedDocumentTabService(Assembly product, IDsDocumentService service,
			out object documentTabSerializer) {
			Type treeViewType = product.GetType("dnSpy.Documents.TreeView.DocumentTreeView", true)!;
			object treeView = RuntimeHelpers.GetUninitializedObject(treeViewType);
			var tree = DispatchProxy.Create<ITreeView, TestTreeViewProxy>();
			var root = DispatchProxy.Create<ITreeNode, TestTreeNodeProxy>();
			((TestTreeViewProxy)(object)tree).RootNode = root;
			((TestTreeNodeProxy)(object)root).ChildNodes = new[] { root };
			SetBackingField(treeView, "TreeView", tree);
			SetBackingField(treeView, "DocumentService", service);
			SetField(treeView, "dispatcher", Dispatcher.CurrentDispatcher);
			SetField(treeView, "actionsToCall", new List<Action>());

			Type tabGroupServiceType = product.GetType("dnSpy.Tabs.TabGroupService", true)!;
			object tabGroups = Activator.CreateInstance(tabGroupServiceType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
				new object?[] { null, null, null, null }, null)!;

			Type tabServiceType = product.GetType("dnSpy.Documents.Tabs.DocumentTabService", true)!;
			object tabService = RuntimeHelpers.GetUninitializedObject(tabServiceType);
			SetBackingField(tabService, "DocumentTreeView", treeView);
			SetBackingField(tabService, "TabGroupService", tabGroups);

			Type serializerType = product.GetType("dnSpy.Documents.Tabs.DocumentTabSerializer", true)!;
			documentTabSerializer = Activator.CreateInstance(serializerType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
				new object?[] { null, tabService }, null)!;
			return tabService;
		}

		static object CreateConfiguredDocumentListService(Assembly product, object documentList) {
			Type type = product.GetType("dnSpy.Documents.Tabs.DocumentListService", true)!;
			object service = Activator.CreateInstance(type,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
				Array.Empty<object>(), null)!;
			FieldInfo lists = type.GetField("documentsList", BindingFlags.Instance |
				BindingFlags.NonPublic)!;
			var list = (IList)lists.GetValue(service)!;
			list.Add(documentList);
			type.GetField("selectedIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(service, 0);
			type.GetField("hasLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(service, true);
			return service;
		}

		static void SetBackingField(object instance, string propertyName, object? value) {
			FieldInfo field = instance.GetType().GetField($"<{propertyName}>k__BackingField",
				BindingFlags.Instance | BindingFlags.NonPublic)!;
			field.SetValue(instance, value);
		}

		static void SetField(object instance, string fieldName, object? value) {
			FieldInfo field = instance.GetType().GetField(fieldName,
				BindingFlags.Instance | BindingFlags.NonPublic)!;
			field.SetValue(instance, value);
		}

		internal static object CreateDocumentList() {
			Type type = Assembly.Load("dnSpy").GetType("dnSpy.Documents.Tabs.DocumentList", true)!;
			return Activator.CreateInstance(type, new object[] { "CloseGuardTest" })!;
		}

		internal static object CreateDefaultDocumentLoader(IDsDocumentService service) {
			Type type = Assembly.Load("dnSpy").GetType("dnSpy.Documents.DefaultDsDocumentLoader", true)!;
			return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic, null, new object[] { service }, null)!;
		}

		internal static void CreateAppCloseLoader(IAppWindow appWindow, IDsDocumentService service,
			IDsDocumentCloseGuardService coordinator) {
			Type type = Assembly.Load("dnSpy").GetType("dnSpy.Documents.DocumentCloseGuardCommandLoader", true)!;
			Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic, null, new object[] { appWindow, service, coordinator }, null);
		}

		static string CopyAssembly() {
			string source = typeof(DocumentCloseGuardContractTests).Assembly.Location;
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			File.Copy(source, destination);
			return destination;
		}

		static void RunDirtyBundleCase(Action<IAppWindow, Dispatcher, IDsDocumentService,
			DsDocumentCloseGuardService, BundleDsDocument, BundleEntry, RecordingMessageBoxService,
			int> action) {
			RunOnSta(window => {
				IAppWindow appWindow = CreateAppWindow(window);
				var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Cancel };
				var guard = new BundleDocumentCloseGuard(messageBox);
				var coordinator = new DsDocumentCloseGuardService(appWindow,
					new[] { CreateLazyGuard("BundleWorkspace",
						DsDocumentCloseGuardConstants.ORDER_BUNDLE_WORKSPACE, guard) });
				using var composition = ActualDocumentServiceComposition.Create(coordinator);
				using BundleDsDocument bundle = BundleWorkspaceTreeStateTests.CreateBundleDocument(
					out BundleEntry entry);
				bundle.Workspace.SetReplacement(entry, new byte[] { 0x2A },
					new BundleReplacementInfo("close guard test"));
				composition.Service.ForceAdd(bundle, false, null);
				action(appWindow, window.Dispatcher, composition.Service, coordinator, bundle, entry,
					messageBox,
					Environment.CurrentManagedThreadId);
			});
		}

		internal static T RunWorkerWithDispatcher<T>(Dispatcher dispatcher, Func<T> action) {
			if (dispatcher is null)
				throw new ArgumentNullException(nameof(dispatcher));
			if (action is null)
				throw new ArgumentNullException(nameof(action));
			Task<T> task = Task.Run(action);
			var frame = new DispatcherFrame();
			task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
				new Action(() => frame.Continue = false)), CancellationToken.None,
				TaskContinuationOptions.None, TaskScheduler.Default);
			var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) {
				Interval = TimeSpan.FromSeconds(5),
			};
			bool timedOut = false;
			timeout.Tick += (_, _) => {
				timedOut = true;
				frame.Continue = false;
			};
			timeout.Start();
			Dispatcher.PushFrame(frame);
			timeout.Stop();
			if (timedOut)
				throw new TimeoutException("The worker close operation did not complete on the UI dispatcher.");
			return task.GetAwaiter().GetResult();
		}

		internal static void RunWorkerWithDispatcher(Dispatcher dispatcher, Action action) {
			RunWorkerWithDispatcher(dispatcher, () => {
				action();
				return true;
			});
		}

		static List<Exception> InstallBundlePromptProbe(RecordingMessageBoxService messageBox,
			IDsDocumentService service, BundleDsDocument bundle, BundleEntry entry, int uiThreadId) {
			var failures = new List<Exception>();
			messageBox.OnShow = () => {
				try {
					Assert.Equal(uiThreadId, Environment.CurrentManagedThreadId);
					IDsDocument[] documents = service.GetDocuments();
					Assert.Single(documents);
					Assert.Same(bundle, documents[0]);
					Assert.True(bundle.HasPendingChanges);
					using Stream stream = bundle.Workspace.OpenCurrentRead(entry);
					Assert.Equal(0x2A, stream.ReadByte());
					Exception? workerFailure = null;
					Task probe = Task.Run(() => {
						try {
							_ = service.GetDocuments();
							_ = bundle.HasPendingChanges;
							using Stream workerStream = bundle.Workspace.OpenCurrentRead(entry);
							_ = workerStream.ReadByte();
						}
						catch (Exception ex) {
							workerFailure = ex;
						}
					});
					if (!probe.Wait(TimeSpan.FromSeconds(2)))
						throw new InvalidOperationException("A document/workspace lock was held during the close prompt.");
					if (workerFailure is not null)
						throw new InvalidOperationException("The prompt lock probe failed.", workerFailure);
				}
				catch (Exception ex) {
					failures.Add(ex);
				}
			};
			return failures;
		}

		static void DeleteFiles(IEnumerable<string> files) {
			foreach (string file in files)
				try { File.Delete(file); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
		}

		static Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata> CreateGuard(
			string name, double order, List<string> calls) =>
			CreateLazyGuard(name, order, new CallbackGuard((_, _) => {
				calls.Add(name);
				return true;
			}));

		internal static IAppWindow CreateAppWindow(Window window) {
			var appWindow = DispatchProxy.Create<IAppWindow, AppWindowProxy>();
			((AppWindowProxy)(object)appWindow).Window = window;
			return appWindow;
		}

		internal static void RunOnSta(Action<Window> action) {
			Exception? failure = null;
			Dispatcher? dispatcher = null;
			var thread = new Thread(() => {
				try {
					var window = new Window();
					dispatcher = window.Dispatcher;
					action(window);
					window.Close();
				}
				catch (Exception ex) {
					failure = ex;
				}
			});
			thread.SetApartmentState(ApartmentState.STA);
			thread.IsBackground = true;
			thread.Start();
			if (!thread.Join(TimeSpan.FromSeconds(15))) {
				// A timed-out UI action must not leave a foreground STA thread keeping the test
				// process alive. Interrupt the managed wait after requesting dispatcher shutdown.
				try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); }
				catch (Exception) { }
				try { thread.Interrupt(); }
				catch (Exception) { }
				thread.Join(TimeSpan.FromSeconds(5));
				throw new Xunit.Sdk.XunitException(
					$"The STA test action timed out (thread stopped: {!thread.IsAlive}).");
			}
			if (failure is not null)
				throw new Xunit.Sdk.XunitException(failure.ToString());
		}

		internal sealed class ActualDocumentServiceComposition : IDisposable {
			readonly CompositionContainer container;

			ActualDocumentServiceComposition(CompositionContainer container, IDsDocumentService service) {
				this.container = container;
				Service = service;
			}

			public IDsDocumentService Service { get; }

			public static ActualDocumentServiceComposition Create(IDsDocumentCloseGuardService coordinator) {
				Assembly product = Assembly.Load("dnSpy");
				Type serviceType = product.GetType("dnSpy.Documents.DsDocumentService", true)!;
				Type providerType = product.GetType("dnSpy.Documents.DefaultDsDocumentProvider", true)!;
				Type settingsType = product.GetType("dnSpy.Documents.DsDocumentServiceSettings", true)!;
				Type settingsContractType = product.GetType("dnSpy.Documents.IDsDocumentServiceSettings", true)!;
				object settings = Activator.CreateInstance(settingsType)!;
				var provider = (IDsDocumentProvider)Activator.CreateInstance(providerType)!;
				var container = new CompositionContainer(new TypeCatalog(serviceType));
				var batch = new CompositionBatch();
				AttributedModelServices.AddExportedValue(batch, settingsContractType.FullName!, settings);
				batch.AddExportedValue<IDsDocumentProvider>(provider);
				batch.AddExportedValue<IDsDocumentCloseGuardService>(coordinator);
				container.Compose(batch);
				return new ActualDocumentServiceComposition(container,
					container.GetExportedValue<IDsDocumentService>()!);
			}

			public void Dispose() => container.Dispose();
		}

		internal static void RaiseClosing(IAppWindow appWindow, CancelEventArgs args) =>
			((AppWindowProxy)(object)appWindow).RaiseClosing(args);

		sealed class CallbackGuard : IDsDocumentCloseGuard {
			readonly Func<IReadOnlyList<IDsDocument>, DsDocumentCloseReason, bool> callback;

			public CallbackGuard(Func<IReadOnlyList<IDsDocument>, DsDocumentCloseReason, bool> callback) =>
				this.callback = callback;

			public bool CanClose(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason) =>
				callback(documents, reason);
		}

		sealed class CloseGuardMetadata : IDsDocumentCloseGuardMetadata {
			public CloseGuardMetadata(string name, double order) {
				Name = name;
				Order = order;
			}

			public string Name { get; }
			public double Order { get; }
		}

		sealed class DocumentProxy : DispatchProxy {
			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.ReturnType == typeof(string))
					return string.Empty;
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class AppWindowProxy : DispatchProxy {
			public Window? Window { get; set; }
			EventHandler<CancelEventArgs>? closing;

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_MainWindow")
					return Window;
				if (targetMethod?.Name == "add_MainWindowClosing") {
					closing += (EventHandler<CancelEventArgs>)args![0]!;
					return null;
				}
				if (targetMethod?.Name == "remove_MainWindowClosing") {
					closing -= (EventHandler<CancelEventArgs>)args![0]!;
					return null;
				}
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}

			public void RaiseClosing(CancelEventArgs args) => closing?.Invoke(this, args);
			public void AddClosing(EventHandler<CancelEventArgs> handler) => closing += handler;
		}

		sealed class TestTreeViewProxy : DispatchProxy {
			public ITreeNode? RootNode { get; set; }

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_Root")
					return RootNode;
				if (targetMethod?.ReturnType == typeof(TreeNodeData[]))
					return Array.Empty<TreeNodeData>();
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class TestTreeNodeProxy : DispatchProxy {
			public IEnumerable<ITreeNode> ChildNodes { get; set; } = Array.Empty<ITreeNode>();

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_Children")
					return ChildNodes.ToList();
				if (targetMethod?.Name == "get_DataChildren")
					return Array.Empty<TreeNodeData>();
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class BundleProxy : DispatchProxy {
			public bool Dirty { get; set; }
			public int RevertCount { get; private set; }

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				switch (targetMethod?.Name) {
				case "get_Key": return new FilenameKey("bundle.exe");
				case "get_HasPendingChanges": return Dirty;
				case "get_HasWorkspaceErrors": return false;
				case "get_SourceBundleFilename": return "bundle.exe";
				case "RevertAllWorkspaceChanges":
					RevertCount++;
					Dirty = false;
					return null;
				}
				if (targetMethod?.ReturnType == typeof(string))
					return string.Empty;
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class RecordingMessageBoxService : IMessageBoxService {
			public MsgBoxButton Result { get; set; }
			public List<string> Messages { get; } = new List<string>();
			public Action? OnShow { get; set; }

			public MsgBoxButton? ShowIgnorableMessage(Guid guid, string message,
				MsgBoxButton buttons = MsgBoxButton.OK, Window? ownerWindow = null) {
				Messages.Add(message);
				return Result;
			}

			public MsgBoxButton Show(string message, MsgBoxButton buttons = MsgBoxButton.OK,
				Window? ownerWindow = null) {
				OnShow?.Invoke();
				Messages.Add(message);
				return Result;
			}

			public T? Ask<T>(string labelMessage, string? defaultText = null, string? title = null,
				Func<string, T>? converter = null, Func<string, string?>? verifier = null,
				Window? ownerWindow = null) => default;

			public void Show(Exception exception, string? msg = null, Window? ownerWindow = null) =>
				Messages.Add(msg ?? exception.Message);
		}
	}
}
