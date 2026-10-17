// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Protocol;

namespace MKITS.SupportSuite.Common.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void IsCompatible_SameMajor_ReturnsTrue()
    {
        Assert.True(ProtocolVersion.IsCompatible(ProtocolVersion.Major));
    }

    [Fact]
    public void IsCompatible_DifferentMajor_ReturnsFalse()
    {
        Assert.False(ProtocolVersion.IsCompatible(ProtocolVersion.Major + 1));
    }

    [Fact]
    public void AsString_HasMajorDotMinorFormat()
    {
        Assert.Equal($"{ProtocolVersion.Major}.{ProtocolVersion.Minor}", ProtocolVersion.AsString);
    }
}
