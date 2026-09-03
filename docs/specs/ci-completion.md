# CI Completion for Bundle Delivery

Status: ready for independent review
Repository baseline: `73faa5808` (`master`)
Specification date: 2026-09-03
Observed failing run: `33673455945` (`https://github.com/RestitvtorOrbis/netspy/actions/runs/33673455945`)

## 1. Outcome

The existing Windows GitHub Actions workflow will build all four product modes, generate every pinned historical single-file fixture, run the parser matrix, and expose the bundle integration gates needed by BND-027 and BND-028. The repair preserves exact SDK selection and fixes restore/build property mismatches rather than weakening, skipping, or marking failing jobs successful.

This specification owns CI/build mechanics only. Bundle behavior remains owned by `docs/specs/dotnet-single-file-bundles.md`; visible branding remains owned by `docs/specs/netspy-ui-branding.md`.

## 2. Evidence and repository findings

Run `33673455945` used commit `73faa5808` on Windows Server 2025 and produced three distinct failures:

| Job | Observed failure | Root contract violation |
|---|---|---|
| `Build (net)` | `NETSDK1005` for `Microsoft.NET.HostModel.Bundle` and `dnSpy.Bundles`: assets lacked `net10.0` and contained the globally supplied `net10.0-windows` target | One `msbuild -restore -t:Build -p:TargetFramework=net10.0-windows` invocation lets the solution-level global property contaminate restore evaluation of portable project references whose real TFM is `net10.0`. |
| `Historical bundle fixtures (NetCoreApp31)` and `(Net5)` | `MSB1001`, unknown switch `--self-contained`, during `dotnet build` | The generator uses a CLI switch not accepted by these historical SDK build drivers. The MSBuild property is the cross-version contract. |
| `Historical bundle fixtures (Net10)` | `NETSDK1112`, `Microsoft.NETCore.App.Runtime.win-x64` was not downloaded, during `publish --no-build` | Build's implicit restore is not an adequate publish restore contract. Restore, build, and publish must use the same RID, self-contained state, output isolation, and publish properties. |

Net6 and Net8 fixture jobs passed. The NetCoreApp31/Net5 failures skipped artifact upload; the downstream parser job was therefore correctly skipped. `Build (net-x86)`, `Build (net-x64)`, and `Build (netframework)` were cancelled by matrix fail-fast after `Build (net)` failed, so they are unproven rather than known-good.

The worktree also contains pre-existing work which these CI tickets must preserve:

| Path | Classification | Ownership rule |
|---|---|---|
| `Tests/dnSpy.Bundles.IntegrationTests/BundleLogicalEquivalenceTests.cs` | untracked BND-027 implementation | BND-027 owns; CI tickets may not edit or stage it |
| `Tests/dnSpy.Bundles.IntegrationTests/IntegrationFixtureLocator.cs` | untracked BND-027 fixture helper | BND-027 owns; CI tickets may not edit or stage it |
| `Tests/dnSpy.Bundles.IntegrationTests/OrdinaryOpenSaveRegressionTests.cs` | untracked BND-027 regression | BND-027 owns; CI tickets may not edit or stage it |
| `Libraries/Microsoft.NET.HostModel.Bundle/packages.lock.json` | modified restore by-product demonstrating the `net10.0-windows` contamination | Preserve until CI-002 reconciles it from an intentional portable restore; never accept `net10.0-windows7.0` as the portable library's lock target |
| `docs/specs/dotnet-single-file-bundles.md` | already-modified coordinating specification for BND-027/BND-028 | Only the specification/review pass and later BND-027/BND-028 ledger updates may edit or stage it; CI and NSPY implementation commits must not include it |
| `docs/specs/netspy-ui-branding.md` | untracked specification input | specification work only; CI implementation tickets may not edit or stage it |

## 3. Requirements

### CI-R1 — Cross-SDK fixture command contract

For every historical and modern variant, generation uses three explicit phases with identical semantic properties:

