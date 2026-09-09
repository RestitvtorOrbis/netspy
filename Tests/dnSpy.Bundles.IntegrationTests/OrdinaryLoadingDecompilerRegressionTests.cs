// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
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
				using var support = BundlePipelineTestSupport.Create();
				IDecompiler decompiler = support.CSharpDecompiler;
				foreach (string filename in new[] { dll, exe }) {
					IDsDocument? raw = support.DocumentService.TryGetOrCreate(
						DsDocumentInfo.CreateDocument(filename));
					var document = Assert.IsType<DsDotNetDocument>(raw);
					try {
						Assert.NotNull(document.ModuleDef);
						AssemblyDef? assembly = document.AssemblyDef;
						Assert.NotNull(assembly);
						var output = new StringBuilderDecompilerOutput();
						decompiler.Decompile(assembly!, output, new DecompilationContext());
						string decompiledHeader = output.GetText();
						if (StringComparer.OrdinalIgnoreCase.Equals(filename, exe)) {
							Assert.Contains("OrdinaryExecutable", decompiledHeader, StringComparison.Ordinal);
							TypeDef executableType = BundlePipelineTestSupport.FindMethodBearingType(
								document.ModuleDef!, "Program");
							Assert.Contains(executableType.Methods, a => a.HasBody);
							var typeOutput = BundlePipelineTestSupport.DecompileType(decompiler, executableType);
							string decompiledBody = typeOutput.GetText();
							Assert.Contains("Console.WriteLine", decompiledBody, StringComparison.Ordinal);
							Assert.Contains("ORDINARY_EXE", decompiledBody, StringComparison.Ordinal);
						}
						else
							Assert.Contains("dnSpy.Bundles", decompiledHeader, StringComparison.Ordinal);
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

	}
}
