// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Common.Tests.Sessions;

public sealed class AdHocClientFileNameTests
{
    [Theory]
    [InlineData("support.example.com", null, "1234 5678", ".exe", "MKITS-Support_support.example.com_12345678.exe")]
    [InlineData("Support.Example.com", 8443, "12345678", ".exe", "MKITS-Support_support.example.com+8443_12345678.exe")]
    [InlineData("192.0.2.10", null, "123456", ".AppImage", "MKITS-Support_192.0.2.10_123456.AppImage")]
    [InlineData("[2001:db8::1]", null, "12345678", ".exe", "MKITS-Support.exe")]
    [InlineData("support.example.com", null, "abc", ".exe", "MKITS-Support.exe")]
    public void Build_EncodesHostPortAndCode(string host, int? port, string code, string extension, string expected)
    {
        Assert.Equal(expected, AdHocClientFileName.Build(host, port, code, extension));
    }

    [Theory]
    [InlineData("MKITS-Support_support.example.com_12345678.exe", "support.example.com", null, "12345678")]
    [InlineData("MKITS-Support_support.example.com_12345678 (1).exe", "support.example.com", null, "12345678")]
    [InlineData("MKITS-Support_support.example.com_12345678(2).exe", "support.example.com", null, "12345678")]
    [InlineData(@"C:\Users\Max\Downloads\MKITS-Support_SUPPORT.example.com+8443_12345678.exe", "support.example.com", 8443, "12345678")]
    [InlineData("/home/max/MKITS-Support_support.example.com_123456789012.AppImage", "support.example.com", null, "123456789012")]
    [InlineData("MKITS-Support_localhost+5080_12345678", "localhost", 5080, "12345678")]
    public void TryParse_ValidNames(string fileName, string host, int? port, string code)
    {
        Assert.Equal(new AdHocClientTarget(host, port, code), AdHocClientFileName.TryParse(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MKITS-Support.exe")]
    [InlineData("setup.exe")]
    [InlineData("MKITS-Support_support.example.com_12345.exe")]
    [InlineData("MKITS-Support_-bad-.example.com_12345678.exe")]
    [InlineData("MKITS-Support_support..example.com_12345678.exe")]
    [InlineData("MKITS-Support_support.example.com+70000_12345678.exe")]
    [InlineData("MKITS-Support_support.example.com+0_12345678.exe")]
    [InlineData("MKITS-Support_evil.com_x_12345678.exe")]
    public void TryParse_InvalidNames_ReturnNull(string? fileName)
    {
        Assert.Null(AdHocClientFileName.TryParse(fileName));
    }

    [Fact]
    public void BuildThenParse_RoundTrips()
    {
        var name = AdHocClientFileName.Build("support.example.com", 8443, "87654321", ".exe");

        Assert.Equal(new AdHocClientTarget("support.example.com", 8443, "87654321"), AdHocClientFileName.TryParse(name));
    }
}
