// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Bundles;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Menus;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	/// <summary>Focused coverage for the Save Bundle As command and workspace-save contract.</summary>
	public sealed class SaveBundleAsCommandTests {
		[Fact]
		public void SaveBundleAsPublishesToNewPathAndMarksWorkspaceClean() {
			string source = FindCompressedFixture();
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source));
			try {
				BundleOpenResult opened = new BundleReader().Open(source);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), opened.Bundle!);
				BundleEntry entry = document.Bundle.Entries.First(a => a.FileType == BundleFileType.Assembly);
				byte[] current = entry.ReadAllBytes(entry.Size);
				document.Workspace.SetReplacement(entry, current, new BundleReplacementInfo("Save As test"));
				Assert.True(document.HasPendingChanges);

				var messageBox = new RecordingMessageBoxService();
				var service = new BundleSaveAsService(CreateAppWindow(), messageBox,
					new NullSaveFilename(), (workspace, path, cancellationToken) => {
						cancellationToken.ThrowIfCancellationRequested();
						File.Copy(workspace.Bundle.Filename, path);
						return Path.GetFullPath(path);
					});

				Assert.True(service.SaveBundleAs(document, destination, TestContext.Current.CancellationToken));
				Assert.True(File.Exists(destination));
				Assert.False(document.HasPendingChanges);
				Assert.Equal(Path.GetFullPath(destination), document.LastSavedBundleFilename);
				Assert.Equal(BundleWorkspaceEntryState.Unchanged, document.Workspace.GetEntryState(entry));
				Assert.Equal(current, Read(document.Workspace.OpenCurrentRead(entry)));
				Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(source)));
				Assert.Empty(messageBox.Messages);
			}
			finally {
				TryDelete(destination);
			}
		}

		[Fact]
		public void SaveBundleAsRejectsSourcePathBeforePublishing() {
			string source = FindCompressedFixture();
			BundleOpenResult opened = new BundleReader().Open(source);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), opened.Bundle!);
			bool published = false;
			var messageBox = new RecordingMessageBoxService();
			var service = new BundleSaveAsService(CreateAppWindow(), messageBox,
				new NullSaveFilename(), (_, _, _) => {
					published = true;
					return source;
				});

		Assert.False(service.SaveBundleAs(document, source, TestContext.Current.CancellationToken));
			Assert.False(published);
			Assert.Contains(messageBox.Messages, message => message.Contains(
				"must differ from the source", StringComparison.OrdinalIgnoreCase));
			Assert.Null(document.LastSavedBundleFilename);
		}

		[Fact]
		public void AuthenticodeWarningCancelsWithoutPublishing() {
			string source = CopyWithCertificateTable(FindCompressedFixture());
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			try {
				BundleOpenResult opened = new BundleReader().Open(source);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), opened.Bundle!);
				bool published = false;
				var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.No };
				var service = new BundleSaveAsService(CreateAppWindow(), messageBox,
					new NullSaveFilename(), (_, _, _) => {
						published = true;
						return destination;
					});

				Assert.False(service.SaveBundleAs(document, destination, TestContext.Current.CancellationToken));
				Assert.False(published);
				Assert.DoesNotContain(messageBox.Messages, message => message.StartsWith("Unable to", StringComparison.Ordinal));
				Assert.Contains(messageBox.Messages, message => message.Contains(
					"invalidate its Authenticode signature", StringComparison.Ordinal));
				Assert.False(File.Exists(destination));
			}
			finally {
				TryDelete(source);
				TryDelete(destination);
			}
		}

		[Fact]
		public void CancellationLeavesWorkspaceDirtyAndDoesNotPublish() {
			string source = FindCompressedFixture();
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			try {
				BundleOpenResult opened = new BundleReader().Open(source);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), opened.Bundle!);
				BundleEntry entry = document.Bundle.Entries.First(a => a.FileType == BundleFileType.Assembly);
				document.Workspace.SetReplacement(entry, entry.ReadAllBytes(entry.Size),
					new BundleReplacementInfo("cancel test"));
				var messageBox = new RecordingMessageBoxService();
				var service = new BundleSaveAsService(CreateAppWindow(), messageBox,
					new NullSaveFilename(), (_, _, cancellationToken) => {
						cancellationToken.ThrowIfCancellationRequested();
						throw new InvalidOperationException("The canceled operation reached the publisher.");
					});

				using var cancellation = new CancellationTokenSource();
				cancellation.Cancel();
				Assert.False(service.SaveBundleAs(document, destination, cancellation.Token));
				Assert.True(document.HasPendingChanges);
				Assert.Null(document.LastSavedBundleFilename);
				Assert.False(File.Exists(destination));
			}
			finally {
				TryDelete(destination);
			}
		}

		[Fact]
		public void CloseGuardUsesComposedSaveBundleAsService() {
			var bundle = DispatchProxy.Create<IDsBundleDocument, DirtyBundleProxy>();
			((DirtyBundleProxy)(object)bundle).Dirty = true;
			var saver = new RecordingBundleSaver();
			var messageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Yes };
			var guard = new BundleDocumentCloseGuard(messageBox,
				new[] { new Lazy<IBundleWorkspaceSaveService>(() => saver) });

			Assert.True(guard.CanClose(new IDsDocument[] { bundle }, DsDocumentCloseReason.Remove));
			Assert.Same(bundle, saver.Document);
			Assert.Single(messageBox.Messages);
		}

		[Fact]
		public void SaveThenRevertOneRestoresSourceAndBecomesDirtyAgain() {
			string source = FindCompressedFixture();
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			try {
				BundleOpenResult opened = new BundleReader().Open(source);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), opened.Bundle!);
				BundleEntry entry = document.Bundle.Entries.First(a => a.FileType == BundleFileType.Assembly);
				byte[] original = Read(entry.OpenLogicalRead());
				document.Workspace.SetReplacement(entry, original, new BundleReplacementInfo("save/revert"));
				var messageBox = new RecordingMessageBoxService();
				var service = CreateCopyingService(messageBox);

				Assert.True(service.SaveBundleAs(document, destination, TestContext.Current.CancellationToken));
				var entryDocument = new BundleEntryDocument(
					new BundleFolderDocument(document, BundleFolderKind.Assemblies), entry);
				Assert.False(document.HasPendingChanges);
				Assert.True(document.Workspace.HasSavedReplacements);
				Assert.True(IsRevertCommandEnabled(entryDocument));
				Assert.True(IsRevertAllCommandEnabled(document));

				Assert.True(document.Workspace.Revert(entry));
				Assert.Equal(original, Read(document.Workspace.OpenCurrentRead(entry)));
				Assert.Equal(BundleWorkspaceEntryState.Reverted, entryDocument.WorkspaceState);
				Assert.True(document.HasPendingChanges);
				Assert.False(document.HasWorkspaceErrors);

				var closeMessageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Cancel };
				var guard = new BundleDocumentCloseGuard(closeMessageBox);
				Assert.False(guard.CanClose(new IDsDocument[] { document }, DsDocumentCloseReason.Remove));
				Assert.Single(closeMessageBox.Messages);
				Assert.False(IsRevertCommandEnabled(entryDocument));
			}
			finally {
				TryDelete(destination);
			}
		}

		[Fact]
		public void SaveThenRevertAllRestoresSourcesAndRemainsDirty() {
			string source = FindCompressedFixture();
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			string resavedDestination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			try {
				BundleOpenResult opened = new BundleReader().Open(source);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				using var document = new BundleDsDocument(DsDocumentInfo.CreateDocument(source), opened.Bundle!);
				BundleEntry[] entries = document.Bundle.Entries
					.Where(a => a.FileType == BundleFileType.Assembly).Take(2).ToArray();
				Assert.Equal(2, entries.Length);
				var originals = entries.ToDictionary(entry => entry, entry => Read(entry.OpenLogicalRead()));
				foreach (BundleEntry entry in entries)
					document.Workspace.SetReplacement(entry, originals[entry], new BundleReplacementInfo("save/revert all"));
				var messageBox = new RecordingMessageBoxService();
				var service = CreateCopyingService(messageBox);

				Assert.True(service.SaveBundleAs(document, destination, TestContext.Current.CancellationToken));
				Assert.False(document.HasPendingChanges);
				Assert.True(document.Workspace.HasSavedReplacements);
				Assert.True(IsRevertCommandEnabled(new BundleEntryDocument(
					new BundleFolderDocument(document, BundleFolderKind.Assemblies), entries[0])));
				Assert.True(IsRevertAllCommandEnabled(document));

				document.Workspace.RevertAll();
				Assert.True(document.HasPendingChanges);
				foreach (BundleEntry entry in entries) {
					Assert.Equal(originals[entry], Read(document.Workspace.OpenCurrentRead(entry)));
					Assert.Equal(BundleWorkspaceEntryState.Reverted,
						document.Workspace.GetEntryState(entry));
				}

				var closeMessageBox = new RecordingMessageBoxService { Result = MsgBoxButton.Cancel };
				var guard = new BundleDocumentCloseGuard(closeMessageBox);
				Assert.False(guard.CanClose(new IDsDocument[] { document }, DsDocumentCloseReason.Remove));
				Assert.Single(closeMessageBox.Messages);

				var savedEvents = new List<BundleWorkspaceChangedEventArgs>();
				document.Workspace.Changed += (_, change) => savedEvents.Add(change);
				Assert.True(service.SaveBundleAs(document, resavedDestination,
					TestContext.Current.CancellationToken));
				Assert.False(document.HasPendingChanges);
				Assert.False(document.Workspace.HasSavedReplacements);
				Assert.Equal(Path.GetFullPath(resavedDestination), document.LastSavedBundleFilename);
				Assert.All(entries, entry => Assert.Equal(BundleWorkspaceEntryState.Unchanged,
					document.Workspace.GetEntryState(entry)));
				Assert.Equal(entries.Length, savedEvents.Count);
				Assert.All(savedEvents, change => {
					Assert.True(change.IsSaved);
					Assert.Null(change.ReplacementInfo);
				});
			}
			finally {
				TryDelete(destination);
				TryDelete(resavedDestination);
			}
		}

		[Fact]
		public void SaveBundleAsCommandsAreExportedForFileAndDocumentContextMenus() {
			Assembly assembly = typeof(BundleSaveAsService).Assembly;
			var exports = assembly.GetTypes()
				.SelectMany(type => type.GetCustomAttributes<ExportMenuItemAttribute>()
					.Select(attribute => (type, attribute)))
				.Where(item => item.attribute.Header == "Save Bundle As...")
				.ToArray();

			Assert.Equal(2, exports.Length);
			Assert.Contains(exports, item => item.attribute.OwnerGuid == MenuConstants.APP_MENU_FILE_GUID &&
				item.attribute.Group == MenuConstants.GROUP_APP_MENU_FILE_SAVE);
			Assert.Contains(exports, item => item.attribute.OwnerGuid is null &&
				item.attribute.Group == MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_MISC);
		}

		static string FindCompressedFixture() {
			string? configured = Environment.GetEnvironmentVariable("DNSPY_BUNDLE_FIXTURES");
			var roots = new List<string>();
			if (!String.IsNullOrWhiteSpace(configured))
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

		static byte[] Read(Stream stream) {
			using (stream)
			using (var output = new MemoryStream()) {
				stream.CopyTo(output);
				return output.ToArray();
			}
		}

		static BundleSaveAsService CreateCopyingService(RecordingMessageBoxService messageBox) =>
			new BundleSaveAsService(CreateAppWindow(), messageBox, new NullSaveFilename(),
				(workspace, path, cancellationToken) => {
					cancellationToken.ThrowIfCancellationRequested();
					File.Copy(workspace.Bundle.Filename, path);
					return Path.GetFullPath(path);
				});

		static bool IsRevertCommandEnabled(BundleEntryDocument entryDocument) {
			Type commandType = typeof(BundleSaveAsService).Assembly.GetType(
				"dnSpy.Bundles.Extension.BundleWorkspaceEntryMenuCommand", throwOnError: true)!;
			MethodInfo method = commandType.GetMethod("CanRevert",
				BindingFlags.Static | BindingFlags.NonPublic)!;
			return (bool)method.Invoke(null, new object?[] { entryDocument })!;
		}

		static bool IsRevertAllCommandEnabled(BundleDsDocument document) {
			Type commandType = typeof(BundleSaveAsService).Assembly.GetType(
				"dnSpy.Bundles.Extension.RevertAllBundleChangesMenuCommand", throwOnError: true)!;
			MethodInfo method = commandType.GetMethod("CanRevertAll",
				BindingFlags.Static | BindingFlags.NonPublic)!;
			return (bool)method.Invoke(null, new object?[] { document })!;
		}

		static string CopyWithCertificateTable(string source) {
			string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
			byte[] bytes = File.ReadAllBytes(source);
			int peOffset = checked((int)ReadUInt32(bytes, 0x3C));
			ushort optionalHeaderMagic = ReadUInt16(bytes, peOffset + 24);
			int dataDirectory = peOffset + 24 + (optionalHeaderMagic == 0x20B ? 112 : 96);
			int certificateDirectory = checked(dataDirectory + 8 * 4);
			int certificateOffset = bytes.Length;
			Array.Resize(ref bytes, bytes.Length + 8);
			WriteUInt32(bytes, certificateDirectory, (uint)certificateOffset);
			WriteUInt32(bytes, certificateDirectory + 4, 8);
			File.WriteAllBytes(destination, bytes);
			return destination;
		}

		static ushort ReadUInt16(byte[] bytes, int offset) => (ushort)(bytes[offset] | bytes[offset + 1] << 8);
		static uint ReadUInt32(byte[] bytes, int offset) =>
			(uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
		static void WriteUInt32(byte[] bytes, int offset, uint value) {
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
			bytes[offset + 2] = (byte)(value >> 16);
			bytes[offset + 3] = (byte)(value >> 24);
		}

		static IAppWindow CreateAppWindow() => DispatchProxy.Create<IAppWindow, NullAppWindowProxy>();

		static void TryDelete(string filename) {
			try { File.Delete(filename); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		sealed class NullAppWindowProxy : DispatchProxy {
			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
				targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
		}

		sealed class DirtyBundleProxy : DispatchProxy {
			public bool Dirty { get; set; }

			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				switch (targetMethod?.Name) {
				case "get_HasPendingChanges": return Dirty;
				case "get_HasWorkspaceErrors": return false;
				case "get_SourceBundleFilename": return "bundle.exe";
				case "get_Key": return new FilenameKey("bundle.exe");
				}
				if (targetMethod?.ReturnType == typeof(string))
					return string.Empty;
				return targetMethod?.ReturnType is not null && targetMethod.ReturnType.IsValueType
					? Activator.CreateInstance(targetMethod.ReturnType) : null;
			}
		}

		sealed class RecordingBundleSaver : IBundleWorkspaceSaveService {
			public IDsBundleDocument? Document { get; private set; }

			public bool SaveBundleAs(IDsBundleDocument document) {
				Document = document;
				return true;
			}
		}

		sealed class NullSaveFilename : IPickSaveFilename {
			public string? GetFilename(string? currentFileName, string? defaultExtension, string? filter = null) => null;
		}

		sealed class RecordingMessageBoxService : IMessageBoxService {
			public MsgBoxButton Result { get; set; } = MsgBoxButton.OK;
			public List<string> Messages { get; } = new List<string>();

			public MsgBoxButton? ShowIgnorableMessage(Guid guid, string message,
				MsgBoxButton buttons = MsgBoxButton.OK, Window? ownerWindow = null) {
				Messages.Add(message);
				return Result;
			}

			public MsgBoxButton Show(string message, MsgBoxButton buttons = MsgBoxButton.OK,
				Window? ownerWindow = null) {
				Messages.Add(message);
				return Result;
			}

			public T? Ask<T>(string labelMessage, string? defaultText = null, string? title = null,
				Func<string, T>? converter = null, Func<string, string?>? verifier = null,
				Window? ownerWindow = null) => default;

			public void Show(Exception exception, string? msg = null, Window? ownerWindow = null) =>
				Messages.Add(msg ?? exception.Message);
		}
	}
}
