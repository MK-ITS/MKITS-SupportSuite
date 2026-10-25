// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.Downloads;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class PortalTests : IDisposable
{
    private const string WindowsUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";
    private static readonly byte[] FakeClient = [0x4D, 0x5A, 1, 2, 3];

    private readonly MkitsWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public PortalTests()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(WindowsUserAgent);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Home_ShowsCodeForm()
    {
        var html = await _client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("name=\"Input.Code\"", html, StringComparison.Ordinal);
        Assert.Contains("Remote support", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCode_ShowsError_AndBansAfterFiveAttempts()
    {
        const string ip = "203.0.113.50";
        for (var i = 0; i < 5; i++)
        {
            using var response = await SubmitCodeAsync("0000 0000", ip);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("This code is not valid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var banned = await HtmlForms.SendAsync(_client, HttpMethod.Get, "/", ip, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, banned.StatusCode);
    }

    [Fact]
    public async Task ValidCode_WithoutClients_ExplainsThatNoDownloadExists()
    {
        var code = await CreateCodeAsync();

        using var response = await SubmitCodeAsync(code);

        Assert.Contains("No support client is available", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidCode_OffersDownload_WithCodeAndHostInFileName()
    {
        PlaceClient("windows-x64/MKITS-Support.exe");
        var code = await CreateCodeAsync();

        using var page = await SubmitCodeAsync(code);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Download for Windows", html, StringComparison.Ordinal);
        Assert.Contains("value=\"WindowsX64\"", html, StringComparison.Ordinal);

        using var download = await DownloadAsync(html, code, "WindowsX64");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal($"MKITS-Support_support.example.test_{code}.exe", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(FakeClient, await download.Content.ReadAsByteArrayAsync());
        Assert.True(download.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Download_RevokedCode_RedirectsWithError()
    {
        PlaceClient("windows-x64/MKITS-Support.exe");
        var code = await CreateCodeAsync();
        using var page = await SubmitCodeAsync(code);
        var html = await page.Content.ReadAsStringAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var list = await sessions.ListAsync(null, includeEnded: false);
            await sessions.EndAsync(list[0].Id, list[0].OwnerUserId, actorIsAdmin: true);
        }

        using var download = await DownloadAsync(html, code, "WindowsX64");

        Assert.Equal(HttpStatusCode.Redirect, download.StatusCode);
        Assert.Equal("/?error=invalid", HtmlForms.RedirectTarget(download));
    }

    [Fact]
    public async Task Download_WithoutAntiforgeryToken_IsRejected()
    {
        PlaceClient("windows-x64/MKITS-Support.exe");
        var code = await CreateCodeAsync();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["platform"] = "WindowsX64" });
        using var response = await _client.PostAsync(new Uri(PortalEndpoints.DownloadPath, UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(WindowsUserAgent, ClientPlatform.WindowsX64)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0", ClientPlatform.LinuxX64)]
    [InlineData("Mozilla/5.0 (X11; Linux aarch64; rv:140.0) Gecko/20100101 Firefox/140.0", ClientPlatform.LinuxArm64)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/140.0 Mobile Safari/537.36", ClientPlatform.Android)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 Safari/605.1.15", null)]
    [InlineData(null, null)]
    public void PlatformDetector_FromUserAgent(string? userAgent, ClientPlatform? expected)
    {
        Assert.Equal(expected, PlatformDetector.FromUserAgent(userAgent));
    }

    private Task<HttpResponseMessage> SubmitCodeAsync(string code, string? ip = null) =>
        HtmlForms.SubmitAsync(_client, "/portal/code", "portal-code", new Dictionary<string, string> { ["Input.Code"] = code }, ip);

    private async Task<HttpResponseMessage> DownloadAsync(string pageHtml, string code, string platform)
    {
        var token = HtmlForms.AntiforgeryToken(pageHtml) ?? throw new InvalidOperationException("no antiforgery token");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["platform"] = platform,
            ["__RequestVerificationToken"] = token,
        });
        return await _client.PostAsync(new Uri(PortalEndpoints.DownloadPath, UriKind.Relative), content);
    }

    private async Task<string> CreateCodeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var supporter = new MkitsUser { Id = Guid.NewGuid(), UserName = $"s{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@example.test", DisplayName = "Supporter" };
        Assert.True((await users.CreateAsync(supporter, "a long test passphrase")).Succeeded);

        var result = await scope.ServiceProvider.GetRequiredService<SessionService>().CreateAdHocAsync(supporter.Id, null);
        return Assert.IsType<CreatedSession>(result.Session).Code;
    }

    [Fact]
    public async Task AgentUpdate_WithoutSignedManifest_IsNotOffered()
    {
        PlaceClient(Path.Combine("windows-x64", "MKITS-Support.exe"));

        using var offer = await _client.GetAsync(new Uri("/api/agent-updates/windows-x64", UriKind.Relative));
        using var unknown = await _client.GetAsync(new Uri("/api/agent-updates/macos-arm64", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, offer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AgentUpdate_ServesManifestSignatureAndFile_AsStored()
    {
        PlaceClient(Path.Combine("windows-x64", "MKITS-Support.exe"));
        var directory = Path.Combine(_factory.DataDirectory, "agents", "windows-x64");
        var manifest = "{\"product\":\"MKITS-SupportSuite\"}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(directory, "MKITS-Support.exe.manifest.json"), manifest);
        await File.WriteAllTextAsync(Path.Combine(directory, "MKITS-Support.exe.manifest.sig"), "c2lnbmF0dXJl\n");

        using var offer = await _client.GetAsync(new Uri("/api/agent-updates/windows-x64", UriKind.Relative));
        var json = await offer.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var file = await _client.GetByteArrayAsync(new Uri("/api/agent-updates/windows-x64/file", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, offer.StatusCode);
        Assert.Equal(Convert.ToBase64String(manifest), json.GetProperty("manifest").GetString());
        Assert.Equal("c2lnbmF0dXJl", json.GetProperty("signature").GetString());
        Assert.Equal(FakeClient, file);
    }

    [Fact]
    public async Task LinuxAgentPackage_IsFoundForAdmins_ButNotOfferedOnThePortal()
    {
        PlaceClient(ClientCatalog.LinuxAgentPackage);
        var catalog = _factory.Services.GetRequiredService<ClientCatalog>();

        using var update = await _client.GetAsync(new Uri("/api/agent-updates/linux-x64", UriKind.Relative));

        Assert.NotNull(catalog.FindFile(ClientCatalog.LinuxAgentPackage));
        Assert.Empty(catalog.GetAvailable()); // the portal offers ad-hoc clients only
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    private void PlaceClient(string relativePath)
    {
        var path = Path.Combine(_factory.DataDirectory, "agents", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, FakeClient);
    }
}
