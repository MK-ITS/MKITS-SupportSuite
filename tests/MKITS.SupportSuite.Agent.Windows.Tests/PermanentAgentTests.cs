// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using System.Text;
using MKITS.SupportSuite.Agent.Windows.Permanent;
using MKITS.SupportSuite.Agent.Windows.Platform;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

public sealed class PermanentAgentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mkits-agent-" + Guid.NewGuid().ToString("N"));

    public PermanentAgentTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("https://support.example.com", "https://support.example.com/")]
    [InlineData("https://support.example.com:8443/some/path?x=1", "https://support.example.com:8443/")]
    [InlineData("http://localhost:5080", "http://localhost:5080/")]
    [InlineData("http://support.example.com", null)]
    [InlineData("https://user:pass@support.example.com", null)]
    [InlineData("ftp://support.example.com", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void ParseServer_RequiresHttpsExceptLocalhost(string? input, string? expected) =>
        Assert.Equal(expected, AgentConfig.ParseServer(input)?.ToString());

    [Fact]
    public void ParseInstallArguments_TokenRequiredUnlessEnrolled()
    {
        string[] withToken = ["--install", "--server", "https://support.example.com", "--token", "abc"];
        string[] withoutToken = ["--install", "--server", "https://support.example.com"];
        string[] reenrollWithoutToken = ["--install", "--server", "https://support.example.com", "--reenroll"];

        var parsed = AgentInstaller.ParseInstallArguments(withToken, alreadyEnrolled: false);
        Assert.NotNull(parsed);
        Assert.Equal("abc", parsed.Token);
        Assert.Null(AgentInstaller.ParseInstallArguments(withoutToken, alreadyEnrolled: false));
        Assert.NotNull(AgentInstaller.ParseInstallArguments(withoutToken, alreadyEnrolled: true));
        Assert.Null(AgentInstaller.ParseInstallArguments(reenrollWithoutToken, alreadyEnrolled: true));
    }

    [Fact]
    public void Dpapi_RoundTrip_AndPurposeSeparatesSecrets()
    {
        var secret = Encoding.UTF8.GetBytes("enrollment token");

        var protectedData = Dpapi.Protect(secret, "a");

        Assert.NotEqual(secret, protectedData);
        Assert.Equal(secret, Dpapi.Unprotect(protectedData, "a"));
        Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(protectedData, "b"));
    }

    [Fact]
    public void AgentConfig_StoresTheTokenOnlyProtected()
    {
        var path = Path.Combine(_directory, "agent.json");

        AgentConfig.Create(new Uri("https://support.example.com/"), "secret-token-value").Save(path);
        var loaded = AgentConfig.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("secret-token-value", loaded.ReadToken());
        Assert.DoesNotContain("secret-token-value", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(new AgentConfig("https://support.example.com/", null).ReadToken());
    }
}
