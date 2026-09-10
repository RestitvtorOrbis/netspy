# Repair the publish CI prerequisites

Status: approved; independent plan review: `APPROVED`. Repository: `RestitvtorOrbis/netspy`, baseline `d5b102572f95d817502613db1a7e5b07e2bf5c22`.

## Requirements and evidence

Repair the failed GitHub CI prerequisites so the existing publication job can execute after successful validation. Run 34388893161 on master and PR run 34389118773 show the same defects. The failed log is `/tmp/netspy-34388893161.log`. All four product builds succeeded; packaging produced files before its wrapper falsely failed. The publication job itself was gated off.

1. Archive creation must return a real process exit code, fail on actual packaging errors, and retain existing archive, checksum, commit sidecar and publication validation.
2. Pinned historical SDKs must restore/build/publish using supported arguments, selecting implementation assemblies rather than reference assemblies or unrelated copies.
3. Repair integration harness violations of existing contracts without changing metadata-only inventory documents or bypassing production decompilers.
4. Selecting/decompiling a managed inventory entry header opens that entry once and no sibling payloads. Later type/body decompilation and navigation may resolve needed siblings once through the existing contextual resolver. Inventory rendering performs zero logical reads. Ordinary DLL/EXE loading and decompilation remain unchanged.

## Design and contracts

Archive: invoke `pwsh -NoProfile -File` in the workflow, preserving the checked `$LASTEXITCODE` branch. A subprocess boundary test must cover success with an unset and a stale nonzero caller exit status, plus real validation failure. Do not modify publication event gates, artifact names, permissions or upload destinations.

Fixtures: remove cosmetic `--nologo` from all three shared dotnet phases (SDK 3.1 restore rejects it); preserve RID, self-contained, no-build/no-restore and property contracts. Directory.Build.props sets build output to `build/$(MSBuildProjectName)/`; historical logs confirm App output at `build/App/Release/<TFM>/win-x64/SingleFile.App.dll` and dependency output at `build/SingleFile.Dependency/Release/netstandard2.0/SingleFile.Dependency.dll`. Use those exact leaf paths, and `publish/SingleFile.App.exe`; require their existence. SDK 5 can place reference output below the build tree, making recursive basename uniqueness invalid. The failed log does not enumerate the second candidate; the exact path contract avoids dependence on its origin. Keep strict sidecar generation and hashes of implementation bytes. Put an exact-path helper in the shared PowerShell file for direct tests; do not use first-match selection or disable reference generation.

Harness: DispatchProxy derives from its supplied proxy class; unseal the two BundleManagedDocumentTests proxy classes and the BundleAssemblyResolverTests DocumentServiceProxy used by scoped resolver tests. Metadata BundleEntryDocument.ModuleDef remains null after explicit activation; the loaded ModuleDef lives on ManagedDocument. Detached tree nodes have no Context, so invoke their existing `ToString(IDecompiler, ...)` overload, not the parameterless overload that dereferences Context.

Lazy header: CSharpDecompiler.Decompile(AssemblyDef) invokes `ctx.DisableAssemblyLoad()` before AstBuilder.AddAssembly and attribute transforms; AstBuilder resolves attribute types. Production contexts inhibit DsDocumentService loading, but the per-bundle resolver opens its candidates before consulting that service. Test contexts also omit the callback. Implement an internal, instance-owned `AsyncLocal<int>` nesting counter and idempotent IDisposable scope on BundleAssemblyResolver. During a scope, requests from that bundle retain already-loaded workspace selection/ambiguity and already-loaded top-level lookup, but do not activate candidates or invoke fallback. DsDocumentService.FindAssembly (lines 222 onward) searches existing documents and its temporary cache only, so this top-level lookup does not perform new resolution. Requests from unrelated sources, and other resolver instances, retain existing behavior. In BundleManagedEntryDocumentNode.Decompile, activate the selected module first and use that scope around the existing assembly/module header decompiler call; disposal must restore resolution on success and exception. Do not mutate shared DecompilationContext callbacks, dnlib module contexts or global service state. No public API changes and no ILSpy/submodule changes. Direct ordinary assembly nodes are outside this patch's scope; this repair addresses the inventory-node header path reported by CI.

## Assumptions and non-goals