1. `dotnet restore <App.csproj> --runtime win-x64` plus `SelfContained`, `SingleFileFixtureRoot`, `PublishSingleFile`, symbol/compression/compatibility flags, deterministic flags, and the generation's isolated intermediate/output paths.
2. `dotnet build ... --no-restore` with the same TFM, RID, `SelfContained`, and properties.
3. `dotnet publish ... --no-build --no-restore` with the same TFM, RID, `SelfContained`, properties, and explicit publish output.

Use `-p:SelfContained=true|false` in all three phases; do not pass `--self-contained` to historical `dotnet build`. Keep each adjacent `global.json`, exact `dotnet --version` assertion, and per-variant output isolation. A missing runtime pack must fail during the explicit restore phase with the full restore command visible in logs.

The same phase helper/argument construction must be used by `Generate-HistoricalFixtures.ps1` and `Generate-ModernFixtures.ps1` where their contracts overlap. Do not add SDK-version string branching when one MSBuild property works across all five SDKs.

### CI-R2 — Product restore/build isolation

`build.ps1` must not combine a solution restore with a global leaf `TargetFramework` value. For each product mode:

- perform a solution/project-graph restore with `TargetFramework` unset so every project evaluates its declared `TargetFramework(s)` (`net48;net10.0` for portable bundle libraries and `net48;net10.0-windows` for product projects);
- for self-contained modes, provide the selected `RuntimeIdentifier` and `SelfContained=True` to restore as well as publish;
- only after successful restore invoke `Build` or `Publish` with the existing selected product TFM/RID and without another implicit restore;
- retain standalone Visual Studio MSBuild for the authoritative COM/WPF build path and retain `-NoMsbuild` as a secondary developer path with equivalent restore/build separation.

The portable projects remain `net48;net10.0`; adding `net10.0-windows`, changing them to Windows-only, or changing their lock targets to hide `NETSDK1005` is forbidden. After an intentional locked restore, `Libraries/Microsoft.NET.HostModel.Bundle/packages.lock.json` must contain `.NETFramework,Version=v4.8` and `net10.0`, and must not contain `net10.0-windows7.0`.

### CI-R3 — Complete and diagnostic workflow

- Set `fail-fast: false` on the product build matrix so all four modes report evidence in one run.
- Keep the five historical SDK jobs independent and artifact names isolated by generation.
- Keep missing fixture artifacts fatal and the parser job dependent on all five fixture jobs.
- Do not use `continue-on-error`, conditional skips, broad retry loops, or warning suppression on restore/build/test gates.
- Add concise log assertions for selected SDK, TFM, RID, and self-contained state before generation.
- Preserve existing product artifact names and layouts.

### CI-R4 — Actual GitHub Actions acceptance

Local emulation is supporting evidence only. Completion requires a fresh run of `.github/workflows/build.yml` in `RestitvtorOrbis/netspy` at the exact candidate commit, not a rerun of failed run `33673455945`.

The candidate commit must first be present on an authorized remote branch or tag (a separate push/PR authorization boundary). `candidateRef` is the branch or tag name accepted by `workflow_dispatch`; run discovery deliberately does not use `--branch`, because tag dispatches are valid too. It selects only the exact commit SHA:

```powershell
$repo = 'RestitvtorOrbis/netspy'
$candidateRef = '<authorized-branch-or-tag-name>'
$candidateSha = '<40-character-CI-002-commit-sha>'
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
  'Historical bundle parser tests'
)
foreach ($name in $requiredJobs) {
  $matches = @($result.jobs | Where-Object name -eq $name)
  if ($matches.Count -ne 1) { throw "Expected exactly one required job '$name'; found $($matches.Count)" }
  if ($matches[0].conclusion -ne 'success') { throw "Required job '$name': $($matches[0].conclusion)" }
}
```

Record the run ID, URL, head SHA, and all ten required job conclusions in the ticket ledger. A branch/tag mismatch that dispatches another SHA is rejected by the `--commit` lookup and `headSha` assertion. Cancelled, skipped, missing, duplicated, or non-success required jobs do not pass.

The required workflow job IDs are `build` (four matrix instances), `historical-bundle-fixtures` (five matrix instances), and `historical-bundle-tests`. Their canonical displayed names are the ten strings asserted above; renaming or removing one requires a specification update rather than weakening the assertion.

