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

Status: approved. The five implementation paths were reviewed independently by a read-only gpt-5.6-sol reviewer and are ready for the expected local commit. Preserve all pre-existing changes listed in the master spec. Do not push or publish.

## Delivery evidence

- Changed paths: `.github/workflows/build.yml`, `Extensions/dnSpy.Bundles/BundleAssemblyResolver.cs`, `Extensions/dnSpy.Bundles/BundleDocumentNodeProvider.cs`, `Tests/dnSpy.Bundles.IntegrationTests/BundleAssemblyResolverTests.cs`, and `Tests/dnSpy.Bundles.IntegrationTests/BundleDecompilerAnalyzerTests.cs`.
- `BundleAssemblyResolver` now owns an instance `AsyncLocal<int>` suppression scope with balanced nested and idempotent disposal. Owning-bundle loaded workspace resolution and ambiguity remain first; existing top-level lookup remains available; candidate activation, fallback, and suppressed failure caching are skipped while the scope is active. The managed-entry node explicitly activates the selected module and scopes only its existing header decompile. Focused tests cover nesting, exception restoration, loaded/top-level resolution, candidate/fallback suppression and failure-cache behavior, unrelated sources and other resolver instances, deterministic two-task isolation, and compressed lazy header/body/analyzer behavior.
- The existing workflow integration filter retains all prior classes and appends `BundleAssemblyResolverTests` and `BundleTreeNodeTests`.
- Independent Sol review: `APPROVED`, no findings. The protected pre-existing `build.ps1` mode change and the three named untracked tests were excluded and remain unstaged.
- `dotnet build Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -p:EnableWindowsTargeting=true` passed with 0 warnings and 0 errors.
- `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter 'FullyQualifiedName~BundleAssemblyResolverTests|FullyQualifiedName~BundleDecompilerAnalyzerTests|FullyQualifiedName~BundleManagedDocumentTests|FullyQualifiedName~BundleTreeNodeTests|FullyQualifiedName~BundleOpenPipelineTests|FullyQualifiedName~OrdinaryLoadingDecompilerRegressionTests'` reached VSTest but aborted before execution because Linux lacks `Microsoft.WindowsDesktop.App 10.0.0`; no Windows execution is claimed.
- `pwsh -NoProfile -File Build/Test-ReleasePackaging.ps1` passed. `pwsh -NoProfile -File Build/Test-ReleasePublication.ps1` passed all 56 cases. `git diff --check` passed.
- Expected commit: `fix(bundles): PCI-04 keep header resolution lazy`.
