// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Bundles;
using dnSpy.Bundles.Extension;
using dnSpy.Contracts.Documents;
using dnlib.DotNet;
using Xunit;

namespace dnSpy.Bundles.IntegrationTests {
	public sealed class BundleAssemblyResolverTests {
		[Fact]
		public void SameBundleDependencyIsLazyAndBeatsFallback() {
			using TestBundle bundle = TestBundle.FromPublishedFixture();
			var fallback = new ReturningAssemblyResolver(bundle.DependencyAssembly);
			using BundleDsDocument document = bundle.CreateDocument(fallback: fallback);
			BundleModuleDocument source = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(source.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = source.ModuleDef.Context!.AssemblyResolver.Resolve(reference, source.ModuleDef);
			Assert.NotNull(resolved);
			Assert.Equal("SingleFile.Dependency", resolved!.Name.String);
			Assert.NotSame(bundle.DependencyAssembly, resolved);
			Assert.Equal(1, bundle.ReadCount(bundle.DependencyIndex));
			Assert.Equal(0, fallback.Calls);
			Assert.Same(resolved, source.ModuleDef.Context.AssemblyResolver.Resolve(reference, source.ModuleDef));
			Assert.Equal(1, bundle.ReadCount(bundle.DependencyIndex));
		}

		[Fact]
		public void SameBundleCandidateWinsBeforeExistingTopLevelDocument() {
			using TestBundle bundle = TestBundle.FromPublishedFixture();
			using var ordinary = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary-dependency.dll"),
				ModuleDefMD.Load(bundle.DependencyBytes), loadSyms: false);
			var service = DispatchProxy.Create<IDsDocumentService, DocumentServiceProxy>();
			((DocumentServiceProxy)(object)service).FindResult = ordinary;
			using BundleDsDocument document = bundle.CreateDocument(documentService: service);
			BundleModuleDocument source = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(source.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = source.ModuleDef.Context!.AssemblyResolver.Resolve(reference, source.ModuleDef);
			Assert.NotNull(resolved);
			Assert.NotSame(ordinary.AssemblyDef, resolved);
			Assert.Equal("SingleFile.Dependency", resolved!.Name.String);
			Assert.Equal(1, bundle.ReadCount(bundle.DependencyIndex));
		}

		[Fact]
		public void AlreadyLoadedSameBundleModuleWinsBeforeUnloadedCandidate() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle duplicate = source.WithDuplicateDependency();
			using BundleDsDocument document = duplicate.CreateDocument();
			BundleModuleDocument loaded = duplicate.LoadDependency(document,
				"a/SingleFile.Dependency.dll");
			BundleModuleDocument sourceModule = duplicate.LoadSource(document);
			AssemblyRef reference = Assert.Single(sourceModule.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = sourceModule.ModuleDef.Context!.AssemblyResolver.Resolve(reference, sourceModule.ModuleDef);
			Assert.Same(loaded.AssemblyDef, resolved);
			Assert.Equal(1, duplicate.ReadCount(duplicate.DependencyIndex));
			Assert.Equal(0, duplicate.ReadCount(duplicate.DuplicateDependencyIndex));
		}

		[Fact]
		public void DisabledAssemblyLoadPreservesLoadedWorkspaceResolutionAndAmbiguity() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle duplicate = source.WithDuplicateDependency();
			var fallback = new ReturningAssemblyResolver(duplicate.DependencyAssembly);
			using BundleDsDocument document = duplicate.CreateDocument(fallback: fallback);
			BundleModuleDocument loaded = duplicate.LoadDependency(document,
				"a/SingleFile.Dependency.dll");
			BundleModuleDocument sourceModule = duplicate.LoadSource(document);
			AssemblyRef reference = Assert.Single(sourceModule.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			using (DisableAssemblyLoad(document.AssemblyResolver)) {
				Assert.Same(loaded.AssemblyDef, Resolve(sourceModule, reference));
				Assert.Equal(1, duplicate.ReadCount(duplicate.DependencyIndex));
				Assert.Equal(0, duplicate.ReadCount(duplicate.DuplicateDependencyIndex));
				Assert.Equal(0, fallback.Calls);
			}

			BundleModuleDocument duplicateLoaded = duplicate.LoadDependency(document,
				"b/singlefile.dependency.dll");
			Assert.NotNull(duplicateLoaded.AssemblyDef);
			using (DisableAssemblyLoad(document.AssemblyResolver)) {
				Assert.Null(Resolve(sourceModule, reference));
				Assert.Contains("Ambiguous same-bundle assembly", document.AssemblyResolver.LastDiagnostic,
					StringComparison.Ordinal);
				Assert.Equal(0, fallback.Calls);
			}
		}

		[Fact]
		public void DisabledAssemblyLoadPreservesAlreadyLoadedTopLevelDocument() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle bundle = source.WithoutDependency();
			using var ordinary = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary-dependency.dll"),
				ModuleDefMD.Load(bundle.DependencyBytes), loadSyms: false);
			var service = DispatchProxy.Create<IDsDocumentService, DocumentServiceProxy>();
			var proxy = (DocumentServiceProxy)(object)service;
			proxy.FindResult = ordinary;
			var fallback = new ReturningAssemblyResolver(bundle.DependencyAssembly);
			using BundleDsDocument document = bundle.CreateDocument(
				documentService: service, fallback: fallback);
			BundleModuleDocument sourceModule = bundle.LoadSource(document);
			IAssembly request = new AssemblyNameInfo(bundle.DependencyAssembly);

