# PCI-03: Repair integration test setup to match existing document contracts

## Objective and dependencies

Repair integration test setup to match existing document contracts. Dependencies: PCI-02. Master: [publish-ci-repair](../publish-ci-repair.md).

## In scope and exclusions

`Tests/dnSpy.Bundles.IntegrationTests/BundleManagedDocumentTests.cs`, `BundleAssemblyResolverTests.cs`, `BundleDecompilerAnalyzerTests.cs`, `BundleTreeNodeTests.cs`. No production code changes and no unrelated proxy cleanup.

## Implementation decisions and data flow

Unseal public TreeViewProxy and NodeContextProxy in BundleManagedDocumentTests, and DocumentServiceProxy in BundleAssemblyResolverTests. Retain constructors/dispatch behavior. Replace selected.ModuleDef same-instance assertion with explicit Assert.Null(selected.ModuleDef) plus identity through selected.ManagedDocument.ModuleDef (and repeated CreateManagedDocument identity). For detached inventory nodes, use existing ToString(IDecompiler, ...) overload with the test CSharp decompiler, casting TreeNodeData to DocumentTreeNodeData where necessary. Apply to affected BundleDecompilerAnalyzerTests and BundleTreeNodeTests render paths, preserving path text and zero-read assertions. Do not add nullable-context fallback to production tree contracts.

## Ordered implementation

1. Correct proxy inheritance.
2. Correct inventory/managed document assertions.
3. Render detached nodes using explicit decompiler.
4. Run focused tests excluding the known header-read failure deferred to PCI-04, then cross-build or report Windows blockers.

## Required tests and exact scoped verification

Use the master Windows restore/MSBuild commands before Windows test execution. On Linux attempt cross-build and record the concrete blocker; never claim execution from a build. Commands from repository root:

```text
dotnet build Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -p:EnableWindowsTargeting=true
dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter 'FullyQualifiedName~BundleManagedDocumentTests|FullyQualifiedName~BundleTreeNodeTests|FullyQualifiedName~EnumeratingCompressedBundleInventoryDoesNotMaterializeUnexpandedEntries|FullyQualifiedName~BundleAssemblyResolverTests'
git diff --check
```

## Acceptance criteria

Proxy classes are inheritable; detached rendering reaches production WriteCore; metadata documents remain metadata-only; zero inventory reads and one selected module read assertions remain enforced.

## Expected commit boundary

`test(bundles): PCI-03 correct integration harness contracts`. Stage only in-scope paths actually changed and this ticket/master ledger after approval.

## Delivery contract

Status: approved. The four test-only changes were implemented, independently reviewed by a read-only gpt-5.6-sol reviewer, and are ready for the expected local commit. Preserve all pre-existing changes listed in the master spec. Do not push or publish.

## Delivery evidence

- Changed paths: `Tests/dnSpy.Bundles.IntegrationTests/BundleManagedDocumentTests.cs`, `BundleAssemblyResolverTests.cs`, `BundleDecompilerAnalyzerTests.cs`, and `BundleTreeNodeTests.cs`.
- The three requested `DispatchProxy` classes are unsealed with their constructors and dispatch behavior unchanged. The managed-entry test now asserts `selected.ModuleDef` is null, checks ownership through `selected.ManagedDocument.ModuleDef`, and checks repeated `CreateManagedDocument()` identity. Detached inventory rendering uses the existing explicit `ToString(IDecompiler, ...)` overload with the test C# decompiler and retains path and zero-read assertions.
- Independent Sol review: `APPROVED`, no findings. The protected pre-existing `build.ps1` mode change and the three named untracked tests were excluded and remain unstaged.
- `dotnet build Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -p:EnableWindowsTargeting=true` passed with 0 errors and 6 nullable warnings.
- `dotnet test Tests/dnSpy.Bundles.IntegrationTests/dnSpy.Bundles.IntegrationTests.csproj -c Release -f net10.0-windows --no-build --no-restore --filter 'FullyQualifiedName~BundleManagedDocumentTests|FullyQualifiedName~BundleTreeNodeTests|FullyQualifiedName~EnumeratingCompressedBundleInventoryDoesNotMaterializeUnexpandedEntries|FullyQualifiedName~BundleAssemblyResolverTests'` reached VSTest but aborted before execution because Linux lacks `Microsoft.WindowsDesktop.App 10.0.0`; no Windows execution is claimed.
- `git diff --check` passed. Expected commit: `test(bundles): PCI-03 correct integration harness contracts`.
