// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class OrdinaryEditSaveRegressionTests {
		[Fact]
		public void OrdinaryManagedEditUndoRedoAndStandaloneSaveRemainUnchanged() {
			using var fixture = OrdinaryEditFixture.Create();
			byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(fixture.SourceFilename));
			const string originalValue = "ORDINARY_VALUE";
			const string editedValue = "ORDINARY_EDITED_VALUE";
			ExistingEditSaveHarness.EditSession edit = ExistingEditSaveHarness.ApplyMethodEdit(
				fixture.Document, fixture.Method, originalValue, editedValue);
			object undoService = edit.UndoService;
			object methodAnnotations = edit.MethodAnnotations;
			Assert.Contains(fixture.Method.Body!.Instructions,
				instruction => instruction.Operand as string == editedValue);
			Assert.True(ExistingEditSaveHarness.IsBodyModified(methodAnnotations, fixture.Method));
			Assert.True(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Contains(fixture.Document, ExistingEditSaveHarness.GetModifiedDocuments(edit));

			ExistingEditSaveHarness.Undo(undoService);
			Assert.Contains(fixture.Method.Body!.Instructions,
				instruction => instruction.Operand as string == originalValue);
			Assert.False(ExistingEditSaveHarness.IsBodyModified(methodAnnotations, fixture.Method));
			Assert.False(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Empty(ExistingEditSaveHarness.GetModifiedDocuments(edit));

			ExistingEditSaveHarness.Redo(undoService);
			Assert.Contains(fixture.Method.Body!.Instructions,
				instruction => instruction.Operand as string == editedValue);
			Assert.True(ExistingEditSaveHarness.IsBodyModified(methodAnnotations, fixture.Method));
			Assert.True(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Contains(fixture.Document, ExistingEditSaveHarness.GetModifiedDocuments(edit));

			string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				object options = ExistingEditSaveHarness.CreateSaveOptions(fixture.Document);
				Assert.True(ExistingEditSaveHarness.WriteToFile(options, output));
				using ModuleDefMD reopened = ModuleDefMD.Load(output);
				Assert.Contains(reopened.GetTypes().SelectMany(a => a.Methods), method =>
					method.Body is CilBody body && body.Instructions.Any(instruction =>
						instruction.Operand as string == editedValue));
				Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(fixture.SourceFilename)));
			}
			finally {
				try { File.Delete(output); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}

	sealed class OrdinaryEditFixture : IDisposable {
		public DsDotNetDocument Document { get; }
		public ModuleDef Module { get; }
		public MethodDef Method { get; }
		public string SourceFilename { get; }

		OrdinaryEditFixture(DsDotNetDocument document, ModuleDef module, MethodDef method, string sourceFilename) {
			Document = document;
			Module = module;
			Method = method;
			SourceFilename = sourceFilename;
		}

		public static OrdinaryEditFixture Create() {
			var module = new ModuleDefUser("OrdinaryEditFixture.dll") {
				Kind = ModuleKind.Dll,
			};
			var assembly = new AssemblyDefUser("OrdinaryEditFixture", new Version(1, 0, 0, 0));
			assembly.Modules.Add(module);
			var type = new TypeDefUser("OrdinaryEditFixture", "Values",
				module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
			};
			var method = new MethodDefUser("GetValue", MethodSig.CreateStatic(module.CorLibTypes.String),
				MethodImplAttributes.IL | MethodImplAttributes.Managed,
				MethodAttributes.Public | MethodAttributes.Static) {
				Body = new CilBody(),
			};
			method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "ORDINARY_VALUE"));
			method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			type.Methods.Add(method);
			module.Types.Add(type);
			string sourceFilename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			module.Location = sourceFilename;
			using (FileStream stream = File.Create(sourceFilename))
				assembly.Write(stream);
			var loadedModule = ModuleDefMD.Load(sourceFilename);
			loadedModule.Location = sourceFilename;
			var document = DsDotNetDocument.CreateAssembly(
				DsDocumentInfo.CreateDocument(sourceFilename), loadedModule, loadSyms: false);
			MethodDef loadedMethod = loadedModule.GetTypes().SelectMany(a => a.Methods)
				.Single(a => a.Name == "GetValue");
			return new OrdinaryEditFixture(document, loadedModule, loadedMethod, sourceFilename);
		}

		public void Dispose() {
			Document.Dispose();
			try { File.Delete(SourceFilename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}
}