			using (DisableAssemblyLoad(document.AssemblyResolver)) {
				Assert.Same(ordinary.AssemblyDef, Resolve(sourceModule, request));
			}

			Assert.Equal(1, proxy.FindCalls);
			Assert.Equal(0, fallback.Calls);
			Assert.Equal(0, bundle.ReadCount(bundle.DependencyIndex));
		}

		[Fact]
		public void DisabledAssemblyLoadDoesNotActivateCandidateFallbackOrFailureCache() {
			using TestBundle bundle = TestBundle.FromPublishedFixture().WithInvalidDependency();
			var fallback = new ReturningAssemblyResolver(bundle.DependencyAssembly);
			using BundleDsDocument document = bundle.CreateDocument(fallback: fallback);
			BundleModuleDocument sourceModule = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(sourceModule.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			using (DisableAssemblyLoad(document.AssemblyResolver)) {
				Assert.Null(Resolve(sourceModule, reference));
				Assert.Equal(0, bundle.ReadCount(bundle.DependencyIndex));
				Assert.Equal(0, fallback.Calls);
				Assert.False(HasFailure(document.AssemblyResolver, bundle.DependencyIndex));
			}

			Assert.Same(bundle.DependencyAssembly, Resolve(sourceModule, reference));
			Assert.Equal(1, bundle.ReadCount(bundle.DependencyIndex));
			Assert.Equal(1, fallback.Calls);
			Assert.True(HasFailure(document.AssemblyResolver, bundle.DependencyIndex));
		}

		[Fact]
		public void DisabledAssemblyLoadScopesAreNestedIdempotentAndRestoreAfterException() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle bundle = source.WithoutDependency();
			var fallback = new ReturningAssemblyResolver(bundle.DependencyAssembly);
			using BundleDsDocument document = bundle.CreateDocument(fallback: fallback);
			BundleModuleDocument sourceModule = bundle.LoadSource(document);
			IAssembly request = new AssemblyNameInfo(bundle.DependencyAssembly);

			IDisposable outer = DisableAssemblyLoad(document.AssemblyResolver);
			try {
				Assert.Null(Resolve(sourceModule, request));
				IDisposable inner = DisableAssemblyLoad(document.AssemblyResolver);
				Assert.Null(Resolve(sourceModule, request));
				inner.Dispose();
				inner.Dispose();
				Assert.Null(Resolve(sourceModule, request));

				Action throwScopeException = () => {
					using (DisableAssemblyLoad(document.AssemblyResolver)) {
						Assert.Null(Resolve(sourceModule, request));
						throw new InvalidOperationException("scope test");
					}
				};
				Assert.Throws<InvalidOperationException>(throwScopeException);
				Assert.Null(Resolve(sourceModule, request));
			}
			finally {
				outer.Dispose();
				outer.Dispose();
			}

			Assert.Same(bundle.DependencyAssembly, Resolve(sourceModule, request));
			Assert.Equal(1, fallback.Calls);
		}

