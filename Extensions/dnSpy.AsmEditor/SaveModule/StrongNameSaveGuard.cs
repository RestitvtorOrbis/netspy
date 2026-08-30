/*
    Copyright (C) 2026 netSpy Single-File contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using dnSpy.AsmEditor.Properties;
using dnSpy.Contracts.App;
using dnSpy.Contracts.MVVM;

namespace dnSpy.AsmEditor.SaveModule {
	internal enum StrongNameSaveDisposition {
		Cancel,
		Remove,
		ReSign,
	}

	/// <summary>
	/// Applies a strong-name save disposition only for the duration of one write.
	/// </summary>
	internal sealed class StrongNameSaveGuard : IDisposable {
		readonly ModuleDef module;
		readonly AssemblyDef? assembly = null;
		readonly AssemblyAttributes originalAssemblyAttributes = 0;
		readonly PublicKey? originalPublicKey = null;
		readonly ComImageFlags originalCor20HeaderFlags = 0;
		readonly bool restoreModule;
		readonly StrongNameKey? strongNameKey = null;
		bool disposed;

		public bool CanWrite { get; }
		public StrongNameSaveDisposition Disposition { get; }

		StrongNameSaveGuard(ModuleDef module, StrongNameSaveDisposition disposition, string? keyFilename) {
			this.module = module ?? throw new ArgumentNullException(nameof(module));
			Disposition = disposition;

			if (!IsRequired(module)) {
				CanWrite = true;
				return;
			}

			switch (disposition) {
			case StrongNameSaveDisposition.Remove:
				assembly = module.Assembly;
				if (assembly is not null) {
					originalAssemblyAttributes = assembly.Attributes;
					originalPublicKey = assembly.PublicKey;
					assembly.Attributes &= ~AssemblyAttributes.PublicKey;
					assembly.PublicKey = null!;
				}
				originalCor20HeaderFlags = module.Cor20HeaderFlags;
				module.Cor20HeaderFlags &= ~ComImageFlags.StrongNameSigned;
				restoreModule = true;
				CanWrite = true;
				break;

			case StrongNameSaveDisposition.ReSign:
				if (string.IsNullOrWhiteSpace(keyFilename))
					return;
				strongNameKey = new StrongNameKey(keyFilename);
				CanWrite = true;
				break;

			default:
				CanWrite = false;
				break;
			}
		}

		public static StrongNameSaveGuard Create(ModuleDef module, StrongNameSaveDisposition disposition, string? keyFilename) =>
			new StrongNameSaveGuard(module, disposition, keyFilename);

		public static bool IsRequired(ModuleDef module) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			var publicKey = module.Assembly?.PublicKey;
			return (publicKey is not null && !publicKey.IsNullOrEmpty) || module.IsStrongNameSigned;
		}

		public void ConfigureWriterOptions(ModuleWriterOptionsBase writerOptions) {
			if (writerOptions is null)
				throw new ArgumentNullException(nameof(writerOptions));
			if (!CanWrite)
				throw new InvalidOperationException("The strong-name save was canceled");

			switch (Disposition) {
			case StrongNameSaveDisposition.Remove:
				writerOptions.Cor20HeaderOptions.Flags &= ~ComImageFlags.StrongNameSigned;
				writerOptions.StrongNameKey = null;
				writerOptions.StrongNamePublicKey = null;
				break;

			case StrongNameSaveDisposition.ReSign:
				writerOptions.InitializeStrongNameSigning(module, strongNameKey!);
				break;
			}
		}

		public void Dispose() {
			if (disposed)
				return;
			disposed = true;
			if (!restoreModule)
				return;

			module.Cor20HeaderFlags = originalCor20HeaderFlags;
			if (assembly is not null) {
				assembly.Attributes = originalAssemblyAttributes;
				assembly.PublicKey = originalPublicKey;
			}
		}

		public static bool TryPrompt(ModuleDef module, Window? ownerWindow, out StrongNameSaveDisposition disposition, out string? keyFilename) {
			if (!IsRequired(module)) {
				disposition = StrongNameSaveDisposition.Cancel;
				keyFilename = null;
				return true;
			}

			var result = MsgBox.Instance.Show(dnSpy_AsmEditor_Resources.StrongNameSave_Message,
				MsgBoxButton.Yes | MsgBoxButton.No | MsgBoxButton.Cancel, ownerWindow);
			if (result == MsgBoxButton.Yes) {
				disposition = StrongNameSaveDisposition.Remove;
				keyFilename = null;
				return true;
			}
			if (result != MsgBoxButton.No) {
				disposition = StrongNameSaveDisposition.Cancel;
				keyFilename = null;
				return false;
			}

			var dialog = new OpenFileDialog {
				Filter = PickFilenameConstants.StrongNameKeyFilter,
				RestoreDirectory = true,
				CheckFileExists = true,
			};
			if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(dialog.FileName)) {
				disposition = StrongNameSaveDisposition.Cancel;
				keyFilename = null;
				return false;
			}

			try {
				_ = new StrongNameKey(dialog.FileName);
			}
			catch {
				MsgBox.Instance.Show(string.Format(dnSpy_AsmEditor_Resources.Error_NotSNKFile, dialog.FileName), MsgBoxButton.OK, ownerWindow);
				disposition = StrongNameSaveDisposition.Cancel;
				keyFilename = null;
				return false;
			}

			disposition = StrongNameSaveDisposition.ReSign;
			keyFilename = dialog.FileName;
			return true;
		}
	}
}
