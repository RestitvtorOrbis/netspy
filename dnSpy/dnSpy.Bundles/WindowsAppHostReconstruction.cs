// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading;

namespace dnSpy.Bundles {
	/// <summary>
	/// Owns a temporary unbundled Windows apphost produced for HostModel input.
	/// </summary>
	public sealed class WindowsAppHostReconstruction : IDisposable {
		readonly string temporaryDirectory;
		int disposed;

		internal WindowsAppHostReconstruction(string hostPath, string temporaryDirectory,
			long payloadStart, long headerPointerOffset, bool hadAuthenticodeSignature) {
			HostPath = hostPath ?? throw new ArgumentNullException(nameof(hostPath));
			this.temporaryDirectory = temporaryDirectory ??
				throw new ArgumentNullException(nameof(temporaryDirectory));
			if (payloadStart < 0)
				throw new ArgumentOutOfRangeException(nameof(payloadStart));
			if (headerPointerOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(headerPointerOffset));
			PayloadStart = payloadStart;
			HeaderPointerOffset = headerPointerOffset;
			HadAuthenticodeSignature = hadAuthenticodeSignature;
		}

		/// <summary>Path to the temporary reconstructed apphost.</summary>
		public string HostPath { get; }

		/// <summary>Alias for <see cref="HostPath"/> for HostModel adapters.</summary>
		public string Path => HostPath;

		/// <summary>Private directory containing <see cref="HostPath"/>.</summary>
		public string TemporaryDirectory => temporaryDirectory;

		/// <summary>Offset at which the source bundle payload begins.</summary>
		public long PayloadStart { get; }

		/// <summary>Offset of the eight-byte bundle header pointer in the source host.</summary>
		public long HeaderPointerOffset { get; }

		/// <summary>
		/// True when the source PE contained an in-file certificate table. Rebuilding does not
		/// preserve that signature.
		/// </summary>
		public bool HadAuthenticodeSignature { get; }

		/// <summary>Releases the temporary host and its private directory.</summary>
		public void Dispose() {
			if (Interlocked.Exchange(ref disposed, 1) != 0)
				return;
			TryDelete(HostPath);
			TryDeleteDirectory(temporaryDirectory);
		}

		internal static void TryDelete(string filename) {
			try {
				if (File.Exists(filename))
					File.Delete(filename);
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
		}

		internal static void TryDeleteDirectory(string directory) {
			try {
				if (Directory.Exists(directory))
					Directory.Delete(directory, recursive: true);
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
		}
	}
}
