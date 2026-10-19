// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace MKITS.SupportSuite.Server.Tests.TestSupport;

/// <summary>Minimal RFC 6238 TOTP generator (SHA-1, 6 digits, 30 s) – what an authenticator app computes.</summary>
public static class Totp
{
    public static string Compute(string base32Key, DateTimeOffset now)
    {
        var key = DecodeBase32(base32Key);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, now.ToUnixTimeSeconds() / 30);

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // HMAC-SHA1 is mandated by RFC 6238 / authenticator apps.
        HMACSHA1.HashData(key, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = value.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
