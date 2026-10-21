// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using MKITS.SupportSuite.Agent.Core.Devices;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class DeviceIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mkits-identity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Sign_ProducesP1363SignatureVerifiableWithThePublicKey()
    {
        using var identity = DeviceIdentity.FromKey(Guid.NewGuid(), DeviceIdentity.CreateKey());
        var data = RandomNumberGenerator.GetBytes(32);

        var signature = identity.Sign(data);

        Assert.Equal(64, signature.Length);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(identity.PublicKey, out _);
        Assert.True(verifier.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void FileStore_RoundTrip_KeepsIdAndKey_AndUsesProtector()
    {
        var path = Path.Combine(_directory, "identity.json");
        var protectCalls = 0;
        var store = new FileDeviceIdentityStore(
            path,
            protect: data => { protectCalls++; return data.Select(b => (byte)(b ^ 0x5A)).ToArray(); },
            unprotect: data => data.Select(b => (byte)(b ^ 0x5A)).ToArray());
        using var identity = DeviceIdentity.FromKey(Guid.NewGuid(), DeviceIdentity.CreateKey());

        store.Save(identity);
        using var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(identity.DeviceId, loaded.DeviceId);
        Assert.Equal(identity.PublicKey, loaded.PublicKey);
        Assert.Equal(1, protectCalls);
        Assert.DoesNotContain(Convert.ToBase64String(identity.ExportPrivateKey()), File.ReadAllText(path), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":1,\"DeviceId\":\"x\",\"Key\":\"AAAA\"}")]
    [InlineData("{\"Version\":1,\"DeviceId\":\"0192f0c4-1c2d-7a3b-8000-000000000001\",\"Key\":\"AAAA\"}")]
    [InlineData("{\"Version\":2,\"DeviceId\":\"0192f0c4-1c2d-7a3b-8000-000000000001\",\"Key\":\"AAAA\"}")]
    public void FileStore_UnusableFile_LoadsNull(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "identity.json");
        File.WriteAllText(path, content);

        Assert.Null(new FileDeviceIdentityStore(path).Load());
    }

    [Fact]
    public void FileStore_Missing_LoadsNull_AndDeleteRemovesTheFile()
    {
        var path = Path.Combine(_directory, "identity.json");
        var store = new FileDeviceIdentityStore(path);
        Assert.Null(store.Load());

        using var identity = DeviceIdentity.FromKey(Guid.NewGuid(), DeviceIdentity.CreateKey());
        store.Save(identity);
        store.Delete();

        Assert.False(File.Exists(path));
    }
}
