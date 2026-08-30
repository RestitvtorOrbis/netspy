// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleEditSaveModuleTests {
		[Fact]
		public void LazyBundleEditUndoRedoSavesStandaloneWithoutChangingSourceBundle() {
			string filename = FindCompressedFixture();
			byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(filename));
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using BundleFile bundle = result.Bundle!;
			var reads = new Dictionary<int, int>();
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), bundle,
				openLogicalRead: entry => {
					reads.TryGetValue(entry.Index, out int count);
					reads[entry.Index] = count + 1;
					return entry.OpenLogicalRead();
				});

			BundleEntryDocument[] entries = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().ToArray();
			BundleEntryDocument selected = entries.Single(a =>
				a.Entry.RelativePath == "SingleFile.App.dll");
			Assert.Null(selected.ManagedDocument);
			Assert.Empty(reads);

			using BundleModuleDocument moduleDocument = selected.CreateManagedDocument();
			Assert.Equal(1, reads[selected.Entry.Index]);
			Assert.DoesNotContain(entries, entry => entry.Entry.Index != selected.Entry.Index &&
				reads.ContainsKey(entry.Entry.Index));
			ModuleDef module = moduleDocument.ModuleDef!;
			const string originalValue = "BUNDLE_VALUE=";
			const string editedValue = "BND015_EDITED_VALUE=";
			MethodDef method = FindMethodContainingString(module, originalValue);

			ExistingEditSaveHarness.EditSession edit = ExistingEditSaveHarness.ApplyMethodEdit(
				moduleDocument, method, originalValue, editedValue);
			object undoService = edit.UndoService;
			object methodAnnotations = edit.MethodAnnotations;
			Assert.Equal(editedValue, FindStringOperand(method, editedValue));
			Assert.True(ExistingEditSaveHarness.IsBodyModified(methodAnnotations, method));
			Assert.True(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Contains(moduleDocument, ExistingEditSaveHarness.GetModifiedDocuments(edit));
			Assert.DoesNotContain(document, ExistingEditSaveHarness.GetModifiedDocuments(edit));
			Assert.Same(moduleDocument, edit.LastRefreshDocument);
			Assert.True(ExistingEditSaveHarness.CanUndo(undoService));
			Assert.False(ExistingEditSaveHarness.CanRedo(undoService));

			ExistingEditSaveHarness.Undo(undoService);
			Assert.Equal(originalValue, FindStringOperand(method, originalValue));
			Assert.False(ExistingEditSaveHarness.IsBodyModified(methodAnnotations, method));
			Assert.False(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Empty(ExistingEditSaveHarness.GetModifiedDocuments(edit));
			Assert.Same(moduleDocument, edit.LastRefreshDocument);
			Assert.False(ExistingEditSaveHarness.CanUndo(undoService));
			Assert.True(ExistingEditSaveHarness.CanRedo(undoService));

			ExistingEditSaveHarness.Redo(undoService);
			Assert.Equal(editedValue, FindStringOperand(method, editedValue));
			Assert.True(ExistingEditSaveHarness.IsBodyModified(methodAnnotations, method));
			Assert.True(ExistingEditSaveHarness.IsUndoObjectModified(edit));
			Assert.Contains(moduleDocument, ExistingEditSaveHarness.GetModifiedDocuments(edit));
			Assert.DoesNotContain(document, ExistingEditSaveHarness.GetModifiedDocuments(edit));
			Assert.Same(moduleDocument, edit.LastRefreshDocument);
			Assert.Equal(3, edit.RefreshCount);
			Assert.True(ExistingEditSaveHarness.CanUndo(undoService));
			Assert.False(ExistingEditSaveHarness.CanRedo(undoService));

			string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				object options = ExistingEditSaveHarness.CreateSaveOptions(moduleDocument);
				Assert.True(ExistingEditSaveHarness.WriteToFile(options, output));
				using ModuleDefMD reopened = ModuleDefMD.Load(output);
				Assert.Equal(editedValue, FindStringOperand(reopened, editedValue));
				Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(filename)));
			}
			finally {
				try { File.Delete(output); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		static MethodDef FindMethodContainingString(ModuleDef module, string value) =>
			module.GetTypes().SelectMany(a => a.Methods)
				.Single(a => a.Body is CilBody body && body.Instructions.Any(i => i.Operand is string s && s == value));

		static string FindStringOperand(MethodDef method, string expected) {
			Assert.NotNull(method.Body);
			Assert.Contains(method.Body!.Instructions, instruction => instruction.Operand as string == expected);
			return expected;
		}

		static string FindStringOperand(ModuleDef module, string expected) {
			MethodDef method = FindMethodContainingString(module, expected);
			return FindStringOperand(method, expected);
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
	}

	internal static class ExistingEditSaveHarness {
		static readonly Assembly ProductAssembly = Assembly.Load("dnSpy");
		static readonly Assembly AsmEditorAssembly = Assembly.Load("dnSpy.AsmEditor.x");
		static readonly Type MethodBodyOptionsType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.MethodBody.MethodBodyOptions", throwOnError: true)!;
		static readonly Type EditCommandType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.MethodBody.EditMethodBodyILCommand", throwOnError: true)!;
		static readonly Type UndoServiceType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.UndoRedo.UndoCommandService", throwOnError: true)!;
		static readonly Type UndoableProviderType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.UndoRedo.IUndoableDocumentsProvider", throwOnError: true)!;
		static readonly Type MethodAnnotationsType = ProductAssembly.GetType(
			"dnSpy.Documents.MethodAnnotations", throwOnError: true)!;
		static readonly Type SaveModuleOptionsType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.SaveModule.SaveModuleOptionsVM", throwOnError: true)!;
		static readonly Type SerializationServiceType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.SaveModule.ModuleSerializationService", throwOnError: true)!;

		public sealed class EditSession {
			public object UndoService { get; }
			public object MethodAnnotations { get; }
			public object UndoObject { get; }
			readonly RefreshTracker refreshTracker;
			public int RefreshCount => refreshTracker.Count;
			public IDsDocument? LastRefreshDocument => refreshTracker.LastDocument;

			public EditSession(object undoService, object methodAnnotations, object undoObject,
				RefreshTracker refreshTracker) {
				UndoService = undoService;
				MethodAnnotations = methodAnnotations;
				UndoObject = undoObject;
				this.refreshTracker = refreshTracker;
			}
		}

		public static EditSession ApplyMethodEdit(IDsDocument document, MethodDef method,
			string originalValue, string editedValue) {
			object methodAnnotations = Activator.CreateInstance(MethodAnnotationsType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null, args: Array.Empty<object>(), culture: null)!;
			object bodyOptions = Activator.CreateInstance(MethodBodyOptionsType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null, args: new object?[] { method }, culture: null)!;
			object cilBodyOptions = MethodBodyOptionsType.GetField("CilBodyOptions",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(bodyOptions)!;
			var instructions = (List<Instruction>)cilBodyOptions.GetType().GetField("Instructions",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(cilBodyOptions)!;
			int index = instructions.FindIndex(a => a.Operand is string s && s == originalValue);
			Assert.True(index >= 0);
			instructions[index] = Instruction.Create(OpCodes.Ldstr, editedValue);

			var methodNode = new EditMethodNode(method);
			var documentNode = new TestDocumentNode(document);
			var documentTreeNode = new TestTreeNode(documentNode, parent: null);
			var methodTreeNode = new TestTreeNode(methodNode, documentTreeNode);
			documentNode.TreeNode = documentTreeNode;
			methodNode.TreeNode = methodTreeNode;
			object command = EditCommandType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				.Single().Invoke(new[] { methodAnnotations, methodNode, bodyOptions });
			IDocumentTreeView documentTreeView = DispatchProxy.Create<IDocumentTreeView, DocumentTreeViewProxy>();
			((DocumentTreeViewProxy)(object)documentTreeView).DocumentNode = documentNode;
			IDocumentTabService documentTabService = DispatchProxy.Create<IDocumentTabService, DocumentTabServiceProxy>();
			var documentTabServiceProxy = (DocumentTabServiceProxy)(object)documentTabService;
			documentTabServiceProxy.DocumentTreeView = documentTreeView;
			Type providerType = AsmEditorAssembly.GetType(
				"dnSpy.AsmEditor.UndoRedo.DsDocumentUndoableDocumentsProvider", throwOnError: true)!;
			object provider = Activator.CreateInstance(providerType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null, args: new object?[] { documentTabService }, culture: null)!;
			Type lazyProviderType = typeof(Lazy<>).MakeGenericType(UndoableProviderType);
			Array providers = Array.CreateInstance(lazyProviderType, 1);
			providers.SetValue(CreateLazy(UndoableProviderType, provider), 0);
			object undoService = Activator.CreateInstance(UndoServiceType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null, args: new object?[] { providers }, culture: null)!;
			Invoke(UndoServiceType, undoService, "Add", command);
			object undoObject = UndoServiceType.GetMethod("GetUndoObject")!.Invoke(undoService,
				new object?[] { methodNode })!;
			return new EditSession(undoService, methodAnnotations, undoObject,
				documentTabServiceProxy.RefreshTracker);
		}

		public static bool IsBodyModified(object methodAnnotations, MethodDef method) =>
			(bool)MethodAnnotationsType.GetMethod("IsBodyModified")!.Invoke(methodAnnotations, new object?[] { method })!;

		public static bool CanUndo(object undoService) => GetBoolean(undoService, "CanUndo");
		public static bool CanRedo(object undoService) => GetBoolean(undoService, "CanRedo");
		public static void Undo(object undoService) => Invoke(UndoServiceType, undoService, "Undo");
		public static void Redo(object undoService) => Invoke(UndoServiceType, undoService, "Redo");

		public static bool IsUndoObjectModified(EditSession session) =>
			(bool)UndoServiceType.GetMethod("IsModified")!.Invoke(session.UndoService,
				new[] { session.UndoObject })!;

		public static object[] GetModifiedDocuments(EditSession session) =>
			((IEnumerable)UndoServiceType.GetMethod("GetModifiedDocuments")!.Invoke(
				session.UndoService, null)!).Cast<object>().ToArray();

		public static object CreateSaveOptions(IDsDocument document) =>
			Activator.CreateInstance(SaveModuleOptionsType, new object?[] { document })!;

		public static bool WriteToFile(object options, string filename) {
			MethodInfo method = SerializationServiceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.Single(a => a.Name == "WriteToFile");
			try {
				return (bool)method.Invoke(null, new object?[] { options, filename, DummyLogger.NoThrowInstance, null })!;
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		static bool GetBoolean(object instance, string propertyName) =>
			(bool)instance.GetType().GetProperty(propertyName)!.GetValue(instance)!;

		static object CreateLazy(Type itemType, object value) =>
			typeof(ExistingEditSaveHarness).GetMethod(nameof(CreateLazyCore),
				BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(itemType)
				.Invoke(null, new[] { value })!;

		static object CreateLazyCore<T>(T value) => new Lazy<T>(() => value);

		static void Invoke(Type type, object instance, string methodName, params object[] args) {
			try {
				type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(instance, args);
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		public sealed class RefreshTracker {
			public int Count { get; private set; }
			public IDsDocument? LastDocument { get; private set; }
			public void Increment(IDsDocument document) {
				Count++;
				LastDocument = document;
			}
		}

		public sealed class DocumentTabServiceProxy : DispatchProxy {
			public IDocumentTreeView DocumentTreeView { get; set; } = null!;
			public RefreshTracker RefreshTracker { get; } = new RefreshTracker();

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == "get_DocumentTreeView")
					return DocumentTreeView;
				if (targetMethod?.Name == nameof(IDocumentTabService.RefreshModifiedDocument)) {
					RefreshTracker.Increment((IDsDocument)args![0]!);
					return null;
				}
				return DefaultValue(targetMethod?.ReturnType);
			}
		}

		public sealed class DocumentTreeViewProxy : DispatchProxy {
			public DsDocumentNode DocumentNode { get; set; } = null!;

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == nameof(IDocumentTreeView.GetAllCreatedDocumentNodes))
					return new[] { DocumentNode };
				return DefaultValue(targetMethod?.ReturnType);
			}
		}

		sealed class TestDocumentNode : DsDocumentNode {
			public TestDocumentNode(IDsDocument document) : base(document) { }

			public override Guid Guid => typeof(TestDocumentNode).GUID;

			protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => default;

			protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
				DocumentNodeWriteOptions options) {
			}
		}

		sealed class TestTreeNode : ITreeNode {
			readonly List<ITreeNode> children = new List<ITreeNode>();

			public TestTreeNode(TreeNodeData data, ITreeNode? parent) {
				Data = data;
				Parent = parent;
			}

			public ITreeView TreeView => null!;
			public ITreeNode? Parent { get; }
			public IList<ITreeNode> Children => children;
			public IEnumerable<TreeNodeData> DataChildren => children.Select(a => a.Data);
			public TreeNodeData Data { get; }
			public bool LazyLoading { get; set; }
			public bool IsExpanded { get; set; }
			public bool IsHidden { get; set; }
			public bool IsVisible => !IsHidden && (Parent?.IsVisible ?? true);

			public void EnsureChildrenLoaded() { }
			public void AddChild(ITreeNode node) => children.Add(node);
			public IEnumerable<ITreeNode> Descendants() => children.SelectMany(a => a.DescendantsAndSelf());
			public IEnumerable<ITreeNode> DescendantsAndSelf() {
				yield return this;
				foreach (ITreeNode child in children)
					foreach (ITreeNode descendant in child.DescendantsAndSelf())
						yield return descendant;
			}
			public void RefreshUI() { }
		}

		static object? DefaultValue(Type? type) => type is null || !type.IsValueType
			? null : Activator.CreateInstance(type);

		sealed class EditMethodNode : MethodNode {
			public EditMethodNode(MethodDef method) : base(method) { }

			public override Guid Guid => typeof(EditMethodNode).GUID;

			public override NodePathName NodePathName => new NodePathName(
				typeof(EditMethodNode).GUID, MethodDef.FullName);

			protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => default;

			protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler,
				DocumentNodeWriteOptions options) {
			}
		}
	}
}
