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
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace dnSpy.AsmEditor.SaveModule {
	/// <summary>
	/// Serializes a module using the validated options from the Save Module dialog.
	/// </summary>
	internal static class ModuleSerializationService {
		public static void WriteToFile(SaveModuleOptionsVM options, string filename, ILogger logger, EventHandler2<ModuleWriterProgressEventArgs>? progressUpdated) {
			if (options is null)
				throw new ArgumentNullException(nameof(options));
			if (filename is null)
				throw new ArgumentNullException(nameof(filename));
			Write(options, filename, null, logger, progressUpdated);
		}

		public static void WriteToStream(SaveModuleOptionsVM options, Stream stream, ILogger logger, EventHandler2<ModuleWriterProgressEventArgs>? progressUpdated) {
			if (options is null)
				throw new ArgumentNullException(nameof(options));
			if (stream is null)
				throw new ArgumentNullException(nameof(stream));
			if (!stream.CanWrite)
				throw new ArgumentException("The stream must be writable", nameof(stream));
			Write(options, null, stream, logger, progressUpdated);
		}

		static void Write(SaveModuleOptionsVM options, string? filename, Stream? stream, ILogger logger, EventHandler2<ModuleWriterProgressEventArgs>? progressUpdated) {
			var writerOptions = options.CreateWriterOptions();
			if (progressUpdated is not null)
				writerOptions.ProgressUpdated += progressUpdated;
			writerOptions.Logger = logger;
			// Make sure the order of the interfaces don't change, see https://github.com/dotnet/roslyn/issues/3905
			writerOptions.MetadataOptions.Flags |= MetadataFlags.RoslynSortInterfaceImpl;

			if (writerOptions is NativeModuleWriterOptions nativeOptions) {
				if (options.Module is not ModuleDefMD module)
					throw new InvalidOperationException("Native module writing requires a metadata module");
				if (stream is not null)
					module.NativeWrite(stream, nativeOptions);
				else
					module.NativeWrite(filename!, nativeOptions);
			}
			else {
				var managedOptions = (ModuleWriterOptions)writerOptions;
				if (stream is not null)
					options.Module.Write(stream, managedOptions);
				else
					options.Module.Write(filename!, managedOptions);
			}
		}
	}
}
