// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.IO;
using System.Runtime.ExceptionServices;

namespace dnSpy.Bundles {
	/// <summary>Exposes exactly the declared logical length of a decompressed entry.</summary>
	/// <remarks>
	/// Deflate has no knowledge of the bundle manifest's logical size. This
	/// wrapper rejects an early end and probes one byte beyond the declared
	/// length before reporting successful completion.
	/// </remarks>
	sealed class ExactLengthReadStream : Stream {
		readonly BundleFile owner;
		readonly Stream stream;
		readonly long length;
		readonly Action? validateCompletion;
		long position;
		bool completed;
		bool disposed;
		ExceptionDispatchInfo? failure;

		public ExactLengthReadStream(BundleFile owner, Stream stream, long length,
			Action? validateCompletion = null) {
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			if (length < 0)
				throw new ArgumentOutOfRangeException(nameof(length));
			this.length = length;
			this.validateCompletion = validateCompletion;
		}

		public override bool CanRead => !disposed && !owner.IsDisposed && stream.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length {
			get {
				EnsureUsable();
				return length;
			}
		}
		public override long Position {
			get {
				EnsureUsable();
				return position;
			}
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			if (offset < 0 || count < 0 || offset > buffer.Length - count)
				throw new ArgumentOutOfRangeException();
			EnsureUsable();
			failure?.Throw();
			if (count == 0)
				return 0;
			if (completed)
				return 0;

			long remaining = length - position;
			if (remaining <= 0) {
				ValidateEnd();
				return 0;
			}

			int requested = (int)Math.Min((long)count, remaining);
			int read = stream.Read(buffer, offset, requested);
			if (read < 0 || read > requested)
				throw new InvalidDataException("The bundle decompressor returned an invalid length.");
			if (read == 0)
				throw new InvalidDataException("The bundle entry ended before its declared logical length.");

			position = checked(position + read);
			if (position == length)
				ValidateEnd();
			return read;
		}

		public override int ReadByte() {
			byte[] buffer = new byte[1];
			return Read(buffer, 0, 1) == 0 ? -1 : buffer[0];
		}

		void ValidateEnd() {
			if (completed)
				return;
			try {
				int extra = stream.ReadByte();
				if (extra >= 0)
					throw new InvalidDataException("The bundle entry exceeds its declared logical length.");
				validateCompletion?.Invoke();
				completed = true;
			}
			catch (Exception ex) {
				failure = ExceptionDispatchInfo.Capture(ex);
				throw;
			}
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void Flush() => EnsureUsable();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		void EnsureUsable() {
			if (disposed)
				throw new ObjectDisposedException(nameof(ExactLengthReadStream));
			owner.EnsureNotDisposed();
		}

		protected override void Dispose(bool disposing) {
			if (!disposed) {
				disposed = true;
				if (disposing)
					stream.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
