// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;
using System.IO;

namespace dnSpy.Bundles {
	/// <summary>
	/// Read-only stream tied to one bundle and one exact mapped range.
	/// </summary>
	sealed class BoundedReadStream : Stream {
		readonly BundleFile owner;
		readonly Stream stream;
		readonly long length;
		bool disposed;

		public BoundedReadStream(BundleFile owner, Stream stream, long length) {
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			if (length < 0)
				throw new ArgumentOutOfRangeException(nameof(length));
			this.length = length;
		}

		public override bool CanRead => !disposed && !owner.IsDisposed && stream.CanRead;
		public override bool CanSeek => !disposed && !owner.IsDisposed && stream.CanSeek;
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
				return stream.Position;
			}
			set {
				EnsureUsable();
				if (value < 0 || value > length)
					throw new ArgumentOutOfRangeException(nameof(value));
				stream.Position = value;
			}
		}

		public override int Read(byte[] buffer, int offset, int count) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			if (offset < 0 || count < 0 || offset > buffer.Length - count)
				throw new ArgumentOutOfRangeException();
			EnsureUsable();
			long remaining = length - stream.Position;
			if (remaining <= 0 || count == 0)
				return 0;
			int requested = (int)Math.Min((long)count, remaining);
			return stream.Read(buffer, offset, requested);
		}

		public override int ReadByte() {
			EnsureUsable();
			if (stream.Position >= length)
				return -1;
			return stream.ReadByte();
		}

		public override long Seek(long offset, SeekOrigin origin) {
			EnsureUsable();
			long target;
			switch (origin) {
				case SeekOrigin.Begin:
					target = offset;
					break;
				case SeekOrigin.Current:
					target = checked(stream.Position + offset);
					break;
				case SeekOrigin.End:
					target = checked(length + offset);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(origin));
			}
			if (target < 0 || target > length)
				throw new IOException("The requested position is outside the entry range.");
			stream.Position = target;
			return target;
		}

		public override void Flush() {
			EnsureUsable();
		}

		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		void EnsureUsable() {
			if (disposed)
				throw new ObjectDisposedException(nameof(BoundedReadStream));
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
