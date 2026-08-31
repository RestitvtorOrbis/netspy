// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class SaveModuleIndependenceRegressionTests {
		[Fact]
		public void StandaloneModuleSaveDoesNotOverwriteWorkspaceReplacementOrMutateSource() {
			string filename = FindCompressedFixture();
			byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(filename));
			BundleOpenResult result = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, result.Status);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(filename), result.Bundle!);
			BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
				.Single(a => a.Kind == BundleFolderKind.Assemblies).Children
				.Cast<BundleEntryDocument>().First();
			using BundleModuleDocument module = entry.CreateManagedDocument();
			string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			string workspaceOutput = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				Assert.True(ExistingEditSaveHarness.WriteToFile(
					ExistingEditSaveHarness.CreateSaveOptions(module), workspaceOutput));
				module.SetWorkspaceReplacement(File.ReadAllBytes(workspaceOutput));
				byte[] workspaceBytes = Read(document.Workspace.OpenCurrentRead(entry.Entry));

				Assert.True(ExistingEditSaveHarness.WriteToFile(
					ExistingEditSaveHarness.CreateSaveOptions(module), output));
				Assert.True(module.HasWorkspaceReplacement);
				Assert.True(document.HasPendingChanges);
				Assert.Equal(workspaceBytes, Read(document.Workspace.OpenCurrentRead(entry.Entry)));
				Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(filename)));
			}
			finally {
				try { File.Delete(output); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
				try { File.Delete(workspaceOutput); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		static byte[] Read(Stream stream) {
			using (stream)
			using (var output = new MemoryStream()) {
				stream.CopyTo(output);
				return output.ToArray();
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
	}
}
