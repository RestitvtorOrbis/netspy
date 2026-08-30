// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using dnSpy.Debugger.DbgUI;
using dnlib.DotNet;
using dnlib.PE;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleDebugCompatibilityTests {
		[Fact]
		public void BundleChildDebugTargetUsesTopLevelPhysicalSource() {
			string source = typeof(BundleDebugCompatibilityTests).Assembly.Location;
			var entry = new BundleEntry(0, 0, 0, 0, 1, BundleFileType.Assembly, "app.dll");
			var bundle = new BundleFile(source, 1, 128, 160,
				new BundleManifest(6, 0, "debug-path"), new[] { entry });
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), bundle);
			IDocumentTreeView view = CreateProductionTreeView();
			DsDocumentNode root = view.CreateNode(null, document);
			DsDocumentNode child = root.CreateChildren().Cast<DsDocumentNode>()
				.Single(node => ((BundleFolderDocument)node.Document).Kind == BundleFolderKind.Assemblies)
				.CreateChildren().Cast<DsDocumentNode>().Single();

			Assert.Contains("!/app.dll", child.Document.Filename, StringComparison.Ordinal);
			Assert.Equal(Path.GetFullPath(source), DebugTargetCompatibility.GetPhysicalFilename(child));
		}

		[Fact]
		public void ReadyToRunRequiresExactSignatureAndBoundedDirectory() {
			using ModuleDefMD exact = ModuleDefMD.Load(CreateReadyToRunImage(4, 0x00525452));
			using ModuleDefMD wrong = ModuleDefMD.Load(CreateReadyToRunImage(4, 0x00525453));
			using ModuleDefMD shortDirectory = ModuleDefMD.Load(CreateReadyToRunImage(3, 0x00525452));
			using ModuleDefMD extendsPastEntry = ModuleDefMD.Load(CreateReadyToRunImage(
				uint.MaxValue, 0x00525452));
			using ModuleDefMD outOfBounds = ModuleDefMD.Load(CreateReadyToRunImage(4, 0x00525452,
				managedNativeRva: uint.MaxValue - 3));

			Assert.True(BundleManagedEntryAdapter.IsReadyToRun(exact));
			Assert.False(BundleManagedEntryAdapter.IsReadyToRun(wrong));
			Assert.False(BundleManagedEntryAdapter.IsReadyToRun(shortDirectory));
			Assert.False(BundleManagedEntryAdapter.IsReadyToRun(extendsPastEntry));
			Assert.False(BundleManagedEntryAdapter.IsReadyToRun(outOfBounds));
		}

		[Fact]
		public void ReadyToRunAssemblyNodeIsAnnotated() {
			byte[] bytes = CreateReadyToRunImage(4, 0x00525452);
			string source = typeof(BundleDebugCompatibilityTests).Assembly.Location;
			var entry = new BundleEntry(0, 0, bytes.LongLength, 0, 1,
				BundleFileType.Assembly, "app.dll");
			var bundle = new BundleFile(source, bytes.LongLength, 128, 160,
				new BundleManifest(6, 0, "r2r"), new[] { entry });
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), bundle,
				openLogicalRead: _ => new MemoryStream(bytes, writable: false));
			var folder = new BundleFolderDocument(document, BundleFolderKind.Assemblies);
			var entryDocument = new BundleEntryDocument(folder, entry);
			using BundleModuleDocument module = entryDocument.CreateManagedDocument();
			DsDotNetDocument assembly = module.CreateAssemblyDocument();
			try {
				DsDocumentNode node = new BundleDocumentNodeProvider().Create(null!, null, assembly)!;
				var output = new StringBuilderTextColorOutput();
				node.Write(output, CreateDummyDecompiler(), DocumentNodeWriteOptions.None);
				Assert.Contains("[ReadyToRun]", output.Text, StringComparison.Ordinal);
			}
			finally {
				assembly.Dispose();
			}
		}

		[Theory]
		[InlineData("DotNetRuntimeDebugHeader")]
		[InlineData("DotNetRuntimeContractDescriptor")]
		public void RecognizedNativeAotExportProducesExplanatoryUnsupportedResult(string exportName) {
			string filename = WriteTemp(CreateNativeExportImage(exportName));
			try {
				Assert.True(DebugTargetCompatibility.IsHighConfidenceNativeAot(filename));
				Assert.NotNull(DebugTargetCompatibility.GetNativeAotUnsupportedMessage(filename));
				string message = DebugTargetCompatibility.GetNativeAotUnsupportedMessage(filename)!;
				Assert.Contains("NativeAOT", message, StringComparison.Ordinal);
				Assert.Contains("editable managed IL", message, StringComparison.Ordinal);
			}
			finally {
				Delete(filename);
			}
		}

		[Fact]
		public void NativeAotRequiresExactExportAndNoCor20Header() {
			string nonMatching = WriteTemp(CreateNativeExportImage("NotANativeAotExport"));
			string managed = typeof(BundleDebugCompatibilityTests).Assembly.Location;
			try {
				Assert.False(DebugTargetCompatibility.IsHighConfidenceNativeAot(nonMatching));
				Assert.Null(DebugTargetCompatibility.GetNativeAotUnsupportedMessage(nonMatching));
				Assert.False(DebugTargetCompatibility.IsHighConfidenceNativeAot(managed));
			}
			finally {
				Delete(nonMatching);
			}
		}

		[Fact]
		public void MalformedNativeAotExportTableIsRejectedWithoutThrowing() {
			string filename = WriteTemp(CreateMalformedNativeExportImage());
			try {
				Assert.False(DebugTargetCompatibility.IsHighConfidenceNativeAot(filename));
				Assert.Null(DebugTargetCompatibility.GetNativeAotUnsupportedMessage(filename));
			}
			finally {
				Delete(filename);
			}
		}

		static IDocumentTreeView CreateProductionTreeView() {
			IDocumentTreeView view = DispatchProxy.Create<IDocumentTreeView,
				BundleManagedDocumentTests.TreeViewProxy>();
			IDocumentTreeNodeDataContext context = DispatchProxy.Create<IDocumentTreeNodeDataContext,
				BundleManagedDocumentTests.NodeContextProxy>();
			((BundleManagedDocumentTests.NodeContextProxy)(object)context).View = view;
			((BundleManagedDocumentTests.TreeViewProxy)(object)view).Initialize(context);
			return view;
		}

		static byte[] CreateReadyToRunImage(uint managedNativeSize, uint signature,
			uint? managedNativeRva = null) {
			byte[] bytes = CreateManagedModuleBytes();
			using var peImage = new PEImage(bytes, verify: false);
			ImageDataDirectory cor20 = peImage.ImageNTHeaders.OptionalHeader.DataDirectories[14];
			uint cor20Offset = (uint)peImage.ToFileOffset((RVA)cor20.VirtualAddress);
			ImageSectionHeader section = peImage.ImageSectionHeaders[0];
			uint nativeRva = managedNativeRva ?? (uint)section.VirtualAddress;
			WriteUInt32(bytes, checked(cor20Offset + 0x40), nativeRva);
			WriteUInt32(bytes, checked(cor20Offset + 0x44), managedNativeSize);
			if (managedNativeRva is null)
				WriteUInt32(bytes, section.PointerToRawData, signature);
			return bytes;
		}

		static byte[] CreateNativeExportImage(string exportName) {
			byte[] bytes = CreateManagedModuleBytes();
			using (var peImage = new PEImage(bytes, verify: false)) {
				ImageSectionHeader section = peImage.ImageSectionHeaders[0];
				uint exportRva = (uint)section.VirtualAddress;
				uint exportOffset = section.PointerToRawData;
				uint functionsRva = exportRva + 0x40;
				uint namesRva = exportRva + 0x44;
				uint ordinalsRva = exportRva + 0x48;
				uint nameRva = exportRva + 0x4A;
				uint moduleNameRva = exportRva + 0x80;
				WriteUInt32(bytes, exportOffset + 12, moduleNameRva);
				WriteUInt32(bytes, exportOffset + 16, 1); // ordinal base
				WriteUInt32(bytes, exportOffset + 20, 1); // number of functions
				WriteUInt32(bytes, exportOffset + 24, 1); // number of names
				WriteUInt32(bytes, exportOffset + 28, functionsRva);
				WriteUInt32(bytes, exportOffset + 32, namesRva);
				WriteUInt32(bytes, exportOffset + 36, ordinalsRva);
				WriteUInt32(bytes, exportOffset + 0x40, exportRva + 0x100);
				WriteUInt32(bytes, exportOffset + 0x44, nameRva);
				WriteUInt16(bytes, exportOffset + 0x48, 0);
				WriteAsciiZ(bytes, exportOffset + 0x4A, exportName);
				WriteAsciiZ(bytes, exportOffset + 0x80, "NativeAotFixture");
			}

			int peHeaderOffset = checked((int)ReadUInt32(bytes, 0x3C));
			int optionalHeaderOffset = checked(peHeaderOffset + 24);
			ushort magic = ReadUInt16(bytes, optionalHeaderOffset);
			int dataDirectoryOffset = checked(optionalHeaderOffset + (magic == 0x20B ? 112 : 96));
			WriteUInt32(bytes, checked((uint)dataDirectoryOffset),
				ReadUInt32(bytes, checked((uint)dataDirectoryOffset)));
			WriteUInt32(bytes, checked((uint)dataDirectoryOffset + 4), 0x28);
			WriteUInt32(bytes, checked((uint)dataDirectoryOffset + 14 * 8), 0);
			WriteUInt32(bytes, checked((uint)dataDirectoryOffset + 14 * 8 + 4), 0);
			using var updated = new PEImage(bytes, verify: false);
			ImageSectionHeader firstSection = updated.ImageSectionHeaders[0];
			WriteUInt32(bytes, checked((uint)dataDirectoryOffset), (uint)firstSection.VirtualAddress);
			return bytes;
		}

		static byte[] CreateMalformedNativeExportImage() {
			byte[] bytes = CreateNativeExportImage("DotNetRuntimeDebugHeader");
			using var peImage = new PEImage(bytes, verify: false);
			ImageSectionHeader section = peImage.ImageSectionHeaders[0];
			// Keep the PE structurally readable but make the export's name count impossible to
			// satisfy within the bounded image.
			WriteUInt32(bytes, section.PointerToRawData + 24, uint.MaxValue);
			return bytes;
		}

		static byte[] CreateManagedModuleBytes() {
			var module = new ModuleDefUser("DebugCompatibilityFixture") {
				Kind = ModuleKind.Dll,
				Characteristics = Characteristics.Bit32Machine | Characteristics.ExecutableImage |
					Characteristics.Dll,
			};
			using var stream = new MemoryStream();
			module.Write(stream);
			return stream.ToArray();
		}

		static string WriteTemp(byte[] bytes) {
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			File.WriteAllBytes(filename, bytes);
			return filename;
		}

		static void WriteAsciiZ(byte[] bytes, uint offset, string value) {
			byte[] text = System.Text.Encoding.ASCII.GetBytes(value);
			text.CopyTo(bytes, checked((int)offset));
			bytes[checked((int)offset + text.Length)] = 0;
		}

		static void WriteUInt16(byte[] bytes, uint offset, ushort value) =>
			BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(checked((int)offset), sizeof(ushort)), value);

		static void WriteUInt32(byte[] bytes, uint offset, uint value) =>
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(checked((int)offset), sizeof(uint)), value);

		static ushort ReadUInt16(byte[] bytes, int offset) =>
			BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));

		static uint ReadUInt32(byte[] bytes, uint offset) =>
			BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)offset), sizeof(uint)));

		static void Delete(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		static IDecompiler CreateDummyDecompiler() {
			Assembly product = Assembly.Load("dnSpy");
			Type decompilerType = product.GetType("dnSpy.Decompiler.DummyDecompiler", true)!;
			return (IDecompiler)Activator.CreateInstance(decompilerType, nonPublic: true)!;
		}
	}
}
