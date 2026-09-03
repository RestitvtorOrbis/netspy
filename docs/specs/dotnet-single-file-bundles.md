# Official .NET Single-File Bundle Support

Status: specification ready for independent review
Repository baseline: `1f920a801` (`master`)
Specification date: 2026-08-29

## 1. Outcome

dnSpyEx will recognize official .NET single-file applications, expose their contents without normally extracting them to disk, and load embedded managed assemblies into the existing dnlib/decompiler/editor pipeline. A bundled managed module can be saved independently. A later workspace operation can record serialized module replacements and rebuild a new Windows PE bundle without modifying the source executable.

This specification refines the repository's `pre-spec.md` and the six `tickets/PR-01` through `PR-06` phases. It is the decision-complete companion to the required repository-facing design at `docs/single-file-bundle-design.md`; the companion points here for normative detail. Neither document replaces the supplied tickets or changes their ordering. The implementation tickets below split each phase into changes small enough for one focused implementation, review, and commit.

The controlling invariants are:

- A normal managed DLL or EXE follows the existing provider and `DsDocumentService.CreateDocumentCore()` path unchanged.
- Bundle parsing is in a UI-independent library and has no dnlib, decompiler, tree, WPF, Roslyn, or editor dependency.
- An embedded managed entry becomes a normal `ModuleDefMD`; the existing decompiler, analyzer, Roslyn editor, IL editor, metadata editor, undo system, and dnlib writer remain authoritative.
- Resolution is contextual to the source bundle and never changes unrelated documents.
- Entry reads are bounded. Compressed reads are bounded by both the compressed range and the declared logical size.
- `Save Module As...` and `Save Bundle As...` are different operations.
- Neither operation silently changes strong-name state. Rebuilding never claims to preserve Authenticode.
- The source bundle is never overwritten by the MVP.

## 2. Scope and requirements

### 2.1 Supported inspection and editing scope

The supported bundle manifest generations are:

| Manifest | Runtime generation | Required behavior |
|---|---|---|
| v1 | .NET Core 3.1 | Detect, enumerate, open entries |
| v2 | .NET 5 | Detect, enumerate, open entries and v2 header fields |
| v6 | .NET 6 through .NET 10 | Detect, enumerate, open compressed and uncompressed entries |

For v2/v6 headers the only accepted flag bits are `None = 0` and `NetcoreApp3CompatMode = 0x1`, matching HostModel's private `Manifest.HeaderFlags`. Any other bit returns `BundleOpenStatus.InvalidBundle` with stable code `UnknownManifestFlags` rather than being silently ignored. v1 has no serialized flags and is modeled as `NetcoreApp3CompatMode = true` because .NET Core 3.1 bundles all content for extraction. Its serialized raw file-type byte is format truth: raw `0` is exposed as `BundleFileType.Unknown` and is not rewritten by the parser. During the Windows rebuild preflight only, raw-zero v1 entries may be classified with bounded HostModel-compatible inference; a nonzero v1 raw type is rejected. On rebuild, a parsed v2/v6 compatibility bit maps exactly to `BundleOptions.BundleAllContent`; absence maps to no such option. Compression remains the independent `BundleOptions.EnableCompression` choice and is legal only for v6.

Both framework-dependent and self-contained bundles are in scope. Managed assemblies, native binaries, `.deps.json`, `.runtimeconfig.json`, PDBs, and unknown official manifest file-type values are represented. Unknown types remain inspectable and retain their raw type byte.

Managed IL entries are decompilable and editable. ReadyToRun means the COR20 `ManagedNativeHeader` directory resolves within the bounded entry and begins with the official little-endian `RTR` signature `0x00525452`; a merely non-zero native-header directory is not enough. R2R entries are inspectable/decompilable, but applying a modified R2R module to a bundle workspace and rebuilding it is disabled until dedicated rewrite tests exist. NativeAOT executables are not bundle containers containing conventional editable IL. For Windows PE, high-confidence NativeAOT identification means: no COR20 managed header and a valid PE export named `DotNetRuntimeDebugHeader` or `DotNetRuntimeContractDescriptor`, the runtime diagnostic exports used by NativeAOT. Such a file gets an explanatory unsupported result; every other native PE keeps existing behavior. ELF/Mach-O NativeAOT identification is deferred.

The initial rebuild scope is official Windows PE `win-x64` bundles. Other Windows architectures are detected and rejected with a precise unsupported-architecture message until covered by an execution test. Linux ELF and macOS Mach-O bundles remain readable; rebuilding them is not part of this specification.

### 2.2 Preservation requirements

- Existing DLL/EXE loading, editing, analysis, saving, and debugger behavior must remain covered by regression tests whenever the relevant pipeline changes.
- Opening a bundle must not eagerly load all embedded assemblies or materialize all files.
- Opening or editing must not write to the source executable.
- Standalone module save writes only the selected module and does not update the bundle workspace.
- Applying a module to the workspace serializes to memory first, then atomically installs the complete replacement.
- Revert restores the original logical entry without reopening the application.
- Rebuild preserves entry order, relative paths, logical content, official file type, required bundle flags, config files, native files, symbols, and whether compression was enabled at bundle level. HostModel may choose not to compress an individual entry whose compression ratio is insufficient; byte-for-byte compressed representation is not promised.
- The rebuilt output is parsed with this project's reader before it is offered as successful.

### 2.3 Security requirements

Bundle inputs are untrusted. Parser limits live in `BundleReaderOptions` and have secure defaults:

| Limit | Default | Rationale |
|---|---:|---|
| Signature search | 32 MiB | Official marker is in apphost; bounds work on arbitrary files |
| Manifest entries | 100,000 | Far above normal applications; bounds allocation/work |
| Bundle ID UTF-8 bytes | 16,383 | Matches official HostModel writer string limit |
| Relative-path UTF-8 bytes | 16,383 | Matches official HostModel writer string limit |
| One logical entry | 2 GiB | Bounds decompression and byte materialization |
| Total declared logical bytes | 16 GiB | Bounds malicious aggregate manifests |

The 8 MiB text-preview limit is not a parser option: it lives in the extension-only `BundleTextViewOptions.MaximumPreviewBytes`. The text node checks it before allocation and reports truncation. This keeps the UI-independent parser free of presentation policy while resolving the limit at the only consumer that needs it.

The reader must reject:

- a signature before the preceding 8-byte header pointer can fit;
- a zero, negative, overflowing, pre-marker, or out-of-file header offset;
- unsupported major versions (including skipped 3, 4, and 5);
- any v2/v6 manifest flag outside `NetcoreApp3CompatMode (0x1)`;
- negative or excessive file counts;
- invalid 7-bit string lengths, invalid UTF-8, NUL characters, rooted paths, empty paths, `.` or `..` path segments, and backslash/forward-slash traversal variants;
- exact duplicate relative paths (ordinal comparison after normalizing `\` to `/`);
- negative offsets/sizes, checked-add overflow, physical ranges beyond EOF, entry data overlapping another non-empty entry or the manifest, inconsistent compressed sizes, and excessive logical totals;
- inconsistent v2 deps/runtimeconfig ranges when the corresponding entry is present;
- truncated manifests and unknown file types only when their raw byte cannot be represented (all byte values are otherwise retained as `Unknown`);
- malformed Deflate data, output shorter or longer than declared `Size`, or output beyond a configured logical limit.

Different relative paths with the same assembly simple name are legal input. They are handled as a resolution ambiguity, not rejected by the parser.

`OpenLogicalRead()` never returns the underlying unbounded file stream. An uncompressed entry uses a view limited to `[Offset, Offset + Size)`. A compressed entry uses a view limited to `[Offset, Offset + CompressedSize)`, `DeflateStream`, and an exact-length validating wrapper limited to `Size`. Reading one additional byte after the declared logical size is used to detect overrun before successful completion is reported.

No parser path allocates a byte array sized directly from an unvalidated file field. `ReadAllBytes()` is an explicit convenience operation that checks both the entry and caller-provided maximum first.

### 2.4 Non-goals

- Replacing or updating the ILSpy submodule.
- Migrating dnSpyEx to current ILSpy.
- A new decompiler, C#/VB editor, IL editor, metadata editor, or project-rebuild workflow.
- Reconstructing source projects or calling `dotnet publish` to save a user bundle.
- A custom production bundle writer.
- In-place bundle patching or overwriting the original bundle.
- Normal-path extraction of the entire bundle to `%TEMP%`.
- Third-party packers, obfuscators, Fody packers, or arbitrary self-extractors.
- Reliable ReadyToRun rewriting, NativeAOT editing, macOS signing, or non-Windows rebuild.
- Preserving an Authenticode signature after changing bytes.

## 3. Repository findings and exact extension points

### 3.1 Loading and document creation

The real file-open path is:

```text
DefaultDsDocumentLoader / DsDocumentLoader
    -> IDsDocumentService.TryGetOrCreate(DsDocumentInfo)
    -> DsDocumentService.TryCreateKey()
    -> ordered IDsDocumentProvider exports
    -> DsDocumentService.TryCreateDocument()
    -> DefaultDsDocumentProvider
    -> IDsDocumentService.CreateDocument(filename)
    -> DsDocumentService.CreateDocumentCore()
    -> PEImage
    -> ModuleDefMD.Load(peImage, ModuleCreationOptions) or DsPEDocument
```

Relevant existing files and contracts:

- `dnSpy/dnSpy.Contracts.DnSpy/Documents/IDsDocumentProvider.cs`: narrow MEF document-provider extension point; providers are thread-safe and ordered.
- `dnSpy/dnSpy.Contracts.DnSpy/Documents/DocumentConstants.cs`: the default provider is last (`double.MaxValue`).
- `dnSpy/dnSpy/Documents/DefaultDsDocumentProvider.cs`: normal file/GAC/in-memory provider.
- `dnSpy/dnSpy/Documents/DsDocumentService.cs`: provider iteration and the only existing on-disk/byte-array `PEImage` to `ModuleDefMD` creation path.
- `dnSpy/dnSpy.Contracts.DnSpy/Documents/DsDocument.cs`: `DsDocument`, lazy `Children`, `DsDotNetDocumentBase`, `CreateModuleContext()`, and existing managed/PE documents.
- `dnSpy/dnSpy.Contracts.DnSpy/Documents/DsDocumentInfo.cs`: persisted top-level file identity and current in-memory byte delegate.
- `dnSpy/dnSpy.Contracts.DnSpy/Documents/FilenameKey.cs`: top-level source bundle identity.

The bundle integration is a new extension, `Extensions/dnSpy.Bundles`, not a special case in `DsDocumentService.CreateDocumentCore()`. Its `BundleDsDocumentProvider` is exported before the default provider. It probes only existing files with a recognized PE, ELF, or Mach-O executable magic, then returns `null` for `NotBundle`, a `BundleDsDocument` for `Success`, and a visible `BundleErrorDocument` for an executable containing an official marker but an invalid/unsupported manifest. Consequently ordinary files still reach `DefaultDsDocumentProvider` unchanged, and arbitrary text/data files are not scanned.

For a candidate executable file, `CreateKey()` returns `FilenameKey`; this is the same key returned by the bundle root and by the default provider. It does not parse or retain a handle. `Create()` performs the one bundle probe. Non-file document infos and files without executable magic return `null` from both methods.

### 3.2 Tree and non-managed views

Relevant existing contracts:

- `IDsDocument.Children` and `DsDocument.CreateChildren()` provide lazy document hierarchy.
- `dnSpy/dnSpy.Contracts.DnSpy/Documents/TreeView/IDsDocumentNodeProvider.cs` is the ordered tree-node extension point.
- `dnSpy/dnSpy/Documents/TreeView/DocumentTreeView.cs::CreateNode()` tries providers, then creates `UnknownDocumentNodeImpl`.
- `DefaultDsDocumentNodeProvider` creates existing `AssemblyDocumentNodeImpl`, `ModuleDocumentNodeImpl`, and `PEDocumentNodeImpl` nodes.
- A custom node can implement `IDecompileSelf`; `Extensions/Examples/Example2.Extension/NewDsDocument.cs` is the repository example.

`BundleDsDocument` lazily creates four `BundleFolderDocument` children: Assemblies, Runtime, Native, and Symbols/Other. Folder expansion creates entry documents. Expanding Assemblies creates one `BundleModuleDocument` per selected managed entry and wraps it with the existing public `DsDotNetDocument.CreateAssembly(IDsDotNetDocument)` helper. The wrapper is annotated with its bundle origin. Because `DefaultDsDocumentNodeProvider` intentionally treats every child document as a module when `owner != null`, the bundle node provider creates a narrow `BundleAssemblyDocumentNode : AssemblyDocumentNode` for the annotated wrapper; its child is then created by the normal provider as `ModuleDocumentNodeImpl`. This preserves the assembly/module shape required by existing editor commands while loading only expanded entries.

Runtime JSON nodes implement `IDecompileSelf` and show a bounded UTF-8 preview. Native, symbol, and unknown nodes show metadata (path, type, logical/compressed sizes) without pretending to be managed modules. No temporary file is needed for these views.

### 3.3 Managed module creation and identity

For a managed entry:

```text
BundleEntry.OpenLogicalRead()
    -> bounded/decompressed stream
    -> bounded byte[] (only this selected managed entry)
    -> PEImage(byte[], synthetic display name, ImageLayout.File, verify: true)
    -> ModuleDefMD.Load(peImage, ModuleCreationOptions)
    -> BundleModuleDocument : DsDotNetDocumentBase
    -> DsDotNetDocument.CreateAssembly(BundleModuleDocument)
    -> BundleAssemblyDocumentNode / existing ModuleDocumentNodeImpl
```

dnlib's current load API used by dnSpy is byte-array or `IPEImage` based. Materializing one selected managed module is therefore the narrow adapter; it does not materialize unrelated entries. `ModuleDef.Location` is set to the empty string after load so the existing save dialog behaves as Save As and can never default to the source executable. `BundleModuleDocument.Filename` remains a synthetic display identity of the form `<bundle-full-path>!/<normalized-relative-path>`.

New cross-extension contracts in `dnSpy.Contracts.DnSpy/Documents/Bundles` are limited to:

```csharp
public interface IDsBundleDocument : IDsDocument {
    string SourceBundleFilename { get; }
    bool HasPendingChanges { get; }
}

public interface IDsBundleEntryDocument : IDsDotNetDocument {
    IDsBundleDocument BundleDocument { get; }
    string BundleRelativePath { get; }
    bool HasWorkspaceReplacement { get; }
    bool IsReadyToRun { get; }
    void SetWorkspaceReplacement(byte[] bytes);
    void RevertWorkspaceReplacement();
}
```

The implementation validates that replacement bytes are a managed PE before installing them. These interfaces expose only the behavior needed by the separately built assembly-editor extension; parser types do not enter dnSpy's broad contracts.

### 3.4 Contextual assembly resolution

The existing global resolver is `dnSpy/dnSpy/Documents/AssemblyResolver.cs`, installed by `DsDocumentService` in each ordinary module context. Its current behavior includes runtime resolver exports, loaded document lookup, source-directory/config probing, shared runtime paths, GAC, and disk fallbacks. `IDsDocumentService.FindAssembly()` checks top-level documents, not bundle children.

Each `BundleDsDocument` owns one `BundleAssemblyResolver : dnlib.DotNet.IAssemblyResolver`. Only its child modules receive a context using this resolver. “Already loaded” is deliberately workspace-scoped so an unrelated open bundle can never preempt a candidate from the requester's bundle. Its order is:

1. An already-loaded module from the requesting `BundleDsDocument`, matched by name, public-key token, culture, content type, and best compatible version.
2. An unloaded candidate entry from that same bundle. A filename/simple-name index narrows candidates without loading every assembly; candidates are loaded lazily and full identity is checked. An exact identity wins. Multiple exact candidates return null with an ambiguity diagnostic rather than selecting by tree order.
3. `IDsDocumentService.FindAssembly(request, FindAssemblyOptions.All & ~Version)` for already-loaded ordinary top-level documents, explicitly rejecting any `IDsBundleEntryDocument` whose `BundleDocument` is not the requesting bundle.
4. `IDsDocumentService.AssemblyResolver.Resolve(request, sourceModule)` for the existing resolver's runtime/GAC/source-directory/disk fallbacks.

This is the repository requirement “already loaded, same bundle, existing resolver, runtime/GAC/disk” with “already loaded” partitioned safely: same-workspace loaded modules are step 1, ordinary loaded documents are the loaded-document portion of the existing resolver at step 3, and children of other bundles are never eligible. There is no process-wide cross-bundle module registry; the new `BundleWorkspaceDocumentIndex` is owned/disposed by one bundle root.

Recursive load attempts are guarded per entry. A failure is cached for the workspace but does not poison the existing global resolver's failure cache. Closing one bundle disposes only its resolver/index.

The analyzer and compiler already call `Module.Context.AssemblyResolver`; no analyzer-specific or Roslyn-specific bundle resolver is introduced.

### 3.5 Editing, undo, and standalone save

Existing assembly editing is node/`ModuleDef` based. Once the default module nodes are used, method, IL, metadata, analyzer, and Roslyn commands continue to operate. `Extensions/dnSpy.AsmEditor/UndoRedo/DsDocumentUndoableDocumentsProvider.cs` already discovers every created document node and stores the undo object as an annotation.

The existing source-edit path is grounded in `Extensions/dnSpy.AsmEditor/Compiler/EditCodeVM.cs` (Roslyn-facing edit state), `ModuleImporter.cs` (compiled definition import), and `MDEditorPatcher.cs`/the existing command objects (apply and undo). No bundle type enters these classes; integration tests exercise them through their existing module/node contracts.

The existing save path is:

```text
SaveModuleCommand
    -> DocumentSaver
    -> SaveModuleOptionsVM / dialogs
    -> ModuleSaver
    -> ModuleDef.Write() or ModuleDefMD.NativeWrite()
```

Relevant files are `Extensions/dnSpy.AsmEditor/SaveModule/SaveModuleCommand.cs`, `DocumentSaver.cs`, `SaveModuleOptionsVM.cs`, and `ModuleSaver.cs`. Bundle modules use this same path. Empty `ModuleDef.Location` forces a new standalone destination. The bundle source filename and workspace bytes remain unchanged.

Existing memory-mapped behavior is retained: `DsDocumentService.CreateDocumentCore()` selects file-backed `PEImage` when `IDsDocumentServiceSettings.UseMemoryMappedIO` is enabled, `MemoryMappedIOHelper` can detach normal documents, and `Extensions/dnSpy.AsmEditor/SaveModule/MmapDisabler.cs` protects normal save. Bundle entry mappings are owned by `BundleFile`; standalone module save never asks `MmapDisabler` to detach or overwrite the container.

The save implementation is factored narrowly so the same validated writer options can target either a file or a `MemoryStream`. The new internal `ModuleSerializationService` in `dnSpy.AsmEditor` performs the write; no bundle-specific writer is added.

### 3.6 Strong-name policy

Current dnSpy exposes the `StrongNameSigned` COR20 flag and public-key editing, but its save UI has no complete cancel/remove/re-sign guard. This project cannot rely on that as safe signature handling.

Before either standalone save or workspace serialization of a module whose assembly has a public key or whose COR20 header is strong-name signed, `StrongNameSaveGuard` requires one explicit choice:

- Cancel.
- Remove strong name for this output. The writer applies a reversible output-only transform: clear `AssemblyAttributes.PublicKey`, public-key bytes, `ComImageFlags.StrongNameSigned`, and the strong-name directory while writing, then restores the in-memory model in `finally`.
- Re-sign this output with a user-selected `.snk`; create `StrongNameKey` and call dnlib's writer-option strong-name initialization.

The chosen disposition is recorded with workspace replacement metadata so rebuild does not ask again or silently change it. Tests verify the emitted assembly, not merely UI state. Ordinary signed module saving receives the same guard, while unsigned behavior remains unchanged.

### 3.7 Debugging preservation

`Extensions/dnSpy.Debugger/dnSpy.Debugger/DbgUI/StartDebuggingOptionsProvider.cs::GetCurrentFilename()` currently uses the selected document node's filename. A bundle child has a synthetic filename. The narrow generic correction is to use `GetTopNode()?.Document.Filename` when the selected document has no existing physical file. For ordinary roots and assembly/module children this resolves to the same existing filename; for a bundle child it resolves to the source apphost. A Windows test confirms F5 selection points at the bundle executable.

This project does not rewrite debugger engines. Original and rebuilt bundles are started through the existing .NET debug-program workflow.

### 3.8 Workspace and dirty state

`BundleWorkspace` is added to the UI-independent core only after the read-only parser is stable. It owns a `BundleFile` and an ordinal dictionary keyed by immutable `BundleEntry` identity. Its public operations are:

```csharp
public sealed class BundleWorkspace : IDisposable {
    public BundleFile Bundle { get; }
    public bool HasChanges { get; }
    public bool HasSavedReplacements { get; }
    public IReadOnlyCollection<BundleEntry> ModifiedEntries { get; }
    public event EventHandler<BundleWorkspaceChangedEventArgs>? Changed;
    public Stream OpenCurrentRead(BundleEntry entry);
    public void SetReplacement(BundleEntry entry, byte[] bytes, BundleReplacementInfo info);
    public bool Revert(BundleEntry entry);
    public void RevertAll();
    public void MarkSaved();
}
```

Replacement arrays are defensively copied once and exposed only as read-only streams. Failed serialization never calls `SetReplacement`, leaving the prior replacement/original state intact. Non-managed replacement is not exposed by UI in the MVP.

After a successful Save Bundle As, `MarkSaved()` establishes the published logical bytes as the clean baseline while retaining replacement bytes for later saves. Entry and root Revert commands remain available for saved replacements. Reverting then restores source-bundle bytes and is dirty relative to the published baseline, so close guards still protect it; saving that reverted state establishes the source bytes as the new clean baseline and raises `Saved` change events for tree refresh.

`Apply Module Changes to Bundle` lives in `dnSpy.AsmEditor` because that extension owns dnlib writer options and undo state. It accepts only `IDsBundleEntryDocument`, blocks R2R, serializes completely, validates the result by reopening it with dnlib, then calls `SetWorkspaceReplacement`. Revert commands and dirty-node refresh live in `Extensions/dnSpy.Bundles`.

A new small, generic MEF contract is added at `dnSpy/dnSpy.Contracts.DnSpy/Documents/IDsDocumentCloseGuard.cs`. It follows the repository's metadata-attribute convention exactly:

```csharp
public enum DsDocumentCloseReason { Remove, ReloadList, LoadList, AppExit }

public interface IDsDocumentCloseGuard {
    bool CanClose(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason);
}

public interface IDsDocumentCloseGuardMetadata {
    string Name { get; }
    double Order { get; }
}

[MetadataAttribute, AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ExportDsDocumentCloseGuardAttribute : ExportAttribute,
        IDsDocumentCloseGuardMetadata {
    public ExportDsDocumentCloseGuardAttribute(string name, double order)
        : base(typeof(IDsDocumentCloseGuard)) {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Order = order;
    }
    public string Name { get; }
    public double Order { get; }
}

public static class DsDocumentCloseGuardConstants {
    public const double ORDER_BUNDLE_WORKSPACE = 1000d;
    public const double ORDER_DEFAULT = double.MaxValue;
}
```

`BundleDocumentCloseGuard` is exported as `[ExportDsDocumentCloseGuard("BundleWorkspace", DsDocumentCloseGuardConstants.ORDER_BUNDLE_WORKSPACE)]`. `DsDocumentCloseGuardService` is a shared export in `dnSpy/Documents`; its importing constructor is:

```csharp
DsDocumentCloseGuardService(
    IAppWindow appWindow,
    [ImportMany] IEnumerable<Lazy<IDsDocumentCloseGuard,
        IDsDocumentCloseGuardMetadata>> guards)
```

It materializes the sequence once, rejects empty/duplicate ordinal `Name` metadata, and sorts by `Order`, then `Name` using `StringComparer.Ordinal`; equal numeric orders are therefore deterministic. `DsDocumentService` adds `Lazy<IDsDocumentCloseGuardService>` to its existing importing constructor. `DocumentListLoader` and `DocumentCloseGuardCommandLoader` import the same shared service; no component imports raw guards independently.

The service's internal `TryExecute(documents, reason, authorizedAction)` is synchronous. It captures immutable document references/dirty summaries before dispatch. If `appWindow.MainWindow.Dispatcher.CheckAccess()` is false it calls `Dispatcher.Invoke(DispatcherPriority.Send, Func<bool>)`; otherwise it executes inline. Only the UI-thread core evaluates guards or shows modal Save/Discard/Cancel. Callers must enter with no dnSpy document, workspace, mmap, or resolver lock held. After all guards return true, the service installs one exact document-reference-set/reason authorization frame and invokes the mutation callback on the UI thread. The one permitted nested `Clear()` consumes that matching frame without a second prompt; a different set/reason, a second consumption, or any removal attempted while guards are being evaluated returns false/no mutation. The frame is removed in `finally`. Guard exceptions are caught, logged, shown as a safe failure, and treated as cancellation.

Its exact integration is:

- `DsDocumentService.Remove(IDsDocumentNameKey)`: snapshot the matching top-level document under the read lock, release the lock, call `TryExecute(..., Remove, RemoveCore)`, then in `RemoveCore` reacquire the write lock and remove only if the same key/document is still present.
- `DsDocumentService.Remove(IEnumerable<IDsDocument>)`: materialize and deduplicate the requested top-level documents, snapshot under the read lock, release it, call `TryExecute(..., Remove, RemoveManyCore)`, then revalidate/remove surviving identities in one write-lock section.
- `DsDocumentService.Clear()`: snapshot all top-level documents under the read lock, release it, and call `TryExecute(..., current list reason or Remove, ClearCore)`. Empty snapshots retain today's no-op behavior. `ClearCore` is private and never exported as a bypass.
- `DocumentListLoader.Load()` and `Reload()`: authorize the current document snapshot with `LoadList` or `ReloadList` before `DocumentTabService.CloseAll()`. A short-lived authorization scope covers the subsequent `IDsDocumentService.Clear()` so it revalidates but does not display a second prompt. Cancel returns `false` before tabs, selected-list state, or documents change.
- `DocumentListLoader.CloseAll()`: authorize with `Remove` before closing tabs/clearing; its scope similarly suppresses only the matching nested `Clear()` call.
- Direct callers of any of the three `IDsDocumentService` removal APIs cannot bypass the guard because the check is inside `DsDocumentService`, not only in commands/list listeners.
- A single new `DocumentCloseGuardCommandLoader` subscribes to `IAppWindow.MainWindowClosing`, authorizes `AppExit`, and sets `CancelEventArgs.Cancel` on cancellation. It is separate from `UndoRedoCommmandLoader`; bundle guard tests cover both event-order permutations and ensure at most one bundle prompt. `MainWindowClosed` remains persistence-only.

No dnSpy collection lock, `BundleWorkspace` lock, mmap handle lock, or resolver lock is held while dispatching, evaluating MEF guards, or running Save/Discard/Cancel UI. Save completion is awaited by the modal UI before a guard returns. Mutation revalidates identity under the write lock. Tests call each path from UI and worker threads, assert modal code ran on the UI dispatcher, verify order `Order` then ordinal `Name`, exercise duplicate metadata/composition failure, exception-as-cancel, guard-triggered reentrancy rejection, exactly one authorized nested Clear, no double prompt, and cancellation before mutation. Ordinary-document tests cover direct `Remove(key)`, batch `Remove`, `Clear`, load, reload, Close All, and app close with zero guards and assert the same collection notifications/order as baseline.

`BundleDocumentCloseGuard` offers Save Bundle As, Discard, or Cancel for dirty workspaces and prevents applied workspace bytes from being lost even after module undo state is marked saved-to-workspace. It does not also implement `IDocumentListListener`; the centralized coordinator is the sole guard path and avoids double prompts/deadlocks. This is the only broad close-flow addition.

### 3.9 HostModel and rebuild decision

Evaluation found a packaging constraint that the implementation must not hide:

- The .NET 10 SDK contains `Microsoft.NET.HostModel.dll` (`/usr/lib/dotnet/sdk/10.0.111/Microsoft.NET.HostModel.dll` in this environment).
- NuGet.org's `Microsoft.NET.HostModel` package history stops at `5.0.0-preview.1.20120.5`. That package predates manifest v6 compression and is not acceptable.
- Referencing an arbitrary installed SDK path would make dnSpy deployment depend on an SDK and would not support the net48 product.

The decision is to source-vendor the smallest Windows-bundling subset of `Microsoft.NET.HostModel` from the `dotnet/runtime` `v10.0.11` commit `79d0c463f1b55624c874a11585f7e47731e8d675` into `Libraries/Microsoft.NET.HostModel.Bundle`. This is official HostModel code, not a custom bundler. Every adapted file retains its MIT header; the directory includes the upstream MIT license, a provenance README with tag and commit, and a patch log. Public namespaces stay `Microsoft.NET.HostModel.*` so divergence is obvious and future updates can be diffed. The project targets `net48;net10.0` and is independently buildable.

The approved upstream dependency closure is exactly the following 15 source files under `https://github.com/dotnet/runtime/tree/79d0c463f1b55624c874a11585f7e47731e8d675/src/installer/managed/Microsoft.NET.HostModel/`; hashes are SHA-256 of the unmodified bytes at that commit:

| Upstream path | SHA-256 |
|---|---|
| `AppHost/BinaryUtils.cs` | `be076a56df428e620eff8057541cfa4b1a529a63ed4232b38eb01e43aa9712ba` |
| `AppHost/PEUtils.cs` | `623ff627b7b50b6c588e10135bd6e874ff573a4a7eff2bd1aa3f7f2d1fbd00f9` |
| `AppHost/PlaceHolderNotFoundInAppHostException.cs` | `95fe8b7f721ab7b48080755924647e81fc1543e2ff32dea90eaafe9bac23d7a6` |
| `AppHost/RetryUtil.cs` | `85469a72f407d9c1cc5282ab4b60888885e6090380022f18ec225e8235ab1a3c` |
| `Bundle/BundleOptions.cs` | `53b5a78b5d0d4c80e18739904501c0b0ec8614ea43784979d7ab1f085336ac00` |
| `Bundle/Bundler.cs` | `611d3da3eb4d25ef9ae411fb5d626f8d55852bedd759d9e0ec3c8ad7b4713b77` |
| `Bundle/FileEntry.cs` | `02419b4d1f18ccd389c8a2145a9e660a002c17294596837b77a280d34de4c848` |
| `Bundle/FileSpec.cs` | `1a9d96b1d6b3b78c02a31a5ac545ebb8f326f631fab1af7c1a2824abd158379d` |
| `Bundle/FileType.cs` | `bb378074145d183576a47169c3651d605eed70f454b18e1c8263b69e83419fae` |
| `Bundle/Manifest.cs` | `b45defad7f4c3545995b7f66af234eab1edbf524f900df8b0784fb35ab6f364f` |
| `Bundle/TargetInfo.cs` | `83f5f6a56a41926295e082400fcb099296b45658ddd7918b48b6cee7f90c72e2` |
| `Bundle/Trace.cs` | `a0c2d63a9edc24c792c1b35ca6eaa5e2a607a8f9b4f3eefae18dde13a0e5e4f0` |
| `HostModelUtils.cs` | `9f60efce1dfc43ea1f1fb96c8cdf537dcac66cc16e7a35e96a6a55f558584d60` |
| `PEOffsets.cs` | `b45684bf36c07823b480df864660694065b518be2cc34008027f83cdfebf4497` |
| `Utils/Base64Url.cs` | `729a814ffcff8bb97b6ada9408be15687300839b7ce27256e5bbdac737b3372b` |

No `AppHostExceptions.cs`, `HostWriter`, resource updater, COM host, ELF, or Mach-O source enters the project. `PEUtils.cs` is intentionally trimmed to its `IsPEImage(string)` method; therefore its deleted methods do not require `AppHostNotCUIException`/`AppHostNotPEFileException`, while retained `IsPEImage` is satisfied by the pristine `PEOffsets.cs` above. `PlaceHolderNotFoundInAppHostException` is retained but rebased directly from `System.Exception`, removing its sole dependency on `AppHostUpdateException`. The patch log permits exactly these five Windows-only adaptations:

1. `Bundler.cs`: remove Mach-O imports/code, the unused immutable import, and Unix mode setting; retain the constructor parameter but reject `macosCodesign: true`. File filtering, alignment, compression, manifest writing, and placeholder replacement remain byte-for-byte upstream logic.
2. `TargetInfo.cs`: replace cross-platform selection with Windows/x64 defaults and rejection, retain framework-to-v1/v2/v6 mapping, 4096-byte alignment, PE inference, and Windows hostfxr/hostpolicy exclusions.
3. `HostModelUtils.cs`: retain only `GetFileLength()` as `new FileInfo(path).Length`; inputs are private regular temp files, removing link/codesign and `Microsoft.IO.Redist` dependencies.
4. `PEUtils.cs`: retain only the license header, `System.IO`, namespace/class, XML summary, and complete upstream `IsPEImage(string)` body; delete mmap/CET/subsystem APIs and their exception/`System.Buffers.Binary`/`System.Reflection.PortableExecutable` dependencies.
5. `PlaceHolderNotFoundInAppHostException.cs`: change only its base type from `AppHostUpdateException` to `System.Exception` (the file already imports `System`); retain constructor/message behavior verbatim.

The canonical patches use LF and `diff -u --label a/<upstream-path> --label b/<upstream-path>`. Their complete patch SHA-256 and resulting compiled-source SHA-256 are:

| Patch artifact | Patch SHA-256 | Result SHA-256 |
|---|---|---|
| `UPSTREAM-PATCHES/Bundler.windows-x64.patch` | `06f6571e02c6fe305e7730c5678bd72366f86ba46d7124afc8155afa2506452c` | `3150fc1a2598ffb05c0d10e72e07d7a088e1ddfd38e762ea8d7582378bc8acc4` |
| `UPSTREAM-PATCHES/TargetInfo.windows-x64.patch` | `148d9edaf4b7f7dc86be18d2a6df4e5b639a07e7f3c2018934ed45f239d072bf` | `36c60c4ae9e50262fe4ee4d3e9caf12b70f206c9fd3db2fa94c9f1794943dd57` |
| `UPSTREAM-PATCHES/HostModelUtils.windows-x64.patch` | `055a07b710ed9f4311b15ed240e22869f2a22822ed4c16ef92c0a6b65a02ab99` | `ed5c5fd4b8a29828b7c1f6021fbc1e7cad3d090461474ae860781f4cb952e377` |
| `UPSTREAM-PATCHES/PEUtils.is-pe-only.patch` | `e7eaa2b309b94b37d2c953c4ee6c78467ceeb8f34b2489be8fb341a5bb8ccc2a` | `359d146291f35fb2d5ee008dfb9733b27ee3c66903d7eaeeac849307863f4c74` |
| `UPSTREAM-PATCHES/PlaceHolderException.standalone.patch` | `a01f5864eaf51a75d1189cb114b77ba772874f9ba13dc5483c8d98f574c05f09` | `b0345e7db623c415bb89f7890c5d984a00cbf8809bfb34420635900033f69478` |

For per-hunk audit, a canonical hunk is the LF byte sequence beginning with its `@@` line and ending immediately before the next `@@` or EOF. Ordered hunk SHA-256 values are:

| Patch | Ordered hunk SHA-256 values |
|---|---|
| `Bundler.windows-x64.patch` | `39360ffd03a1c9e0621cc25bdfd655f637d1cdb3b80b669b01bef28ae9fdce19`, `9f55e68464dc573ab3cd7ca504a0c952fe5c2c495a03966405d1ccd44efdf48d`, `200801f2b8a769ad47a1f3a1a32015cd73e8ab1ff193f22611b97fb60a03692e`, `d823b960cd30b8116647826164d6fff3387601b2885034d2394d5fced69e051e`, `1bc809a9105e50182f16869d457c735371aad2809818e8bb7f791d5524cacd43`, `35b1fbcca0bdcd9383a5acd3e465e1506a9baebd51a4414d8ab3e1bae2e4b969`, `f812bee891125cd229286dbacfd15b437c3a41789329e99415525366126fef83` |
| `TargetInfo.windows-x64.patch` | `47f6785d3a377a17e316e8c235660a6bcc78f40246ce35055315d79472b959cb`, `107ed3d3a5cc9f33e39f08e1ed745182e2d01b348877cc2d726b999dbd1823f1`, `cb21846803fb35cfa366a18fc9f904021b977fe1e982246ecbcbb41584502db1` |
| `HostModelUtils.windows-x64.patch` | `669b505e13d0d8a56ffd9cf566539442196e6bba0ec6f35ff644e62ac898262d` |
| `PEUtils.is-pe-only.patch` | `55c53154a4d690f359bbb1846cb11ef2d0b7da8215c5b5359a67c9a5b8c2e74c`, `cd4422d9b4666f91ae7bf2a912b84eff9829a6d5d02d0994bcd1b7f70182e2c8`, `e752471c4e7364f04399bbdd84edca6805d0959cd9d706002ed31ca89a60eb44` |
| `PlaceHolderException.standalone.patch` | `819c8da601b221766f22607f0fe145beaa1069a641a5a1eca1fd3c0d4cc71124` |

The remaining 10 source files, including `PEOffsets.cs`, compile pristine. `Manifest.cs` retains its upstream `#if NET` Base64Url and `#else` vendored `Utils/Base64Url.cs` branches. No syntax-only edits, generated compile items, default `**/*.cs` globs, or unlisted source files are permitted: the project sets `EnableDefaultCompileItems=false` and explicitly lists the 15 table paths. BND-020 verifies original, patch, per-hunk, and result hashes before compiling. A temporary reconstruction of this exact 15-file/five-patch set was successfully compiled for `net10.0` during specification review; BND-020 turns that check into the permanent test.

For `net48` only, the project directly references `System.Memory` `4.6.3` (for spans used by `BinaryUtils`, `Bundler`, and the placeholder exception) and `System.Reflection.Metadata` `10.0.0` (for `Bundler.IsAssembly`); `System.Collections.Immutable` `10.0.0` is the latter's transitive dependency. No PEUtils-specific metadata/exceptions package is needed after the exact trim. The complete resolved graph and content hashes are locked in `packages.lock.json`. `net10.0` uses framework assemblies and has no package references. `NETFRAMEWORK` selects the vendored Base64Url implementation. Upstream `LICENSE.TXT` is copied from the same commit with SHA-256 `cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310`. Tests compare the vendored API's output with `/usr/lib/dotnet/sdk/10.0.111/Microsoft.NET.HostModel.dll` through an isolated net10 test load and reopen both outputs with the already-complete core parser.

If independent review rejects source vendoring or its license/provenance cannot be made complete, implementation stops at the HostModel acquisition ticket; it must not fall back to a handwritten bundler without a specification change approved by the user.

### 3.10 Apphost reconstruction

Official `Bundler.GenerateBundle()` requires an unbundled host containing an eight-byte zero placeholder immediately before the 32-byte signature. The source bundle instead contains the old header offset. `WindowsAppHostReconstructor` creates a temporary clean source host as follows:

1. Require a valid Windows x64 PE and parsed bundle.
2. Compute `payloadStart = min(entry.Offset)` using validated physical offsets.
3. Require the marker and its preceding pointer to be within `[0, payloadStart)` and the manifest header to be at or after all entry ranges.
4. Copy exactly `[0, payloadStart)` to a private temporary file. Padding before the first entry is harmless and avoids guessing the original apphost file length.
5. Replace only the validated eight-byte header pointer immediately preceding the known marker with zero.
6. Detect a non-zero PE certificate-table directory as Authenticode presence, warn before save, and zero that directory in the temporary host. Certificate bytes are after the bundle and are not copied. Never report signature preservation.
7. Validate the reconstructed host again as PE and verify exactly one HostModel placeholder occurrence.

The original executable is opened read-only and never patched.

### 3.11 Rebuild service

`WindowsBundleRebuilder` is in `Extensions/dnSpy.Bundles` and depends on core workspace plus the vendored HostModel library. It:

1. Rejects output paths canonically equal to the source.
2. Derives manifest target generation: v1 -> framework 3.1, v2 -> 5.0, v6 -> 6.0 (HostModel uses the same v6 format for .NET 6+).
3. Derives `appAssemblyName` from the runtimeconfig/deps basename, falling back to host basename.
4. Creates one unique private temporary directory.
5. Creates the reconstructed host there.
6. Creates the mandatory host input `new FileSpec(reconstructedHostPath, hostName)` and inserts it exactly once at the start of the HostModel input list. Its `BundleRelativePath` must equal the `hostName` passed to `new Bundler(hostName, ...)`; HostModel excludes that input from the manifest and rejects zero/multiple matches.
7. Materializes each current logical entry to a generated flat temporary filename, never to its bundle relative path. Each non-host `FileSpec.BundleRelativePath` retains the validated original path.
8. Enables HostModel inventory for native binaries and symbols. For v1, raw type `0` remains parser metadata truth (`Unknown`) and the preflight performs bounded inference in HostModel order: exact derived `.deps.json`/`.runtimeconfig.json`, `.pdb`, valid PE with COR header (managed), valid PE without COR header (native), then other content; nonzero raw types fail with a preservation diagnostic. For v2/v6, Unknown raw entries remain rejected, so `BundleOtherFiles` is not enabled. For source manifest v2 or v6, maps `NetcoreApp3CompatMode` to `BundleOptions.BundleAllContent`; v1 retains HostModel's default target behavior. Enables compression only when the original v6 bundle has compressed entries.
9. Calls official `Bundler.GenerateBundle()` into a temporary output directory.
10. Reopens the result with `BundleReader`, compares ordered path/type/flags/logical-content inventory, and verifies every replacement byte sequence.
11. Moves the validated complete output to the user-selected destination. Any failure deletes temporary data and leaves both source and destination unchanged (an existing destination is replaced only after the normal save-picker confirmation and successful validation).

Unknown raw file-type preservation is limited by HostModel's type inference. The MVP refuses v2/v6 rebuild when an unknown raw type is present rather than silently changing it; v1 raw zero is the format-defined exception and is allowed only after bounded inference, while any nonzero v1 raw type is rejected. It also refuses bundles with duplicate assembly identity resolution ambiguities, R2R dirty entries, NativeAOT, non-Windows hosts, or non-x64 Windows hosts.

## 4. Core contracts and file layout

### 4.1 New projects

- `dnSpy/dnSpy.Bundles/dnSpy.Bundles.csproj`: signed, UI-independent `Microsoft.NET.Sdk` library, targets `net48;net10.0`, namespace `dnSpy.Bundles`.
- `Extensions/dnSpy.Bundles/dnSpy.Bundles.Extension.csproj`: WPF/MEF integration, targets repository TFMs, references core and contracts, outputs with other extensions.
- `Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj`: cross-platform net10 parser/workspace tests using `xunit.v3` 3.1.0, `xunit.runner.visualstudio` 3.1.5, and `Microsoft.NET.Test.Sdk` 18.0.1.
- `Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj`: `net10.0-windows` tests using the same packages for dnSpy/dnlib/editor/rebuild behavior, executed on Windows CI.
- `Tests/TestAssets/SingleFile`: tiny app plus dependency and deterministic fixture-generation targets/scripts; generated publish output is ignored, not committed.
- `Libraries/Microsoft.NET.HostModel.Bundle`: pinned official-source Windows subset and license/provenance.

All projects are added to `dnSpy.sln`. Core tests are not coupled to WPF or a submodule test project.

### 4.2 Read-only parser API

```csharp
namespace dnSpy.Bundles;

public enum BundleOpenStatus {
    NotBundle,
    Success,
    InvalidBundle,
    UnsupportedVersion,
}

public sealed class BundleOpenResult {
    public BundleOpenStatus Status { get; }
    public BundleFile? Bundle { get; }
    public BundleReadError? Error { get; }
}

public sealed class BundleReaderOptions {
    public const long DefaultMaximumSignatureSearchBytes = 32 * 1024 * 1024;
    public const int DefaultMaximumFileCount = 100_000;
    public const int DefaultMaximumStringByteLength = 16_383;
    public const long DefaultMaximumEntrySize = 2L * 1024 * 1024 * 1024;
    public const long DefaultMaximumTotalLogicalSize = 16L * 1024 * 1024 * 1024;
    public BundleReaderOptions(
        long maximumSignatureSearchBytes = DefaultMaximumSignatureSearchBytes,
        int maximumFileCount = DefaultMaximumFileCount,
        int maximumStringByteLength = DefaultMaximumStringByteLength,
        long maximumEntrySize = DefaultMaximumEntrySize,
        long maximumTotalLogicalSize = DefaultMaximumTotalLogicalSize);
    public long MaximumSignatureSearchBytes { get; }
    public int MaximumFileCount { get; }
    public int MaximumStringByteLength { get; }
    public long MaximumEntrySize { get; }
    public long MaximumTotalLogicalSize { get; }
}

public sealed class BundleReader {
    public BundleReader(BundleReaderOptions? options = null);
    public BundleOpenResult Open(string filename);
}

public sealed class BundleFile : IDisposable {
    public string Filename { get; }
    public long FileLength { get; }
    public long MarkerOffset { get; }
    public long HeaderOffset { get; }
    public BundleManifest Manifest { get; }
    public IReadOnlyList<BundleEntry> Entries { get; }
}

[Flags]
public enum BundleManifestFlags : ulong {
    None = 0,
    NetcoreApp3CompatMode = 1,
}

public sealed class BundleManifest {
    public uint MajorVersion { get; }
    public uint MinorVersion { get; }
    public string BundleId { get; }
    public BundleManifestFlags Flags { get; }
    public BundleRange? DepsJson { get; }
    public BundleRange? RuntimeConfigJson { get; }
}

public sealed class BundleEntry {
    public int Index { get; }
    public long Offset { get; }
    public long Size { get; }
    public long CompressedSize { get; }
    public byte RawFileType { get; }
    public BundleFileType FileType { get; }
    public string RelativePath { get; }
    public bool IsCompressed { get; }
    public Stream OpenLogicalRead();
    public byte[] ReadAllBytes(long maximumBytes);
}
```

`BundleReadError` contains a stable error code, safe message, and optional entry index/offset; it never exposes arbitrary file content. Status/error results are used at the provider boundary, while programming errors such as null arguments still throw normal argument exceptions.

### 4.3 Internal implementation types

- `BundleSignatureScanner`: streaming overlap-aware signature search and preceding pointer read; it evaluates all matches in the configured search window and rejects multiple independently valid headers as ambiguous.
- `BoundedBinaryReader`: checked primitive and strict bounded 7-bit UTF-8 string reads.
- `BundleManifestReader`: version-aware header/entry parse and cross-field validation.
- `BoundedReadStream`: non-seekable read-only range wrapper when a memory-mapped view is not sufficient.
- `ExactLengthReadStream`: enforces declared decompressed length and validates EOF/overrun.
- `BundlePathValidator`: separator normalization and traversal/root/duplicate checks.
- `BundleWorkspace`: transactional replacements, current-entry streams, and change events.

No type in this list references dnlib or dnSpy.

The extension separately defines `BundleTextViewOptions.MaximumPreviewBytes` with a default of `8 * 1024 * 1024`; it is not part of the parser API or `BundleReaderOptions`.

## 5. Loading and save control flows

### 5.1 Open

```text
Open file
  -> BundleDsDocumentProvider
      -> NotBundle: return null -> DefaultDsDocumentProvider unchanged
      -> Invalid/unsupported marker: BundleErrorDocument
      -> Success: BundleDsDocument(BundleWorkspace)
  -> expand category
  -> create selected entry document
  -> for managed entry only: bounded logical bytes -> PEImage -> ModuleDefMD
  -> default dnSpy module tree/decompiler/editor
```

### 5.2 Save standalone module

```text
BundleModuleDocument.ModuleDef
  -> existing Save Module command/options
  -> explicit strong-name disposition if signed
  -> dnlib writer
  -> user-selected standalone DLL/EXE

Source bundle: unchanged
Workspace replacement: unchanged
```

### 5.3 Apply to workspace

```text
BundleModuleDocument.ModuleDef
  -> Apply Module Changes to Bundle
  -> reject dirty R2R
  -> explicit strong-name disposition
  -> serialize fully to memory
  -> reopen bytes with dnlib
  -> atomically SetWorkspaceReplacement(entry, bytes, disposition)
  -> refresh dirty state

Source bundle: unchanged
```

### 5.4 Save bundle

```text
BundleWorkspace
  -> validate supported Windows x64 source and dirty entries
  -> Authenticode warning if applicable
  -> reconstruct private clean apphost
  -> materialize current logical entries into private flat temp files
  -> official vendored Microsoft.NET.HostModel Bundler
  -> reopen and compare with our parser
  -> atomically publish new destination

Source bundle: unchanged
```

## 6. Test and fixture strategy

### 6.1 Synthetic parser fixtures

Tests may construct minimal byte sequences representing v1, v2, and v6 manifests. This helper is test-only and must never be referenced by production or rebuild code. It gives exact coverage for truncation, overflow, path, count, duplicate, unknown-type, and decompression cases without committing large executables.

Core tests include:

- marker at earliest legal offset and across scan-buffer boundary;
- ordinary managed DLL, ordinary managed EXE, native PE, random file -> `NotBundle`;
- valid v1/v2/v6 headers and all official type values;
- unknown raw type preservation;
- uncompressed reads stop exactly at entry end;
- compressed content equality, truncated Deflate, corrupt Deflate, declared-size underflow/overflow, and bomb limit;
- every rejection in section 2.3;
- multiple entries and aggregate-size checking;
- concurrent independent entry streams and disposal behavior.

### 6.2 Real publish fixtures

Source projects are committed; generated outputs are not. Each generation has a separate project because an SDK cannot reliably publish every historical TFM. All projects reference the shared `SingleFile.Dependency` source project and print `BUNDLE_VALUE=v1`; generation copies output plus a JSON inventory/hash sidecar into an ignored SDK-specific artifact directory.

| Project | SDK selected by adjacent `global.json` | TFM | Required variants/properties |
|---|---|---|---|
| `NetCoreApp31/App.csproj` | `3.1.426` | `netcoreapp3.1` | `win-x64`, SCD, `PublishSingleFile=true`, portable PDB; validates manifest v1 |
| `Net5/App.csproj` | `5.0.408` | `net5.0` | FDD and SCD; plus SCD compatibility variant with `IncludeAllContentForSelfExtract=true`; portable bundled-PDB variant with `IncludeSymbolsInSingleFile=true`; validates v2 and flag `0x1` |
| `Net6/App.csproj` | `6.0.428` | `net6.0` | FDD/SCD, uncompressed/compressed via `EnableCompressionInSingleFile`; portable bundled PDB; validates v6 |
| `Net8/App.csproj` | `8.0.419` | `net8.0` | SCD compressed and uncompressed; multi-project dependency; validates v6 |
| `Net10/App.csproj` | `10.0.111` | `net10.0` | FDD/SCD, compressed/uncompressed, bundled PDB, multi-project; validates current v6 |

Every adjacent `global.json` contains the exact version, `"rollForward": "disable"`, and `"allowPrerelease": false`. Windows CI installs all five SDK versions with `actions/setup-dotnet`; the generator `Push-Location`s into each generation directory before `dotnet --version` and `dotnet publish App.csproj`, because SDK resolution starts from the working directory, and a mismatch fails before publish. Common project properties are `RuntimeIdentifier=win-x64`, `PublishSingleFile=true`, `DebugType=portable`, and `DebugSymbols=true`. Variant-specific commands set `--self-contained true|false`; only the PDB variant sets `IncludeSymbolsInSingleFile=true`; only v6 projects set `EnableCompressionInSingleFile=true`. The .NET 5 compatibility fixture sets `IncludeAllContentForSelfExtract=true` and asserts parsed `NetcoreApp3CompatMode`, then rebuild parity asserts it maps to `BundleOptions.BundleAllContent`.

Example current generation command:

```powershell
Push-Location Tests/TestAssets/SingleFile/Net10
if ((dotnet --version) -ne '10.0.111') { throw 'Wrong SDK' }
dotnet publish App.csproj -c Release -f net10.0 -r win-x64 `
  -p:PublishSingleFile=true -p:DebugType=portable `
  -p:DebugSymbols=true --self-contained true
Pop-Location
```

The net10 test runner consumes all artifact roots through `DNSPY_BUNDLE_FIXTURES`. Historical outputs are not silently skipped in CI. Locally, tests requiring an absent historical SDK report a clear inconclusive prerequisite; synthetic version tests always run.

### 6.3 Integration assertions

- Provider returns the same existing document kinds for ordinary DLL and managed EXE.
- Bundle root/folders/non-managed entries appear and only expanded managed entries allocate module bytes.
- Existing decompiler produces output for the main managed entry.
- Analyzer/navigation resolves a dependency from the same bundle.
- Duplicate identity fails deterministically without global hijacking.
- C#/IL-equivalent model edit can be serialized, reopened, and observed.
- Standalone save leaves the source bundle hash unchanged.
- Unsigned, explicit remove-signature, and re-sign paths emit the promised state.
- Apply one/multiple entries, serialization failure rollback, revert one/all, and close guards.
- Rebuilt file reopens; ordered logical inventory is equivalent; dirty behavior changed; source hash unchanged.
- On Windows, execute original and rebuilt fixtures and compare stable stdout/exit codes.
- Selecting a bundle child for debugging resolves the physical top-level bundle path.

## 7. Licensing and provenance boundary

- The dnSpy repository is GPLv3. Every new original C# source file for this project, including test and fixture-generator source, begins with exactly these two lines:

  ```csharp
  // Copyright (C) 2026 netSpy Single-File contributors
  // SPDX-License-Identifier: GPL-3.0-or-later
  ```

  This project-specific header replaces the legacy multiline de4dot header for new project files and must remain the first content in the file.
- Parser behavior is reimplemented from the documented format and official runtime behavior. Do not copy ILSpy's `SingleFileBundle.cs` merely for convenience.
- If an ILSpy fragment is ever adapted, retain its MIT/.NET Foundation header and record exact source URL and commit. The existing ILSpy submodule is neither modified nor updated.
- The vendored HostModel subset is copied only from `dotnet/runtime` tag `v10.0.11`, commit `79d0c463f1b55624c874a11585f7e47731e8d675`; retain every upstream MIT header, upstream license, source revision, file list, and adaptation notes. Vendored upstream HostModel files must not be relabeled with the project-specific header.
- Do not paste code from web snippets or unlicensed third-party parsers.
- A ticket that changes provenance or imports more upstream source must update the provenance README and `pre-spec.md` in the same commit.

## 8. Assumptions and explicit limitations

- Manifest v6 remains the official container version for .NET 6-10; runtime version is not encoded as a new manifest major.
- `ModuleDefMD` needs random-access PE data, so one selected managed entry is materialized in memory. This is compatible with the requirement against materializing all entries.
- Runtime JSON text preview is UTF-8 and bounded. Binary editing of non-managed entries is not provided.
- Exact original compression bytes, bundle ID, padding, and Authenticode are not preserved. Logical content and covered metadata are.
- Source HostModel vendoring is required for both net48 and net10 deployment. An installed SDK is not a runtime dependency.
- Windows NativeAOT identification requires a missing COR20 header plus a valid PE export named `DotNetRuntimeDebugHeader` or `DotNetRuntimeContractDescriptor`. An unrecognized native executable continues to use the existing PE document; ELF/Mach-O detection is not advertised.
- Full WPF/editor/debugger execution tests require Windows. Linux can build/test the new core target, generate Windows bundles with the SDK, and perform parser/rebuild byte validation, but cannot run dnSpy's WPF UI or a Windows executable.

## 9. Dependency-ordered implementation tickets

PR-01 is the first ledger item. After independent approval, its single documentation commit contains only `docs/single-file-bundle-design.md` and `docs/specs/dotnet-single-file-bundles.md`, with proposed subject `docs: specify single-file bundle support`; it contains no production code. BND tickets start only after that commit. BND-001 through BND-028 then execute in mandatory numeric order, each with its narrow test and one local commit.

### BND-001 — Core and test scaffolding (PR-02)

Dependencies: approved specification.

Add `dnSpy.Bundles`, cross-platform test project, solution entries, immutable model shells, options/default limits, status/error model, and no-op `NotBundle` reader. Add architecture tests proving core has no dnSpy/dnlib/WPF reference.

Acceptance: projects build for net10; core also compiles for net48 on Windows; one ordinary-file test returns `NotBundle`; no production detection yet.

### BND-002 — Marker detection and manifest headers (PR-02)

Dependencies: BND-001.

Implement streaming marker scan, checked header pointer, strict bounded strings, v1/v2/v6 header/known-flag parsing, and unsupported-version/error statuses. Fold header-level adversarial coverage here: buffer boundaries, multiple valid markers, early signature, invalid/overflowing pointers, truncation, extreme count without proportional allocation, file-count/string limits, skipped versions, and unknown flag bits.

Acceptance: valid synthetic headers parse; malformed inputs return stable errors without out-of-range reads or unbounded allocation.

### BND-003 — Entry model, validation, and uncompressed streams (PR-02)

Dependencies: BND-002.

Implement versioned entry parse, raw type retention, path normalization/validation, duplicates, checked ranges/totals, v2 config range validation, memory-mapped bounded uncompressed streams, and disposal. Fold entry-level adversarial coverage here: aggregate logical-size exhaustion, overlapping entries/manifest, inconsistent config ranges, traversal variants, duplicate normalized paths, range overflow/EOF, fuzz-seed corpus, and deterministic disposal/failure.

Acceptance: all official/unknown types enumerate; exact bytes are returned; neighbor bytes cannot be read; path/range/overflow tests pass.

### BND-004 — Compressed logical entry streams (PR-02)

Dependencies: BND-003.

Add Deflate and exact logical-length wrappers plus explicit bounded `ReadAllBytes`. Fold compressed-input robustness here: corrupt/truncated/short/long payloads, bomb limits, trailing/neighbor data, fuzz seeds, concurrent streams, and deterministic early-disposal behavior.

Acceptance: valid compressed bytes match expected content, all invalid streams fail predictably within limits, and the named tests across BND-002 through BND-004 cover every rejection in section 2.3. There is no separate robustness ticket.

### BND-005 — Modern real-fixture harness (PR-02)

Dependencies: BND-004.

Add tiny app/dependency sources, deterministic net10 publish commands, ignored output directories, and a fixture locator. Generate compressed/uncompressed, FDD/SCD, multiple-assembly, and bundled-PDB variants and compare extracted main assembly bytes with build output.

Acceptance: net10 fixtures generated by the installed SDK pass; no generated bundle binary is committed.

### BND-006 — Historical manifest fixture matrix (PR-02)

Dependencies: BND-005.

Add the five projects and exact SDK/TFM/property variants from section 6.2 to Windows CI. Each invocation uses its adjacent `global.json` with roll-forward disabled, asserts `dotnet --version`, and writes an isolated hash/inventory artifact so the requested SDK, rather than the latest installed SDK, creates the bundle.

Acceptance: CI requires every historical fixture and parser assertion; missing SDK/output is a failure, not a skip; the .NET 5 `IncludeAllContentForSelfExtract` fixture parses flag `0x1`; FDD/SCD, compression, and PDB assertions match the matrix rather than being inferred from filenames.

### BND-007 — Bundle extension provider and error document (PR-03)

Dependencies: BND-006.

Scaffold `dnSpy.Bundles.Extension`; export the ordered document provider; add root/error documents; preserve serialized filename/key. Add provider tests for ordinary DLL/EXE and valid/invalid bundles.

Acceptance: ordinary documents still come from default loading; valid bundle returns a container; malformed marked executable returns a visible error document.

### BND-008 — Bundle tree and non-managed entry views (PR-03)

Dependencies: BND-007.

Add folder/entry documents, tree-node provider, lazy categories, bounded runtime JSON preview, and metadata views for native/symbol/unknown entries.

Acceptance: expected categories/inventory render without loading a managed module or extracting files.

### BND-009 — Lazy managed entry documents (PR-03)

Dependencies: BND-008.

Add managed-entry byte adapter, verified `PEImage`, `ModuleDefMD`, bundle document contracts, assembly wrapper/node, composite keys, empty `ModuleDef.Location`, and normal module-node handoff.

Acceptance: expanding/selecting one managed entry loads only it; existing module tree receives a `ModuleDefMD`; compressed fixture works; assembly-level editor commands retain their expected node shape.

### BND-010 — Contextual same-bundle resolver (PR-03)

Dependencies: BND-009.

Implement the per-workspace loaded-module/candidate index, identity matching, recursive-load guard, ambiguity diagnostics, explicit exclusion of other-bundle children, and existing-resolver fallback.

Acceptance: an already-loaded module from the requesting workspace wins first; its unloaded same-bundle dependency wins before ordinary/disk fallback; an identically named module already loaded from a different bundle cannot preempt it; ordinary top-level loaded and unrelated module resolution remain unchanged; duplicate ambiguity is deterministic.

### BND-011 — Decompiler/analyzer/loading integration proof (PR-03)

Dependencies: BND-010.

Add Windows integration tests for decompile, basic analyzer/navigation, multiple assemblies, compressed bundle, and ordinary managed DLL/EXE regression. Measure that unexpanded entries are not materialized.

Acceptance: all core PR-03 user-visible behavior is demonstrated without full extraction.

### BND-012 — Debug path and compatibility notices (PR-03)

Dependencies: BND-011.

Use top-level physical filename for debugging selected contained documents. Add R2R annotation and the exact Windows NativeAOT export check from section 2.1 without changing arbitrary native PE handling.

Acceptance: bundle child F5 targets source executable; ordinary debug selection is unchanged; R2R is labeled; recognized NativeAOT explains no editable IL.

### BND-013 — Shared file/stream module serializer (PR-04)

Dependencies: BND-012.

Factor the existing dnlib writer setup so identical `SaveModuleOptionsVM` settings can write to a filename or caller-owned stream. Preserve `NativeWrite`/mixed-mode behavior and normal mmap handling; add stream/file equivalence tests for unsigned ordinary modules.

Acceptance: ordinary Save Module output is unchanged and an in-memory serialization can be reopened by dnlib.

### BND-014 — Explicit strong-name save dispositions (PR-04)

Dependencies: BND-013.

Add guard UI, reversible output-only remove transform, re-sign key selection/writer initialization, resources, and signed fixture tests.

Acceptance: signed output cannot proceed without cancel/remove/re-sign; emitted remove/re-sign state validates; cancellation writes nothing; unsigned behavior is unchanged.

### BND-015 — Bundle editing and Save Module As proof (PR-04)

Dependencies: BND-014.

Exercise the existing edit/undo model against a bundle module, save standalone, reopen, verify changed IL/constant, and compare source bundle hash. Include ordinary edit/save regression.

Acceptance: PR-04 milestone is complete; no workspace/rebundle code is included.

### BND-016 — Transactional core workspace (PR-05)

Dependencies: BND-015.

Add replacement metadata, defensive copies, current logical streams, events, one/all revert, and disposal tests independent of dnlib/UI.

Acceptance: multiple replacements work; failed/pre-install validation leaves last valid state; original entry bytes remain available.

### BND-017 — Apply module changes to workspace (PR-05)

Dependencies: BND-016.

Wire `IDsBundleEntryDocument` and the AsmEditor Apply command, including R2R block, complete serialization, strong-name disposition metadata, replacement reopen validation, undo saved-to-workspace state, and failure UI.

Acceptance: one/multiple managed replacements validate; failure is transactional; Save Module As remains independent; source is unchanged.

### BND-018 — Revert commands and dirty tree state (PR-05)

Dependencies: BND-017.

Add entry/root dirty rendering, Revert Bundle Entry, Revert All Bundle Changes, tree refresh events, and tests for unchanged/modified/reverted/error states.

Acceptance: one/all revert restores logical originals without reopening; multiple entries and prior valid replacements remain coherent.

### BND-019 — Dirty workspace close guards (PR-05)

Dependencies: BND-018.

Add the named/order metadata interface, export attribute/constants, shared `DsDocumentCloseGuardService`, UI-dispatching `TryExecute`, and exact integrations for `Remove(key)`, `Remove(IEnumerable)`, `Clear`, direct removal, `DocumentListLoader.Load/Reload/CloseAll`, and app close from section 3.8; add bundle Save/Discard/Cancel behavior plus worker-thread, ordering, reentrancy, and lock tests.

Acceptance: every enumerated removal path cannot silently discard dirty workspace data; guards run deterministically by numeric order then ordinal name; worker calls marshal synchronously to the main dispatcher; cancel occurs before tabs/lists/documents mutate; only the exact authorized nested Clear avoids a second prompt; duplicate names, exceptions, and reentrancy fail closed; prompts execute with no document/workspace/resolver lock held; ordinary notifications/order and no-guard behavior are unchanged.

### BND-020 — Official HostModel source and provenance gate (PR-06)

Dependencies: BND-019 (mandatory sequential execution therefore includes the completed parser BND-001 through BND-006).

Create the library project with `EnableDefaultCompileItems=false`; import exactly the 15-file/hash closure in section 3.9; apply only the five canonical patch artifacts and verify every patch/hunk/result hash; add the hashed upstream license, explicit compile list, conditional package references, and lock file. Do not add dnSpy rebuild code.

Acceptance: all 15 pristine hashes, five complete patch hashes, 15 ordered hunk hashes, five result hashes, and license hash match section 3.9; `PEOffsets.cs` is compiled, `PlaceHolderNotFoundInAppHostException` derives from `System.Exception`, and no omitted apphost exception is referenced; an MSBuild compile-item assertion proves exactly the listed 15 paths and no unlisted source; the locked dependency graph contains only the stated direct packages plus resolved transitives; license/provenance review passes; the project builds net10; and a v6 Windows bundle produced by the subset reopens with the parser completed in BND-001 through BND-006. If rejected, all later rebuild tickets remain blocked and no custom bundler is substituted.

### BND-021 — HostModel net48 compatibility and parity (PR-06 gate)

Dependencies: BND-020.

Make only documented compatibility adaptations needed to target net48 and net10; compare v1/v2/v6 logical output with the SDK HostModel assembly using isolated loading on net10.

Acceptance: both target frameworks compile on Windows; parity fixtures reopen and have equivalent inventory/content; no installed-SDK runtime dependency exists.

### BND-022 — Windows PE/Authenticode eligibility inspection (PR-06)

Dependencies: BND-021.

Implement Windows x64 eligibility, PE certificate-table detection, unknown/R2R/ambiguous-inventory preflight, source hashing, and precise unsupported diagnostics. Do not reconstruct or write a bundle.

Acceptance: supported fixture passes; signed, wrong-architecture, unknown-type, dirty-R2R, and malformed cases report the specified preflight result without mutation.

### BND-023 — Temporary apphost reconstruction (PR-06)

Dependencies: BND-022.

Implement payload-prefix copy, exact header-pointer reset, temporary certificate-directory clearing, HostModel placeholder validation, and cleanup.

Acceptance: reconstructed host is valid HostModel input; source hash is unchanged; malformed boundaries fail and temp artifacts are cleaned.

### BND-024 — HostModel rebuild input and generation (PR-06)

Dependencies: BND-023.

Implement private flat entry materialization, current-entry selection, manifest-generation/app-name/options mapping, the mandatory reconstructed-host `FileSpec`, HostModel call, cancellation, and unconditional temp cleanup. Output remains in a private temp directory.

Acceptance: HostModel receives exactly one host `FileSpec` whose `SourcePath` is the reconstructed host and whose `BundleRelativePath` equals the `hostName` constructor argument; a missing, duplicate, or mismatched host fails before generation. It generates v1 (raw-zero output with bounded inferred inventory), uncompressed/compressed, and .NET 5 compatibility-mode bundles with one/multiple replacements; nonzero v1 raw types fail with a preservation diagnostic; parser flags prove compatibility mode maps to `BundleOptions.BundleAllContent`; no bundle relative path is used as a disk extraction path.

### BND-025 — Rebuild validation and atomic publication (PR-06)

Dependencies: BND-024.

Reopen generated output, compare ordered paths/types/logical content and replacement bytes, then atomically publish to a non-source destination; preserve an existing destination on validation/failure.

Acceptance: only fully validated output appears at the destination; source hash never changes; corrupted generated output is rejected.

### BND-026 — Save Bundle As UI and Authenticode warning (PR-06)

Dependencies: BND-025.

Add File/context command, save picker excluding source, progress/cancellation, error reporting, Authenticode warning, success state update, and close-guard integration.

Acceptance: user can create a new validated bundle; warning is explicit; cancel/failure leaves source/destination/workspace safe.

### BND-027 — Rebuild logical-equivalence integration tests (PR-06)

Dependencies: BND-026 and approved CI-002 from `docs/specs/ci-completion.md`.

Cover ordered inventory, config/native/symbol preservation, compression behavior, FDD/SCD, PDB, multiple assemblies, corrupt source, source non-overwrite, and own-parser reopen.

Adopt and finish the three pre-existing untracked files in place; they are the starting implementation, not disposable/generated output:

- `Tests/dnSpy.Bundles.IntegrationTests/BundleLogicalEquivalenceTests.cs`
- `Tests/dnSpy.Bundles.IntegrationTests/IntegrationFixtureLocator.cs`
- `Tests/dnSpy.Bundles.IntegrationTests/OrdinaryOpenSaveRegressionTests.cs`

Before editing, record their hashes. BND-027 may revise them only where review, compilation, or the acceptance contract requires it, and its commit owns all three complete files. It must not stage `docs/specs/netspy-ui-branding.md` or unrelated work. `IntegrationFixtureLocator` remains the shared, deterministic locator for the modern artifact root and is retained for BND-028; it must fail with an actionable missing-fixture diagnostic rather than skip. The logical comparison is entry-order/type/path/logical-byte based; compressed representation and HostModel physical offsets are deliberately not byte-for-byte contracts.

Initial SHA-256 preservation record (2026-09-03):

| File | SHA-256 |
|---|---|
| `BundleLogicalEquivalenceTests.cs` | `fa8966a0331d0192dbbaf1fa2d80018f64e50caf03e2e54d3ff54d046f55a81d` |
| `IntegrationFixtureLocator.cs` | `9923b1eb6932ff049358bd571639fa3bfc00a63eb936386a6581a35f67e06753` |
| `OrdinaryOpenSaveRegressionTests.cs` | `16f7e10a075c7361223b87d3ff184c46d9fc723ff89f79d6a84d598486dbdb9f` |

BND-027 extends `IntegrationFixtureLocator` with the historical sidecar contract below and adds a `.NET Core 3.1` / manifest-v1 `scd-uncompressed` logical-equivalence row. That row must reopen source and rebuilt output with this parser; compare ordered paths, raw/file types, and logical bytes; retain both managed assemblies and runtime/native inventory; execute no unsupported compression assertion; and prove source-hash stability. This retains the specification's promised v1 SCD scope.

Historical lookup API:

```csharp
internal static HistoricalIntegrationFixture FindHistorical(
    string generation,
    string variant);

internal sealed record HistoricalIntegrationFixture(
    string Generation,
    string Variant,
    string TargetFramework,
    string RuntimeIdentifier,
    int ManifestMajorVersion,
    bool SelfContained,
    bool Compressed,
    bool IncludesSymbols,
    bool CompatibilityMode,
    string VariantRoot,
    string BundlePath);
```

`DNSPY_BUNDLE_FIXTURES` is an ordered platform-delimited list of roots. For historical lookup, each root may be the common `artifacts/historical` directory, one generation directory, one variant directory, or a `fixture.json` path. The locator normalizes each candidate, searches only the exact `<generation>/<variant>/fixture.json` shape implied by that root, and never recursively selects the first matching filename. It requires exactly one valid result across the configured roots; zero gives an actionable generation/variant diagnostic and duplicates give an ambiguity diagnostic.

The UTF-8 `fixture.json` contract is schema version `2` and requires exact `generation`, `variant`, `targetFramework`, `runtimeIdentifier == "win-x64"`, `manifestMajorVersion`, boolean `selfContained`, `compressed`, `includesSymbols`, and `compatibilityMode`, plus relative `bundle`, `inventory`, and `hashes` paths. Additional schema-v2 fields such as `buildMainAssembly`, `buildDependencyAssembly`, `publishedFiles`, and `expectedEntries` are allowed and ignored by the locator. The locator rejects an unknown schema version, missing/wrongly typed required members, rooted paths, traversal, paths escaping `VariantRoot`, absent files, a sidecar generation/variant mismatch, and a bundle whose resolved path is not a file. BND-027 adds `IntegrationFixtureLocatorTests.cs` covering common-root, generation-root, variant-root, direct-sidecar, multiple-root fallback, missing, duplicate, malformed, traversal, and metadata-mismatch cases. The execution tests consume the returned typed metadata rather than infer properties from directory names.

Acceptance: covered fixtures preserve promised semantics and unsupported cases fail with precise messages; the ordinary DLL and EXE are opened through the existing provider, saved, reopened with dnlib, and leave their sources unchanged; all three adopted files plus the focused new locator-test file are reviewed and committed together with no unrelated path.

### BND-028 — End-to-end execution matrix and CI gates (PR-06)

Dependencies: BND-027.

On Windows, edit the compiled `SingleFile.App.Program.Main` string operand from `BUNDLE_VALUE=v1` to `BUNDLE_VALUE=v2` through the existing dnlib/AsmEditor edit path, apply the serialized `SingleFile.App.dll` to the bundle workspace, rebuild, execute, and require exactly `BUNDLE_VALUE=v2` with exit code `0`. (`BundleValue.Value` is a `const` and is inlined, so editing only the dependency field would not prove changed runtime behavior.) Cover `.NET Core 3.1` v1 SCD; net5 FDD, SCD, and compatibility-mode SCD; net6 FDD, SCD, and compressed SCD; net8 SCD; and net10 FDD and SCD. Every fixture also contains the second managed project assembly and the rebuilt inventory must retain it. Execute the unmodified source first and require exactly `BUNDLE_VALUE=v1` with exit code `0`, hash it before/after, and use a 30-second process timeout with asynchronous stdout/stderr draining and process-tree termination on timeout.

Add `BundleExecutionEndToEndTests.cs` and `OrdinaryEndToEndRegressionTests.cs`. Reuse `IntegrationFixtureLocator` and the downloaded historical artifact root through `DNSPY_BUNDLE_FIXTURES`; do not regenerate historical fixtures inside the test process. The ordinary regression creates or uses an ordinary console module, executes it before and after the existing edit/save pipeline, and proves bundle-specific publication is not involved.

Extend `.github/workflows/build.yml` with the two canonical jobs below. Keep the four product modes as required jobs. No runtime row may skip because an SDK/runtime/fixture is absent.

```yaml
  bundle-core-tests:
    name: Bundle portable core tests
    needs: historical-bundle-fixtures
    runs-on: windows-latest

  bundle-integration-tests:
    name: Bundle Windows integration tests
    needs: [build, historical-bundle-fixtures, historical-bundle-tests, bundle-core-tests]
    runs-on: windows-latest
```

`bundle-core-tests` checks out without submodules, installs SDK `10.0.111`, downloads `dnSpy-single-file-historical-*` with `merge-multiple: true` into `Tests/TestAssets/SingleFile/artifacts/historical`, validates all five generation sidecars, runs `Generate-ModernFixtures.ps1 -Clean`, explicitly restores and builds the test project once, then executes exactly these three disjoint test partitions:

```powershell
$project = (Resolve-Path 'Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj').Path
$historicalRoot = (Resolve-Path 'Tests/TestAssets/SingleFile/artifacts/historical').Path
$modernRoot = (Resolve-Path 'Tests/TestAssets/SingleFile/Net10/artifacts/net10.0').Path
$sdkRoot = (Resolve-Path 'Tests/TestAssets/SingleFile/Net10').Path

Push-Location $sdkRoot
try {
  $actualSdk = (dotnet --version).Trim()
  if ($actualSdk -ne '10.0.111') { throw "Expected SDK 10.0.111, got $actualSdk" }

  dotnet restore $project
  if ($LASTEXITCODE -ne 0) { throw 'Bundle core restore failed' }
  dotnet build $project -c Release -f net10.0 --no-restore
  if ($LASTEXITCODE -ne 0) { throw 'Bundle core build failed' }

  # ModernFixtureLocator sees only the modern layout. It cannot recursively
  # consume historical schema-v2 sidecars from the downloaded common root.
  $env:DNSPY_BUNDLE_FIXTURES = $modernRoot
  dotnet test $project -c Release -f net10.0 --no-build --no-restore `
    --filter 'FullyQualifiedName~ModernPublishedBundleTests' `
    --logger 'trx;LogFileName=bundle-modern.trx'
  if ($LASTEXITCODE -ne 0) { throw 'Modern bundle fixture tests failed' }

  # HistoricalPublishedBundleTests sees only the historical schema-v2 root.
  $env:DNSPY_BUNDLE_FIXTURES = $historicalRoot
  dotnet test $project -c Release -f net10.0 --no-build --no-restore `
    --filter 'FullyQualifiedName~HistoricalPublishedBundleTests' `
    --logger 'trx;LogFileName=bundle-historical.trx'
  if ($LASTEXITCODE -ne 0) { throw 'Historical bundle fixture tests failed' }

  # The remainder excludes historical tests but includes other core/rebuild
  # tests that intentionally consume ModernFixtureLocator.
  $env:DNSPY_BUNDLE_FIXTURES = $modernRoot
  dotnet test $project -c Release -f net10.0 --no-build --no-restore `
    --filter 'FullyQualifiedName!~ModernPublishedBundleTests&FullyQualifiedName!~HistoricalPublishedBundleTests' `
    --logger 'trx;LogFileName=bundle-portable-remainder.trx'
  if ($LASTEXITCODE -ne 0) { throw 'Remaining portable bundle tests failed' }
}
finally {
  Remove-Item Env:DNSPY_BUNDLE_FIXTURES -ErrorAction SilentlyContinue
  Pop-Location
}
```

All three commands must succeed, and their filters are mutually exclusive and exhaustive for the two fixture-owning classes plus the remainder. Both modern and remainder partitions expose only `$modernRoot`; the historical partition exposes only `$historicalRoot`. The job uploads all three TRX files on `always()`. It must not set one multi-root value for the portable suite: `ModernFixtureLocator` owns only the modern generated-artifact layout, while historical schema-v2 discovery is owned by `HistoricalPublishedBundleTests` and the generation-aware integration locator.

`bundle-integration-tests` checks out with submodules, installs SDK `10.0.111` for the test host and x64 shared runtimes for the net5/net6/net8/net10 FDD rows, downloads the same historical artifacts at the same root, runs `Generate-ModernFixtures.ps1 -Clean`, sets `DNSPY_BUNDLE_FIXTURES` to the historical common root plus the modern `Net10/artifacts/net10.0` root, and executes exactly. The .NET Core 3.1 row is self-contained and must execute with shared-framework lookup disabled for that child process, proving the rebuilt v1 SCD is actually self-contained.

```powershell
dotnet test Tests\dnSpy.Bundles.IntegrationTests\dnSpy.Bundles.IntegrationTests.csproj `
  -c Release -f net10.0-windows --no-restore `
  --filter 'FullyQualifiedName~BundleLogicalEquivalenceTests|FullyQualifiedName~OrdinaryOpenSaveRegressionTests|FullyQualifiedName~IntegrationFixtureLocatorTests|FullyQualifiedName~BundleExecutionEndToEndTests|FullyQualifiedName~OrdinaryEndToEndRegressionTests' `
  --logger 'trx;LogFileName=bundle-integration.trx'
```

The job performs an explicit restore before the command and uploads TRX plus the test result/diagnostic directory on `always()`. Artifact download must retain generation directories; the job validates exact sidecar paths before tests.

The integration job's setup list is exactly `5.0.408`, `6.0.428`, `8.0.419`, and `10.0.111`; the installed SDKs provide the x64 shared runtimes required by FDD execution. For every SCD child process the harness sets `DOTNET_MULTILEVEL_LOOKUP=0` and points `DOTNET_ROOT`/`DOTNET_ROOT_X64` at an existing empty test directory; an SCD result that depends on an installed shared framework therefore fails. Tests launch the rebuilt `.exe` directly with an empty test-controlled working directory and no shell. Exit, timeout, stdout, and stderr diagnostics name the generation and variant.

Acceptance: every advertised rebuild/runtime row, including .NET Core 3.1 v1 SCD, has source-output, rebuilt-output, exit-code, timeout, and source-hash evidence; CI runs the complete portable core suite, Windows integration filters, and all four existing build modes. A fresh GitHub Actions run at the exact BND-028 candidate SHA succeeds under the exact remote contract below, and its run ID/URL/SHA/job conclusions are recorded in the BND-028 ledger row. R2R editing, NativeAOT editing, non-x64 rebuild, ELF rebuild, and Mach-O rebuild remain explicitly unsupported and are not silently skipped matrix rows.

### Dependency graph

```text
PR-01 specification
  -> 001 -> 002 -> 003 -> 004 -> 005 -> 006
  -> 007 -> 008 -> 009 -> 010 -> 011 -> 012
  -> 013 -> 014 -> 015
  -> 016 -> 017 -> 018 -> 019
  -> 020 -> 021 -> 022 -> 023 -> 024 -> 025 -> 026 -> 027 -> 028
```

The remaining-work sequence has an additional prerequisite: CI-001 -> CI-002 from `docs/specs/ci-completion.md` must be approved before BND-027. After BND-028, visible branding proceeds as NSPY-001 -> NSPY-002 -> NSPY-003 -> NSPY-004. This preserves the historical BND numbering while making the combined delivery order unambiguous.

The arrows are mandatory execution order, not merely opportunities inferred from technical dependencies: after the approved PR-01 documentation commit, BND-001 through BND-028 execute strictly by number, with one focused Luna implementation, one independent review, and one approved local commit each. A ticket may mention an especially important earlier prerequisite, but that never permits skipping or parallelizing intervening numbers. Changing this sequence requires an explicit specification revision.

This specification file is already modified coordinating work. Until specification review is approved, only the specification author/reviewer loop may edit or stage it. After approval, BND-027 and BND-028 may each stage only its own ledger-row update alongside ticket-owned implementation; CI-001, CI-002, and every NSPY ticket must leave this file byte-identical and unstaged. A CI or branding commit containing this file fails scope review.

## 10. Ticket status ledger

| Ticket | Phase | Status | Commit | Verification evidence |
|---|---|---|---|---|
| PR-01 | PR-01 | Completed | Documentation commit | Approved design/spec documentation; `git diff --check` |
| BND-001 | PR-02 | Completed | `feat(BND-001): scaffold bundle core` | Architecture and managed-DLL regression tests pass; net10/net48 core builds pass; full Windows `build.ps1` blocked locally because `pwsh` is unavailable |
| BND-002 | PR-02 | Completed | `feat(BND-002): parse bundle manifest headers` | 25 header/adversarial tests and managed-DLL regression pass; full 27-test suite and net10/net48 core builds pass; Windows `build.ps1` blocked locally because `pwsh` is unavailable |
| BND-003 | PR-02 | Completed | `feat(BND-003): parse and bound bundle entries` | 20 entry-validation and 5 uncompressed-stream tests pass; full 52-test suite and net10/net48 builds pass; Windows `build.ps1` blocked locally because `pwsh` is unavailable |
| BND-004 | PR-02 | Completed | `feat(BND-004): read compressed bundle entries` | 17 compressed and 6 uncompressed-stream tests pass; full 70-test suite and net10/net48 builds pass; Windows `build.ps1` blocked locally because `pwsh` is unavailable |
| BND-005 | PR-02 | Completed | `test(BND-005): validate modern published bundles` | Five SDK 10.0.111 fixtures generated; 3 real-bundle and 1 synthetic regression tests pass; full 74-test suite and net10/net48 builds pass; generated artifacts ignored; PowerShell/Windows full build unavailable locally |
| BND-006 | PR-02 | Completed | `test(BND-006): add historical bundle matrix` | Windows CI requires pinned SDK 3.1/5/6/8/10 artifacts and exact inventory/compression assertions; locally 74 tests pass with 3 historical prerequisite skips and net10/net48 builds pass; historical PowerShell generation/full build unavailable here |
| BND-007 | PR-03 | Completed | `feat(BND-007): add bundle document provider` | Extension and Windows integration test assembly compile for net10.0-windows; 74 portable tests pass with 3 historical skips; Windows test execution is blocked locally by unavailable Microsoft.WindowsDesktop.App runtime |
| BND-008 | PR-03 | Completed | `feat(BND-008): add bundle tree views` | Extension/integration assemblies compile cleanly for net10.0-windows; 74 portable tests pass with 3 historical skips; focused WPF tests require Windows Desktop runtime unavailable locally |
| BND-009 | PR-03 | Completed | `feat(BND-009): load bundled managed modules` | Extension/integration assemblies compile cleanly for net10.0-windows; 74 portable tests pass with 3 historical skips; managed-tree runtime tests require Windows Desktop runtime unavailable locally |
| BND-010 | PR-03 | Completed | `feat(BND-010): resolve same-bundle assemblies` | Contextual resolver integration project builds with 0 warnings and 0 errors; exact resolver and ordinary-regression filters are blocked locally by unavailable Microsoft.WindowsDesktop.App 10.0.0; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-011 | PR-03 | Completed | `test(BND-011): prove bundle decompile integration` | Decompiler/analyzer integration project builds with 0 errors (1 existing package warning on the final incremental build); exact bundle and ordinary decompiler filters are blocked locally by unavailable Microsoft.WindowsDesktop.App 10.0.0; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-012 | PR-03 | Completed | `feat(BND-012): add bundle debug compatibility` | Debug compatibility integration project builds with 0 errors (6 existing warnings); exact bundle and ordinary debug filters are blocked locally by unavailable Microsoft.WindowsDesktop.App 10.0.0; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-013 | PR-04 | Completed | `refactor(BND-013): share module serialization` | AsmEditor integration project builds with 0 errors (6 existing warnings); exact serialization and ordinary-save filters are blocked locally by unavailable Microsoft.WindowsDesktop.App 10.0.0; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-014 | PR-04 | Completed | `feat(BND-014): guard strong-name saves` | Strong-name integration project builds with 0 errors (6 existing warnings); exact guard and unsigned-save filters are blocked locally by unavailable Microsoft.WindowsDesktop.App 10.0.0; emitted remove/re-sign state and cryptographic key/signature binding are covered; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-015 | PR-04 | Completed | `test(BND-015): prove bundle edit and standalone save` | Bundle edit/save integration project builds with 0 errors (6 existing warnings); exact bundle and ordinary edit/save filters are blocked locally by unavailable Microsoft.WindowsDesktop.App 10.0.0; tests cover real undo-document dirty/refresh state, reopened standalone output, and unchanged source-bundle hash; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-016 | PR-05 | Completed | `feat(BND-016): add transactional bundle workspace` | Bundle workspace tests pass 4/4; reader regressions pass 2/2; replacement arrays are defensively copied and exposed through non-public read-only streams; original/current reads, events, one/all revert, invalid-input state preservation, foreign-entry rejection, and owned disposal are covered; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-017 | PR-05 | Completed | `feat(BND-017): apply module changes to bundle workspace` | Exact ApplyModuleToWorkspaceTests and SaveModuleIndependenceRegressionTests filters compile successfully but cannot execute on this Linux host because Microsoft.WindowsDesktop.App 10.0.0 is unavailable; BundleWorkspaceTests pass 4/4; atomic multi-entry apply, R2R rejection, replacement reopen validation, strong-name disposition metadata, saved-to-workspace undo state, failure preservation, standalone-save independence, and source non-mutation are covered; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-018 | PR-05 | Completed | `feat(BND-018): add bundle workspace revert state` | Core bundle tests pass 80 with 3 prerequisite skips; bundle extension, AsmEditor, integration-test, and affected product projects build with 0 warnings/errors; exact BundleWorkspaceTreeStateTests and OrdinaryTreeRefreshRegressionTests filters compile but cannot execute on this Linux host because Microsoft.WindowsDesktop.App 10.0.0 is unavailable; tests cover modified/error/reverted rendering, dispatcher-safe targeted refresh, deterministic teardown, root/child/multi-bundle command discovery, one/all revert with source-mapped original bytes, prior-valid replacement preservation, and ordinary-tree isolation; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-019 | PR-05 | Completed | `feat(BND-019): guard dirty bundle workspace closure` | Contracts, product, bundle extension, and integration-test projects build successfully; exact DocumentCloseGuardContractTests and OrdinaryDocumentRemovalRegressionTests filters compile but cannot execute on this Linux host because Microsoft.WindowsDesktop.App 10.0.0 is unavailable; tests cover real dirty-bundle key/batch/Clear/load/reload/Close All/app-exit paths, synchronous worker-to-UI dispatch with a pumped harness, lock-free prompts, deterministic guard ordering, duplicate metadata, exception/reentrancy fail-closed behavior, exact reason/set/single-use nested-Clear authorization, cancellation before mutation, at-most-one app-exit prompt, and independently seeded ordinary zero-guard notification/order regressions; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-020 | PR-06 | Completed | `feat(BND-020): vendor official HostModel bundle subset` | Exact HostModel net10 build and 4 provenance/generation tests pass; all 15 pristine hashes, five patch hashes, 15 ordered hunk hashes, five result hashes, license, compile closure, locked graph, standalone exception base, and parser-reopened compressed v6 output are covered; modern regression passes 3/3 and the portable suite passes 84 with 3 historical prerequisite skips; `git diff --check` passes; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-021 | PR-06 | Completed | `test(BND-021): prove HostModel parity` | Vendored/SDK HostModel parity passes 4/4 for reopened v1, v2, and compressed v6 logical output; the net10 build, provenance regression (4/4), and portable suite (88 passed, 3 historical prerequisite skips) pass. The exact net48 command is blocked on this Linux host by missing .NET Framework 4.8 reference assemblies (MSB3644), while the same source compiles for net48 with reference-assembly package injection (73 warnings, 0 errors); `build.ps1` is unavailable because `pwsh` is not installed. `git diff --check` and the untracked-file whitespace audit pass. |
| BND-022 | PR-06 | Completed | `feat(BND-022): inspect Windows bundle eligibility` | Windows eligibility tests pass 13/13 and cover bounded 64 MiB managed-entry inspection (including oversized compressed input), read-only source hashing, x64 PE support, Authenticode certificate-table detection/warning, wrong architecture, unknown raw types, dirty ReadyToRun, resolver-matching duplicate identities including non-identity Retargetable differences, malformed bundle/PE/managed entries including direct truncated-PE UnsupportedPlatform diagnostics, NativeAOT, and no-managed-entry rejection; reader/non-mutation regressions pass 3/3; the core builds for net10 and net48 with 0 warnings/errors; the portable suite passes 102 with 3 historical prerequisite skips; `git diff --check` and the untracked-file whitespace audit pass; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-023 | PR-06 | Completed | `feat(BND-023): reconstruct temporary Windows apphost` | WindowsAppHostReconstructorTests pass 6/6, including malformed entry-range and certificate-placement cleanup; SourceNonMutationRegressionTests pass 1/1 with unchanged source hash; the portable suite passes 109 with 3 historical skips; core net10/net48 builds pass with 0 warnings/errors; `git diff --check` and the untracked-file whitespace audit are clean; full `build.ps1` is unavailable because `pwsh` is not installed |
| BND-024 | PR-06 | Completed | `feat(BND-024): generate bundles with HostModel` | WindowsBundleGenerationTests pass 10/10; WindowsBundleEligibilityTests pass 15/15; HostModelParityTests pass 4/4; the portable suite passes 121 with 3 historical prerequisite skips; v1 preserves parser raw-zero/Unknown truth with bounded config/PDB/PE inference and HostModel default all-content output, v2/v6 map compatibility mode to `BundleAllContent` only when flagged and retain native/symbol inventory, and v6 compression follows original compressed entries; current replacements, flat private inputs, exact reconstructed-host `FileSpec`, cancellation, failure cleanup, and disposable temporary-output cleanup are covered; HostModel/core/extension net10 builds are clean; HostModel net48 is clean with injected reference assemblies and core net48 is available with injection (0 errors; unsigned vendored HostModel warning on recompilation), while extension net48 is blocked by the pre-existing `BundleDocumentKey.cs:32` generic `Enum.IsDefined(kind)` incompatibility; whitespace checks are clean; full `build.ps1` is unavailable because `pwsh` is not installed. |
| BND-025 | PR-06 | Completed | `feat(BND-025): publish validated bundles atomically` | BundlePublicationTests pass 5/5 and SourceDestinationPreservationRegressionTests pass 3/3; tests cover ordered paths/types/logical bytes, current replacement bytes, successful source hashing, existing-destination atomic replacement, corrupted generated content/path/type rejection, generation failure, cancellation, source-path rejection, and private-generation cleanup; the portable suite passes 129 with 3 historical prerequisite skips; HostModel/core net10 builds and whitespace checks are clean; independent review approved. With PowerShell now installed, the exact `build.ps1` reaches the missing standalone `msbuild` blocker, `-NoMsbuild` reaches the missing .NET Framework 4.8 reference assemblies blocker, and the authoritative Windows matrix remains configured in GitHub Actions. |
| BND-026 | PR-06 | Completed | `feat(BND-026): add Save Bundle As workflow` | BundleWorkspaceTests pass 6/6 and the portable suite passes 131 with 3 historical prerequisite skips; extension and integration projects build with 0 warnings/errors. Exact SaveBundleAsCommandTests and SaveModuleCommandRegressionTests filters compile but cannot execute on Linux because Microsoft.WindowsDesktop.App 10.0.0 is unavailable. Coverage includes File/context exports, source exclusion, explicit Authenticode warning, cancellation/failure safety, close-guard save integration, successful destination tracking, saved logical baselines, enabled entry/root revert after save, save→revert one/all→resave dirty/clean transitions and refresh events, source non-mutation, and ordinary Save Module isolation; `git diff --check` passes and independent review approved after two baseline-state revisions. The exact `build.ps1` remains blocked locally by missing standalone `msbuild`; the authoritative Windows matrix is configured in GitHub Actions. |
| BND-027 | PR-06 | Planned | — | — |
| BND-028 | PR-06 | Planned | — | — |

The coordinating agent updates exactly one row after independent approval of each ticket and includes that ledger change in the ticket commit.

### 10.1 Exact ticket verification matrix

Every row is mandatory before that ticket is reviewed. Test class names are contracts established by the ticket; implementations must use these names so commands do not drift. The “normal build” command for BND-001 through BND-027 is exactly `pwsh -NoProfile -File ./build.ps1`; BND-028 runs its four modes separately as shown. On this Ubuntu host, PowerShell is installed, but standalone MSBuild and Windows/.NET Framework COM/WPF build support are absent, so the authoritative command must run on Windows CI. Linux runs cross-platform net10 scoped commands; historical generation also requires its five pinned SDKs, while `net10.0-windows`, net48, and execution rows remain explicitly blocked here and must not be claimed locally.

| Ticket | Exact scoped test/build | Exact relevant regression | Exact normal build |
|---|---|---|---|
| PR-01 | `git diff --check && test -f docs/single-file-bundle-design.md && test -f docs/specs/dotnet-single-file-bundles.md` | `git status --short` (documentation files only) | Not applicable: documentation-only PR; baseline command attempted is `pwsh -NoProfile -File ./build.ps1`, blocked on this Linux host as stated above |
| BND-001 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~ArchitectureTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~NormalFileRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-002 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~BundleHeaderReaderTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~NormalFileRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-003 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~BundleEntryValidationTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~UncompressedEntryRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-004 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~CompressedEntryStreamTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~UncompressedEntryRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-005 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~ModernPublishedBundleTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~SyntheticManifestRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-006 | `pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1 && dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~HistoricalPublishedBundleTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~ModernPublishedBundleTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-007 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleDocumentProviderTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryDocumentProviderRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-008 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleTreeNodeTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryTreeNodeRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-009 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleManagedDocumentTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryManagedDocumentRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-010 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleAssemblyResolverTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryAssemblyResolverRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-011 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleDecompilerAnalyzerTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryLoadingDecompilerRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-012 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleDebugCompatibilityTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryDebugPathRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-013 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~ModuleSerializationServiceTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinarySaveModuleRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-014 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~StrongNameSaveGuardTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~UnsignedSaveModuleRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-015 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleEditSaveModuleTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryEditSaveRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-016 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~BundleWorkspaceTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~BundleReaderRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-017 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~ApplyModuleToWorkspaceTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~SaveModuleIndependenceRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-018 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleWorkspaceTreeStateTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryTreeRefreshRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-019 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~DocumentCloseGuardContractTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryDocumentRemovalRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-020 | `dotnet build Libraries/Microsoft.NET.HostModel.Bundle/Microsoft.NET.HostModel.Bundle.csproj -c Release -f net10.0 && dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~VendoredHostModelProvenanceTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~ModernPublishedBundleTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-021 | `dotnet build Libraries/Microsoft.NET.HostModel.Bundle/Microsoft.NET.HostModel.Bundle.csproj -c Release -f net10.0 && dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~HostModelParityTests && dotnet build Libraries/Microsoft.NET.HostModel.Bundle/Microsoft.NET.HostModel.Bundle.csproj -c Release -f net48` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~VendoredHostModelProvenanceTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-022 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~WindowsBundleEligibilityTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~BundleReaderRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-023 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~WindowsAppHostReconstructorTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~SourceNonMutationRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-024 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~WindowsBundleGenerationTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~HostModelParityTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-025 | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~BundlePublicationTests` | `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~SourceDestinationPreservationRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-026 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~SaveBundleAsCommandTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~SaveModuleCommandRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-027 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter 'FullyQualifiedName~BundleLogicalEquivalenceTests\|FullyQualifiedName~IntegrationFixtureLocatorTests'` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryOpenSaveRegressionTests` | `pwsh -NoProfile -File ./build.ps1` |
| BND-028 | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~BundleExecutionEndToEndTests` | `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --filter FullyQualifiedName~OrdinaryEndToEndRegressionTests` | `pwsh -NoProfile -File ./build.ps1 netframework && pwsh -NoProfile -File ./build.ps1 net && pwsh -NoProfile -File ./build.ps1 net-x86 && pwsh -NoProfile -File ./build.ps1 net-x64` |

## 11. Verification commands

### 11.1 Per-ticket portable core gate (this Linux environment)

The current environment has .NET SDK `10.0.111` and PowerShell 7.6.5, but no standalone `msbuild` or .NET Framework 4.8 reference assemblies.

```bash
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0

dotnet build dnSpy/dnSpy.Bundles/dnSpy.Bundles.csproj \
  -c Release -f net10.0

git diff --check
git status --short
```

HostModel and rebuild verification is cumulative but ticket-scoped. Do not run a filter before its owning ticket creates it.

BND-020 runs the HostModel build, provenance contract, and modern published-bundle regression from section 10.1:

```bash
dotnet build Libraries/Microsoft.NET.HostModel.Bundle/Microsoft.NET.HostModel.Bundle.csproj \
  -c Release -f net10.0

dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~VendoredHostModelProvenanceTests

dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~ModernPublishedBundleTests
```

BND-021 reruns the net10 HostModel build, provenance regression, and the parity tests introduced by that ticket. Its net48 build remains a Windows gate as recorded in section 10.1.

```bash
dotnet build Libraries/Microsoft.NET.HostModel.Bundle/Microsoft.NET.HostModel.Bundle.csproj \
  -c Release -f net10.0

dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~VendoredHostModelProvenanceTests

dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~HostModelParityTests
```

BND-022 adds only eligibility inspection, and BND-023 adds only apphost reconstruction. Their scoped/regression pairs exactly match section 10.1 and reference tests that exist by their respective ticket:

```bash
# BND-022
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~WindowsBundleEligibilityTests
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~BundleReaderRegressionTests

# BND-023
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~WindowsAppHostReconstructorTests
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~SourceNonMutationRegressionTests
```

`WindowsBundleGenerationTests` is introduced by BND-024 and is required only from BND-024 onward:

```bash
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~WindowsBundleGenerationTests

dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj \
  -c Release -f net10.0 --filter FullyQualifiedName~HostModelParityTests
```

Linux can generate Windows fixtures:

```bash
(cd Tests/TestAssets/SingleFile/Net10 && \
  test "$(dotnet --version)" = 10.0.111 && \
  dotnet publish App.csproj -c Release -f net10.0 -r win-x64 \
    -p:PublishSingleFile=true -p:DebugType=portable \
    -p:DebugSymbols=true --self-contained true)
```

It cannot execute `net10.0-windows` WPF tests or Windows bundles. Those results must not be claimed locally.

### 11.2 Windows scoped gates

```powershell
dotnet test Tests\dnSpy.Bundles.Tests\dnSpy.Bundles.Tests.csproj `
  -c Release -f net10.0

dotnet test Tests\dnSpy.Bundles.IntegrationTests\dnSpy.Bundles.IntegrationTests.csproj `
  -c Release -f net10.0-windows

dotnet build Extensions\dnSpy.Bundles\dnSpy.Bundles.Extension.csproj `
  -c Release -f net10.0-windows
```

Ticket-specific filters use `--filter FullyQualifiedName~<area>` and are recorded in the ledger.

### 11.3 Required final repository build

The repository documents `build.ps1`; its comment explains that ordinary `dotnet build` does not cover COM-reference behavior. The final authoritative gates run on Windows:

```powershell
.\build.ps1 netframework
.\build.ps1 net
.\build.ps1 net-x86
.\build.ps1 net-x64
```

Equivalently, the final integration run may use:

```powershell
.\build.ps1
```

`-NoMsbuild` is a secondary developer path, not a replacement for the normal CI/MSBuild gate. On this Linux host, the maximum valid subset is the core/test/HostModel net10 commands in section 11.1; absence of PowerShell and Windows WPF/runtime execution is the exact blocker for the full build.

### 11.4 Required remote acceptance for BND-028

After the reviewed BND-028 candidate commit is present on an authorized remote ref, dispatch `.github/workflows/build.yml` and validate the exact SHA and canonical job names:

```powershell
$repo = 'RestitvtorOrbis/netspy'
$candidateRef = '<authorized-branch-or-tag-name>'
$candidateSha = '<40-character-BND-028-commit-sha>'
gh workflow run build.yml --repo $repo --ref $candidateRef

$run = $null
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
  $runs = @(gh run list --repo $repo --workflow build.yml --event workflow_dispatch `
    --commit $candidateSha --limit 10 --json databaseId,headSha,url | ConvertFrom-Json)
  $run = $runs | Where-Object headSha -eq $candidateSha | Select-Object -First 1
  if ($null -eq $run) { Start-Sleep -Seconds 3 }
} while ($null -eq $run -and [DateTime]::UtcNow -lt $deadline)
if ($null -eq $run) { throw "No workflow_dispatch run appeared for $candidateSha" }

gh run watch $run.databaseId --repo $repo --exit-status
$result = gh run view $run.databaseId --repo $repo `
  --json headSha,conclusion,jobs,url | ConvertFrom-Json
if ($result.headSha -ne $candidateSha) { throw "Run used $($result.headSha), expected $candidateSha" }
if ($result.conclusion -ne 'success') { throw "Run conclusion: $($result.conclusion)" }

$requiredJobs = @(
  'Build (netframework)',
  'Build (net)',
  'Build (net-x86)',
  'Build (net-x64)',
  'Historical bundle fixtures (NetCoreApp31)',
  'Historical bundle fixtures (Net5)',
  'Historical bundle fixtures (Net6)',
  'Historical bundle fixtures (Net8)',
  'Historical bundle fixtures (Net10)',
  'Historical bundle parser tests',
  'Bundle portable core tests',
  'Bundle Windows integration tests'
)
foreach ($name in $requiredJobs) {
  $matches = @($result.jobs | Where-Object name -eq $name)
  if ($matches.Count -ne 1) { throw "Expected exactly one required job '$name'; found $($matches.Count)" }
  if ($matches[0].conclusion -ne 'success') { throw "Required job '$name': $($matches[0].conclusion)" }
}
```

Workflow job IDs are respectively `build` (four matrix instances), `historical-bundle-fixtures` (five matrix instances), `historical-bundle-tests`, `bundle-core-tests`, and `bundle-integration-tests`. Dependencies are exact: `historical-bundle-tests.needs = historical-bundle-fixtures`, `bundle-core-tests.needs = historical-bundle-fixtures`, and `bundle-integration-tests.needs = [build, historical-bundle-fixtures, historical-bundle-tests, bundle-core-tests]`. The workflow must retain `strategy.fail-fast: false` for both matrices so every required conclusion is produced.

Record run ID, URL, head SHA, and all twelve conclusions in the BND-028 ledger row. A local Windows run, a rerun of an older SHA, a differently named/missing job, or a run with skipped/cancelled required jobs is not final acceptance.

## 12. Final acceptance criteria

The project is complete only when:

1. Valid official v1/v2/v6 fixtures enumerate and return exact logical bytes; known flags round-trip, unknown bits are rejected, and malformed inputs meet all bounded-failure requirements.
2. Ordinary DLL/EXE loading/editing/saving tests remain green.
3. Bundle tree nodes expose managed and non-managed entries lazily.
4. Existing decompiler/editor/analyzer behavior works for covered managed entries and same-bundle dependency resolution.
5. Standalone module save is valid, explicit about strong names, and leaves source/workspace unchanged.
6. Workspace apply/revert is transactional and dirty state cannot be discarded silently.
7. Windows x64 Save Bundle As uses the licensed official HostModel subset, supplies exactly one correctly named host `FileSpec`, produces a new parser-valid executable, preserves covered logical inventory/compatibility flags, and leaves the source untouched.
8. Authenticode loss is detected/warned; strong-name disposition is explicit; no signature-preservation claim is false.
9. Rebuilt covered fixtures execute with the edited behavior on Windows.
10. The exact tests/builds in the ledger and final gates pass; limitations are reported without advertising untested R2R, NativeAOT, architecture, or platform support.
