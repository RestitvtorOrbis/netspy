# PCI-01: Repair archive process exit handling

## Objective and dependencies

Repair archive process exit handling. Dependencies: None. Master: [publish-ci-repair](../publish-ci-repair.md).

## In scope and exclusions

`.github/workflows/build.yml`, `Build/Test-ReleasePackaging.ps1`. Excluded: Publish-Release.ps1, event gates, archive content contract and all fixture/product code.

## Implementation decisions and data flow

Change only the archive step invocation to `& pwsh -NoProfile -File .\Build\New-ReleaseArchive.ps1` with its current parameters and error check. Existing archive helper remains reusable in-process. Extend packaging tests with a child wrapper script using the same invocation and check as CI; test initially absent `$LASTEXITCODE`, initially nonzero `$LASTEXITCODE`, and missing-required-file child failure. Use distinct temp output roots so overwrite rejection does not contaminate success cases; maintain safe cleanup. Verify the workflow invocation remains the tested child-process form using a narrow source assertion. Do not merely set `$LASTEXITCODE=0` or suppress errors.

## Ordered implementation

1. Change workflow invocation.
2. Add boundary tests alongside existing archive content/hash/overwrite checks.
3. Run both packaging and publication script tests; inspect diff for unchanged release gates.

## Required tests and exact scoped verification

Use the master Windows restore/MSBuild commands before Windows test execution. On Linux attempt cross-build and record the concrete blocker; never claim execution from a build. Commands from repository root:

```text
pwsh -NoProfile -File Build/Test-ReleasePackaging.ps1
pwsh -NoProfile -File Build/Test-ReleasePublication.ps1
git diff --check
```

## Acceptance criteria

Successful child process returns 0 even when caller exit state is unset/stale; real packaging failure returns nonzero and wrapper fails; all previous package/publication checks pass.

## Expected commit boundary

`fix(ci): PCI-01 use process exit status for release archives`. Stage only in-scope paths actually changed and this ticket/master ledger after approval.

## Delivery contract

Status: approved. Preserve all pre-existing changes listed in the master spec. Do not push or publish.

## Delivery evidence

- Changed paths: `.github/workflows/build.yml`, `Build/Test-ReleasePackaging.ps1`.
- Independent Sol review: `APPROVED`.
- `pwsh -NoProfile -File Build/Test-ReleasePackaging.ps1`: passed; independently confirmed by the user, including the child boundary checks.
- `pwsh -NoProfile -File Build/Test-ReleasePublication.ps1`: passed all 56 cases.
- `git diff --check`: passed.
- `pwsh -NoProfile -File build.ps1 all -NoMsbuild`: attempted and blocked at Product graph restore because this Linux environment lacks the Windows/MSBuild/WPF/COM prerequisites; no Windows execution is claimed.
- `/tmp/netspy-34388893161.log`: unavailable.
- Expected local commit: `fix(ci): PCI-01 use process exit status for release archives`.