## 4. Design and contracts

### 4.1 Generator command builder

CI-001 adds `Tests/TestAssets/SingleFile/FixtureGeneration.Common.ps1`. It is the sole shared phase implementation and exports one function when dot-sourced:

```powershell
function Invoke-SingleFileFixturePhases {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $TargetFramework,
        [Parameter(Mandatory)] [string] $RuntimeIdentifier,
        [Parameter(Mandatory)] [bool] $SelfContained,
        [Parameter(Mandatory)] [string] $PublishRoot,
        [Parameter(Mandatory)] [string[]] $MSBuildProperties
    )
}
```

The function validates absolute project/publish paths, converts `$SelfContained` to lowercase once, rejects caller-supplied `SelfContained`, `TargetFramework`, `RuntimeIdentifier`, `OutputPath`, `NoBuild`, or `NoRestore` duplicates in `$MSBuildProperties`, and invokes exactly:

```text
dotnet restore <project> --nologo --runtime <rid> -p:TargetFramework=<tfm> -p:SelfContained=<value> <properties>
dotnet build   <project> --nologo --configuration Release --framework <tfm> --runtime <rid> --no-restore -p:SelfContained=<value> <properties>
dotnet publish <project> --nologo --configuration Release --framework <tfm> --runtime <rid> --output <publishRoot> --no-build --no-restore -p:SelfContained=<value> <properties>
```

It throws on any nonzero exit code and includes phase, project, TFM, RID, and self-contained state in the error. Both generators dot-source this file relative to `$PSScriptRoot` and call the function once per isolated variant. They retain SDK selection, safe cleanup, metadata/sidecar generation, and manifest assertions; they no longer own independent restore/build/publish argument construction. No function mutates global location or environment state.

Each phase receives the complete property set; it must not rely on an earlier variant leaving a compatible `obj/project.assets.json`. `SingleFileFixtureRoot` remains variant-specific, which makes each restore graph independent.

The generator continues to validate the produced executable, manifest version/flags, inventory, and hashes. Generated binaries remain ignored and are never committed.

### 4.2 Build script restore boundary

Add a small checked invocation helper if needed, but do not redesign packaging. Conceptually the authoritative path becomes:

```text
MSBuild Restore (no global TargetFramework)
  -> MSBuild Build/Publish (selected product TargetFramework; no implicit restore)
  -> existing apphost patch/layout logic
```

For RID publishes, restore and publish receive the same RID and self-contained property. Lock-file reconciliation must be produced by this intentional graph; hand-editing the lock file is not acceptance evidence.

### 4.3 Workflow relationship to bundle completion

CI-001 and CI-002 repair the baseline workflow. BND-027 then adopts the preserved untracked logical-equivalence tests. BND-028 adds the execution tests and their dedicated integration jobs/gates. Branding begins only after BND-028 so branding failures cannot be confused with completion of the bundle MVP.

## 5. Assumptions

- The pinned SDKs remain `3.1.426`, `5.0.408`, `6.0.428`, `8.0.419`, and `10.0.111`.
- `windows-latest` and the currently pinned action major versions remain acceptable; runner-image pinning is not requested.
- The project continues to require Visual Studio MSBuild for authoritative COM reference behavior.
- The user will decide whether to authorize pushing a candidate ref. Without that authority, CI-002 can be implemented and locally reviewed but cannot be marked accepted.

## 6. Non-goals

- Changing product TFMs, SDK versions, NuGet dependency versions, or runtime support.
- Removing historical fixture generations or reducing their variants/assertions.
- Replacing MSBuild with `dotnet build`, containers, another CI provider, or a custom runner.
- Committing generated fixtures, caches, `bin`, or `obj` directories.
- Fixing unrelated compiler warnings or upgrading actions.
- Implementing BND-027/BND-028 behavior or branding in a CI repair commit.

## 7. Dependency-ordered tickets

```text
CI-001 deterministic fixture restore/build/publish
  -> CI-002 product graph restore and green baseline workflow
      -> BND-027 -> BND-028 -> NSPY-001 -> NSPY-002 -> NSPY-003 -> NSPY-004
```

