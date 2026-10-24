// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MKITS.SupportSuite.Agent.Core.Updates;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class AgentUpdateTests : IDisposable
{
    private static readonly Uri Server = new("https://support.example.com/");

    private readonly ECDsa _releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mkits-update-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _file = RandomNumberGenerator.GetBytes(10_000);

    public AgentUpdateTests() => Directory.CreateDirectory(_directory);

    private byte[] PublicKey => _releaseKey.ExportSubjectPublicKeyInfo();

    public void Dispose()
    {
        _releaseKey.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Manifest_SignedAndVerified_RoundTrip()
    {
        var bytes = Manifest(build: 200).ToBytes();
        var signature = UpdateManifest.Sign(bytes, _releaseKey);

        var verified = UpdateManifest.Verify(bytes, signature, PublicKey);

        Assert.NotNull(verified);
        Assert.Equal(200, verified.Build);
        Assert.Equal("windows-x64", verified.Platform);
    }

    [Fact]
    public void Manifest_TamperedWrongKeyOrMissingKey_IsRejected()
    {
        var bytes = Manifest(build: 200).ToBytes();
        var signature = UpdateManifest.Sign(bytes, _releaseKey);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"build\":200", "\"build\":999", StringComparison.Ordinal));

        Assert.Null(UpdateManifest.Verify(tampered, signature, PublicKey));
        Assert.Null(UpdateManifest.Verify(bytes, signature, otherKey.ExportSubjectPublicKeyInfo()));
        Assert.Null(UpdateManifest.Verify(bytes, signature, null));
    }

    [Fact]
    public void Manifest_SignedButImplausible_IsRejected()
    {
        var invalid = new UpdateManifest("Other", "windows-x64", 200, null, new string('a', 64), 10);
        var bytes = invalid.ToBytes();

        Assert.Null(UpdateManifest.Verify(bytes, UpdateManifest.Sign(bytes, _releaseKey), PublicKey));
    }

    [Theory]
    [InlineData(200, "windows-x64", true)]
    [InlineData(100, "windows-x64", false)] // same build
    [InlineData(50, "windows-x64", false)]  // downgrade
    [InlineData(200, "linux-x64", false)]   // other platform
    public async Task Check_OffersOnlyNewerBuildsForThisPlatform(long build, string platform, bool offered)
    {
        using var http = new HttpClient(new FakeServer(Offer(Manifest(build, platform)), _file));
        var client = new AgentUpdateClient(http, Server, "windows-x64", currentBuild: 100, PublicKey);

        var manifest = await client.CheckAsync(CancellationToken.None);

        Assert.Equal(offered, manifest is not null);
    }

    [Fact]
    public async Task Check_Disabled_WithoutKeyOrForLocalBuilds()
    {
        using var http = new HttpClient(new FakeServer(Offer(Manifest(200)), _file));

        Assert.False(new AgentUpdateClient(http, Server, "windows-x64", 100, null).Enabled);
        Assert.False(new AgentUpdateClient(http, Server, "windows-x64", 0, PublicKey).Enabled);
        Assert.Null(await new AgentUpdateClient(http, Server, "windows-x64", 0, PublicKey).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Download_VerifiesSizeAndHash()
    {
        var manifest = Manifest(200);
        var target = Path.Combine(_directory, "MKITS-Agent.exe");

        using (var good = new HttpClient(new FakeServer(Offer(manifest), _file)))
        {
            Assert.True(await new AgentUpdateClient(good, Server, "windows-x64", 100, PublicKey).DownloadAsync(manifest, target, CancellationToken.None));
            Assert.Equal(_file, await File.ReadAllBytesAsync(target));
        }

        File.Delete(target);
        var forged = (byte[])_file.Clone();
        forged[0] ^= 0xFF;
        using (var evil = new HttpClient(new FakeServer(Offer(manifest), forged)))
        {
            Assert.False(await new AgentUpdateClient(evil, Server, "windows-x64", 100, PublicKey).DownloadAsync(manifest, target, CancellationToken.None));
        }

        Assert.Empty(Directory.EnumerateFiles(_directory)); // nothing left behind
    }

    private UpdateManifest Manifest(long build, string platform = "windows-x64") =>
        new(UpdateManifest.ProductName, platform, build, "test", Convert.ToHexStringLower(SHA256.HashData(_file)), _file.Length);

    private UpdateOffer Offer(UpdateManifest manifest)
    {
        var bytes = manifest.ToBytes();
        return new UpdateOffer(Convert.ToBase64String(bytes), Convert.ToBase64String(UpdateManifest.Sign(bytes, _releaseKey)));
    }

    private sealed class FakeServer(UpdateOffer offer, byte[] file) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response = path switch
            {
                "/api/agent-updates/windows-x64" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(offer) },
                "/api/agent-updates/windows-x64/file" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }
    }
}
