# Bundle loading verification, CI repair, and downloadable builds

Status: planned; independent review pending. Baseline: `0ceb3fb5ec19feb396f13a32dd36a0c34c4bf7b7`.

## Requirements and evidence

The user requests working decompilation of the installed Hand2Note single-file executable, repair of [run 33879171699](https://github.com/RestitvtorOrbis/netspy/actions/runs/33879171699), and compiled artifacts published in this repository. Downloads mean GitHub Actions archives and GitHub Release assets, not checked-in binaries.

The original six tickets and `../pre-spec.md` have already been implemented substantially. These are narrow follow-up fixes authorized by the current request, not a restart of that roadmap. No parser format or save architecture change is proposed.

Confirmed findings:

- The failed run's .NET Framework build reports the unavailable generic `Enum.IsDefined(kind)` overload at `Extensions/dnSpy.Bundles/BundleDocumentKey.cs:32`.
- All five historical generation jobs fail in the SDK assertion before invoking their generators. The assertion runs at the checkout root, outside each fixture's exact-version `global.json`; `setup-dotnet` installation does not make that SDK the newest installed SDK. The generator already selects its generation working directory correctly. The modern assertion has the same working-directory issue.
- Current production parser opens the user's original executable read-only as manifest v6 with 136 entries, including 126 managed assemblies. Every managed entry successfully materializes through its logical stream and loads with dnlib; `Hand2Note.dll` has 36 top-level types. This is evidence against an inferred parser defect, not proof of all application methods or UI interactions.
- A Windows runtime diagnostic using existing Debug binaries returns `BundleDsDocument` and activates `Hand2Note.dll`. Existing integration tests incorrectly seek `dnSpy.Decompiler.ILSpy.Core.CSharp.DecompilerProvider` in `dnSpy.Decompiler.ILSpy.x`; the type is in `dnSpy.Decompiler.ILSpy.Core`. Debug providers also contain multiple C# variants, so select the normal decompiler by `UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY`.
- The completed temporary Windows diagnostic composes the actual document service and providers through MEF, opens the original Hand2Note executable, and invokes the existing C# decompiler: `Provider=BundleDsDocument entries=136`, `Module=Hand2Note types=36`, assembly output 1157 characters, selected type output 4929 characters, error probe false. The harness built with zero warnings/errors. This bounded success does not establish the cause or resolution of the user's still-unreproduced UI failure, or support for every application method.
- The existing ordinary regression helper exports its settings as `System.Object`, so MEF rejects the service; the test must provide the real settings contract's export type identity. Existing body-text assertions also incorrectly target assembly-level decompiler output; assert headers there and decompile the `Program` type for body text. These defects affect tests, not the production provider.
- The workflow builds four product variants but has no integration-test gate and no Release publication. Three modern build archives were produced by the failing run; the .NET Framework archive was not.

## Inspected architecture and narrow seams

`DsDocumentService.TryGetOrCreate` obtains ordered `IDsDocumentProvider` exports. `BundleDsDocumentProvider` claims validated bundles before `DefaultDsDocumentProvider`, which delegates ordinary files to the existing managed/native creation path. `BundleDsDocument` keeps entries lazy. `BundleManagedEntryAdapter` opens only a selected bounded logical stream, creates file-layout `PEImage` and `ModuleDefMD`, and clears the physical module location. `BundleModuleDocument` reuses `DsDotNetDocumentBase`. Its per-bundle `BundleAssemblyResolver` resolves workspace modules before normal fallback, without installing a global resolver.

`BundleDocumentNodeProvider` supplies container/entry nodes; selected managed entries implement `IDecompileSelf` and call the existing decompiler. The root currently displays its name as a comment. `ModuleSerializationService`, `StrongNameSaveGuard`, and the existing Save Module command own editing/writing; no change to those paths is needed. Tests must reach the real document service through MEF exports and real C# decompiler rather than replacing either.

## Decisions, contracts, and non-goals

1. Preserve `BundleDocumentKey` validation semantics with the .NET Framework-compatible `Enum.IsDefined(typeof(BundleDocumentKeyKind), kind)` call. No public contract change.
2. SDK assertions explicitly use the existing generation directory (and Net10 directory for modern fixtures). Preserve exact SDK pins and fatal checks; do not disable historical jobs or relax equality.
3. Repair the existing two decompiler regression helpers and add one focused test of MEF-discovered bundle loading plus decompilation. Generated fixtures are the portable CI acceptance source. An optional environment variable `NETSPY_EXTERNAL_BUNDLE` enables a local read-only real-file smoke test; absence is an explicit skipped test, while an invalid configured path is a test failure. Proprietary input and decompiled text are never checked in or uploaded.
4. Publish four zip archives named `netSpy-netframework.zip`, `netSpy-net.zip`, `netSpy-net-win32.zip`, and `netSpy-net-win64.zip`. Preserve existing Actions artifact names (`dnSpy-netframework`, `dnSpy-net`, `dnSpy-net-win32`, `dnSpy-net-win64`) for consumers, but store the ready-to-download zip inside each artifact. Archive root is the existing portable executable/bin layout.
5. A successful push or manually dispatched run on `master` publishes an immutable prerelease tag `build-<full github.sha>` and title `netSpy build <first 12 SHA characters>`. A `release: published` run attaches assets to that event's existing release/tag. No tag is force-moved. Pull requests build and test without publishing Releases. Only the publication job receives `contents: write`.
6. Publication depends on all four builds, all historical fixture/parser tests, and the focused Windows load/decompile gate. A failed gate cannot publish a release. Generated fixtures are not Release assets. Existing release asset names must never be silently overwritten: a rerun with a name collision fails with a useful message and leaves the original asset intact.
7. Produce a `SHA256SUMS.txt` covering all four archives and release notes containing the exact source SHA, workflow run URL, download variant requirements, and the supported bundle open/expand/select workflow. Downloaded zip contents must include `dnSpy.exe`, `bin/dnSpy.Bundles.x.dll`, `bin/dnSpy.Bundles.dll`, and `bin/Microsoft.NET.HostModel.Bundle.dll`.
8. For `release: published`, preserve the current release body and append a generated provenance/usage section delimited by `<!-- netspy-build:<full SHA> -->` and `<!-- /netspy-build -->`, using `gh release edit --notes-file`. Fetch the current body immediately before appending; never replace existing prose. If this SHA's opening marker already exists, fail explicitly before editing notes or uploading assets. Newly created automatic prereleases use that same section as their initial body. Existing prereleases reused on a retry follow the append/collision rule as well.

Non-goals: parser replacement, third-party packer support, Hand2Note modification or redistribution, rebuilding its executable, ILSpy modernization, editor/save changes, eager extraction, unrelated test cleanup, strong-name/Authenticode policy changes, debug-feature expansion, and broad robustness testing. Do not claim all Hand2Note methods decompile or the user's reported UI symptom is explained solely by passing parser checks.

## Assumptions and environmental boundaries

The repository is `/home/ramon/netspy/dnSpy`, not its parent. Preserve the pre-existing `build.ps1` executable-bit change and the three untracked integration files `BundleLogicalEquivalenceTests.cs`, `IntegrationFixtureLocator.cs`, and `OrdinaryOpenSaveRegressionTests.cs`; never stage or edit them. Git identity is configured.

The original is accessible at `/mnt/c/Program Files/Hand2Note 4.1/Hand2Note.exe` / `C:\Program Files\Hand2Note 4.1\Hand2Note.exe`. Linux has .NET SDK 10.0.111 and PowerShell; Windows has .NET 10 and WPF runtimes but no installed SDK/MSBuild. Windows-targeted test binaries can be built as far as Linux permits and executed by the Windows runtime over a WSL UNC path. The normal build still requires its documented Windows MSBuild/COM environment; record any precise environmental failure rather than claiming a full build passed.

GitHub credentials currently fail authentication. This does not block local code, tests, packaging logic, or commits. Actual push/dispatch/Release publication remains pending until authenticated access exists. The coordinator must pursue already-authorized remote publication if access becomes available, and report the exact remaining limitation otherwise. No ticket may record remote green status without a matching run SHA.

## Dependency graph and ticket ledger

Execute sequentially: BLC-001 → BLC-002 → BLC-003 → BLC-004. Each implementation has its own independent review and local conventional commit, after a dedicated plan-documentation commit.

| Ticket | Outcome | Status | Evidence / commit |
|---|---|---|---|
| [BLC-001](bundle-load-ci-artifacts-tickets/BLC-001.md) | .NET Framework enum compatibility | Planned | Pending |
| [BLC-002](bundle-load-ci-artifacts-tickets/BLC-002.md) | Pinned SDK assertion scope | Planned | Pending |
| [BLC-003](bundle-load-ci-artifacts-tickets/BLC-003.md) | Real document/decompiler regression gate | Planned | Pending |
| [BLC-004](bundle-load-ci-artifacts-tickets/BLC-004.md) | Validated archives and Release publication | Planned | Pending |

## Acceptance and exact final verification

From repository root, run these commands (PowerShell syntax; pinned SDKs and Windows MSBuild are prerequisites where noted):

```powershell
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0
msbuild Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -nologo -m -t:Restore -p:Configuration=Release
msbuild Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -nologo -m -t:Build -p:Configuration=Release -p:TargetFramework=net10.0-windows
dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter 'FullyQualifiedName~BundleDocumentKeyTests|FullyQualifiedName~BundleDocumentProviderTests|FullyQualifiedName~BundleManagedDocumentTests|FullyQualifiedName~BundleDecompilerAnalyzerTests|FullyQualifiedName~OrdinaryLoadingDecompilerRegressionTests|FullyQualifiedName~BundleOpenPipelineTests'
pwsh -NoProfile -File build.ps1 all
pwsh -NoProfile -File Build/Test-ReleasePackaging.ps1
$env:NETSPY_EXTERNAL_BUNDLE = 'C:\Program Files\Hand2Note 4.1\Hand2Note.exe'
dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter FullyQualifiedName~ExternalBundleOpenSmokeTests
git diff --check
git status --short
git log -5 --oneline
```

Run the maximum available subset locally and record missing SDKs, Windows build tools, runtime limitations, network failures, and missing authorization/authentication separately from test failures. On Windows CI the focused integration gate is mandatory, even if it could not run locally. When authenticated remote execution is possible: push only authorized commits/ref, dispatch `build.yml` on that ref if necessary, use `gh run watch <run-id> --repo RestitvtorOrbis/netspy --exit-status`, verify the run's `headSha` equals the implementation SHA, and verify all four zip assets and checksum file on the expected Release. Pending remote acceptance is reported as pending, never inferred from workflow text.

Final acceptance requires evidence for enum semantics, .NET Framework compilation or explicit environmental limitation, SDK selections from generation directories, generated compressed bundle decompilation through the real document pipeline, ordinary DLL/EXE regression, original real-file nonmutation, complete archives, and Release gating. A local implementation can be delivered while authenticated remote publication remains explicitly unfulfilled.