### CI-001 — Make fixture generation cross-SDK and restore-complete

Owned files:

- `Tests/TestAssets/SingleFile/FixtureGeneration.Common.ps1`
- `Tests/TestAssets/SingleFile/Generate-HistoricalFixtures.ps1`
- `Tests/TestAssets/SingleFile/Generate-ModernFixtures.ps1`
- `Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1`
- the CI specification ledger only

Acceptance:

- All five pinned SDK generations use explicit restore/build/publish with identical RID/self-contained/publish properties.
- NetCoreApp31 and Net5 never receive `dotnet build --self-contained`.
- Net10 obtains `Microsoft.NETCore.App.Runtime.win-x64` during restore and publishes with both `--no-build` and `--no-restore`.
- A focused Pester-free PowerShell contract check dot-sources the helper, places an injected `dotnet` shim in a test-created temporary directory prepended to `PATH`, invokes the helper, and asserts the three ordered invocations and identical semantic properties without writing source paths. It also asserts both generators dot-source the common file, call `Invoke-SingleFileFixturePhases`, and contain no direct `dotnet restore`, `dotnet build`, or `dotnet publish` invocation. It restores `PATH` in `finally` and removes only its validated temporary directory. The check lives in `Tests/TestAssets/SingleFile/Test-FixtureGenerationCommon.ps1`.
- Existing manifest, flags, inventory, hash, FDD/SCD, compression, and PDB assertions remain unchanged and pass.
- No generated binary is staged.

### CI-002 — Isolate solution restore and prove the complete baseline workflow

Depends on: CI-001.

Owned files:

- `build.ps1`
- `.github/workflows/build.yml`
- `Libraries/Microsoft.NET.HostModel.Bundle/packages.lock.json` only to reconcile the pre-existing contaminated worktree result through an intentional restore
- the CI specification ledger only

Acceptance:

- Portable bundle projects restore/build as `net10.0` when the product is selected as `net10.0-windows`; no `NETSDK1005` occurs.
- All product modes keep existing output layout and artifact names.
- The lock file has the declared portable targets and locked restore leaves it unchanged.
- The fresh candidate-SHA GitHub Actions run required by CI-R4 has every required job successful; its evidence is recorded below.

## 8. Ticket ledger

| Ticket | Status | Commit | Evidence / notes |
|---|---|---|---|
| CI-001 | pending | — | — |
| CI-002 | pending | — | Must include a fresh GitHub run ID/URL/SHA and all required job conclusions |

## 9. Exact verification

Run the relevant pinned-SDK generation command from repository root after installing that exact SDK. The final Windows candidate runs all of these:

```powershell
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Generate-HistoricalFixtures.ps1 -Generation NetCoreApp31
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Generate-HistoricalFixtures.ps1 -Generation Net5
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Generate-HistoricalFixtures.ps1 -Generation Net6
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Generate-HistoricalFixtures.ps1 -Generation Net8
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Generate-HistoricalFixtures.ps1 -Generation Net10
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Generate-ModernFixtures.ps1 -Clean
pwsh -NoProfile -File .\Tests\TestAssets\SingleFile\Test-FixtureGenerationCommon.ps1

dotnet test Tests\dnSpy.Bundles.Tests\dnSpy.Bundles.Tests.csproj `
  -c Release -f net10.0 --filter 'FullyQualifiedName~HistoricalPublishedBundleTests|FullyQualifiedName~ModernPublishedBundleTests'

pwsh -NoProfile -File .\build.ps1 netframework
pwsh -NoProfile -File .\build.ps1 net
pwsh -NoProfile -File .\build.ps1 net-x86
pwsh -NoProfile -File .\build.ps1 net-x64

dotnet restore Libraries\Microsoft.NET.HostModel.Bundle\Microsoft.NET.HostModel.Bundle.csproj --locked-mode
git diff --exit-code -- Libraries/Microsoft.NET.HostModel.Bundle/packages.lock.json
git diff --check
git status --short
```

Finally execute CI-R4 and retain the `gh run view ... --json` output in the CI-002 ledger evidence. The worktree audit must show the three BND-027 files untouched until BND-027 owns them and must show no generated fixture artifacts.
