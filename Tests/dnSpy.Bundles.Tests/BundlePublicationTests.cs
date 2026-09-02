// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using dnSpy.Bundles;
using Xunit;

namespace dnSpy.Bundles.Tests {
	[Collection("Bundle temporary directory")]
	public sealed class BundlePublicationTests {
		[Fact]
		public void PublishesOnlyAfterOrderedLogicalValidation() {
			ModernBundleFixture fixture = GetFixture();
			byte[] sourceHash = Hash(fixture.BundlePath);
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			BundleEntry runtimeConfig = Assert.Single(workspace.Bundle.Entries,
				entry => entry.FileType == BundleFileType.RuntimeConfigJson);
			byte[] replacement = System.Text.Encoding.UTF8.GetBytes(
				"{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}\n");
			workspace.SetReplacement(runtimeConfig, replacement, new BundleReplacementInfo("test"));
			using var destination = new TemporaryPath("published.exe");
			var generator = new RecordingGenerator();

			string published = new WindowsBundlePublicationService(generator).Publish(workspace,
				destination.Path, TestContext.Current.CancellationToken);

			Assert.Equal(Path.GetFullPath(destination.Path), published);
			Assert.Equal(sourceHash, Hash(fixture.BundlePath));
			Assert.True(File.Exists(destination.Path));
			Assert.NotNull(generator.TemporaryDirectory);
			Assert.False(Directory.Exists(generator.TemporaryDirectory));
			using BundleFile output = Open(destination.Path);
			Assert.Equal(workspace.Bundle.Entries.Select(entry => entry.RelativePath),
				output.Entries.Select(entry => entry.RelativePath));
			Assert.Equal(replacement, Assert.Single(output.Entries,
				entry => entry.RelativePath == runtimeConfig.RelativePath).ReadAllBytes(replacement.LongLength));
		}

		[Fact]
		public void ExistingDestinationIsReplacedAfterSuccessfulValidation() {
			ModernBundleFixture fixture = GetFixture();
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			using var destination = new TemporaryPath("existing.exe");
			File.WriteAllText(destination.Path, "old destination");

			new WindowsBundlePublicationService().Publish(workspace, destination.Path,
				TestContext.Current.CancellationToken);

			using BundleFile output = Open(destination.Path);
			Assert.Equal(workspace.Bundle.Entries.Select(entry => entry.RelativePath),
				output.Entries.Select(entry => entry.RelativePath));
		}

