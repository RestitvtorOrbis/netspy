// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using dnlib.DotNet;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>
	/// Composes the production document service and bundle provider for integration tests.
	/// </summary>
	internal sealed class BundlePipelineTestSupport : IDisposable {
		const string ModernCompressedBundleRelativePath =
			"Tests/TestAssets/SingleFile/Net10/artifacts/net10.0/scd-compressed/publish/SingleFile.App.exe";

		readonly CompositionContainer container;

		BundlePipelineTestSupport(CompositionContainer container, IDsDocumentService documentService,
			IDecompiler cSharpDecompiler, IDsDocumentNodeProvider bundleNodeProvider) {
			this.container = container;
			DocumentService = documentService;
			CSharpDecompiler = cSharpDecompiler;
			BundleNodeProvider = bundleNodeProvider;
		}

		public IDsDocumentService DocumentService { get; }
		public IDecompiler CSharpDecompiler { get; }
		public IDsDocumentNodeProvider BundleNodeProvider { get; }

		public static BundlePipelineTestSupport Create() {
			Assembly product = Assembly.Load("dnSpy");
			Type serviceType = product.GetType("dnSpy.Documents.DsDocumentService", true)!;
			Type defaultProviderType = product.GetType("dnSpy.Documents.DefaultDsDocumentProvider", true)!;
			Type settingsType = product.GetType("dnSpy.Documents.DsDocumentServiceSettings", true)!;
			Type settingsContractType = product.GetType("dnSpy.Documents.IDsDocumentServiceSettings", true)!;
			object settings = Activator.CreateInstance(settingsType)!;

			var catalog = new TypeCatalog(serviceType, defaultProviderType,
				typeof(BundleDsDocumentProvider), typeof(BundleDocumentNodeProvider));
			var container = new CompositionContainer(catalog);
			var batch = new CompositionBatch();
			var metadata = new Dictionary<string, object?> {
				[CompositionConstants.ExportTypeIdentityMetadataName] =
					AttributedModelServices.GetTypeIdentity(settingsContractType),
			};
			batch.AddExport(new Export(settingsContractType.FullName!, metadata, () => settings));
			try {
				container.Compose(batch);
				return new BundlePipelineTestSupport(container,
					container.GetExportedValue<IDsDocumentService>()!,
					CreateCSharpDecompiler(),
					container.GetExportedValue<IDsDocumentNodeProvider>()!);
			}
			catch {
				container.Dispose();
				throw;
			}
		}

		public static IDecompiler CreateCSharpDecompiler() {
			Assembly assembly = Assembly.Load("dnSpy.Decompiler.ILSpy.Core");
			Type providerType = assembly.GetType(
				"dnSpy.Decompiler.ILSpy.Core.CSharp.DecompilerProvider", true)!;
			object provider = Activator.CreateInstance(providerType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, Array.Empty<object>(), null)!;
			MethodInfo create = providerType.GetMethod("Create",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
			return ((IEnumerable)create.Invoke(provider, null)!).Cast<IDecompiler>()
				.Single(a => a.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY);
		}

		public static string FindModernCompressedBundle() {
			foreach (DirectoryInfo directory in EnumerateAncestors(AppContext.BaseDirectory)) {
				string candidate = Path.Combine(directory.FullName, ModernCompressedBundleRelativePath);
				if (File.Exists(candidate))
					return candidate;
			}
			foreach (DirectoryInfo directory in EnumerateAncestors(Directory.GetCurrentDirectory())) {
				string candidate = Path.Combine(directory.FullName, ModernCompressedBundleRelativePath);
				if (File.Exists(candidate))
					return candidate;
			}
			throw new InvalidOperationException(
				$"The generated compressed net10 bundle fixture is missing: {ModernCompressedBundleRelativePath}");
		}

		public static BundleEntryDocument FindAssemblyEntry(BundleDsDocument document,
			Func<BundleEntry, bool> predicate) {
			if (document is null)
				throw new ArgumentNullException(nameof(document));
			if (predicate is null)
				throw new ArgumentNullException(nameof(predicate));
			BundleFolderDocument folder = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies);
			return folder.Children.Cast<BundleEntryDocument>()
				.Single(a => a.Entry.FileType == BundleFileType.Assembly && predicate(a.Entry));
		}

		public static TypeDef FindMethodBearingType(ModuleDef module, string? preferredName = null) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			TypeDef[] types = GetTypes(module.Types).ToArray();
			IEnumerable<TypeDef> candidates = types;
			if (!string.IsNullOrEmpty(preferredName)) {
				IEnumerable<TypeDef> preferred = types.Where(a =>
					(a.Name.String ?? string.Empty).Contains(preferredName, StringComparison.Ordinal));
				TypeDef? preferredWithBody = preferred.FirstOrDefault(HasMethodBody);
				if (preferredWithBody is not null)
					return preferredWithBody;
			}
			return candidates.FirstOrDefault(HasMethodBody)
				?? throw new InvalidOperationException("The managed module has no method-bearing type.");
		}

		public static DiagnosticDecompilerOutput DecompileType(IDecompiler decompiler, TypeDef type) {
			var output = new DiagnosticDecompilerOutput();
			decompiler.Decompile(type, output, new DecompilationContext());
			return output;
		}

		public static string ComputeSha256(string filename) {
			using SHA256 sha256 = SHA256.Create();
			using FileStream stream = File.OpenRead(filename);
			return Convert.ToHexString(sha256.ComputeHash(stream));
		}

		public void Dispose() => container.Dispose();

		static bool HasMethodBody(TypeDef type) => type.Methods.Any(a => a.HasBody);

		static IEnumerable<TypeDef> GetTypes(IEnumerable<TypeDef> types) {
			foreach (TypeDef type in types) {
				yield return type;
				foreach (TypeDef nested in GetTypes(type.NestedTypes))
					yield return nested;
			}
		}

		static IEnumerable<DirectoryInfo> EnumerateAncestors(string path) {
			DirectoryInfo? directory = new DirectoryInfo(path);
			while (directory is not null) {
				yield return directory;
				directory = directory.Parent;
			}
		}
	}

	internal sealed class DiagnosticDecompilerOutput : StringBuilderDecompilerOutput {
		public bool HasErrors { get; private set; }

		public override void Write(string text, object color) {
			Record(color);
			base.Write(text, color);
		}

		public override void Write(string text, int index, int length, object color) {
			Record(color);
			base.Write(text, index, length, color);
		}

		public override void Write(string text, object? reference, DecompilerReferenceFlags flags,
			object color) {
			Record(color);
			base.Write(text, reference, flags, color);
		}

		public override void Write(string text, int index, int length, object? reference,
			DecompilerReferenceFlags flags, object color) {
			Record(color);
			base.Write(text, index, length, reference, flags, color);
		}

		void Record(object color) {
			if (Equals(color, BoxedTextColor.Error))
				HasErrors = true;
		}
	}

	internal sealed class BundleDecompileNodeContext : IDecompileNodeContext {
		public BundleDecompileNodeContext(IDecompiler decompiler) => Decompiler = decompiler;

		public DiagnosticDecompilerOutput Output { get; } = new DiagnosticDecompilerOutput();
		IDecompilerOutput IDecompileNodeContext.Output => Output;
		public IDocumentWriterService DocumentWriterService => null!;
		public IDecompiler Decompiler { get; }
		public DecompilationContext DecompilationContext { get; } = new DecompilationContext();
		public Microsoft.VisualStudio.Utilities.IContentType? ContentType { get; set; }
		public string? ContentTypeString { get; set; }
		public T UIThread<T>(Func<T> func) => func();
	}
}
