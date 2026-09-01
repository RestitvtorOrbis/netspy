// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;

namespace Microsoft.NET.HostModel
{
    internal static class HostModelUtils
    {
        public static long GetFileLength(string path) => new FileInfo(path).Length;
    }
}
