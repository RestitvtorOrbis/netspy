// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;

namespace Microsoft.NET.HostModel.AppHost
{
    public static class PEUtils
    {
        /// <summary>
        /// Check whether the apphost file is a windows PE image by looking at the first few bytes.
        /// </summary>
        public static bool IsPEImage(string filePath)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(filePath)))
            {
                if (reader.BaseStream.Length < PEOffsets.DosStub.PESignatureOffset + sizeof(uint))
                {
                    return false;
                }

                ushort signature = reader.ReadUInt16();
                return signature == PEOffsets.DosImageSignature;
            }
        }
    }
}
