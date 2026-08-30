// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnlib.IO;
using dnlib.PE;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Debugger.DbgUI {
	/// <summary>Small, side-effect-free compatibility checks used by debug target selection.</summary>
	public static class DebugTargetCompatibility {
		const uint MaximumExportItems = 1_000_000;
		const uint ExportDirectorySize = 0x28;
		const string NativeAotExport1 = "DotNetRuntimeDebugHeader";
		const string NativeAotExport2 = "DotNetRuntimeContractDescriptor";

		/// <summary>
		/// Returns the physical filename represented by a selected document-tree item.
		/// </summary>
		/// <remarks>
		/// Bundle child documents intentionally have synthetic names. Ordinary physical documents
		/// take the first branch and therefore retain the existing debug-selection behavior.
		/// </remarks>
		public static string GetPhysicalFilename(TreeNodeData? selectedItem) {
			DsDocumentNode? selectedNode = selectedItem?.GetDocumentNode();
			string? filename = selectedNode?.Document.Filename;
			if (File.Exists(filename))
				return filename!;

			filename = selectedItem?.GetTopNode()?.Document.Filename;
			return File.Exists(filename) ? filename! : string.Empty;
		}

		/// <summary>
		/// Identifies a Windows NativeAOT image with the two high-confidence runtime exports.
		/// </summary>
		/// <remarks>
		/// A non-zero COR20 directory excludes this classification. Export tables are read through
		/// bounded dnlib readers and count fields are capped before any arithmetic or iteration.
		/// </remarks>
		public static bool IsHighConfidenceNativeAot(string? filename) {
			if (string.IsNullOrWhiteSpace(filename))
				return false;
			try {
				using var peImage = new PEImage(filename, verify: true);
				return IsHighConfidenceNativeAot(peImage);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
				ex is BadImageFormatException || ex is dnlib.IO.DataReaderException ||
				ex is ArgumentException || ex is OverflowException) {
				return false;
			}
		}

		/// <summary>Returns the user-facing explanation for a recognized NativeAOT image.</summary>
		public static string? GetNativeAotUnsupportedMessage(string? filename) {
			if (!IsHighConfidenceNativeAot(filename))
				return null;
			string displayName = Path.GetFileName(filename) ?? filename!;
			return $"'{displayName}' is a Windows NativeAOT executable. NativeAOT does not contain editable managed IL, so dnSpy cannot decompile or debug it as a managed application.";
		}

		static bool IsHighConfidenceNativeAot(IPEImage peImage) {
			ImageDataDirectory[] directories = peImage.ImageNTHeaders.OptionalHeader.DataDirectories;
			if (directories.Length <= 14 || directories[14].VirtualAddress != 0)
				return false;
			ImageDataDirectory exportDirectory = directories[0];
			if (exportDirectory.VirtualAddress == 0 || exportDirectory.Size < ExportDirectorySize)
				return false;
			if (!TryCreateReader(peImage, exportDirectory.VirtualAddress, ExportDirectorySize,
				out DataReader exportReader))
				return false;

			exportReader.Position = 16;
			_ = exportReader.ReadUInt32(); // ordinal base
			uint functionCount = exportReader.ReadUInt32();
			uint nameCount = exportReader.ReadUInt32();
			uint functionsRva = exportReader.ReadUInt32();
			uint namesRva = exportReader.ReadUInt32();
			uint ordinalsRva = exportReader.ReadUInt32();
			if (functionCount == 0 || nameCount == 0 || functionCount > MaximumExportItems ||
				nameCount > MaximumExportItems)
				return false;
			if (!TryGetByteCount(functionCount, sizeof(uint), out uint functionsLength) ||
				!TryGetByteCount(nameCount, sizeof(uint), out uint namesLength) ||
				!TryGetByteCount(nameCount, sizeof(ushort), out uint ordinalsLength) ||
				!TryCreateReader(peImage, (RVA)functionsRva, functionsLength, out DataReader functionsReader) ||
				!TryCreateReader(peImage, (RVA)namesRva, namesLength, out DataReader namesReader) ||
				!TryCreateReader(peImage, (RVA)ordinalsRva, ordinalsLength, out DataReader ordinalsReader))
				return false;

			for (uint i = 0; i < nameCount; i++) {
				namesReader.Position = checked(i * sizeof(uint));
				uint nameRva = namesReader.ReadUInt32();
				ordinalsReader.Position = checked(i * sizeof(ushort));
				uint functionIndex = ordinalsReader.ReadUInt16();
				if (functionIndex >= functionCount)
					continue;
				functionsReader.Position = checked(functionIndex * sizeof(uint));
				if (functionsReader.ReadUInt32() == 0)
					continue;
				if (IsExportName(peImage, (RVA)nameRva, NativeAotExport1) ||
					IsExportName(peImage, (RVA)nameRva, NativeAotExport2))
					return true;
			}
			return false;
		}

		static bool TryGetByteCount(uint count, uint elementSize, out uint byteCount) {
			ulong result = (ulong)count * elementSize;
			if (result > uint.MaxValue) {
				byteCount = 0;
				return false;
			}
			byteCount = (uint)result;
			return byteCount != 0;
		}

		static bool TryCreateReader(IPEImage peImage, RVA rva, uint length, out DataReader reader) {
			reader = default;
			if (rva == 0 || length == 0)
				return false;
			try {
				reader = peImage.CreateReader((RVA)rva, length);
				return reader.Length >= length;
			}
			catch (Exception ex) when (ex is dnlib.IO.DataReaderException ||
				ex is ArgumentException || ex is OverflowException) {
				return false;
			}
		}

		static bool IsExportName(IPEImage peImage, RVA rva, string expected) {
			if (!TryCreateReader(peImage, rva, checked((uint)expected.Length + 1),
				out DataReader reader))
				return false;
			for (int i = 0; i < expected.Length; i++) {
				if (reader.ReadByte() != expected[i])
					return false;
			}
			return reader.ReadByte() == 0;
		}
	}
}
