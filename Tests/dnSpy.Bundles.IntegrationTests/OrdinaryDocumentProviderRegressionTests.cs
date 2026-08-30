// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Reflection;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class OrdinaryDocumentProviderRegressionTests {
		[Fact]
		public void ExistingManagedAssemblyIsNotClaimedByBundleProvider() {
			string filename = typeof(BundleDsDocumentProvider).Assembly.Location;
			Assert.True(File.Exists(filename));
			var provider = new BundleDsDocumentProvider();
			DsDocumentInfo info = DsDocumentInfo.CreateDocument(filename);
			Assert.Null(provider.Create(null!, info));
			Assert.NotNull(provider.CreateKey(null!, info));
		}

		[Fact]
		public void ExistingProviderOrderRemainsTheDefaultForOrdinaryFiles() {
			var bundleProvider = new BundleDsDocumentProvider();
			Assert.True(bundleProvider.Order < DocumentConstants.ORDER_DEFAULT_DOCUMENT_PROVIDER);
			Assert.Equal(double.MaxValue, DocumentConstants.ORDER_DEFAULT_DOCUMENT_PROVIDER);
		}

		[Fact]
		public void ActualDocumentServiceUsesDefaultProviderForOrdinaryDllAndExe() {
			string source = typeof(BundleDsDocumentProvider).Assembly.Location;
			string dll = Copy(source, ".dll");
			string exe = Copy(source, ".exe");
			try {
				using var composition = ActualDocumentServiceComposition.Create();
				IDsDocument dllDocument = Assert.IsType<DsDotNetDocument>(
					composition.Service.TryGetOrCreate(DsDocumentInfo.CreateDocument(dll)));
				IDsDocument exeDocument = Assert.IsType<DsDotNetDocument>(
					composition.Service.TryGetOrCreate(DsDocumentInfo.CreateDocument(exe)));
				Assert.IsType<DsDotNetDocument>(dllDocument);
				Assert.IsType<DsDotNetDocument>(exeDocument);
			}
			finally {
				Delete(dll);
				Delete(exe);
			}
		}

		static string Copy(string source, string extension) {
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
			File.Copy(source, destination);
			return destination;
		}

		static void Delete(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		sealed class ActualDocumentServiceComposition : IDisposable {
			readonly CompositionContainer container;

			ActualDocumentServiceComposition(CompositionContainer container, IDsDocumentService service) {
				this.container = container;
				Service = service;
			}

			public IDsDocumentService Service { get; }

			public static ActualDocumentServiceComposition Create() {
				Assembly product = Assembly.Load("dnSpy");
				Type serviceType = product.GetType("dnSpy.Documents.DsDocumentService", throwOnError: true)!;
				Type providerType = product.GetType("dnSpy.Documents.DefaultDsDocumentProvider", throwOnError: true)!;
				Type settingsType = product.GetType("dnSpy.Documents.DsDocumentServiceSettings", throwOnError: true)!;
				Type settingsContractType = product.GetType("dnSpy.Documents.IDsDocumentServiceSettings", throwOnError: true)!;

				object settings = Activator.CreateInstance(settingsType)!;
				var defaultProvider = (IDsDocumentProvider)Activator.CreateInstance(providerType)!;
				var container = new CompositionContainer(new TypeCatalog(serviceType));
				var batch = new CompositionBatch();
				AttributedModelServices.AddExportedValue(batch, settingsContractType.FullName!, settings);
				batch.AddExportedValue<IDsDocumentProvider>(new BundleDsDocumentProvider());
				batch.AddExportedValue<IDsDocumentProvider>(defaultProvider);
				container.Compose(batch);
				return new ActualDocumentServiceComposition(
					container, container.GetExportedValue<IDsDocumentService>());
			}

			public void Dispose() => container.Dispose();
		}
	}
}
