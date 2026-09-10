# PCI-02: Use historical SDK compatible fixture commands and exact implementation paths

## Objective and dependencies

Use historical SDK compatible fixture commands and exact implementation paths. Dependencies: PCI-01. Master: [publish-ci-repair](../publish-ci-repair.md).

## In scope and exclusions

`Tests/TestAssets/SingleFile/FixtureGeneration.Common.ps1`, `Test-FixtureGenerationCommon.ps1`, `Generate-HistoricalFixtures.ps1`, and `.github/workflows/build.yml` (add fixture contract script test to historical-fixture job before generation). No project/SDK pin changes.

## Implementation decisions and data flow

Remove --nologo from restore/build/publish arrays and update shim expectations. Introduce shared `Get-RequiredFixtureFile` accepting an exact path and description, requiring a leaf and returning its full path with clear missing-file errors. Historical generator selects build/App/Release/<generationInfo.TargetFramework>/win-x64/SingleFile.App.dll, build/SingleFile.Dependency/Release/netstandard2.0/SingleFile.Dependency.dll, publish/SingleFile.App.exe. Remove obsolete recursive helper. Test exact selection with decoy ref, refint and copied implementation basename files; delete only expected leaf and assert fail despite decoys. Preserve JSON schema, hashes, manifest validation, deterministic properties and phase failure behavior. Extend shim to reject --nologo so compatibility regression is behaviorally exercised.

## Ordered implementation

1. Adjust shared CLI arrays and shim.
2. Add exact-path helper, replace all three recursive generator lookups.
3. Add synthetic output-layout test and CI invocation.
4. Run script test and modern/Net10 historical generation; run older pinned generations if SDKs available, otherwise report missing versions.

## Required tests and exact scoped verification

Use the master Windows restore/MSBuild commands before Windows test execution. On Linux attempt cross-build and record the concrete blocker; never claim execution from a build. Commands from repository root:

```text
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1
pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1 -Generation Net10
dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 --filter FullyQualifiedName~ModernPublishedBundleTests
git diff --check
```

## Acceptance criteria

No phase receives unsupported cosmetic flag; implementation leaf always selected regardless of duplicate basename descendants; absent leaf fails; generated sidecars reference/hash selected implementation outputs.

## Expected commit boundary

`fix(fixtures): PCI-02 support historical SDK output contracts`. Stage only in-scope paths actually changed and this ticket/master ledger after approval.

## Delivery contract

Status: approved. Preserve all pre-existing changes listed in the master spec. Do not push or publish.

## Delivery evidence

- Changed paths: `.github/workflows/build.yml`, `Tests/TestAssets/SingleFile/FixtureGeneration.Common.ps1`, `Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1`, `Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1`.
- Independent Sol review: `APPROVED` with no findings.
- `pwsh -NoProfile -File Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1`: passed independently and by the implementer; the Sol sandbox could not rerun it because `/tmp` was read-only.
- `pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1`: passed with SDK `10.0.111`.
- `pwsh -NoProfile -File Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1 -Generation Net10`: passed all five Net10 variants; exact build/publish paths and sidecars were verified.
- `dotnet test Tests/dnSpy.Bundles.Tests/dnSpy.Bundles.Tests.csproj -c Release -f net10.0 -m:1 --filter FullyQualifiedName~ModernPublishedBundleTests`: compiled the test assemblies, but VSTest could not start because the Linux sandbox denied its TCP listener (`SocketException: Permission denied`).
- `git diff --check`: passed.
- Only SDK `10.0.111` is installed; historical SDKs `3.1.426`, `5.0.408`, `6.0.428`, and `8.0.419`, plus Windows execution, are unavailable.
- Expected local commit: `fix(fixtures): PCI-02 support historical SDK output contracts`.
