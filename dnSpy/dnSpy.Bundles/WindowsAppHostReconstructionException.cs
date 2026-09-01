// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace dnSpy.Bundles {
	/// <summary>Stable, safe failure raised while creating a temporary apphost.</summary>
	public sealed class WindowsAppHostReconstructionException : InvalidOperationException {
		/// <summary>Creates a reconstruction failure with a stable category.</summary>
		public WindowsAppHostReconstructionException(
			WindowsAppHostReconstructionErrorCode code, string message)
			: base(message) {
			Code = code;
		}

		/// <summary>Creates a reconstruction failure with a stable category and cause.</summary>
		public WindowsAppHostReconstructionException(
			WindowsAppHostReconstructionErrorCode code, string message, Exception innerException)
			: base(message, innerException) {
			Code = code;
		}

		/// <summary>Stable category of the failure.</summary>
		public WindowsAppHostReconstructionErrorCode Code { get; }
	}
}
