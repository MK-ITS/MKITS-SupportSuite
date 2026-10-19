// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Common.Tests.Sessions;

public sealed class SessionCodeFormatTests
{
    [Theory]
    [InlineData("12345678", "12345678")]
    [InlineData("1234 5678", "12345678")]
    [InlineData(" 1234-5678 ", "12345678")]
    [InlineData("123456", "123456")]
    [InlineData("123456789012", "123456789012")]
    public void Normalize_WellFormed_ReturnsDigits(string input, string expected)
    {
        Assert.Equal(expected, SessionCodeFormat.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("1234567890123")]
    [InlineData("1234a678")]
    [InlineData("１２３４５６７８")] // full-width digits are not accepted
    public void Normalize_Malformed_ReturnsNull(string? input)
    {
        Assert.Null(SessionCodeFormat.Normalize(input));
    }

    [Theory]
    [InlineData("12345678", "1234 5678")]
    [InlineData("123456", "1234 56")]
    [InlineData("123456789012", "1234 5678 9012")]
    public void Format_GroupsOfFour(string code, string expected)
    {
        Assert.Equal(expected, SessionCodeFormat.Format(code));
    }
}
