// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnlib.DotNet;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class OrdinaryAssemblyResolverRegressionTests {
		[Fact]
		public void OrdinaryModuleContextDoesNotReceiveBundleResolver() {
			var module = new ModuleDefUser("ordinary.dll");
			var document = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary.dll"), module, loadSyms: false);
			try {
				Assert.NotNull(module.Context);
				Assert.IsNotType<BundleAssemblyResolver>(module.Context!.AssemblyResolver);
			}
			finally {
				document.Dispose();
			}
		}

		[Fact]
		public void OrdinaryDocumentServiceStillLoadsManagedDllAndExe() {
			string source = typeof(BundleDsDocumentProvider).Assembly.Location;
			string dll = Copy(source, ".dll");
			string exe = Copy(source, ".exe");
			try {
				using var composition = OrdinaryDocumentProviderRegressionTestsComposition.Create();
				foreach (string filename in new[] { dll, exe }) {
					IDsDocument? document = composition.Service.TryGetOrCreate(
						DsDocumentInfo.CreateDocument(filename));
					var managed = Assert.IsType<DsDotNetDocument>(document);
					Assert.IsNotType<BundleDsDocument>(document);
					Assert.NotNull(managed.AssemblyDef);
					IDsDocument? resolved = composition.Service.Resolve(
						managed.AssemblyDef!.ToAssemblyRef(), managed.ModuleDef);
					Assert.NotNull(resolved);
					Assert.Equal(managed.AssemblyDef.FullName, resolved!.AssemblyDef!.FullName);
				}
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

		sealed class OrdinaryDocumentProviderRegressionTestsComposition : IDisposable {
			readonly System.ComponentModel.Composition.Hosting.CompositionContainer container;

			OrdinaryDocumentProviderRegressionTestsComposition(
				System.ComponentModel.Composition.Hosting.CompositionContainer container,
				IDsDocumentService service) {
				this.container = container;
				Service = service;
			}

			public IDsDocumentService Service { get; }

			public static OrdinaryDocumentProviderRegressionTestsComposition Create() {
				Assembly product = Assembly.Load("dnSpy");
				Type serviceType = product.GetType("dnSpy.Documents.DsDocumentService", throwOnError: true)!;
				Type providerType = product.GetType("dnSpy.Documents.DefaultDsDocumentProvider", throwOnError: true)!;
				Type settingsType = product.GetType("dnSpy.Documents.DsDocumentServiceSettings", throwOnError: true)!;
				Type settingsContractType = product.GetType("dnSpy.Documents.IDsDocumentServiceSettings", throwOnError: true)!;

				object settings = Activator.CreateInstance(settingsType)!;
				var defaultProvider = (IDsDocumentProvider)Activator.CreateInstance(providerType)!;
				var container = new System.ComponentModel.Composition.Hosting.CompositionContainer(
					new System.ComponentModel.Composition.Hosting.TypeCatalog(serviceType));
				var batch = new System.ComponentModel.Composition.Hosting.CompositionBatch();
				System.ComponentModel.Composition.AttributedModelServices.AddExportedValue(
					batch, settingsContractType.FullName!, settings);
				batch.AddExportedValue<IDsDocumentProvider>(new BundleDsDocumentProvider());
				batch.AddExportedValue<IDsDocumentProvider>(defaultProvider);
				container.Compose(batch);
				return new OrdinaryDocumentProviderRegressionTestsComposition(container,
					container.GetExportedValue<IDsDocumentService>());
			}

			public void Dispose() => container.Dispose();
		}
	}
}
