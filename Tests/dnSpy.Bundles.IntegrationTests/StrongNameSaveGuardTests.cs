// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using dnlib.PE;
using dnSpy.Contracts.Documents;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class StrongNameSaveGuardTests {
		[Fact]
		public void RemoveDispositionWritesUnsignedOutputAndRestoresModel() {
			using var fixture = StrongNameSerializationTestFixture.Create();
			var module = fixture.Module;
			AssemblyDef assembly = module.Assembly!;
			AssemblyAttributes originalAttributes = assembly.Attributes;
			byte[] originalPublicKey = assembly.PublicKey!.Data!.ToArray();
			ComImageFlags originalFlags = module.Cor20HeaderFlags;

			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "Remove", null);
			using var stream = new MemoryStream();
			Assert.True(fixture.WriteToStream(options, stream));

			Assert.Equal(originalAttributes, assembly.Attributes);
			Assert.Equal(originalPublicKey, assembly.PublicKey!.Data);
			Assert.Equal(originalFlags, module.Cor20HeaderFlags);
			using ModuleDefMD output = ModuleDefMD.Load(stream.ToArray());
			Assert.False(output.IsStrongNameSigned);
			Assert.False(output.Assembly!.HasPublicKey);
			Assert.True(output.Assembly.PublicKey is null || output.Assembly.PublicKey.IsNullOrEmpty);
			Assert.True(output.Metadata.ImageCor20Header.StrongNameSignature.VirtualAddress == 0);
			Assert.Equal(0u, output.Metadata.ImageCor20Header.StrongNameSignature.Size);
		}

		[Fact]
		public void CancelDispositionWritesNothingAndLeavesModelUntouched() {
			using var fixture = StrongNameSerializationTestFixture.Create();
			var module = fixture.Module;
			AssemblyDef assembly = module.Assembly!;
			AssemblyAttributes originalAttributes = assembly.Attributes;
			byte[] originalPublicKey = assembly.PublicKey!.Data!.ToArray();
			ComImageFlags originalFlags = module.Cor20HeaderFlags;
			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "Cancel", null);

			using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
			stream.Position = stream.Length;
			Assert.False(fixture.WriteToStream(options, stream));
			Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
			Assert.Equal(originalAttributes, assembly.Attributes);
			Assert.Equal(originalPublicKey, assembly.PublicKey!.Data);
			Assert.Equal(originalFlags, module.Cor20HeaderFlags);
		}

		[Fact]
		public void ReSignDispositionWritesSignedOutputAndRestoresModel() {
			string keyFilename = FindRepositoryFile("dnSpy.snk");
			using var fixture = StrongNameSerializationTestFixture.Create();
			var module = fixture.Module;
			AssemblyDef assembly = module.Assembly!;
			AssemblyAttributes originalAttributes = assembly.Attributes;
			byte[] originalPublicKey = assembly.PublicKey!.Data!.ToArray();
			ComImageFlags originalFlags = module.Cor20HeaderFlags;
			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "ReSign", keyFilename);

			using var stream = new MemoryStream();
			Assert.True(fixture.WriteToStream(options, stream));

			Assert.Equal(originalAttributes, assembly.Attributes);
			Assert.Equal(originalPublicKey, assembly.PublicKey!.Data);
			Assert.Equal(originalFlags, module.Cor20HeaderFlags);
			using ModuleDefMD output = ModuleDefMD.Load(stream.ToArray());
			Assert.True(output.IsStrongNameSigned);
			Assert.True(output.Assembly!.HasPublicKey);
			Assert.False(output.Assembly.PublicKey is null || output.Assembly.PublicKey.IsNullOrEmpty);
			Assert.True(output.Metadata.ImageCor20Header.StrongNameSignature.VirtualAddress != 0);
			Assert.NotEqual(0u, output.Metadata.ImageCor20Header.StrongNameSignature.Size);
			AssertStrongNameSignature(stream.ToArray(), keyFilename);
		}

		[Fact]
		public void PublicKeyOnlyModuleRequiresStrongNameChoice() {
			using var fixture = StrongNameSerializationTestFixture.CreatePublicKeyOnly();
			Assert.True(fixture.IsStrongNameRequired());
		}

		[Fact]
		public void StrongNameFlagOnlyModuleRequiresStrongNameChoice() {
			using var fixture = StrongNameSerializationTestFixture.CreateStrongNameFlagOnly();
			Assert.True(fixture.IsStrongNameRequired());
		}

		[Fact]
		public void CancelDispositionDoesNotCreateOrOverwriteFile() {
			using var fixture = StrongNameSerializationTestFixture.Create();
			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "Cancel", null);
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				Assert.False(fixture.WriteToFile(options, filename));
				Assert.False(File.Exists(filename));

				byte[] sentinel = new byte[] { 9, 8, 7, 6 };
				File.WriteAllBytes(filename, sentinel);
				Assert.False(fixture.WriteToFile(options, filename));
				Assert.Equal(sentinel, File.ReadAllBytes(filename));
			}
			finally {
				try { File.Delete(filename); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void RemoveDispositionWritesUnsignedFileOutput() {
			using var fixture = StrongNameSerializationTestFixture.Create();
			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "Remove", null);
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				Assert.True(fixture.WriteToFile(options, filename));
				using ModuleDefMD output = ModuleDefMD.Load(filename);
				Assert.False(output.IsStrongNameSigned);
				Assert.False(output.Assembly!.HasPublicKey);
				Assert.True(output.Metadata.ImageCor20Header.StrongNameSignature.VirtualAddress == 0);
				Assert.Equal(0u, output.Metadata.ImageCor20Header.StrongNameSignature.Size);
				Assert.True(fixture.Module.IsStrongNameSigned);
				Assert.True(fixture.Module.Assembly!.HasPublicKey);
			}
			finally {
				try { File.Delete(filename); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void ReSignDispositionWritesCryptographicallyValidFileOutput() {
			string keyFilename = FindRepositoryFile("dnSpy.snk");
			using var fixture = StrongNameSerializationTestFixture.Create();
			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "ReSign", keyFilename);
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			try {
				Assert.True(fixture.WriteToFile(options, filename));
				AssertStrongNameSignature(File.ReadAllBytes(filename), keyFilename);
				Assert.True(fixture.Module.IsStrongNameSigned);
				Assert.True(fixture.Module.Assembly!.HasPublicKey);
			}
			finally {
				try { File.Delete(filename); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void RemoveDispositionRestoresOriginalDirectoryWhenWriterFails() {
			using var fixture = StrongNameSerializationTestFixture.CreateLoadedSigned();
			var module = (ModuleDefMD)fixture.Module;
			AssemblyDef assembly = module.Assembly!;
			var originalDirectory = module.Metadata.ImageCor20Header.StrongNameSignature;
			AssemblyAttributes originalAttributes = assembly.Attributes;
			byte[] originalPublicKey = assembly.PublicKey!.Data!.ToArray();
			ComImageFlags originalFlags = module.Cor20HeaderFlags;
			object options = fixture.CreateOptions();
			fixture.SetChoice(options, "Remove", null);

			Assert.Throws<IOException>(() => fixture.WriteToStream(options, new ThrowingStream()));
			Assert.Equal(originalAttributes, assembly.Attributes);
			Assert.Equal(originalPublicKey, assembly.PublicKey!.Data);
			Assert.Equal(originalFlags, module.Cor20HeaderFlags);
			Assert.Equal(originalDirectory.VirtualAddress, module.Metadata.ImageCor20Header.StrongNameSignature.VirtualAddress);
			Assert.Equal(originalDirectory.Size, module.Metadata.ImageCor20Header.StrongNameSignature.Size);
		}

		static string FindRepositoryFile(string name) {
			DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
			while (directory is not null) {
				string filename = Path.Combine(directory.FullName, name);
				if (File.Exists(filename))
					return filename;
				directory = directory.Parent;
			}
			throw new FileNotFoundException(name);
		}

		static void AssertStrongNameSignature(byte[] image, string keyFilename) {
			using ModuleDefMD output = ModuleDefMD.Load(image);
			var directory = output.Metadata.ImageCor20Header.StrongNameSignature;
			Assert.True(directory.VirtualAddress != 0);
			Assert.NotEqual(0u, directory.Size);
			var key = new StrongNameKey(keyFilename);
			byte[] expectedPublicKey = new StrongNamePublicKey(key.PublicKey).CreatePublicKey();
			Assert.Equal(expectedPublicKey, output.Assembly!.PublicKey!.Data);
			uint signatureOffset = (uint)output.Metadata.PEImage.ToFileOffset((RVA)directory.VirtualAddress);
			Assert.True(signatureOffset <= image.Length);
			Assert.True((ulong)signatureOffset + directory.Size <= (ulong)image.Length);
			byte[] actualSignature = image.AsSpan(checked((int)signatureOffset), checked((int)directory.Size)).ToArray();
			var signer = new StrongNameSigner(new MemoryStream(image, writable: false));
			byte[] expectedSignature = signer.CalculateSignature(key, signatureOffset);
			Assert.Equal(expectedSignature, actualSignature);
		}

		sealed class ThrowingStream : Stream {
			public override bool CanRead => false;
			public override bool CanSeek => false;
			public override bool CanWrite => true;
			public override long Length => 0;
			public override long Position { get => 0; set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new IOException("test writer failure");
		}
	}

	internal sealed class StrongNameSerializationTestFixture : IDisposable {
		static readonly Assembly AsmEditorAssembly = Assembly.Load("dnSpy.AsmEditor.x");
		static readonly Type SaveModuleOptionsType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.SaveModule.SaveModuleOptionsVM", throwOnError: true)!;
		static readonly Type SerializationServiceType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.SaveModule.ModuleSerializationService", throwOnError: true)!;
		static readonly Type StrongNameGuardType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.SaveModule.StrongNameSaveGuard", throwOnError: true)!;
		static readonly Type DispositionType = AsmEditorAssembly.GetType(
			"dnSpy.AsmEditor.SaveModule.StrongNameSaveDisposition", throwOnError: true)!;

		readonly DsDotNetDocument document;
		public ModuleDef Module { get; }

		StrongNameSerializationTestFixture(DsDotNetDocument document, ModuleDef module) {
			this.document = document;
			Module = module;
		}

		public static StrongNameSerializationTestFixture Create() => Create(includePublicKey: true, strongNameSigned: true);

		public static StrongNameSerializationTestFixture CreatePublicKeyOnly() =>
			Create(includePublicKey: true, strongNameSigned: false);

		public static StrongNameSerializationTestFixture CreateStrongNameFlagOnly() =>
			Create(includePublicKey: false, strongNameSigned: true);

		static StrongNameSerializationTestFixture Create(bool includePublicKey, bool strongNameSigned) {
			var module = new ModuleDefUser("StrongNameFixture.dll") {
				Kind = ModuleKind.Dll,
				Cor20HeaderFlags = ComImageFlags.ILOnly |
					(strongNameSigned ? ComImageFlags.StrongNameSigned : 0),
			};
			AssemblyDefUser assembly;
			if (includePublicKey) {
				assembly = new AssemblyDefUser("StrongNameFixture", new Version(1, 0, 0, 0),
					new PublicKey(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
				assembly.Attributes |= AssemblyAttributes.PublicKey;
			}
			else
				assembly = new AssemblyDefUser("StrongNameFixture", new Version(1, 0, 0, 0));
			assembly.Modules.Add(module);
			string source = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			module.Location = source;
			var document = DsDotNetDocument.CreateAssembly(
				DsDocumentInfo.CreateDocument(source), module, loadSyms: false);
			return new StrongNameSerializationTestFixture(document, module);
		}

		public static StrongNameSerializationTestFixture CreateLoadedSigned() {
			using var source = Create();
			var writerOptions = new ModuleWriterOptions(source.Module);
			writerOptions.InitializeStrongNameSigning(source.Module,
				new StrongNameKey(FindRepositoryFile("dnSpy.snk")));
			using var stream = new MemoryStream();
			source.Module.Write(stream, writerOptions);
			var module = ModuleDefMD.Load(stream.ToArray());
			string filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
			module.Location = filename;
			var document = DsDotNetDocument.CreateAssembly(
				DsDocumentInfo.CreateDocument(filename), module, loadSyms: false);
			return new StrongNameSerializationTestFixture(document, module);
		}

		static string FindRepositoryFile(string name) {
			DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
			while (directory is not null) {
				string filename = Path.Combine(directory.FullName, name);
				if (File.Exists(filename))
					return filename;
				directory = directory.Parent;
			}
			throw new FileNotFoundException(name);
		}

		public object CreateOptions() => Activator.CreateInstance(SaveModuleOptionsType, document)!;

		public bool IsStrongNameRequired() {
			MethodInfo method = StrongNameGuardType.GetMethod("IsRequired",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
			return (bool)method.Invoke(null, new object?[] { Module })!;
		}

		public void SetChoice(object options, string disposition, string? keyFilename) {
			object dispositionValue = Enum.Parse(DispositionType, disposition);
			SaveModuleOptionsType.GetMethod("SetStrongNameSaveChoice",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(options,
				new object?[] { dispositionValue, keyFilename });
		}

		public bool WriteToStream(object options, Stream stream) {
			MethodInfo method = SerializationServiceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.Single(a => a.Name == "WriteToStream");
			try {
				return (bool)method.Invoke(null, new object?[] { options, stream, DummyLogger.NoThrowInstance, null })!;
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		public bool WriteToFile(object options, string filename) {
			MethodInfo method = SerializationServiceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.Single(a => a.Name == "WriteToFile");
			try {
				return (bool)method.Invoke(null, new object?[] { options, filename, DummyLogger.NoThrowInstance, null })!;
			}
			catch (TargetInvocationException ex) when (ex.InnerException is not null) {
				throw ex.InnerException;
			}
		}

		public void Dispose() => document.Dispose();
	}
}
