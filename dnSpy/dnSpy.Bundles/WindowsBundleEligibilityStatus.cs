// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace dnSpy.Bundles {
	/// <summary>Stable outcome of the Windows bundle rebuild preflight.</summary>
	public enum WindowsBundleEligibilityStatus {
		/// <summary>The source is an eligible Windows x64 bundle.</summary>
		Eligible,
		/// <summary>The source is not an official bundle.</summary>
		NotBundle,
		/// <summary>The official bundle metadata is malformed or unsupported.</summary>
		MalformedBundle,
		/// <summary>The source is not a valid Windows PE image.</summary>
		UnsupportedPlatform,
		/// <summary>The Windows PE architecture is not x64.</summary>
		UnsupportedArchitecture,
		/// <summary>The source is a recognized Windows NativeAOT executable.</summary>
		NativeAot,
		/// <summary>The bundle has no conventional managed assembly entry.</summary>
		NoManagedAssembly,
		/// <summary>An entry has a raw type that HostModel cannot preserve.</summary>
		UnknownFileType,
		/// <summary>A modified assembly entry is ReadyToRun.</summary>
		DirtyReadyToRun,
		/// <summary>Two assembly entries have the same complete assembly identity.</summary>
		AmbiguousAssemblyIdentity,
		/// <summary>An assembly entry is not a valid managed PE image.</summary>
		MalformedManagedAssembly,
		/// <summary>A managed entry is too large for bounded eligibility inspection.</summary>
		InspectionLimitExceeded,
	}
}
