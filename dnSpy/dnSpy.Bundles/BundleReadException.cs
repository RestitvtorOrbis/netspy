// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later


using System;

namespace dnSpy.Bundles {
	/// <summary>Internal exception used to carry a safe parser error to the result boundary.</summary>
	sealed class BundleReadException : Exception {
		public BundleReadException(BundleReadErrorCode code, string message, long? offset = null)
			: base(message) {
			Code = code;
			Offset = offset;
		}

		public BundleReadException(BundleReadErrorCode code, string message, int entryIndex, long? offset = null)
			: base(message) {
			Code = code;
			EntryIndex = entryIndex;
			Offset = offset;
		}

		public BundleReadException(BundleReadErrorCode code, string message, long? offset, Exception innerException)
			: base(message, innerException) {
			Code = code;
			Offset = offset;
		}

		public BundleReadException(BundleReadErrorCode code, string message, int entryIndex,
			long? offset, Exception innerException)
			: base(message, innerException) {
			Code = code;
			EntryIndex = entryIndex;
			Offset = offset;
		}

		public BundleReadErrorCode Code { get; }
		public int? EntryIndex { get; }
		public long? Offset { get; }
	}
}
