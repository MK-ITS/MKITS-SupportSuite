// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>One entry of an Xauthority file.</summary>
internal sealed record XAuthorityEntry(ushort Family, byte[] Address, string Number, string Name, byte[] Data);

/// <summary>
/// Reads Xauthority files (libXau format: big-endian, length-prefixed fields) and finds the MIT-MAGIC-COOKIE-1 for a
/// display. The system service uses it to connect to the logged-on user's X server without changing its environment.
/// </summary>
internal static class XAuthority
{
    internal const ushort FamilyLocal = 256;
    internal const ushort FamilyWild = 65535;
    internal const string MitMagicCookie = "MIT-MAGIC-COOKIE-1";

    /// <summary>Parses the entries; stops at the first truncated entry.</summary>
    public static IReadOnlyList<XAuthorityEntry> Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<XAuthorityEntry>();
        while (data.Length >= 2)
        {
            var family = BinaryPrimitives.ReadUInt16BigEndian(data);
            data = data[2..];
            if (!TryReadField(ref data, out var address) || !TryReadField(ref data, out var number)
                || !TryReadField(ref data, out var name) || !TryReadField(ref data, out var cookie))
            {
                break;
            }

            entries.Add(new XAuthorityEntry(family, address, Encoding.ASCII.GetString(number), Encoding.ASCII.GetString(name), cookie));
        }

        return entries;
    }

    /// <summary>Serializes entries (used by tests and to hand a cookie to the user's process).</summary>
    public static byte[] Write(IEnumerable<XAuthorityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[2];
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(length, entry.Family);
            stream.Write(length);
            foreach (var field in new[] { entry.Address, Encoding.ASCII.GetBytes(entry.Number), Encoding.ASCII.GetBytes(entry.Name), entry.Data })
            {
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)field.Length);
                stream.Write(length);
                stream.Write(field);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The cookie for display <paramref name="displayNumber"/>: local or wildcard entries for this host, entries without
    /// a display number (X servers started with <c>-auth</c> often write those) only as a fallback.
    /// </summary>
    public static byte[]? FindCookie(IEnumerable<XAuthorityEntry> entries, string displayNumber, string hostName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        XAuthorityEntry? fallback = null;
        foreach (var entry in entries)
        {
            if (entry.Name != MitMagicCookie || entry.Data.Length == 0)
            {
                continue;
            }

            var hostMatches = entry.Family == FamilyWild
                || (entry.Family == FamilyLocal && (entry.Address.Length == 0 || string.Equals(Encoding.ASCII.GetString(entry.Address), hostName, StringComparison.OrdinalIgnoreCase)));
            if (!hostMatches)
            {
                continue;
            }

            if (entry.Number == displayNumber)
            {
                return entry.Data;
            }

            if (entry.Number.Length == 0)
            {
                fallback ??= entry;
            }
        }

        return fallback?.Data;
    }

    /// <summary><c>:0</c>, <c>:0.0</c>, <c>unix:1</c> → <c>0</c>/<c>1</c>; <c>null</c> for remote or malformed displays.</summary>
    public static string? DisplayNumber(string display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var colon = display.LastIndexOf(':');
        if (colon < 0 || (colon > 0 && display[..colon] != "unix"))
        {
            return null;
        }

        var number = display[(colon + 1)..];
        var dot = number.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            number = number[..dot];
        }

        return number.Length is > 0 and <= 5 && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? number : null;
    }

    private static bool TryReadField(ref ReadOnlySpan<byte> data, out byte[] field)
    {
        field = [];
        if (data.Length < 2)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (data.Length < 2 + length)
        {
            return false;
        }

        field = data.Slice(2, length).ToArray();
        data = data[(2 + length)..];
        return true;
    }
}
