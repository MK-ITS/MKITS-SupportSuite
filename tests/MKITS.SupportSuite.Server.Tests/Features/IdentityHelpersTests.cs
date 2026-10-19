// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Logging.Abstractions;
using MKITS.SupportSuite.Server.Features.Identity;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class IdentityHelpersTests
{
    [Theory]
    [InlineData("/admin/users", "/admin/users")]
    [InlineData("/account/manage?x=1", "/account/manage?x=1")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/x\r\nSet-Cookie: a=b", "/")]
    public void LocalUrl_Sanitize_AllowsOnlyLocalPaths(string? input, string expected)
    {
        Assert.Equal(expected, LocalUrl.Sanitize(input));
    }

    [Fact]
    public void TotpQrCode_FormatsKeyInGroups()
    {
        Assert.Equal("abcd efgh ij", TotpQrCode.FormatKey("ABCDEFGHIJ"));
    }

    [Theory]
    [InlineData("https://support.example.com", "MKITS (support.example.com)")]
    [InlineData("not a url", "MKITS")]
    public void TotpQrCode_IssuerContainsHost(string url, string expected)
    {
        Assert.Equal(expected, TotpQrCode.IssuerFor(url));
    }

    [Fact]
    public void TotpQrCode_BuildsEscapedOtpauthUri_AndSvgDataUri()
    {
        var uri = TotpQrCode.BuildUri("MKITS (support.example.com)", "anna@example.com", "KEY123");

        Assert.Equal("otpauth://totp/MKITS%20%28support.example.com%29:anna%40example.com?secret=KEY123&issuer=MKITS%20%28support.example.com%29&digits=6&period=30", uri);
        Assert.StartsWith("data:image/svg+xml;base64,", TotpQrCode.CreateSvgDataUri(uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupToken_WrongToken_ReturnsNull_AndDoesNotRunCreation()
    {
        using var service = new SetupTokenService(NullLogger<SetupTokenService>.Instance);
        service.Activate();
        var ran = false;

        var result = await service.RunSetupAsync("WRONG-TOKEN", () => { ran = true; return Task.FromResult(true); });

        Assert.Null(result);
        Assert.False(ran);
        Assert.True(service.IsSetupRequired);
    }

    [Fact]
    public async Task SetupToken_CorrectToken_IgnoresCaseAndDashes_AndIsConsumed()
    {
        using var service = new SetupTokenService(NullLogger<SetupTokenService>.Instance);
        service.Activate();
        var typed = service.CurrentToken!.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();

        var result = await service.RunSetupAsync(typed, () => Task.FromResult(true));

        Assert.True(result);
        Assert.False(service.IsSetupRequired);
        Assert.Null(await service.RunSetupAsync(typed, () => Task.FromResult(true)));
    }

    [Fact]
    public async Task SetupToken_FailedCreation_KeepsToken()
    {
        using var service = new SetupTokenService(NullLogger<SetupTokenService>.Instance);
        service.Activate();

        var result = await service.RunSetupAsync(service.CurrentToken, () => Task.FromResult(false));

        Assert.False(result);
        Assert.True(service.IsSetupRequired);
    }

    [Fact]
    public void SetupToken_HasFourGroupsOfFive_WithoutAmbiguousCharacters()
    {
        using var service = new SetupTokenService(NullLogger<SetupTokenService>.Instance);
        service.Activate();

        Assert.Matches("^[A-HJ-NP-Z2-9]{5}(-[A-HJ-NP-Z2-9]{5}){3}$", service.CurrentToken);
    }
}
