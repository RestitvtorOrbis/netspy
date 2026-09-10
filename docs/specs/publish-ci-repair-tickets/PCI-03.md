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

Status: planned. Implement without committing; obtain independent review before orchestration updates this ticket and the master ledger and creates the expected local commit. Preserve all pre-existing changes listed in the master spec. Record actual commands/results and limitations here. Do not push or publish.
