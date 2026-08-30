// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class ModuleSerializationServiceTests {
		[Fact]
		public void IdenticalOptionsWriteReopenableFileAndCallerOwnedStream() {
			using var fixture = ModuleSerializationTestFixture.Create();
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				object fileOptions = fixture.CreateOptions();
				object streamOptions = fixture.CreateOptions();
				ModuleSerializationTestFixture.WriteToFile(fileOptions, filename);
				using var stream = new MemoryStream();
				ModuleSerializationTestFixture.WriteToStream(streamOptions, stream);

				Assert.True(stream.CanRead);
				Assert.True(stream.CanWrite);
				Assert.True(stream.Length > 0);
				Assert.True(File.Exists(filename));

				using ModuleDefMD fileModule = ModuleDefMD.Load(File.ReadAllBytes(filename));
				using ModuleDefMD streamModule = ModuleDefMD.Load(stream.ToArray());
				Assert.Equal(fileModule.Name.String, streamModule.Name.String);
				Assert.Equal(fileModule.Assembly?.FullName, streamModule.Assembly?.FullName);
				Assert.Equal(fileModule.Types.Select(a => a.FullName), streamModule.Types.Select(a => a.FullName));
			}
			finally {
				try { File.Delete(filename); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}

	internal sealed class ModuleSerializationTestFixture : IDisposable {
		static readonly Type SaveModuleOptionsType = Assembly.Load("dnSpy.AsmEditor.x").GetType(
			"dnSpy.AsmEditor.SaveModule.SaveModuleOptionsVM", throwOnError: true)!;
		static readonly Type SerializationServiceType = Assembly.Load("dnSpy.AsmEditor.x").GetType(
			"dnSpy.AsmEditor.SaveModule.ModuleSerializationService", throwOnError: true)!;

		readonly DsDotNetDocument document;

		ModuleSerializationTestFixture(DsDotNetDocument document) {
			this.document = document;
		}

		public static ModuleSerializationTestFixture Create() {
			var module = new ModuleDefUser("SerializationFixture.dll") {
				Kind = ModuleKind.Dll,
			};
			var assembly = new AssemblyDefUser("SerializationFixture", new Version(1, 2, 3, 4));
			assembly.Modules.Add(module);
			string source = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			module.Location = source;
			var document = DsDotNetDocument.CreateAssembly(
				DsDocumentInfo.CreateDocument(source), module, loadSyms: false);
			return new ModuleSerializationTestFixture(document);
		}

		public object CreateOptions() => Activator.CreateInstance(SaveModuleOptionsType, document)!;

		public static void WriteToFile(object options, string filename) {
			Invoke("WriteToFile", options, filename, null);
		}

		public static void WriteToStream(object options, Stream stream) {
			Invoke("WriteToStream", options, stream, null);
		}

		static void Invoke(string methodName, object options, object target, EventHandler? progressUpdated) {
			MethodInfo method = SerializationServiceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.Single(a => a.Name == methodName);
			try {
				method.Invoke(null, new object?[] { options, target, DummyLogger.NoThrowInstance, progressUpdated });
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		public void Dispose() {
			document.Dispose();
		}
	}
}
