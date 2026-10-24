// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>
/// DPAPI with machine scope (<c>CRYPTPROTECT_LOCAL_MACHINE</c>): any process on this computer can decrypt, so the data
/// must additionally live in a folder only SYSTEM and Administrators can read. It protects against the files being
/// copied to another machine or read from a backup. A purpose string separates different secrets.
/// </summary>
internal static partial class Dpapi
{
    private const int UiForbidden = 0x1;
    private const int LocalMachine = 0x4;

    public static byte[] Protect(byte[] data, string purpose) => Transform(data, purpose, protect: true);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> for data that cannot be decrypted.</summary>
    public static byte[] Unprotect(byte[] data, string purpose) => Transform(data, purpose, protect: false);

    private static unsafe byte[] Transform(byte[] data, string purpose, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        var entropy = Encoding.UTF8.GetBytes("MKITS-SupportSuite/" + purpose);
        fixed (byte* input = data)
        fixed (byte* entropyBytes = entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = (nint)entropyBytes };
            DataBlob outBlob;
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, UiForbidden | LocalMachine, out outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out outBlob);
            if (!ok)
            {
                throw new System.Security.Cryptography.CryptographicException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                _ = LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
