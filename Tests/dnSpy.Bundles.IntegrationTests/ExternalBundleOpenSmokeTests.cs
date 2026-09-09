// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnlib.DotNet;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class ExternalBundleOpenSmokeTests {
		[Fact]
		public void ConfiguredExternalBundleOpensAndDecompilesWithoutMutatingTheSource() {
			string? configured = Environment.GetEnvironmentVariable("NETSPY_EXTERNAL_BUNDLE");
			if (string.IsNullOrWhiteSpace(configured))
				Assert.Skip("NETSPY_EXTERNAL_BUNDLE is not configured.");

			string filename = Path.GetFullPath(configured.Trim());
			Assert.False(Directory.Exists(filename),
				"NETSPY_EXTERNAL_BUNDLE must identify a bundle file, not a directory.");
			Assert.True(File.Exists(filename), "NETSPY_EXTERNAL_BUNDLE does not identify a readable file.");
			string sourceHashBefore = BundlePipelineTestSupport.ComputeSha256(filename);
			BundleDsDocument? document = null;
			try {
				using (BundlePipelineTestSupport support = BundlePipelineTestSupport.Create()) {
					document = Assert.IsType<BundleDsDocument>(support.DocumentService.TryGetOrCreate(
						DsDocumentInfo.CreateDocument(filename)));
					string expectedAssemblyName = Path.GetFileNameWithoutExtension(filename) + ".dll";
					BundleEntryDocument assemblyEntry = BundlePipelineTestSupport.FindAssemblyEntry(document,
						a => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(a.RelativePath),
							expectedAssemblyName));

					DsDocumentNode assemblyNode = support.BundleNodeProvider.Create(null!, null, assemblyEntry)!;
					var nodeContext = new BundleDecompileNodeContext(support.CSharpDecompiler);
					Assert.True(Assert.IsAssignableFrom<IDecompileSelf>(assemblyNode).Decompile(nodeContext));
					Assert.False(nodeContext.Output.HasErrors);

					BundleModuleDocument module = assemblyEntry.CreateManagedDocument();
					TypeDef type = BundlePipelineTestSupport.FindMethodBearingType(module.ModuleDef!);
					DiagnosticDecompilerOutput typeOutput = BundlePipelineTestSupport.DecompileType(
						support.CSharpDecompiler, type);
					Assert.NotEmpty(typeOutput.GetText());
					Assert.False(typeOutput.HasErrors);
					Assert.Null(document.AssemblyResolver.Diagnostic);
				}
			}
			finally {
				document?.Dispose();
				Assert.Equal(sourceHashBefore, BundlePipelineTestSupport.ComputeSha256(filename));
			}
		}
	}
}
