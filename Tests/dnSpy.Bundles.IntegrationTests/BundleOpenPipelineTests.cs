// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnlib.DotNet;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleOpenPipelineTests {
		[Fact]
		public void GeneratedCompressedBundleOpensAndDecompilesThroughTheDocumentPipeline() {
			string filename = BundlePipelineTestSupport.FindModernCompressedBundle();
			using var support = BundlePipelineTestSupport.Create();
			BundleDsDocument? document = null;
			try {
				document = Assert.IsType<BundleDsDocument>(support.DocumentService.TryGetOrCreate(
					DsDocumentInfo.CreateDocument(filename)));
				BundleEntryDocument app = BundlePipelineTestSupport.FindAssemblyEntry(document,
					a => StringComparer.Ordinal.Equals(a.RelativePath, "SingleFile.App.dll"));

				DsDocumentNode appNode = support.BundleNodeProvider.Create(null!, null, app)!;
				var decompileContext = new BundleDecompileNodeContext(support.CSharpDecompiler);
				Assert.True(Assert.IsAssignableFrom<IDecompileSelf>(appNode).Decompile(decompileContext));
				string decompiledHeader = decompileContext.Output.GetText();
				Assert.Contains("SingleFile.App", decompiledHeader, StringComparison.Ordinal);
				Assert.DoesNotContain("The decompiler extension wasn't built", decompiledHeader,
					StringComparison.Ordinal);
				Assert.False(decompileContext.Output.HasErrors);

				BundleModuleDocument module = app.CreateManagedDocument();
				Assert.Same(module, app.ManagedDocument);
				TypeDef appType = BundlePipelineTestSupport.FindMethodBearingType(module.ModuleDef!, "Program");
				Assert.Contains(appType.Methods, a => a.HasBody);
				DiagnosticDecompilerOutput typeOutput = BundlePipelineTestSupport.DecompileType(
					support.CSharpDecompiler, appType);
				string decompiledBody = typeOutput.GetText();
				Assert.NotEmpty(decompiledBody);
				Assert.Contains("Console.WriteLine", decompiledBody, StringComparison.Ordinal);
				Assert.Contains("BUNDLE_VALUE=", decompiledBody, StringComparison.Ordinal);
				Assert.DoesNotContain("The decompiler extension wasn't built", decompiledBody,
					StringComparison.Ordinal);
				Assert.False(typeOutput.HasErrors);
			}
			finally {
				document?.Dispose();
			}
		}
	}
}
