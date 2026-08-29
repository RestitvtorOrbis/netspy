# .NET Single-File Bundle Design

Status: PR-01 design companion, ready for review
Normative specification: [`specs/dotnet-single-file-bundles.md`](specs/dotnet-single-file-bundles.md)
Repository baseline: `1f920a801` (`master`)

## Decision

dnSpyEx remains the document shell, tree, decompiler, editor, analyzer, saver, and debugger. A new UI-independent `dnSpy.Bundles` library parses official v1/v2/v6 single-file containers and exposes validated bounded logical entry streams. `Extensions/dnSpy.Bundles` adapts selected managed entries to `PEImage` and `ModuleDefMD`; existing dnSpy behavior takes over from that point. The modified ILSpy submodule is neither replaced nor edited.

The decision-complete requirements, APIs, limits, test matrix, licensing provenance, rebuild algorithm, and 28 mandatory sequential implementation tickets are maintained in the normative companion linked above. If this overview and that specification disagree, implementation stops and PR-01 is revised; it must not choose one silently.

## Existing extension points

- Loading: `dnSpy/dnSpy.Contracts.DnSpy/Documents/IDsDocumentProvider.cs`, ordered before `DefaultDsDocumentProvider`, with normal non-bundles returning `null` unchanged.
- Document creation: `dnSpy/dnSpy/Documents/DsDocumentService.cs` and `dnSpy/dnSpy.Contracts.DnSpy/Documents/DsDocument.cs`; one selected entry becomes a verified byte-backed `PEImage` and `ModuleDefMD`.
- Tree: `IDsDocument.Children`, `IDsDocumentNodeProvider`, and `DocumentTreeView.CreateNode()`; bundle folders/entry metadata are custom nodes while managed modules retain the normal assembly/module shape.
- Resolution: a per-bundle `BundleAssemblyResolver`; already-loaded and unloaded candidates from the requesting bundle precede ordinary loaded documents and the existing resolver. Other open bundles are never candidates.
- Editing/saving: existing `EditCodeVM`, `ModuleImporter`, `MDEditorPatcher`, `SaveModuleCommand`, `DocumentSaver`, `SaveModuleOptionsVM`, and `ModuleSaver`; bundle code adds no editor.
- Debugging: `StartDebuggingOptionsProvider.GetCurrentFilename()` resolves a contained document to its physical top-level apphost.
- Removal/exit: a named/order-metadata MEF close-guard coordinator covers every `IDsDocumentService.Remove` overload, `Clear`, list load/reload/Close All, direct removal, and app close. It synchronously marshals worker calls to the main dispatcher before modal UI and holds no document/workspace lock while evaluating guards.

## Control flows

```text
ordinary DLL/EXE -> existing providers -> existing dnSpy document path

official bundle -> BundleDsDocumentProvider -> BundleDsDocument
                -> lazy BundleEntry bounded logical stream
                -> PEImage -> ModuleDefMD -> existing dnSpy pipeline

Save Module As  -> existing dnlib writer -> standalone module
Apply to bundle -> serialize/validate in memory -> transactional replacement
Save Bundle As  -> reconstructed clean Windows x64 apphost
                -> official pinned HostModel subset -> parser validation
                -> atomic new destination; source remains unchanged
```

## Boundaries and risks

- The parser validates marker/header/entry ranges, checked arithmetic, counts, strings, paths, overlaps, known flags, exact decompression length, and configured allocation limits. It never exposes an unbounded underlying stream.
- Ordinary loading and resolution never enter a bundle-specific path unless the ordered provider proves an official bundle marker/manifest.
- Strong-name removal or re-signing is explicit; Authenticode invalidation is warned and never described as preserved.
- ReadyToRun is inspect-only for bundle apply/rebuild. High-confidence Windows NativeAOT is detected and explained. MVP rebuilding is Windows x64 only.
- Rebuild depends on independent approval of the exact 15-file MIT-licensed HostModel source closure, five canonical Windows-only patches, pristine/patch/hunk/result hashes, and explicit compile list in the normative specification. Rejection blocks rebuilding; it does not authorize a handwritten writer.
- Historical fixtures use exact 3.1.426, 5.0.408, 6.0.428, 8.0.419, and 10.0.111 SDKs. Generated binaries are CI artifacts, not committed files.
- Full WPF/.NET Framework/Windows execution verification requires Windows. This Linux host can run future net10 core/parser tests but lacks `pwsh`, standalone MSBuild, and Windows execution.

## Delivery

PR-01 consists only of this design and the normative specification. After independent approval and one documentation commit, BND-001 through BND-028 execute strictly in numeric order, each as one focused Luna implementation, independent review, and approved local commit. Exact tests, regressions, build commands, and environment blockers are listed per ticket in the normative ledger.