		[Fact]
		public void CorruptedGeneratedContentIsRejectedBeforePublication() {
			ModernBundleFixture fixture = GetFixture();
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			using var destination = new TemporaryPath("corrupt.exe");
			byte[] priorDestination = System.Text.Encoding.UTF8.GetBytes("preserve me");
			File.WriteAllBytes(destination.Path, priorDestination);
			var generator = new MutatingGenerator(static generation => {
				BundleOpenResult opened = new BundleReader().Open(generation.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				long offset;
				using (BundleFile generated = opened.Bundle!)
					offset = Assert.Single(generated.Entries,
						entry => entry.FileType == BundleFileType.RuntimeConfigJson).Offset;
				using FileStream stream = new FileStream(generation.BundlePath, FileMode.Open,
					FileAccess.ReadWrite, FileShare.None);
				stream.Position = offset;
				int value = stream.ReadByte();
				Assert.NotEqual(-1, value);
				stream.Position = offset;
				stream.WriteByte((byte)(value ^ 0xFF));
				stream.Flush(flushToDisk: true);
			});

			Assert.Throws<InvalidDataException>(() =>
				new WindowsBundlePublicationService(generator).Publish(workspace, destination.Path,
					TestContext.Current.CancellationToken));

			Assert.Equal(priorDestination, File.ReadAllBytes(destination.Path));
			Assert.NotNull(generator.TemporaryDirectory);
			Assert.False(Directory.Exists(generator.TemporaryDirectory));
		}

		[Fact]
		public void GeneratedInventoryPathChangeIsRejected() {
			ModernBundleFixture fixture = GetFixture();
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			using var destination = new TemporaryPath("inventory.exe");
			var generator = new MutatingGenerator(static generation => {
				byte[] bytes = File.ReadAllBytes(generation.BundlePath);
				byte[] path = System.Text.Encoding.UTF8.GetBytes("SingleFile.App.dll");
				int offset = LastIndexOf(bytes, path);
				Assert.True(offset >= 0);
				bytes[offset] = (byte)'X';
				File.WriteAllBytes(generation.BundlePath, bytes);
			});

			Assert.Throws<InvalidDataException>(() =>
				new WindowsBundlePublicationService(generator).Publish(workspace, destination.Path,
					TestContext.Current.CancellationToken));

			Assert.False(File.Exists(destination.Path));
		}

		[Fact]
		public void GeneratedInventoryTypeChangeIsRejected() {
			ModernBundleFixture fixture = GetFixture();
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			using var destination = new TemporaryPath("type.exe");
			var generator = new MutatingGenerator(static generation => {
				BundleOpenResult opened = new BundleReader().Open(generation.BundlePath);
				Assert.Equal(BundleOpenStatus.Success, opened.Status);
				string relativePath;
				byte rawFileType;
				using (BundleFile generated = opened.Bundle!) {
					BundleEntry assembly = Assert.Single(generated.Entries,
						entry => entry.RelativePath == "SingleFile.App.dll");
					relativePath = assembly.RelativePath;
					rawFileType = assembly.RawFileType;
				}
				byte[] bytes = File.ReadAllBytes(generation.BundlePath);
				byte[] path = System.Text.Encoding.UTF8.GetBytes(relativePath);
				int pathOffset = LastIndexOf(bytes, path);
				Assert.True(pathOffset >= 2);
				Assert.Equal(path.Length, bytes[pathOffset - 1]);
				Assert.Equal(rawFileType, bytes[pathOffset - 2]);
				bytes[pathOffset - 2] = (byte)BundleFileType.NativeBinary;
				File.WriteAllBytes(generation.BundlePath, bytes);
			});

			Assert.Throws<InvalidDataException>(() =>
				new WindowsBundlePublicationService(generator).Publish(workspace, destination.Path,
					TestContext.Current.CancellationToken));

			Assert.False(File.Exists(destination.Path));
		}

		static int LastIndexOf(byte[] bytes, byte[] pattern) {
			for (int offset = bytes.Length - pattern.Length; offset >= 0; offset--) {
				if (bytes.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
					return offset;
			}
			return -1;
		}

		static ModernBundleFixture GetFixture() => ModernFixtureLocator.FindRequired().Single(item =>
			item.Variant == "fdd-uncompressed");

		static byte[] Hash(string filename) {
			using SHA256 sha256 = SHA256.Create();
			using FileStream stream = File.OpenRead(filename);
			return sha256.ComputeHash(stream);
		}

		static BundleWorkspace OpenWorkspace(string filename) {
			BundleOpenResult opened = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			return new BundleWorkspace(opened.Bundle!);
		}

		static BundleFile Open(string filename) {
			BundleOpenResult opened = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			return opened.Bundle!;
		}

		sealed class RecordingGenerator : IWindowsBundleGenerator {
			public string? TemporaryDirectory { get; private set; }

			public WindowsBundleGeneration Generate(BundleWorkspace workspace,
				CancellationToken cancellationToken = default) {
				WindowsBundleGeneration generation = new WindowsBundleRebuilder().Generate(workspace,
					cancellationToken);
				TemporaryDirectory = generation.TemporaryDirectory;
				return generation;
			}
		}

		sealed class MutatingGenerator : IWindowsBundleGenerator {
			readonly Action<WindowsBundleGeneration> mutate;

			public MutatingGenerator(Action<WindowsBundleGeneration> mutate) =>
				this.mutate = mutate;

			public string? TemporaryDirectory { get; private set; }

			public WindowsBundleGeneration Generate(BundleWorkspace workspace,
				CancellationToken cancellationToken = default) {
				WindowsBundleGeneration generation = new WindowsBundleRebuilder().Generate(workspace,
					cancellationToken);
				TemporaryDirectory = generation.TemporaryDirectory;
				mutate(generation);
				return generation;
			}
		}

		sealed class TemporaryPath : IDisposable {
			readonly string directory;

			public TemporaryPath(string filename) {
				directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
					"dnspy-bundle-publication-test-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(directory);
				Path = System.IO.Path.Combine(directory, filename);
			}

			public string Path { get; }

			public void Dispose() {
				if (Directory.Exists(directory))
					Directory.Delete(directory, recursive: true);
			}
		}
	}

	[Collection("Bundle temporary directory")]
	public sealed class SourceDestinationPreservationRegressionTests {
		[Fact]
		public void GeneratorFailurePreservesSourceAndExistingDestination() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			byte[] sourceHash = Hash(fixture.BundlePath);
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			using var destination = new TemporaryPath("existing.exe");
			byte[] destinationBytes = System.Text.Encoding.UTF8.GetBytes("existing destination");
			File.WriteAllBytes(destination.Path, destinationBytes);

			Assert.Throws<TestGenerationException>(() =>
				new WindowsBundlePublicationService(new FailingGenerator()).Publish(workspace,
					destination.Path, TestContext.Current.CancellationToken));

			Assert.Equal(sourceHash, Hash(fixture.BundlePath));
			Assert.Equal(destinationBytes, File.ReadAllBytes(destination.Path));
		}

		[Fact]
		public void SourceDestinationIsRejectedBeforeGeneration() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			byte[] sourceHash = Hash(fixture.BundlePath);
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			var generator = new CountingGenerator();

			Assert.Throws<ArgumentException>(() =>
				new WindowsBundlePublicationService(generator).Publish(workspace,
					Path.GetFullPath(fixture.BundlePath), TestContext.Current.CancellationToken));

			Assert.Equal(0, generator.CallCount);
			Assert.Equal(sourceHash, Hash(fixture.BundlePath));
		}

