// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Regression coverage for ordinary document collection removal behavior.</summary>
	public sealed class OrdinaryDocumentRemovalRegressionTests {
		[Fact]
		public void DirectBatchAndClearRemovalKeepBaselineNotifications() {
			string source = typeof(OrdinaryDocumentRemovalRegressionTests).Assembly.Location;
			var files = Enumerable.Range(0, 3).Select(_ => Copy(source)).ToArray();
			try {
				using var composition = TestComposition.Create();
				var notifications = new List<NotifyDocumentCollectionChangedEventArgs>();
				composition.Service.CollectionChanged += (s, e) => notifications.Add(e);
				var documents = files.Select(a => composition.Service.TryGetOrCreate(
					DsDocumentInfo.CreateDocument(a))).ToArray();
				Assert.All(documents, Assert.NotNull);

				composition.Service.Remove(documents[0]!.Key);
				Assert.Equal(NotifyDocumentCollectionType.Remove, notifications[^1].Type);
				Assert.Equal(new[] { documents[0] }, notifications[^1].Documents);

				composition.Service.Remove(new[] { documents[1]!, documents[1]! });
				Assert.Equal(NotifyDocumentCollectionType.Remove, notifications[^1].Type);
				Assert.Equal(new[] { documents[1] }, notifications[^1].Documents);

				composition.Service.Clear();
				Assert.Equal(NotifyDocumentCollectionType.Clear, notifications[^1].Type);
				Assert.Equal(new[] { documents[2] }, notifications[^1].Documents);
				Assert.Empty(composition.Service.GetDocuments());
			}
			finally {
				foreach (string file in files)
					try { File.Delete(file); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void SharedZeroGuardCoordinatorPreservesOrdinaryRemovalOrder() {
			string source = typeof(OrdinaryDocumentRemovalRegressionTests).Assembly.Location;
			var files = Enumerable.Range(0, 3).Select(_ => Copy(source)).ToArray();
			try {
				var coordinator = new ZeroGuardCoordinator();
				using var composition = TestComposition.Create(coordinator);
				var notifications = new List<NotifyDocumentCollectionChangedEventArgs>();
				composition.Service.CollectionChanged += (s, e) => notifications.Add(e);
				var documents = files.Select(a => composition.Service.TryGetOrCreate(
					DsDocumentInfo.CreateDocument(a))).Where(a => a is not null).Cast<IDsDocument>().ToArray();
				Assert.Equal(3, documents.Length);

				composition.Service.Remove(documents[0].Key);
				composition.Service.Remove(new[] { documents[1], documents[1] });
				composition.Service.Clear();

				Assert.Equal(new[] { DsDocumentCloseReason.Remove, DsDocumentCloseReason.Remove,
					DsDocumentCloseReason.Remove }, coordinator.Reasons);
				Assert.Equal(3, notifications.Count);
				Assert.Equal(NotifyDocumentCollectionType.Remove, notifications[0].Type);
				Assert.Equal(NotifyDocumentCollectionType.Remove, notifications[1].Type);
				Assert.Equal(NotifyDocumentCollectionType.Clear, notifications[2].Type);
				Assert.Equal(new[] { documents[2] }, notifications[2].Documents);
				Assert.Empty(composition.Service.GetDocuments());
			}
			finally {
				foreach (string file in files)
					try { File.Delete(file); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void ActualCloseGuardCoordinatorPreservesListAndAppCloseBehavior() {
			string[] files = Enumerable.Range(0, 4).Select(_ =>
				Copy(typeof(OrdinaryDocumentRemovalRegressionTests).Assembly.Location)).ToArray();
			try {
				DocumentCloseGuardContractTests.RunOnSta(window => {
					AssertOrdinaryListPath(window, files[0], ListOperation.Load);
					AssertOrdinaryListPath(window, files[1], ListOperation.Reload);
					AssertOrdinaryListPath(window, files[2], ListOperation.CloseAll);
					AssertOrdinaryListPath(window, files[3], ListOperation.AppClose);
				});
			}
			finally {
				foreach (string file in files) {
					try { File.Delete(file); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
				}
			}
		}

		enum ListOperation {
			Load,
			Reload,
			CloseAll,
			AppClose,
		}

		static void AssertOrdinaryListPath(Window window, string file, ListOperation operation) {
			IAppWindow appWindow = DocumentCloseGuardContractTests.CreateAppWindow(window);
			var coordinator = new DsDocumentCloseGuardService(appWindow,
				Array.Empty<Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata>>());
			using var composition = DocumentCloseGuardContractTests.ActualDocumentServiceComposition
				.Create(coordinator);
			IDsDocumentService service = composition.Service;
			IDsDocument document = service.TryGetOrCreate(DsDocumentInfo.CreateDocument(file))!;
			Assert.NotNull(document);
			var changes = new List<NotifyDocumentCollectionChangedEventArgs>();
			service.CollectionChanged += (_, e) => changes.Add(e);

			if (operation == ListOperation.AppClose) {
				DocumentCloseGuardContractTests.CreateAppCloseLoader(appWindow, service, coordinator);
				var args = new CancelEventArgs();
				DocumentCloseGuardContractTests.RaiseClosing(appWindow, args);
				Assert.False(args.Cancel);
				Assert.Empty(changes);
				Assert.Single(service.GetDocuments());
				Assert.Same(document, service.GetDocuments()[0]);
				return;
			}

			object documentList = DocumentCloseGuardContractTests.CreateDocumentList();
			object loader = DocumentCloseGuardContractTests.CreateDocumentListLoader(appWindow,
				service, coordinator, documentList);
			object documentLoader = DocumentCloseGuardContractTests.CreateDefaultDocumentLoader(service);
			Type loaderType = loader.GetType();
			Type loaderContract = documentLoader.GetType().GetInterfaces().Single(a =>
				a.Name == "IDsDocumentLoader");
			if (operation == ListOperation.Load) {
				MethodInfo load = loaderType.GetMethod("Load", BindingFlags.Instance |
					BindingFlags.Public | BindingFlags.NonPublic, null,
					new[] { documentList.GetType(), loaderContract }, null)!;
				Assert.True((bool)load.Invoke(loader, new[] { documentList, documentLoader })!);
			}
			else if (operation == ListOperation.Reload) {
				MethodInfo reload = loaderType.GetMethod("Reload", BindingFlags.Instance |
					BindingFlags.Public | BindingFlags.NonPublic)!;
				Assert.True((bool)reload.Invoke(loader, new[] { documentLoader })!);
			}
			else {
				MethodInfo closeAll = loaderType.GetMethod("CloseAll", BindingFlags.Instance |
					BindingFlags.Public | BindingFlags.NonPublic)!;
				closeAll.Invoke(loader, null);
			}

			Assert.Single(changes);
			Assert.Equal(NotifyDocumentCollectionType.Clear, changes[0].Type);
			Assert.Equal(new[] { document }, changes[0].Documents);
			Assert.Empty(service.GetDocuments());
		}

		static string Copy(string source) {
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			File.Copy(source, destination);
			return destination;
		}

		sealed class TestComposition : IDisposable {
			readonly CompositionContainer container;

			TestComposition(CompositionContainer container, IDsDocumentService service) {
				this.container = container;
				Service = service;
			}

			public IDsDocumentService Service { get; }

			public static TestComposition Create(IDsDocumentCloseGuardService? closeGuardService = null) {
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
				if (closeGuardService is not null)
					batch.AddExportedValue<IDsDocumentCloseGuardService>(closeGuardService);
				container.Compose(batch);
				return new TestComposition(container, container.GetExportedValue<IDsDocumentService>()!);
			}

			public void Dispose() => container.Dispose();
		}

		sealed class ZeroGuardCoordinator : IDsDocumentCloseGuardService {
			public List<DsDocumentCloseReason> Reasons { get; } = new List<DsDocumentCloseReason>();

			public bool TryExecute(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason,
				Func<bool> authorizedAction) {
				Reasons.Add(reason);
				return authorizedAction();
			}
		}
	}
}
