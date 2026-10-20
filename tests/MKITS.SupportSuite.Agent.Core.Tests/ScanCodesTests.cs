// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class ScanCodesTests
{
    [Theory]
    [InlineData("KeyA", (ushort)0x1E, false)]
    [InlineData("Enter", (ushort)0x1C, false)]
    [InlineData("NumpadEnter", (ushort)0xE01C, true)]
    [InlineData("ArrowLeft", (ushort)0xE04B, true)]
    [InlineData("MetaLeft", (ushort)0xE05B, true)]
    [InlineData("IntlBackslash", (ushort)0x56, false)]
    public void FromCode_KnownKeys(string code, ushort expected, bool extended)
    {
        var scanCode = ScanCodes.FromCode(code);

        Assert.Equal(expected, scanCode);
        Assert.Equal(extended, ScanCodes.IsExtended(scanCode!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("keya")]
    [InlineData("Unidentified")]
    public void FromCode_Unknown_ReturnsNull(string? code)
    {
        Assert.Null(ScanCodes.FromCode(code));
    }
}
