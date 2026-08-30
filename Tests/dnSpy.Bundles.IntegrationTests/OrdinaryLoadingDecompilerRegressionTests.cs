// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Decompiler;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class OrdinaryLoadingDecompilerRegressionTests {
		[Fact]
		public void OrdinaryManagedDllAndExeStillLoadAndDecompileThroughExistingPipeline() {
			string source = typeof(BundleDsDocumentProvider).Assembly.Location;
			string dll = Copy(source, ".dll");
			string exe = CreateManagedExecutable();
			try {
				using var composition = DocumentServiceComposition.Create();
				IDecompiler decompiler = CreateCSharpDecompiler();
				foreach (string filename in new[] { dll, exe }) {
					IDsDocument? raw = composition.Service.TryGetOrCreate(
						DsDocumentInfo.CreateDocument(filename));
					var document = Assert.IsType<DsDotNetDocument>(raw);
					try {
						Assert.NotNull(document.ModuleDef);
						AssemblyDef? assembly = document.AssemblyDef;
						Assert.NotNull(assembly);
						var output = new StringBuilderDecompilerOutput();
						decompiler.Decompile(assembly!, output, new DecompilationContext());
						string decompiled = output.GetText();
						if (StringComparer.OrdinalIgnoreCase.Equals(filename, exe)) {
							Assert.Contains("OrdinaryExecutable", decompiled, StringComparison.Ordinal);
							Assert.Contains("Console.WriteLine", decompiled, StringComparison.Ordinal);
							Assert.Contains("ORDINARY_EXE", decompiled, StringComparison.Ordinal);
						}
						else
							Assert.Contains("dnSpy.Bundles", decompiled, StringComparison.Ordinal);
					}
					finally {
						document.Dispose();
					}
				}
			}
			finally {
				Delete(dll);
				Delete(exe);
			}
		}

		static IDecompiler CreateCSharpDecompiler() {
			Assembly assembly = Assembly.Load("dnSpy.Decompiler.ILSpy.x");
			Type providerType = assembly.GetType(
				"dnSpy.Decompiler.ILSpy.Core.CSharp.DecompilerProvider", true)!;
			object provider = Activator.CreateInstance(providerType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, Array.Empty<object>(), null)!;
			MethodInfo create = providerType.GetMethod("Create",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
			return ((IEnumerable)create.Invoke(provider, null)!).Cast<IDecompiler>()
				.Single(a => a.GenericNameUI == DecompilerConstants.GENERIC_NAMEUI_CSHARP);
		}

		static string Copy(string source, string extension) {
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
			File.Copy(source, destination);
			return destination;
		}

		static string CreateManagedExecutable() {
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			var assembly = new AssemblyDefUser("OrdinaryExecutable");
			var module = new ModuleDefUser("OrdinaryExecutable.exe") {
				Kind = ModuleKind.Console,
				Characteristics = Characteristics.ExecutableImage | Characteristics.Bit32Machine,
			};
			assembly.Modules.Add(module);

			var program = new TypeDefUser("OrdinaryExecutable", "Program",
				module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Abstract |
					dnlib.DotNet.TypeAttributes.Sealed,
			};
			var main = new MethodDefUser("Main", MethodSig.CreateStatic(module.CorLibTypes.Void),
				dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed,
				dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) {
				Body = new CilBody(),
			};
			main.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "ORDINARY_EXE"));
			main.Body.Instructions.Add(Instruction.Create(OpCodes.Call,
				module.Import(typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })!)));
			main.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			program.Methods.Add(main);
			module.Types.Add(program);
			module.ManagedEntryPoint = main;

			using (FileStream stream = File.Create(filename))
				assembly.Write(stream);
			return filename;
		}

		static void Delete(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
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
				Type serviceType = product.GetType("dnSpy.Documents.DsDocumentService", true)!;
				Type providerType = product.GetType("dnSpy.Documents.DefaultDsDocumentProvider", true)!;
				Type settingsType = product.GetType("dnSpy.Documents.DsDocumentServiceSettings", true)!;
				Type settingsContractType = product.GetType("dnSpy.Documents.IDsDocumentServiceSettings", true)!;
				object settings = Activator.CreateInstance(settingsType)!;
				var defaultProvider = (IDsDocumentProvider)Activator.CreateInstance(providerType)!;
				var container = new CompositionContainer(new TypeCatalog(serviceType));
				var batch = new CompositionBatch();
				AttributedModelServices.AddExportedValue(batch, settingsContractType.FullName!, settings);
				batch.AddExportedValue<IDsDocumentProvider>(new BundleDsDocumentProvider());
				batch.AddExportedValue<IDsDocumentProvider>(defaultProvider);
				container.Compose(batch);
				return new DocumentServiceComposition(container,
					container.GetExportedValue<IDsDocumentService>()!);
			}

			public void Dispose() => container.Dispose();
		}
	}
}