		[Fact]
		public void CancellationAfterGenerationPreservesBothFilesAndCleansGeneration() {
			ModernBundleFixture fixture = ModernFixtureLocator.FindRequired().Single(item =>
				item.Variant == "fdd-uncompressed");
			byte[] sourceHash = Hash(fixture.BundlePath);
			using BundleWorkspace workspace = OpenWorkspace(fixture.BundlePath);
			using var destination = new TemporaryPath("cancel.exe");
			byte[] destinationBytes = System.Text.Encoding.UTF8.GetBytes("existing destination");
			File.WriteAllBytes(destination.Path, destinationBytes);
			using var cancellation = new CancellationTokenSource();
			var generator = new CancellingGenerator(cancellation);

			Assert.Throws<OperationCanceledException>(() =>
				new WindowsBundlePublicationService(generator).Publish(workspace,
					destination.Path, cancellation.Token));

			Assert.Equal(sourceHash, Hash(fixture.BundlePath));
			Assert.Equal(destinationBytes, File.ReadAllBytes(destination.Path));
			Assert.NotNull(generator.TemporaryDirectory);
			Assert.False(Directory.Exists(generator.TemporaryDirectory));
		}

		static byte[] Hash(string filename) {
			using SHA256 sha256 = SHA256.Create();
			using FileStream stream = File.OpenRead(filename);
			return sha256.ComputeHash(stream);
		}

		static BundleWorkspace OpenWorkspace(string filename) {
			BundleOpenResult opened = new BundleReader().Open(filename);
			Assert.Equal(BundleOpenStatus.Success, opened.Status);
			return new BundleWorkspace(opened.Bundle!);
		}

		sealed class FailingGenerator : IWindowsBundleGenerator {
			public WindowsBundleGeneration Generate(BundleWorkspace workspace,
				CancellationToken cancellationToken = default) => throw new TestGenerationException();
		}

		sealed class CountingGenerator : IWindowsBundleGenerator {
			public int CallCount { get; private set; }

			public WindowsBundleGeneration Generate(BundleWorkspace workspace,
				CancellationToken cancellationToken = default) {
				CallCount++;
				throw new TestGenerationException();
			}
		}

		sealed class CancellingGenerator : IWindowsBundleGenerator {
			readonly CancellationTokenSource cancellation;

			public CancellingGenerator(CancellationTokenSource cancellation) =>
				this.cancellation = cancellation;

			public string? TemporaryDirectory { get; private set; }

			public WindowsBundleGeneration Generate(BundleWorkspace workspace,
				CancellationToken cancellationToken = default) {
				WindowsBundleGeneration generation = new WindowsBundleRebuilder().Generate(workspace,
					cancellationToken);
				TemporaryDirectory = generation.TemporaryDirectory;
				cancellation.Cancel();
				return generation;
			}
		}

		sealed class TestGenerationException : Exception {
		}

		sealed class TemporaryPath : IDisposable {
			readonly string directory;

			public TemporaryPath(string filename) {
				directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
					"dnspy-bundle-preservation-test-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(directory);
				Path = System.IO.Path.Combine(directory, filename);
			}

			public string Path { get; }

			public void Dispose() {
				if (Directory.Exists(directory))
					Directory.Delete(directory, recursive: true);
			}
		}
	}
}
