// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.HostModel.AppHost;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Microsoft.NET.HostModel.Bundle
{
    /// <summary>
    /// Target information for the Windows x64-only bundle subset.
    /// </summary>
    public class TargetInfo
    {
        public readonly OSPlatform OS;
        public readonly Architecture Arch;
        public readonly Version FrameworkVersion;
        public readonly uint BundleMajorVersion;
        public readonly BundleOptions DefaultOptions;
        public readonly int AssemblyAlignment;

        public TargetInfo(OSPlatform? os, Architecture? arch, Version targetFrameworkVersion)
        {
            OS = os ?? OSPlatform.Windows;
            Arch = arch ?? Architecture.X64;
            FrameworkVersion = targetFrameworkVersion ?? Environment.Version;

            if (!OS.Equals(OSPlatform.Windows) || Arch != Architecture.X64)
                throw new ArgumentException("This source subset supports only Windows x64 bundle targets.");

            if (FrameworkVersion.Major >= 6)
            {
                BundleMajorVersion = 6u;
                DefaultOptions = BundleOptions.None;
            }
            else if (FrameworkVersion.Major == 5)
            {
                BundleMajorVersion = 2u;
                DefaultOptions = BundleOptions.None;
            }
            else if (FrameworkVersion.Major == 3)
            {
                BundleMajorVersion = 1u;
                DefaultOptions = BundleOptions.BundleAllContent;
            }
            else
            {
                throw new ArgumentException($"Invalid input: Unsupported Target Framework Version {targetFrameworkVersion}");
            }

            AssemblyAlignment = 4096;
        }

        public bool IsNativeBinary(string filePath) => PEUtils.IsPEImage(filePath);

        public string GetAssemblyName(string hostName) => Path.GetFileNameWithoutExtension(hostName);

        public override string ToString() => $"OS: win Arch: {Arch.ToString().ToLowerInvariant()} FrameworkVersion: {FrameworkVersion}";

        public bool IsWindows => true;

        public FileType TargetSpecificFileType(FileType fileType) => (BundleMajorVersion == 1) ? FileType.Unknown : fileType;

        public bool ShouldExclude(string relativePath) =>
            (FrameworkVersion.Major != 3) && (relativePath.Equals("hostfxr.dll") || relativePath.Equals("hostpolicy.dll"));
    }
}
