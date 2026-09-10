// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Reflection;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Ensures ordinary DLL and EXE open/save use the existing document pipeline.</summary>
	public sealed class OrdinaryOpenSaveRegressionTests {
		[Fact]
		public void OrdinaryDllAndExeOpenSaveAndReopenThroughExistingPipeline() {
			string sourceAssembly = typeof(BundleDsDocumentProvider).Assembly.Location;
			Assert.True(File.Exists(sourceAssembly));
			using var composition = DocumentServiceComposition.Create();
			using var consoleFixture = OrdinaryConsoleFixture.Create();
			string ordinaryDll = Path.Combine(Path.GetTempPath(),
				"dnspy-bnd027-ordinary-source-" + Guid.NewGuid().ToString("N") + ".dll");
			File.Copy(sourceAssembly, ordinaryDll);
			foreach (string source in new[] { ordinaryDll, consoleFixture.Filename }) {
				string output = Path.Combine(Path.GetTempPath(),
					"dnspy-bnd027-ordinary-output-" + Guid.NewGuid().ToString("N") +
					Path.GetExtension(source));
				try {
					byte[] sourceHash = File.ReadAllBytes(source);
					using (ModuleDefMD sourceModule = ModuleDefMD.Load(source))
						Assert.Equal(Path.GetExtension(source).Equals(".exe", StringComparison.OrdinalIgnoreCase)
							? ModuleKind.Console : ModuleKind.Dll, sourceModule.Kind);
					DsDotNetDocument document = Assert.IsType<DsDotNetDocument>(
						composition.Service.TryGetOrCreate(DsDocumentInfo.CreateDocument(source)));
					using (document) {
						Assert.NotNull(document.ModuleDef);
						object options = ExistingEditSaveHarness.CreateSaveOptions(document);
						Assert.True(ExistingEditSaveHarness.WriteToFile(options, output));
						using ModuleDefMD reopened = ModuleDefMD.Load(output);
						Assert.Equal(document.ModuleDef!.Assembly?.FullName, reopened.Assembly?.FullName);
					}
					Assert.Equal(sourceHash, File.ReadAllBytes(source));
				}
				finally {
					TryDelete(source);
					TryDelete(output);
				}
			}
			TryDelete(ordinaryDll);
		}

		static void TryDelete(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		sealed class OrdinaryConsoleFixture : IDisposable {
			public string Filename { get; }

			OrdinaryConsoleFixture(string filename) => Filename = filename;

			public static OrdinaryConsoleFixture Create() {
				var module = new ModuleDefUser("OrdinaryOpenSaveFixture.exe") {
					Kind = ModuleKind.Console,
				};
				var assembly = new AssemblyDefUser("OrdinaryOpenSaveFixture", new Version(1, 0, 0, 0));
				assembly.Modules.Add(module);
				var type = new TypeDefUser("OrdinaryOpenSaveFixture", "Program",
					module.CorLibTypes.Object.TypeDefOrRef) {
					Attributes = dnlib.DotNet.TypeAttributes.Public |
						dnlib.DotNet.TypeAttributes.Abstract | dnlib.DotNet.TypeAttributes.Sealed,
				};
				var entryPoint = new MethodDefUser("Main",
					MethodSig.CreateStatic(module.CorLibTypes.Void),
					dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
					dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) {
					Body = new CilBody(),
				};
				entryPoint.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
				type.Methods.Add(entryPoint);
				module.Types.Add(type);
				module.EntryPoint = entryPoint;
				string filename = Path.Combine(Path.GetTempPath(),
					"dnspy-bnd027-console-" + Guid.NewGuid().ToString("N") + ".exe");
				assembly.Write(filename);
				return new OrdinaryConsoleFixture(filename);
			}

			public void Dispose() => TryDelete(Filename);
		}

		sealed class DocumentServiceComposition : IDisposable {
			readonly CompositionContainer container;

			DocumentServiceComposition(CompositionContainer container, IDsDocumentService service) {
				this.container = container;
				Service = service;
			}

			public IDsDocumentService Service { get; }

			public static DocumentServiceComposition Create() {
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
				return new DocumentServiceComposition(container,
					container.GetExportedValue<IDsDocumentService>());
			}

			public void Dispose() => container.Dispose();
		}
	}
}
