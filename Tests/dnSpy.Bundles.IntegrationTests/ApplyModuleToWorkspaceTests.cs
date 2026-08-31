// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.PE;
using System.Windows;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Contract-level coverage for applying managed module bytes to a bundle workspace.</summary>
	public sealed class ApplyModuleToWorkspaceTests {
		[Fact]
		public void ApplyManagedModuleReplacementReopensAndPreservesSourceBundle() {
			string filename = FindCompressedFixture();
			byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(filename));
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleFolderDocument folder = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies);
			BundleEntryDocument entry = folder.Children.Cast<BundleEntryDocument>()
				.Single(a => a.Entry.RelativePath == "SingleFile.App.dll");
			using BundleModuleDocument moduleDocument = entry.CreateManagedDocument();
			MethodDef method = moduleDocument.ModuleDef!.GetTypes().SelectMany(a => a.Methods)
				.Single(a => a.Body is CilBody body && body.Instructions.Any(i =>
					i.Operand as string == "BUNDLE_VALUE="));
			ExistingEditSaveHarness.EditSession edit = ExistingEditSaveHarness.ApplyMethodEdit(
				moduleDocument, method, "BUNDLE_VALUE=", "BND017_APPLIED_VALUE=");

			Assert.True(ApplyUsingAsmEditorCommand(moduleDocument, edit.UndoService));

			Assert.True(moduleDocument.HasWorkspaceReplacement);
			Assert.True(document.HasPendingChanges);
			using (ModuleDefMD reopened = ModuleDefMD.Load(Read(document.Workspace.OpenCurrentRead(entry.Entry))))
			{
				Assert.Equal(moduleDocument.ModuleDef!.Assembly!.FullName,
					reopened.Assembly!.FullName);
				Assert.Contains(reopened.GetTypes().SelectMany(a => a.Methods), method2 =>
					method2.Body is CilBody body && body.Instructions.Any(i =>
						i.Operand as string == "BND017_APPLIED_VALUE="));
			}
			Assert.Equal(new[] { entry.Entry }, document.Workspace.ModifiedEntries);
			Assert.False(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Empty(ExistingEditSaveHarness.GetModifiedDocuments(edit));
			Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(filename)));
			Assert.Equal(BundleStrongNameDisposition.NotRequired,
				moduleDocument.WorkspaceReplacementInfo!.StrongNameDisposition);
		}

		[Fact]
		public void InvalidReplacementFailsBeforeChangingAnExistingWorkspaceReplacement() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().First();
			using BundleModuleDocument moduleDocument = entry.CreateManagedDocument();
			string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				object options = ExistingEditSaveHarness.CreateSaveOptions(moduleDocument);
				Assert.True(ExistingEditSaveHarness.WriteToFile(options, output));
				byte[] valid = File.ReadAllBytes(output);
				moduleDocument.SetWorkspaceReplacement(valid);

				Assert.ThrowsAny<Exception>(() => moduleDocument.SetWorkspaceReplacement(
					new byte[] { 0x4D, 0x5A, 0x00 }));
				Assert.True(moduleDocument.HasWorkspaceReplacement);
				Assert.Equal(valid, Read(document.Workspace.OpenCurrentRead(entry.Entry)));
			}
			finally {
				try { File.Delete(output); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void MultipleManagedReplacementsRemainIndependentThroughApplyCommand() {
			string filename = FindCompressedFixture();
			byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(filename));
			BundleOpenResult result = new BundleReader().Open(filename);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntryDocument[] entries = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().Take(2).ToArray();
			Assert.Equal(2, entries.Length);
			var modules = new List<BundleModuleDocument>();
			try {
				foreach (BundleEntryDocument entry in entries) {
					BundleModuleDocument module = entry.CreateManagedDocument();
					modules.Add(module);
				}
				Assert.True(ApplyUsingAsmEditorCommand(modules.Cast<IDsBundleEntryDocument>().ToArray()));
				Assert.Equal(entries.Select(a => a.Entry), document.Workspace.ModifiedEntries);
				Assert.All(modules, a => Assert.True(a.HasWorkspaceReplacement));
				Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(filename)));
			}
			finally {
				foreach (BundleModuleDocument module in modules)
					module.Dispose();
			}
		}

		[Fact]
		public void ReadyToRunIsRejectedWithFailureUiAndExistingReplacementIsPreserved() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>().First();
			using BundleModuleDocument module = entry.CreateManagedDocument();
			byte[] originalReplacement = Read(document.Workspace.OpenOriginalRead(entry.Entry));
			module.SetWorkspaceReplacement(originalReplacement);
			var messageBox = new RecordingMessageBoxService();
			IDsBundleEntryDocument r2r = DispatchProxy.Create<IDsBundleEntryDocument, ReadyToRunDocumentProxy>();
			((ReadyToRunDocumentProxy)(object)r2r).Module = module.ModuleDef!;
			((ReadyToRunDocumentProxy)(object)r2r).BundleDocument = module.BundleDocument;
			((ReadyToRunDocumentProxy)(object)r2r).ModuleDocument = module;
			Assert.False(ApplyUsingAsmEditorCommand(new IDsBundleEntryDocument[] { module, r2r },
				messageBox: messageBox));
			Assert.Contains(messageBox.Messages, a => a.Contains("ReadyToRun", StringComparison.Ordinal));
			Assert.True(document.HasWorkspaceErrors);
			Assert.Equal(BundleWorkspaceEntryState.Error, module.WorkspaceState);
			Assert.Equal(originalReplacement, Read(document.Workspace.OpenCurrentRead(entry.Entry)));
			Assert.True(document.Workspace.Revert(entry.Entry));
			Assert.False(document.HasWorkspaceErrors);
			Assert.Equal(Read(document.Workspace.OpenOriginalRead(entry.Entry)),
				Read(document.Workspace.OpenCurrentRead(entry.Entry)));
		}

		[Fact]
		public void LaterSerializationFailureDoesNotInstallEarlierReplacement() {
			string filename = FindCompressedFixture();
			BundleOpenResult result = new BundleReader().Open(filename);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>().First();
			using BundleModuleDocument module = entry.CreateManagedDocument();
			byte[] originalReplacement = Read(document.Workspace.OpenOriginalRead(entry.Entry));
			module.SetWorkspaceReplacement(originalReplacement);
			var failing = DispatchProxy.Create<IDsBundleEntryDocument, FailingDocumentProxy>();
			((FailingDocumentProxy)(object)failing).BundleDocument = module.BundleDocument;
			var messageBox = new RecordingMessageBoxService();
			Assert.False(ApplyUsingAsmEditorCommand(new IDsBundleEntryDocument[] { module, failing },
				messageBox: messageBox));
			Assert.Contains(messageBox.Messages, a => a.Contains("Unable to apply", StringComparison.Ordinal));
			Assert.Equal(originalReplacement, Read(document.Workspace.OpenCurrentRead(entry.Entry)));
		}

		[Fact]
		public void ApplyCarriesExplicitStrongNameRemoveAndResignMetadata() {
			string filename = FindCompressedFixture();
			string keyFilename = FindRepositoryFile("dnSpy.snk");
			BundleOpenResult result = new BundleReader().Open(filename);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>().First();
			using BundleModuleDocument module = entry.CreateManagedDocument();
			AssemblyDef assembly = module.ModuleDef!.Assembly!;
			var key = new StrongNameKey(keyFilename);
			assembly.Attributes |= AssemblyAttributes.PublicKey;
			assembly.PublicKey = new PublicKey(new StrongNamePublicKey(key.PublicKey).CreatePublicKey());
			module.ModuleDef.Cor20HeaderFlags |= ComImageFlags.StrongNameSigned;

			Assert.True(ApplyUsingStrongNameChoice(module, remove: true, keyFilename: null));
			BundleReplacementInfo removed = module.WorkspaceReplacementInfo!;
			Assert.Equal(BundleStrongNameDisposition.Removed, removed.StrongNameDisposition);
			Assert.Null(removed.StrongNameKeyFileName);
			using (ModuleDefMD unsigned = ModuleDefMD.Load(Read(document.Workspace.OpenCurrentRead(entry.Entry))))
				Assert.False(unsigned.IsStrongNameSigned);

			Assert.True(ApplyUsingStrongNameChoice(module, remove: false, keyFilename: keyFilename));
			BundleReplacementInfo resigned = module.WorkspaceReplacementInfo!;
			Assert.Equal(BundleStrongNameDisposition.ReSigned, resigned.StrongNameDisposition);
			Assert.Equal(keyFilename, resigned.StrongNameKeyFileName);
			using (ModuleDefMD signed = ModuleDefMD.Load(Read(document.Workspace.OpenCurrentRead(entry.Entry))))
				Assert.True(signed.IsStrongNameSigned);
		}

		static byte[] Read(Stream stream) {
			using (stream)
			using (var output = new MemoryStream()) {
				stream.CopyTo(output);
				return output.ToArray();
			}
		}

		static bool ApplyUsingAsmEditorCommand(IDsBundleEntryDocument document, object? undoService = null,
			RecordingMessageBoxService? messageBox = null) =>
			ApplyUsingAsmEditorCommand(new[] { document }, undoService, messageBox);

		static bool ApplyUsingAsmEditorCommand(IReadOnlyList<IDsBundleEntryDocument> documents,
			object? undoService = null, RecordingMessageBoxService? messageBox = null) {
			Assembly asmEditor = Assembly.Load("dnSpy.AsmEditor.x");
			Type commandType = asmEditor.GetType(
				"dnSpy.AsmEditor.SaveModule.ApplyModuleToWorkspaceCommand", throwOnError: true)!;
			MethodInfo apply = commandType.GetMethod("Apply",
				BindingFlags.Static | BindingFlags.NonPublic)!;
			try {
				return (bool)apply.Invoke(null, new object?[] {
					documents, null, messageBox ?? new RecordingMessageBoxService(), undoService,
				})!;
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		static bool ApplyUsingStrongNameChoice(IDsBundleEntryDocument document, bool remove,
			string? keyFilename) {
			Assembly asmEditor = Assembly.Load("dnSpy.AsmEditor.x");
			Type commandType = asmEditor.GetType(
				"dnSpy.AsmEditor.SaveModule.ApplyModuleToWorkspaceCommand", throwOnError: true)!;
			MethodInfo apply = commandType.GetMethod("ApplyWithStrongNameChoices",
				BindingFlags.Static | BindingFlags.NonPublic)!;
			var messageBox = new RecordingMessageBoxService();
			try {
				return (bool)apply.Invoke(null, new object?[] {
					new[] { document }, null, messageBox, null,
					new Func<IDsBundleEntryDocument, bool>(_ => remove),
					new Func<IDsBundleEntryDocument, string?>(_ => keyFilename),
				})!;
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		sealed class ReadyToRunDocumentProxy : DispatchProxy {
			public ModuleDef Module { get; set; } = null!;
			public IDsBundleDocument BundleDocument { get; set; } = null!;
			public IDsBundleEntryDocument ModuleDocument { get; set; } = null!;

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_ModuleDef")
					return Module;
				if (targetMethod?.Name == "get_IsReadyToRun")
					return true;
				if (targetMethod?.Name == "get_BundleDocument")
					return BundleDocument;
				if (targetMethod?.Name == "get_BundleRelativePath")
					return "ready-to-run.dll";
				if (targetMethod?.Name == "get_WorkspaceError")
					return ModuleDocument.WorkspaceError;
				if (targetMethod?.Name == "RecordWorkspaceError") {
					ModuleDocument.RecordWorkspaceError((Exception)args![0]!);
					return null;
				}
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class FailingDocumentProxy : DispatchProxy {
			public IDsBundleDocument BundleDocument { get; set; } = null!;

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_BundleDocument")
					return BundleDocument;
				if (targetMethod?.Name == "get_IsReadyToRun")
					return false;
				if (targetMethod?.Name == "get_BundleRelativePath")
					return "serialization-failure.dll";
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class RecordingMessageBoxService : IMessageBoxService {
			public List<string> Messages { get; } = new List<string>();

			public MsgBoxButton? ShowIgnorableMessage(Guid guid, string message,
				MsgBoxButton buttons = MsgBoxButton.OK, Window? ownerWindow = null) {
				Messages.Add(message);
				return MsgBoxButton.OK;
			}

			public MsgBoxButton Show(string message, MsgBoxButton buttons = MsgBoxButton.OK,
				Window? ownerWindow = null) {
				Messages.Add(message);
				return MsgBoxButton.OK;
			}

			public T? Ask<T>(string labelMessage, string? defaultText = null, string? title = null,
				Func<string, T>? converter = null, Func<string, string?>? verifier = null,
				Window? ownerWindow = null) => default;

			public void Show(Exception exception, string? msg = null, Window? ownerWindow = null) {
				Messages.Add(msg ?? exception.Message);
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

		static string FindRepositoryFile(string name) {
			DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
			while (directory is not null) {
				string candidate = Path.Combine(directory.FullName, name);
				if (File.Exists(candidate))
					return candidate;
				directory = directory.Parent;
			}
			throw new FileNotFoundException(name);
		}
	}
}
