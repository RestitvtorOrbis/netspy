// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace dnSpy.Bundles {
	/// <summary>Performs the read-only preflight required before Windows bundle reconstruction.</summary>
	public sealed class WindowsBundleEligibilityInspector {
		const uint ReadyToRunSignature = 0x00525452;
		const string RuntimeConfigSuffix = ".runtimeconfig.json";
		const string DepsSuffix = ".deps.json";
		/// <summary>
		/// Bounds the one managed entry materialized for PE/metadata inspection. The bundle reader
		/// deliberately allows much larger entries for callers with an explicit need to stream
		/// them, but eligibility only needs a small, bounded view and must not turn a compressed
		/// manifest declaration into an unbounded allocation.
		/// </summary>
		public const long MaximumManagedAssemblyInspectionBytes = 64L * 1024 * 1024;
		const uint MaximumExportItems = 1_000_000;
		const int MaximumExportNameBytes = 256;
		const string NativeAotExport1 = "DotNetRuntimeDebugHeader";
		const string NativeAotExport2 = "DotNetRuntimeContractDescriptor";

		/// <summary>Opens and inspects an official bundle without modifying it.</summary>
		public WindowsBundleEligibilityResult Inspect(string filename) {
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			string? hash;
			try {
				hash = ComputeSha256(filename);
			}
			catch (Exception ex) when (IsFileFailure(ex)) {
				return Result(WindowsBundleEligibilityStatus.MalformedBundle,
					"The source bundle could not be read safely.", null);
			}

			BundleOpenResult open;
			try {
				open = new BundleReader().Open(filename);
			}
			catch (Exception ex) when (IsFileFailure(ex)) {
				return Result(WindowsBundleEligibilityStatus.MalformedBundle,
					"The source bundle could not be read safely.", hash);
			}
			if (open.Status == BundleOpenStatus.NotBundle) {
				if (TryInspectPe(filename, out PeInspection pe) && pe.IsNativeAot) {
					return Result(WindowsBundleEligibilityStatus.NativeAot,
						"This Windows NativeAOT executable does not contain conventional managed IL that can be rebuilt as a bundle.", hash);
				}
				return Result(WindowsBundleEligibilityStatus.NotBundle,
					"The source is not an official .NET single-file bundle.", hash);
			}
			if (open.Status != BundleOpenStatus.Success) {
				string detail = open.Error is null ? "Unknown" : open.Error.Code.ToString();
				return Result(WindowsBundleEligibilityStatus.MalformedBundle,
					"The source bundle is malformed or unsupported (" + detail + ").", hash);
			}

			using (BundleFile bundle = open.Bundle!)
			using (var workspace = new BundleWorkspace(bundle))
				return InspectCore(workspace, hash);
		}

		/// <summary>Inspects a parsed workspace including its current dirty-entry state.</summary>
		public WindowsBundleEligibilityResult Inspect(BundleWorkspace workspace) {
			if (workspace is null)
				throw new ArgumentNullException(nameof(workspace));
			string hash;
			try {
				hash = ComputeSha256(workspace.Bundle.Filename);
			}
			catch (Exception ex) when (IsFileFailure(ex)) {
				return Result(WindowsBundleEligibilityStatus.MalformedBundle,
					"The source bundle could not be read safely.", null);
			}
			return InspectCore(workspace, hash);
		}

		static WindowsBundleEligibilityResult InspectCore(BundleWorkspace workspace, string hash) {
			if (!TryInspectPe(workspace.Bundle.Filename, out PeInspection pe)) {
				return Result(WindowsBundleEligibilityStatus.UnsupportedPlatform,
					"Save Bundle As currently supports only valid Windows PE bundles.", hash);
			}
			if (pe.MalformedCertificateTable) {
				return Result(WindowsBundleEligibilityStatus.UnsupportedPlatform,
					"The Windows PE certificate-table directory is malformed.", hash);
			}
			if (pe.Machine != Machine.Amd64) {
				return Result(WindowsBundleEligibilityStatus.UnsupportedArchitecture,
					"Save Bundle As currently supports only Windows x64 bundles; source machine is " + pe.Machine + ".",
					hash, pe.HasAuthenticodeSignature);
			}

			bool isV1 = workspace.Bundle.Manifest.MajorVersion == 1;
			if (isV1) {
				// v1 serializes every file type as raw zero. The parser deliberately exposes that
				// format truth as Unknown; eligibility may infer the payload kind only after this
				// preservation invariant has been checked.
				foreach (BundleEntry entry in workspace.Bundle.Entries) {
					if (entry.RawFileType != 0)
						return EntryResult(WindowsBundleEligibilityStatus.UnknownFileType,
							"v1 bundle entry '" + entry.RelativePath + "' has raw file type " +
							entry.RawFileType + "; only raw type 0 can be preserved.", hash,
							pe.HasAuthenticodeSignature, entry);
				}
			}
			else {
				BundleEntry? unknown = workspace.Bundle.Entries.FirstOrDefault(entry =>
					entry.FileType == BundleFileType.Unknown);
				if (unknown is not null) {
					return EntryResult(WindowsBundleEligibilityStatus.UnknownFileType,
						"Bundle entry type " + unknown.RawFileType + " cannot be preserved by HostModel.",
						hash, pe.HasAuthenticodeSignature, unknown);
				}
			}

			string appAssemblyName = isV1 ? GetAppAssemblyName(workspace.Bundle) : string.Empty;
			var identities = new Dictionary<AssemblyIdentity, BundleEntry>();
			bool hasManagedAssembly = false;
			foreach (BundleEntry entry in workspace.Bundle.Entries) {
				BundleFileType fileType = isV1
					? InferV1FileType(workspace, entry, appAssemblyName)
					: entry.FileType;
				if (fileType != BundleFileType.Assembly)
					continue;
				hasManagedAssembly = true;
				try {
					byte[] image = ReadManagedAssembly(workspace, entry);
					using var stream = new MemoryStream(image, writable: false);
					// ReadManagedAssembly() has already imposed an explicit upper bound. A seekable
					// MemoryStream lets PEReader inspect only the required sections without asking it
					// to prefetch a potentially huge compressed logical stream.
					using var reader = new PEReader(stream);
					if (!reader.HasMetadata || reader.PEHeaders.CorHeader is null) {
						return EntryResult(WindowsBundleEligibilityStatus.MalformedManagedAssembly,
							"A managed bundle entry is not a valid managed PE assembly.", hash,
							pe.HasAuthenticodeSignature, entry);
					}
					if (workspace.HasReplacement(entry) && IsReadyToRun(reader)) {
						return EntryResult(WindowsBundleEligibilityStatus.DirtyReadyToRun,
							"A modified ReadyToRun entry cannot be rebuilt until ReadyToRun rewriting is supported.",
							hash, pe.HasAuthenticodeSignature, entry);
					}
					AssemblyIdentity identity = ReadAssemblyIdentity(reader);
					if (identities.TryGetValue(identity, out BundleEntry? first)) {
						string paths = new[] { first.RelativePath, entry.RelativePath }
							.OrderBy(path => path, StringComparer.Ordinal)
							.Aggregate((left, right) => left + ", " + right);
						return EntryResult(WindowsBundleEligibilityStatus.AmbiguousAssemblyIdentity,
							"Duplicate managed assembly identity is ambiguous: " + paths + ".", hash,
							pe.HasAuthenticodeSignature, entry);
					}
					identities.Add(identity, entry);
				}
				catch (InspectionLimitException) {
					return EntryResult(WindowsBundleEligibilityStatus.InspectionLimitExceeded,
						"A managed bundle entry exceeds the safe eligibility inspection limit.", hash,
						pe.HasAuthenticodeSignature, entry);
				}
				catch (OutOfMemoryException) {
					// A malformed metadata image can still trigger an allocation failure inside the
					// framework reader. Keep this untrusted-input boundary fail-closed and stable.
					return EntryResult(WindowsBundleEligibilityStatus.InspectionLimitExceeded,
						"A managed bundle entry exceeds the safe eligibility inspection limit.", hash,
						pe.HasAuthenticodeSignature, entry);
				}
				catch (Exception ex) when (IsManagedPeFailure(ex)) {
					return EntryResult(WindowsBundleEligibilityStatus.MalformedManagedAssembly,
						"A managed bundle entry is not a valid managed PE assembly.", hash,
						pe.HasAuthenticodeSignature, entry);
				}
			}
			if (!hasManagedAssembly) {
				return Result(WindowsBundleEligibilityStatus.NoManagedAssembly,
					"The bundle contains no conventional managed assembly entry.", hash,
					pe.HasAuthenticodeSignature);
			}

			string message = pe.HasAuthenticodeSignature
				? "The Windows x64 bundle is eligible, but rebuilding it will invalidate its Authenticode signature."
				: "The Windows x64 bundle is eligible for rebuilding.";
			return Result(WindowsBundleEligibilityStatus.Eligible, message, hash,
				pe.HasAuthenticodeSignature);
		}

		static bool IsReadyToRun(PEReader reader) {
			CorHeader? cor = reader.PEHeaders.CorHeader;
			if (cor is null || cor.ManagedNativeHeaderDirectory.RelativeVirtualAddress == 0 ||
				cor.ManagedNativeHeaderDirectory.Size < sizeof(uint))
				return false;
			PEMemoryBlock block = reader.GetSectionData(cor.ManagedNativeHeaderDirectory.RelativeVirtualAddress);
			if (block.Length < cor.ManagedNativeHeaderDirectory.Size)
				return false;
			return block.GetReader().ReadUInt32() == ReadyToRunSignature;
		}

		static BundleFileType InferV1FileType(BundleWorkspace workspace, BundleEntry entry,
			string appAssemblyName) {
			// Keep this order aligned with HostModel's InferType(): exact config names win over
			// the extension/PE checks, then PDBs win over PE-shaped content. This is important for
			// malformed or adversarial config/symbol payloads that happen to begin with MZ.
			if (StringComparer.Ordinal.Equals(entry.RelativePath, appAssemblyName + DepsSuffix))
				return BundleFileType.DepsJson;
			if (StringComparer.Ordinal.Equals(entry.RelativePath, appAssemblyName + RuntimeConfigSuffix))
				return BundleFileType.RuntimeConfigJson;
			if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(entry.RelativePath), ".pdb"))
				return BundleFileType.Symbols;

			try {
				// v1 entries are uncompressed, so the bounded logical stream is seekable and
				// PEReader can inspect headers without materializing the whole entry. Managed
				// entries are materialized only once below, under the 64 MiB inspection limit.
				using Stream current = workspace.OpenCurrentRead(entry);
				using var reader = new PEReader(current);
				if (reader.PEHeaders.PEHeader is null)
					return BundleFileType.Unknown;
				return reader.PEHeaders.CorHeader is null
					? BundleFileType.NativeBinary : BundleFileType.Assembly;
			}
			catch (OutOfMemoryException) {
				return BundleFileType.Unknown;
			}
			catch (Exception ex) when (IsManagedPeFailure(ex)) {
				return BundleFileType.Unknown;
			}
		}

		static string GetAppAssemblyName(BundleFile bundle) {
			string? name = FindConfigBaseName(bundle, RuntimeConfigSuffix) ??
				FindConfigBaseName(bundle, DepsSuffix);
			if (string.IsNullOrWhiteSpace(name))
				name = RemoveExtension(Path.GetFileName(bundle.Filename));
			return name ?? string.Empty;
		}

		static string? FindConfigBaseName(BundleFile bundle, string suffix) {
			foreach (BundleEntry entry in bundle.Entries) {
				string path = entry.RelativePath;
				if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
					continue;
				int slash = path.LastIndexOf('/');
				string fileName = slash < 0 ? path : path.Substring(slash + 1);
				string baseName = fileName.Substring(0, fileName.Length - suffix.Length);
				if (!string.IsNullOrWhiteSpace(baseName))
					return baseName;
			}
			return null;
		}

		static string RemoveExtension(string filename) {
			int dot = filename.LastIndexOf('.');
			return dot <= 0 ? filename : filename.Substring(0, dot);
		}

		static byte[] ReadManagedAssembly(BundleWorkspace workspace, BundleEntry entry) {
			if (!workspace.HasReplacement(entry)) {
				if (entry.Size > MaximumManagedAssemblyInspectionBytes)
					throw new InspectionLimitException();
				try {
					return entry.ReadAllBytes(MaximumManagedAssemblyInspectionBytes);
				}
				catch (InvalidOperationException ex) when (ex.Message ==
					"The entry exceeds the bundle read limit.") {
					throw new InspectionLimitException();
				}
			}

			// A workspace replacement is already materialized by the editor. Check its actual
			// current length rather than the original manifest size, so a small replacement for a
			// large original can still be inspected without ever opening/decompressing the original.
			using Stream current = workspace.OpenCurrentRead(entry);
			long length = current.Length;
			if (length < 0 || length > MaximumManagedAssemblyInspectionBytes || length > int.MaxValue)
				throw new InspectionLimitException();
			byte[] result = new byte[(int)length];
			int position = 0;
			while (position < result.Length) {
				int read = current.Read(result, position, result.Length - position);
				if (read <= 0)
					throw new InvalidDataException("The current managed entry ended before its declared length.");
				position = checked(position + read);
			}
			if (current.ReadByte() >= 0)
				throw new InspectionLimitException();
			return result;
		}

		static AssemblyIdentity ReadAssemblyIdentity(PEReader reader) {
			MetadataReader metadata = reader.GetMetadataReader();
			if (!metadata.IsAssembly)
				throw new BadImageFormatException();
			AssemblyDefinition definition = metadata.GetAssemblyDefinition();
			string name = metadata.GetString(definition.Name);
			string culture = definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture);
			byte[] key = definition.PublicKey.IsNil ? Array.Empty<byte>() : metadata.GetBlobBytes(definition.PublicKey);
			byte[] token = CreatePublicKeyToken(key);
			int contentType = (int)(definition.Flags & AssemblyFlags.ContentTypeMask);
			return new AssemblyIdentity(name, definition.Version, token,
				NormalizeCulture(culture), contentType);
		}

		static byte[] CreatePublicKeyToken(byte[] publicKey) {
			if (publicKey.Length == 0)
				return Array.Empty<byte>();
			using SHA1 sha1 = SHA1.Create();
			byte[] hash = sha1.ComputeHash(publicKey);
			var token = new byte[8];
			for (int index = 0; index < token.Length; index++)
				token[index] = hash[hash.Length - index - 1];
			return token;
		}

		static string NormalizeCulture(string culture) {
			string normalized = culture.ToUpperInvariant();
			return normalized == "NEUTRAL" ? string.Empty : normalized;
		}

		static bool TryInspectPe(string filename, out PeInspection inspection) {
			inspection = default;
			try {
				using var stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
				using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
				PEHeader? header = reader.PEHeaders.PEHeader;
				if (header is null)
					return false;
				DirectoryEntry certificate = header.CertificateTableDirectory;
				long certificateOffset = (uint)certificate.RelativeVirtualAddress;
				long certificateSize = (uint)certificate.Size;
				bool hasOffset = certificateOffset != 0;
				bool hasSize = certificateSize != 0;
				bool malformed = hasOffset != hasSize;
				if (hasOffset && hasSize) {
					long end = checked(certificateOffset + certificateSize);
					malformed = end > stream.Length;
				}
				inspection = new PeInspection(reader.PEHeaders.CoffHeader.Machine,
					hasOffset && hasSize && !malformed, malformed, IsNativeAot(reader));
				return true;
			}
			catch (Exception ex) when (IsPeFailure(ex)) {
				return false;
			}
		}

		static bool IsNativeAot(PEReader reader) {
			if (reader.PEHeaders.CorHeader is not null)
				return false;
			PEHeader? header = reader.PEHeaders.PEHeader;
			if (header is null || header.ExportTableDirectory.RelativeVirtualAddress == 0 ||
				header.ExportTableDirectory.Size < 40)
				return false;
			if (!TryReadUInt32(reader, header.ExportTableDirectory.RelativeVirtualAddress + 20, out uint functionCount) ||
				!TryReadUInt32(reader, header.ExportTableDirectory.RelativeVirtualAddress + 24, out uint nameCount) ||
				!TryReadUInt32(reader, header.ExportTableDirectory.RelativeVirtualAddress + 28, out uint functionsRva) ||
				!TryReadUInt32(reader, header.ExportTableDirectory.RelativeVirtualAddress + 32, out uint namesRva) ||
				!TryReadUInt32(reader, header.ExportTableDirectory.RelativeVirtualAddress + 36, out uint ordinalsRva) ||
				functionCount == 0 || nameCount == 0 || functionCount > MaximumExportItems ||
				nameCount > MaximumExportItems)
				return false;
			for (uint index = 0; index < nameCount; index++) {
				if (!TryReadUInt32(reader, checked((int)(namesRva + index * 4)), out uint nameRva) ||
					!TryReadUInt16(reader, checked((int)(ordinalsRva + index * 2)), out ushort ordinal) ||
					ordinal >= functionCount ||
					!TryReadUInt32(reader, checked((int)(functionsRva + ordinal * 4)), out uint functionRva) ||
					functionRva == 0 || !TryReadAsciiZ(reader, checked((int)nameRva), out string? name))
					continue;
				if (name == NativeAotExport1 || name == NativeAotExport2)
					return true;
			}
			return false;
		}

		static bool TryReadUInt32(PEReader reader, int rva, out uint value) {
			value = 0;
			if (rva <= 0)
				return false;
			PEMemoryBlock block = reader.GetSectionData(rva);
			if (block.Length < 4)
				return false;
			value = block.GetReader().ReadUInt32();
			return true;
		}

		static bool TryReadUInt16(PEReader reader, int rva, out ushort value) {
			value = 0;
			if (rva <= 0)
				return false;
			PEMemoryBlock block = reader.GetSectionData(rva);
			if (block.Length < 2)
				return false;
			value = block.GetReader().ReadUInt16();
			return true;
		}

		static bool TryReadAsciiZ(PEReader reader, int rva, out string? value) {
			value = null;
			if (rva <= 0)
				return false;
			PEMemoryBlock block = reader.GetSectionData(rva);
			int length = Math.Min(block.Length, MaximumExportNameBytes);
			if (length == 0)
				return false;
			BlobReader data = block.GetReader(0, length);
			var text = new StringBuilder();
			while (data.RemainingBytes != 0) {
				byte current = data.ReadByte();
				if (current == 0) {
					value = text.ToString();
					return true;
				}
				if (current > 0x7F)
					return false;
				text.Append((char)current);
			}
			return false;
		}

		static string ComputeSha256(string filename) {
			using SHA256 sha = SHA256.Create();
			using FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
			return ToHex(sha.ComputeHash(stream));
		}

		static string ToHex(byte[] bytes) {
			const string alphabet = "0123456789abcdef";
			var result = new char[bytes.Length * 2];
			for (int index = 0; index < bytes.Length; index++) {
				result[index * 2] = alphabet[bytes[index] >> 4];
				result[index * 2 + 1] = alphabet[bytes[index] & 15];
			}
			return new string(result);
		}

		static bool IsFileFailure(Exception ex) => ex is IOException ||
			ex is UnauthorizedAccessException || ex is NotSupportedException ||
			ex is System.Security.SecurityException;

		static bool IsPeFailure(Exception ex) => IsFileFailure(ex) || ex is BadImageFormatException ||
			ex is ArgumentException || ex is OverflowException;

		static bool IsManagedPeFailure(Exception ex) => IsPeFailure(ex) ||
			ex is InvalidOperationException;

		static WindowsBundleEligibilityResult EntryResult(WindowsBundleEligibilityStatus status,
			string message, string? hash, bool signed, BundleEntry entry) =>
			new WindowsBundleEligibilityResult(status, message, hash, signed,
				entry.Index, entry.RelativePath);

		static WindowsBundleEligibilityResult Result(WindowsBundleEligibilityStatus status,
			string message, string? hash, bool signed = false) =>
			new WindowsBundleEligibilityResult(status, message, hash, signed);

		sealed class InspectionLimitException : Exception {
		}

		readonly struct AssemblyIdentity : IEquatable<AssemblyIdentity> {
			readonly string name;
			readonly Version version;
			readonly byte[] publicKeyToken;
			readonly string culture;
			readonly int contentType;

			public AssemblyIdentity(string name, Version version, byte[] publicKeyToken,
				string culture, int contentType) {
				this.name = name;
				this.version = version;
				this.publicKeyToken = publicKeyToken;
				this.culture = culture;
				this.contentType = contentType;
			}

			public bool Equals(AssemblyIdentity other) =>
				StringComparer.OrdinalIgnoreCase.Equals(name, other.name) &&
				version.Equals(other.version) &&
				publicKeyToken.AsSpan().SequenceEqual(other.publicKeyToken) &&
				StringComparer.Ordinal.Equals(culture, other.culture) &&
				contentType == other.contentType;

			public override bool Equals(object? obj) =>
				obj is AssemblyIdentity other && Equals(other);

			public override int GetHashCode() {
				int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name);
				hash = unchecked(hash * 31 + version.GetHashCode());
				hash = unchecked(hash * 31 + GetByteHashCode(publicKeyToken));
				hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(culture));
				return unchecked(hash * 31 + contentType);
			}

			static int GetByteHashCode(byte[] bytes) {
				int hash = 17;
				foreach (byte value in bytes)
					hash = unchecked(hash * 31 + value);
				return hash;
			}
		}

		readonly struct PeInspection {
			public PeInspection(Machine machine, bool signed, bool malformedCertificateTable,
				bool isNativeAot) {
				Machine = machine;
				HasAuthenticodeSignature = signed;
				MalformedCertificateTable = malformedCertificateTable;
				IsNativeAot = isNativeAot;
			}
			public Machine Machine { get; }
			public bool HasAuthenticodeSignature { get; }
			public bool MalformedCertificateTable { get; }
			public bool IsNativeAot { get; }
		}
	}
}
