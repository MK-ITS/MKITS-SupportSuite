// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Common.Sessions;

/// <summary>
/// Shape of ad-hoc session codes (docs/session-lifecycle.md §2): decimal digits only, 6–12 long,
/// displayed in groups of four (<c>1234 5678</c>). Users may type spaces or dashes; they are ignored.
/// </summary>
public static class SessionCodeFormat
{
    public const int MinLength = 6;

    public const int MaxLength = 12;

    public const int DefaultLength = 8;

    /// <summary>
    /// Removes spaces and dashes and checks the remaining characters. Returns <c>null</c> if the input
    /// is not a well-formed code.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxLength * 2)
        {
            return null;
        }

        Span<char> digits = stackalloc char[MaxLength];
        var count = 0;
        foreach (var c in input)
        {
            if (c is ' ' or '-' or ' ')
            {
                continue;
            }

            if (c is < '0' or > '9' || count == MaxLength)
            {
                return null;
            }

            digits[count++] = c;
        }

        return count >= MinLength ? new string(digits[..count]) : null;
    }

    /// <summary>Formats a normalized code for display, e.g. <c>12345678</c> → <c>1234 5678</c>.</summary>
    public static string Format(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var groups = new List<string>();
        for (var i = 0; i < code.Length; i += 4)
        {
            groups.Add(code.Substring(i, Math.Min(4, code.Length - i)));
        }

        return string.Join(' ', groups);
    }
}
