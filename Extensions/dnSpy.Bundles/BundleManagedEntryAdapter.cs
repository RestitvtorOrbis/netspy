// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using dnlib.DotNet;
using dnlib.PE;

namespace dnSpy.Bundles.Extension {
	/// <summary>
	/// Adapts one validated bundle entry to dnlib's byte-backed PE reader.
	/// </summary>
	/// <remarks>
	/// This adapter is intentionally per-entry. It opens a bounded logical stream only when a
	/// caller activates that entry, materializes that entry's validated logical size, and never
	/// reads or extracts neighboring entries.
	/// </remarks>
	public static class BundleManagedEntryAdapter {
		/// <summary>Reads one managed entry into a bounded byte array.</summary>
		public static byte[] ReadLogicalBytes(BundleDsDocument bundleDocument, BundleEntry entry) {
			if (bundleDocument is null)
				throw new ArgumentNullException(nameof(bundleDocument));
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));
			if (entry.FileType != BundleFileType.Assembly)
				throw new ArgumentException("The bundle entry is not a managed assembly.", nameof(entry));

			// BundleReader validates this limit before an entry reaches the extension. Repeat the
			// check here because this is the allocation boundary and BundleFile can also be built by
			// callers in tests.
			if (entry.Size < 0 || entry.Size > BundleReaderOptions.DefaultMaximumEntrySize)
				throw new InvalidDataException("The managed bundle entry exceeds the configured read limit.");
			if (entry.Size > int.MaxValue)
				throw new InvalidDataException("The managed bundle entry cannot be materialized in memory.");

			byte[] bytes = new byte[(int)entry.Size];
			using (Stream stream = bundleDocument.OpenLogicalRead(entry)) {
				int position = 0;
				while (position < bytes.Length) {
					int read = stream.Read(bytes, position, bytes.Length - position);
					if (read <= 0)
						throw new InvalidDataException("The managed bundle entry ended before its declared logical length.");
					position = checked(position + read);
				}

				// BoundedReadStream and ExactLengthReadStream both enforce their limit. The explicit
				// probe also protects the injectable stream seam used by integration tests and keeps
				// this adapter correct if a future stream implementation does not probe on its own.
				if (stream.ReadByte() >= 0)
					throw new InvalidDataException("The managed bundle entry exceeds its declared logical length.");
			}
			return bytes;
		}

		/// <summary>
		/// Loads one selected managed entry as a verified file-layout <see cref="ModuleDefMD"/>.
		/// </summary>
		public static ModuleDefMD LoadModule(BundleDsDocument bundleDocument, BundleEntry entry) {
			if (bundleDocument is null)
				throw new ArgumentNullException(nameof(bundleDocument));
			if (entry is null)
				throw new ArgumentNullException(nameof(entry));

			byte[] bytes = ReadLogicalBytes(bundleDocument, entry);
			string displayName = BundleFolderDocument.GetSyntheticFilename(bundleDocument, entry.RelativePath);
			IPEImage peImage;
			try {
				peImage = new PEImage(bytes, displayName, ImageLayout.File, verify: true);
			}
			catch {
				// PEImage owns no external resource at this point; keeping this block makes the
				// ownership boundary explicit and leaves room for future reader changes.
				throw;
			}

			try {
				ModuleContext moduleContext = bundleDocument.CreateModuleContext();
				var options = new ModuleCreationOptions(moduleContext) {
					TryToLoadPdbFromDisk = false,
				};
				ModuleDefMD module = ModuleDefMD.Load(peImage, options);
				// A contained module must never be offered as a physical source file. This is also
				// what makes the existing Save Module command select Save As instead of the bundle.
				module.Location = string.Empty;
				return module;
			}
			catch {
				peImage.Dispose();
				throw;
			}
		}
	}
}