		[Fact]
		public void DisabledAssemblyLoadLeavesUnrelatedSourceAndOtherResolverUnchanged() {
			using TestBundle first = TestBundle.FromPublishedFixture();
			var fallback = new ReturningAssemblyResolver(first.DependencyAssembly);
			using BundleDsDocument firstDocument = first.CreateDocument(fallback: fallback);
			using ModuleDefMD unrelatedSource = ModuleDefMD.Load(first.DependencyBytes);
			IAssembly request = new AssemblyNameInfo(first.DependencyAssembly);

			using TestBundle second = TestBundle.FromPublishedFixture();
			using BundleDsDocument secondDocument = second.CreateDocument();
			BundleModuleDocument secondSource = second.LoadSource(secondDocument);
			AssemblyRef secondReference = Assert.Single(secondSource.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			using (DisableAssemblyLoad(firstDocument.AssemblyResolver)) {
				Assert.Same(first.DependencyAssembly,
					firstDocument.AssemblyResolver.Resolve(request, unrelatedSource));
				Assert.Equal(1, fallback.Calls);

				AssemblyDef? resolved = Resolve(secondSource, secondReference);
				Assert.NotNull(resolved);
				Assert.Equal(1, second.ReadCount(second.DependencyIndex));
			}
		}

		[Fact]
		public async Task DisabledAssemblyLoadIsIsolatedAcrossIndependentTaskFlows() {
			using TestBundle bundle = TestBundle.FromPublishedFixture();
			using BundleDsDocument document = bundle.CreateDocument();
			BundleModuleDocument source = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(source.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");
			var scopeEntered = new TaskCompletionSource<bool>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var independentResolved = new TaskCompletionSource<AssemblyDef?>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			AssemblyDef? suppressed = null;

			// Start task B before task A creates its scope, so B captures the unsuppressed
			// execution context and can resolve while A is still paused in its scope.
			Task taskB = Task.Run(async () => {
				await scopeEntered.Task;
				try {
					independentResolved.SetResult(Resolve(source, reference));
				}
				catch (Exception ex) {
					independentResolved.SetException(ex);
				}
			}, TestContext.Current.CancellationToken);

			Task taskA = Task.Run(async () => {
				using (DisableAssemblyLoad(document.AssemblyResolver)) {
					try {
						suppressed = Resolve(source, reference);
					}
					finally {
						scopeEntered.SetResult(true);
					}
					await independentResolved.Task;
				}
			}, TestContext.Current.CancellationToken);

			await Task.WhenAll(taskA, taskB);
			Assert.Null(suppressed);
			Assert.NotNull(await independentResolved.Task);
			Assert.Equal(1, bundle.ReadCount(bundle.DependencyIndex));
		}

		[Fact]
		public void RecursiveCandidateProbeIsRejectedWhileEntryIsLoading() {
			using TestBundle bundle = TestBundle.FromPublishedFixture();
			BundleDsDocument? document = null;
			BundleModuleDocument? source = null;
			bool reentered = false;
			document = bundle.CreateDocument(readOverride: entry => {
				if (entry.Index == bundle.DependencyIndex && !reentered) {
					reentered = true;
					AssemblyRef request = Assert.Single(source!.ModuleDef!.GetAssemblyRefs(),
						a => a.Name.String == "SingleFile.Dependency");
					Assert.Null(document!.AssemblyResolver.Resolve(request, source.ModuleDef));
				}
				return new MemoryStream(bundle.GetBytes(entry), writable: false);
			});
			source = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(source.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = source.ModuleDef.Context!.AssemblyResolver.Resolve(reference, source.ModuleDef);
			Assert.True(reentered);
			Assert.NotNull(resolved);
			Assert.Equal(1, bundle.ReadCount(bundle.DependencyIndex));
		}

		[Fact]
		public void SameBundleIdentityFieldsAreComparedBeforeSelection() {
			using TestBundle bundle = TestBundle.FromPublishedFixture();
			using BundleDsDocument document = bundle.CreateDocument();
			BundleModuleDocument source = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(source.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			var publicKeyRequest = new AssemblyNameInfo(reference);
			publicKeyRequest.PublicKeyOrToken = new PublicKeyToken(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
			Assert.Null(Resolve(source, publicKeyRequest));

			var cultureRequest = new AssemblyNameInfo(reference);
			cultureRequest.Culture = new UTF8String("fr-FR");
			Assert.Null(Resolve(source, cultureRequest));

			var contentTypeRequest = new AssemblyNameInfo(reference);
			contentTypeRequest.ContentType = AssemblyAttributes.ContentType_WindowsRuntime;
			Assert.Null(Resolve(source, contentTypeRequest));
		}

		[Fact]
		public void SameBundleCandidatesUseExactAndNearestCompatibleVersion() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle versioned = source.WithVersionedDependencies();
			using BundleDsDocument document = versioned.CreateDocument();
			BundleModuleDocument sourceModule = versioned.LoadSource(document);

			var lowerRequest = new AssemblyNameInfo(versioned.DependencyAssembly) {
				Version = new Version(1, 5, 0, 0),
			};
			AssemblyDef? lower = Resolve(sourceModule, lowerRequest);
			Assert.NotNull(lower);
			Assert.Equal(new Version(1, 0, 0, 0), lower!.Version);

			var newerRequest = new AssemblyNameInfo(versioned.DependencyAssembly) {
				Version = new Version(3, 0, 0, 0),
			};
			AssemblyDef? newer = Resolve(sourceModule, newerRequest);
			Assert.NotNull(newer);
			Assert.Equal(new Version(2, 0, 0, 0), newer!.Version);

			var exactRequest = new AssemblyNameInfo(versioned.DependencyAssembly) {
				Version = new Version(2, 0, 0, 0),
			};
			AssemblyDef? exact = Resolve(sourceModule, exactRequest);
			Assert.NotNull(exact);
			Assert.Equal(new Version(2, 0, 0, 0), exact!.Version);
		}

		[Fact]
		public void OrdinaryTopLevelDocumentIsUsedAfterSameBundleMiss() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle bundle = source.WithoutDependency();
			using var ordinary = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary-dependency.dll"),
				ModuleDefMD.Load(bundle.DependencyBytes), loadSyms: false);
			var service = DispatchProxy.Create<IDsDocumentService, DocumentServiceProxy>();
			((DocumentServiceProxy)(object)service).FindResult = ordinary;
			using BundleDsDocument document = bundle.CreateDocument(documentService: service);
			BundleModuleDocument sourceModule = bundle.LoadSource(document);
			IAssembly request = new AssemblyNameInfo(bundle.DependencyAssembly);

			AssemblyDef? resolved = Resolve(sourceModule, request);
			Assert.Same(ordinary.AssemblyDef, resolved);
		}

		[Fact]
		public void ExistingResolverIsUsedAfterSameBundleMiss() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle bundle = source.WithoutDependency();
			var fallback = new ReturningAssemblyResolver(bundle.DependencyAssembly);
			using BundleDsDocument document = bundle.CreateDocument(fallback: fallback);
			BundleModuleDocument sourceModule = bundle.LoadSource(document);
			IAssembly request = new AssemblyNameInfo(bundle.DependencyAssembly);

			AssemblyDef? resolved = Resolve(sourceModule, request);
			Assert.Same(bundle.DependencyAssembly, resolved);
			Assert.Equal(1, fallback.Calls);
		}

		[Fact]
		public void ForeignBundleResultIsRejectedWhenNoOwnCandidateExists() {
			using TestBundle first = TestBundle.FromPublishedFixture();
			using BundleDsDocument firstDocument = first.CreateDocument();
			BundleModuleDocument foreign = first.LoadDependency(firstDocument);

			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle second = source.WithoutDependency();
			var fallback = new ReturningAssemblyResolver(foreign.AssemblyDef!);
			using BundleDsDocument secondDocument = second.CreateDocument(fallback: fallback);
			BundleModuleDocument sourceModule = second.LoadSource(secondDocument);
			AssemblyRef request = Assert.Single(sourceModule.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = sourceModule.ModuleDef.Context!.AssemblyResolver.Resolve(request, sourceModule.ModuleDef);
			Assert.Null(resolved);
			Assert.Equal(1, fallback.Calls);
			Assert.Contains("another bundle", secondDocument.AssemblyResolver.LastDiagnostic,
				StringComparison.Ordinal);
		}

		[Fact]
		public void DifferentBundleCannotPreemptRequestingBundle() {
			using TestBundle first = TestBundle.FromPublishedFixture();
			using TestBundle second = TestBundle.FromPublishedFixture();
			using BundleDsDocument firstDocument = first.CreateDocument();
			using BundleDsDocument secondDocument = second.CreateDocument(
				fallback: new ReturningAssemblyResolver(first.DependencyAssembly));
			BundleModuleDocument firstSource = first.LoadSource(firstDocument);
			BundleModuleDocument secondSource = second.LoadSource(secondDocument);
			AssemblyRef reference = Assert.Single(secondSource.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = secondSource.ModuleDef.Context!.AssemblyResolver.Resolve(reference, secondSource.ModuleDef);
			Assert.NotNull(resolved);
			Assert.NotSame(first.DependencyAssembly, resolved);
			Assert.Equal(1, second.ReadCount(second.DependencyIndex));
			Assert.Equal(0, first.ReadCount(first.DependencyIndex));
		}

		[Fact]
		public void DuplicateExactCandidatesAreAmbiguousInPathOrder() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle duplicate = source.WithDuplicateDependency();
			using BundleDsDocument document = duplicate.CreateDocument();
			BundleModuleDocument sourceModule = duplicate.LoadSource(document);
			AssemblyRef reference = Assert.Single(sourceModule.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = sourceModule.ModuleDef.Context!.AssemblyResolver.Resolve(reference, sourceModule.ModuleDef);
			Assert.Null(resolved);
			Assert.Contains("a/SingleFile.Dependency.dll, b/singlefile.dependency.dll", document.AssemblyResolver.LastDiagnostic,
				StringComparison.Ordinal);
			Assert.Equal(1, duplicate.ReadCount(duplicate.DependencyIndex));
			Assert.Equal(1, duplicate.ReadCount(duplicate.DuplicateDependencyIndex));
		}

		[Fact]
		public void DuplicateAmbiguityStopsBeforeOrdinaryAndGlobalFallback() {
			using TestBundle source = TestBundle.FromPublishedFixture();
			using TestBundle duplicate = source.WithDuplicateDependency();
			using var ordinary = DsDotNetDocument.CreateModule(
				DsDocumentInfo.CreateDocument("ordinary-dependency.dll"),
				ModuleDefMD.Load(duplicate.DependencyBytes), loadSyms: false);
			var service = DispatchProxy.Create<IDsDocumentService, DocumentServiceProxy>();
			var proxy = (DocumentServiceProxy)(object)service;
			proxy.FindResult = ordinary;
			var fallback = new ReturningAssemblyResolver(ordinary.AssemblyDef!);
			using BundleDsDocument document = duplicate.CreateDocument(
				documentService: service, fallback: fallback);
			BundleModuleDocument sourceModule = duplicate.LoadSource(document);
			AssemblyRef request = Assert.Single(sourceModule.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			AssemblyDef? resolved = sourceModule.ModuleDef.Context!.AssemblyResolver.Resolve(request, sourceModule.ModuleDef);
			Assert.Null(resolved);
			Assert.Equal(0, proxy.FindCalls);
			Assert.Equal(0, fallback.Calls);
			Assert.Equal("Ambiguous same-bundle assembly 'SingleFile.Dependency'; matching entries: " +
				"a/SingleFile.Dependency.dll, b/singlefile.dependency.dll.",
				document.AssemblyResolver.LastDiagnostic);
		}

		[Fact]
		public void CandidateFailureIsCachedOnlyInsideWorkspaceAndDisposeClearsIt() {
			using TestBundle bundle = TestBundle.FromPublishedFixture().WithInvalidDependency();
			using BundleDsDocument document = bundle.CreateDocument();
			BundleModuleDocument source = bundle.LoadSource(document);
			AssemblyRef reference = Assert.Single(source.ModuleDef!.GetAssemblyRefs(),
				a => a.Name.String == "SingleFile.Dependency");

			Assert.Null(source.ModuleDef.Context!.AssemblyResolver.Resolve(reference, source.ModuleDef));
			int readsAfterFirstAttempt = bundle.ReadCount(bundle.DependencyIndex);
			Assert.Null(source.ModuleDef.Context.AssemblyResolver.Resolve(reference, source.ModuleDef));
			Assert.Equal(readsAfterFirstAttempt, bundle.ReadCount(bundle.DependencyIndex));
			Assert.NotNull(document.AssemblyResolver.LastDiagnostic);

			document.Dispose();
			Assert.Null(document.AssemblyResolver.Resolve(reference, source.ModuleDef));
		}

		sealed class ReturningAssemblyResolver : IAssemblyResolver {
			readonly AssemblyDef assembly;
			public ReturningAssemblyResolver(AssemblyDef assembly) => this.assembly = assembly;
			public int Calls { get; private set; }
			public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule) {
				Calls++;
				return this.assembly;
			}
		}

		static AssemblyDef? Resolve(BundleModuleDocument source, IAssembly request) {
			return source.ModuleDef!.Context!.AssemblyResolver.Resolve(request, source.ModuleDef);
		}

		static IDisposable DisableAssemblyLoad(BundleAssemblyResolver resolver) {
			MethodInfo method = typeof(BundleAssemblyResolver).GetMethod("DisableAssemblyLoad",
				BindingFlags.Instance | BindingFlags.NonPublic)!;
			return (IDisposable)method.Invoke(resolver, null)!;
		}

		static bool HasFailure(BundleAssemblyResolver resolver, int entryIndex) {
			PropertyInfo property = typeof(BundleAssemblyResolver).GetProperty("WorkspaceIndex",
				BindingFlags.Instance | BindingFlags.NonPublic)!;
			object index = property.GetValue(resolver)!;
			MethodInfo method = index.GetType().GetMethod("TryGetFailure",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
			object?[] arguments = new object?[] { entryIndex, null };
			return (bool)method.Invoke(index, arguments)!;
		}

		public class DocumentServiceProxy : DispatchProxy {
			public IDsDocument? FindResult { get; set; }
			public int FindCalls { get; private set; }
			protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
				if (targetMethod?.Name == nameof(IDsDocumentService.FindAssembly)) {
					FindCalls++;
					return FindResult;
				}
				if (targetMethod?.Name == "get_AssemblyResolver")
					return null;
				return targetMethod?.ReturnType is { IsValueType: true } type
					? Activator.CreateInstance(type) : null;
			}
		}

		sealed class TestBundle : IDisposable {
			readonly BundleFile sourceBundle;
			readonly Dictionary<int, byte[]> bytes;
			public readonly int DependencyIndex;
			public int DuplicateDependencyIndex { get; private set; } = -1;
			public byte[] DependencyBytes { get; }
			public AssemblyDef DependencyAssembly { get; }
			public Dictionary<int, int> Reads { get; } = new Dictionary<int, int>();

			TestBundle(BundleFile sourceBundle, Dictionary<int, byte[]> bytes,
				int dependencyIndex, byte[] dependencyBytes, AssemblyDef dependencyAssembly) {
				this.sourceBundle = sourceBundle;
				this.bytes = bytes;
				DependencyIndex = dependencyIndex;
				DependencyBytes = dependencyBytes;
				DependencyAssembly = dependencyAssembly;
			}

			public static TestBundle FromPublishedFixture() {
				string filename = FindCompressedFixture();
				BundleOpenResult result = new BundleReader().Open(filename);
				Assert.Equal(BundleOpenStatus.Success, result.Status);
				BundleFile parsed = result.Bundle!;
				BundleEntry sourceEntry = parsed.Entries.Single(a => a.RelativePath == "SingleFile.App.dll");
				BundleEntry dependencyEntry = parsed.Entries.Single(a => a.RelativePath == "SingleFile.Dependency.dll");
				byte[] sourceBytes = sourceEntry.ReadAllBytes(BundleReaderOptions.DefaultMaximumEntrySize);
				byte[] dependencyBytes = dependencyEntry.ReadAllBytes(BundleReaderOptions.DefaultMaximumEntrySize);
				var dependency = ModuleDefMD.Load(dependencyBytes).Assembly!;
				var bytes = new Dictionary<int, byte[]> {
					[sourceEntry.Index] = sourceBytes,
					[dependencyEntry.Index] = dependencyBytes,
				};
				return new TestBundle(parsed, bytes, dependencyEntry.Index, dependencyBytes, dependency);
			}

			public TestBundle WithoutDependency() {
				BundleEntry sourceEntry = sourceBundle.Entries.Single(a => a.RelativePath == "SingleFile.App.dll");
				var entry = new BundleEntry(0, 0, bytes[sourceEntry.Index].LongLength, 0,
					sourceEntry.RawFileType, sourceEntry.FileType, sourceEntry.RelativePath);
				var file = new BundleFile("without-dependency-bundle.exe", 1, 0, 0,
					new BundleManifest(6, 0, "without-dependency"), new[] { entry });
				return new TestBundle(file,
					new Dictionary<int, byte[]> { [entry.Index] = bytes[sourceEntry.Index] },
					-1, DependencyBytes, DependencyAssembly);
			}

			public TestBundle WithVersionedDependencies() {
				BundleEntry sourceEntry = sourceBundle.Entries.Single(a => a.RelativePath == "SingleFile.App.dll");
				byte[] versionOne = RewriteAssemblyVersion(DependencyBytes, new Version(1, 0, 0, 0));
				byte[] versionTwo = RewriteAssemblyVersion(DependencyBytes, new Version(2, 0, 0, 0));
				var entries = new[] {
					new BundleEntry(0, 0, bytes[sourceEntry.Index].LongLength, 0,
						sourceEntry.RawFileType, sourceEntry.FileType, sourceEntry.RelativePath),
					new BundleEntry(1, 0, versionOne.LongLength, 0, 1,
						BundleFileType.Assembly, "a/SingleFile.Dependency.dll"),
					new BundleEntry(2, 0, versionTwo.LongLength, 0, 1,
						BundleFileType.Assembly, "b/SingleFile.Dependency.dll"),
				};
				var file = new BundleFile("versioned-bundle.exe", 1, 0, 0,
					new BundleManifest(6, 0, "versioned"), entries);
				var payloads = new Dictionary<int, byte[]> {
					[entries[0].Index] = bytes[sourceEntry.Index],
					[entries[1].Index] = versionOne,
					[entries[2].Index] = versionTwo,
				};
				return new TestBundle(file, payloads, entries[1].Index, versionOne,
					ModuleDefMD.Load(versionOne).Assembly!) {
					DuplicateDependencyIndex = entries[2].Index,
				};
			}

			public TestBundle WithDuplicateDependency() {
				var entries = new List<BundleEntry>();
				var payloads = new Dictionary<int, byte[]>();
				BundleEntry sourceEntry = sourceBundle.Entries.Single(a => a.RelativePath == "SingleFile.App.dll");
				BundleEntry dependencyEntry = sourceBundle.Entries.Single(a => a.RelativePath == "SingleFile.Dependency.dll");
				foreach (BundleEntry entry in new[] { sourceEntry, dependencyEntry }) {
					string path = entry.RelativePath == "SingleFile.Dependency.dll"
						? "a/SingleFile.Dependency.dll" : entry.RelativePath;
					var copy = new BundleEntry(entries.Count, 0, bytes[entry.Index].LongLength, 0, entry.RawFileType,
						entry.FileType, path);
					entries.Add(copy);
					payloads[copy.Index] = bytes[entry.Index];
				}
				var duplicate = new BundleEntry(entries.Count, 0, DependencyBytes.LongLength, 0,
					1, BundleFileType.Assembly, "b/singlefile.dependency.dll");
				entries.Add(duplicate);
				payloads[duplicate.Index] = DependencyBytes;
				var file = new BundleFile("duplicate-bundle.exe", 1, 0, 0,
					new BundleManifest(6, 0, "duplicate"), entries);
				var result = new TestBundle(file, payloads,
					entries.Single(a => a.RelativePath == "a/SingleFile.Dependency.dll").Index,
					DependencyBytes, DependencyAssembly) {
					DuplicateDependencyIndex = duplicate.Index,
				};
				return result;
			}

			public TestBundle WithInvalidDependency() {
				var payloads = new Dictionary<int, byte[]>(bytes);
				payloads[DependencyIndex] = new byte[] { 0x4D, 0x5A, 0x01 };
				return new TestBundle(sourceBundle, payloads, DependencyIndex, DependencyBytes, DependencyAssembly);
			}

			public BundleDsDocument CreateDocument(IDsDocumentService? documentService = null,
				IAssemblyResolver? fallback = null, Func<BundleEntry, Stream>? readOverride = null) {
				return new BundleDsDocument(DsDocumentInfo.CreateDocument(sourceBundle.Filename), sourceBundle,
					openLogicalRead: entry => {
						Reads.TryGetValue(entry.Index, out int count);
						Reads[entry.Index] = count + 1;
						return readOverride is null ? new MemoryStream(bytes[entry.Index], writable: false) : readOverride(entry);
					}, assemblyResolver: fallback, documentService: documentService);
			}

			public BundleModuleDocument LoadDependency(BundleDsDocument document,
				string relativePath = "SingleFile.Dependency.dll") {
				BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
					.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>()
					.Single(a => a.Entry.RelativePath == relativePath);
				return entry.CreateManagedDocument();
			}

			public BundleModuleDocument LoadSource(BundleDsDocument document) {
				BundleEntryDocument entry = document.Children.Cast<BundleFolderDocument>()
					.Single(a => a.Kind == BundleFolderKind.Assemblies).Children.Cast<BundleEntryDocument>()
					.Single(a => a.Entry.RelativePath.EndsWith("SingleFile.App.dll", StringComparison.Ordinal));
				return entry.CreateManagedDocument();
			}

			public int ReadCount(int index) => Reads.TryGetValue(index, out int count) ? count : 0;

			public byte[] GetBytes(BundleEntry entry) => bytes[entry.Index];

			public void Dispose() {
				if (!ReferenceEquals(sourceBundle, null))
					sourceBundle.Dispose();
			}

			static string FindCompressedFixture() {
				string? configured = Environment.GetEnvironmentVariable("DNSPY_BUNDLE_FIXTURES");
				var roots = new List<string>();
				if (!string.IsNullOrWhiteSpace(configured))
					roots.AddRange(configured.Split(new[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries));
				roots.Add(Path.Combine(AppContext.BaseDirectory, "../../../../TestAssets/SingleFile/Net10/artifacts/net10.0"));
				roots.Add(Path.Combine(Directory.GetCurrentDirectory(), "Tests/TestAssets/SingleFile/Net10/artifacts/net10.0"));
				foreach (string root in roots) {
					string candidate = Path.GetFullPath(Path.Combine(root, "scd-compressed/publish/SingleFile.App.exe"));
					if (File.Exists(candidate))
						return candidate;
				}
				throw new InvalidOperationException("The generated compressed net10 bundle fixture is missing.");
			}

			static byte[] RewriteAssemblyVersion(byte[] bytes, Version version) {
				ModuleDefMD module = ModuleDefMD.Load(bytes);
				try {
					module.Assembly!.Version = version;
					using var stream = new MemoryStream();
					module.Write(stream);
					return stream.ToArray();
				}
				finally {
					module.Dispose();
				}
			}
		}
	}
}