Parent AGENTS.md and pre-spec.md apply; no nested AGENTS.md was found. This corrective CI work does not introduce later roadmap features or alter pre-spec architecture. Git author is configured. User requests Luna xhigh delivery, overriding skill max. No push, workflow dispatch, GitHub release creation, asset upload or external publication is authorized here. Existing mode-only build.ps1 modification and untracked IntegrationFixtureLocator.cs, BundleLogicalEquivalenceTests.cs and OrdinaryOpenSaveRegressionTests.cs must remain untouched and unstaged. No parser, rebuild, signatures, editor or packaging format redesign. No broad robustness campaign or repairs to unrelated tests.

## Dependency graph and ledger

Execute sequentially: PCI-01 → PCI-02 → PCI-03 → PCI-04. Each has a separate implementation/review/commit boundary after the dedicated reviewed plan commit.

| Ticket | Status | Evidence / commit |
|---|---|---|
| [PCI-01: archive process boundary](publish-ci-repair-tickets/PCI-01.md) | approved | `fix(ci): PCI-01 use process exit status for release archives` |
| [PCI-02: historical fixture compatibility](publish-ci-repair-tickets/PCI-02.md) | approved | `fix(fixtures): PCI-02 support historical SDK output contracts` |
| [PCI-03: integration harness contracts](publish-ci-repair-tickets/PCI-03.md) | approved | `test(bundles): PCI-03 correct integration harness contracts` |
| [PCI-04: lazy header resolution](publish-ci-repair-tickets/PCI-04.md) | planned | pending |

## Acceptance and final verification

Every requirement above must have passing scoped evidence or an explicit environmental limitation. Never claim a green Windows CI run without observing one. Keep all existing filters; append resolver and tree-node test classes for the repaired paths. Do not skip failures or remove assertions to get green. Capture commands, exit codes, changed paths, limitations and local commits in ledger/tickets.

Run from repository root (PowerShell commands work via pwsh on Linux):

```text
pwsh -NoProfile -File Build/Test-ReleasePackaging.ps1
pwsh -NoProfile -File Build/Test-ReleasePublication.ps1
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~ModernPublishedBundleTests
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter 'FullyQualifiedName!~HistoricalPublishedBundleTests'
```

For each installed historical SDK, run `pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1 -Generation NetCoreApp31` (repeat replacing generation with Net5, Net6, Net8, Net10). Required SDKs: 3.1.426, 5.0.408, 6.0.428, 8.0.419, 10.0.111. When all five outputs exist, in PowerShell:

```powershell
$env:DNSPY_BUNDLE_FIXTURES = (Resolve-Path 'Tests/TestAssets/SingleFile/artifacts/historical').Path
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~HistoricalPublishedBundleTests
```

Windows with Visual Studio MSBuild and pinned SDKs:

```text
msbuild Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -nologo -m -t:Restore -p:Configuration=Release
msbuild Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -nologo -m -t:Build -p:Configuration=Release -p:TargetFramework=net10.0-windows
dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter 'FullyQualifiedName~BundleDocumentKeyTests|FullyQualifiedName~BundleDocumentProviderTests|FullyQualifiedName~BundleManagedDocumentTests|FullyQualifiedName~BundleDecompilerAnalyzerTests|FullyQualifiedName~OrdinaryLoadingDecompilerRegressionTests|FullyQualifiedName~BundleOpenPipelineTests|FullyQualifiedName~BundleAssemblyResolverTests|FullyQualifiedName~BundleTreeNodeTests'
pwsh -NoProfile -File build.ps1 all
```

Current Linux environment has pwsh and SDK 10.0.111 only. Attempt `pwsh -NoProfile -File build.ps1 all -NoMsbuild` and capture actual blocker; WPF/WindowsDesktop, COM and Visual Studio MSBuild requirements prevent full equivalence. Attempt integration cross-build with `dotnet build Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -p:EnableWindowsTargeting=true`; do not present compilation as execution. Run the maximum portable subset above, Net10 historical generation, and report missing historical SDKs separately. Do not install retired SDKs or change production build paths merely to circumvent environment constraints. Preserve untracked tests during these checks; report any failure sourced from them separately. Finish with `git diff --check`, `git status --short` and `git log -5 --oneline`.
