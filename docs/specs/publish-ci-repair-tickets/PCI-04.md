# PCI-04: Preserve lazy sibling loading during inventory header decompilation

## Objective and dependencies

Preserve lazy sibling loading during inventory header decompilation. Dependencies: PCI-03. Master: [publish-ci-repair](../publish-ci-repair.md).

## In scope and exclusions

`Extensions/dnSpy.Bundles/BundleAssemblyResolver.cs`, `BundleDocumentNodeProvider.cs`, `Tests/dnSpy.Bundles.IntegrationTests/BundleAssemblyResolverTests.cs`, `BundleDecompilerAnalyzerTests.cs`, `.github/workflows/build.yml` (append resolver/tree classes to existing filter). Existing OrdinaryLoadingDecompilerRegressionTests is required verification, with changes only if needed for a focused ordinary-source regression.

## Implementation decisions and data flow

Implement internal DisableAssemblyLoad scope as specified in master using instance AsyncLocal<int>, balanced nesting and idempotent disposal. Loaded-workspace resolution/ambiguity runs first. If owning-source suppression active, return already-loaded top-level match or null before candidate and fallback paths. Do not cache suppressed misses as failures. Unrelated source requests and other bundle instances ignore this instance's scope. Node header call obtains scope after explicit activation, wraps only existing header decompilation in using, and always disposes. Keep decompiler/header/body/read assertions intact. Add focused tests for zero candidate reads/fallback calls within nested scope, already-loaded resolution, restored dependency resolution after disposal/exception, and unaffected ordinary-source/other-bundle resolution. Add a deterministic two-task isolation test: task A enters suppression and waits on a completion signal; an independently started task B resolves an unloaded dependency on the same resolver while A is still scoped, proving B is not suppressed. Coordinate with TaskCompletionSource signals, no sleeps; do not start B inside A's inherited execution context. Existing real compressed-header test is the end-to-end regression; retain body and analyzer verification.

## Ordered implementation

1. Add contextual suppression scope and resolver guard.
2. Apply scope at managed inventory header call.
3. Add targeted scope tests including restoration and isolation.
4. Append BundleAssemblyResolverTests and BundleTreeNodeTests to existing workflow filter without removing classes.
5. Run complete master verification subset/environment attempts and prepare evidence for final independent review.

## Required tests and exact scoped verification

Use the master Windows restore/MSBuild commands before Windows test execution. On Linux attempt cross-build and record the concrete blocker; never claim execution from a build. Commands from repository root:

```text
dotnet build Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -p:EnableWindowsTargeting=true
dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter 'FullyQualifiedName~BundleAssemblyResolverTests|FullyQualifiedName~BundleDecompilerAnalyzerTests|FullyQualifiedName~BundleManagedDocumentTests|FullyQualifiedName~BundleTreeNodeTests|FullyQualifiedName~BundleOpenPipelineTests|FullyQualifiedName~OrdinaryLoadingDecompilerRegressionTests'
pwsh -NoProfile -File Build/Test-ReleasePackaging.ps1
pwsh -NoProfile -File Build/Test-ReleasePublication.ps1
git diff --check
```

## Acceptance criteria

Real compressed header decompiles with exactly one selected read and no sibling reads; later needed dependency resolves once; scope restores after failure/nesting; ordinary loading/resolution unchanged; no global callback mutation, parser or ILSpy changes.

## Expected commit boundary

`fix(bundles): PCI-04 keep header resolution lazy`. Stage only in-scope paths actually changed and this ticket/master ledger after approval.

## Delivery contract

Status: planned. Implement without committing; obtain independent review before orchestration updates this ticket and the master ledger and creates the expected local commit. Preserve all pre-existing changes listed in the master spec. Record actual commands/results and limitations here. Do not push or publish.
