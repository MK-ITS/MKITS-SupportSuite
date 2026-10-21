// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class DeviceServiceTests : IDisposable
{
    private static readonly IPAddress Agent = IPAddress.Parse("198.51.100.30");
    private static readonly DeviceInfo Info = new("PC-0815", "windows", "Windows 11 Pro", "10.0.26100", "0.1.0");

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly IpBanOptions _banOptions = new();
    private readonly AuditLog _auditLog;
    private readonly IpBanService _ipBans;
    private readonly DeviceService _service;
    private readonly Guid _admin;
    private readonly Guid _supporter;
    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public DeviceServiceTests()
    {
        _auditLog = new AuditLog(_database, _time);
        _ipBans = new IpBanService(
            _database, new StaticOptionsMonitor<IpBanOptions>(_banOptions), new FailureTracker(), _auditLog, _time,
            NullLogger<IpBanService>.Instance);
        _service = new DeviceService(_database, _ipBans, _auditLog, _time);
        _admin = AddUser("admin", "Admin");
        _supporter = AddUser("alice", "Alice Supporter");
    }

    public void Dispose()
    {
        _deviceKey.Dispose();
        _auditLog.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task CreateGroup_RejectsEmptyAndDuplicateNames()
    {
        Assert.Equal(DeviceService.ErrorNameRequired, (await _service.CreateGroupAsync(_admin, "  ", null, DeviceConsentPolicy.Ask, false)).ErrorKey);
        Assert.NotNull((await _service.CreateGroupAsync(_admin, " Customer A ", null, DeviceConsentPolicy.Notify, false)).Id);
        Assert.Equal(DeviceService.ErrorNameTaken, (await _service.CreateGroupAsync(_admin, "Customer A", null, DeviceConsentPolicy.Ask, false)).ErrorKey);

        var group = Assert.Single(await _service.ListGroupsAsync());
        Assert.Equal("Customer A", group.Name);
        Assert.Equal(DeviceConsentPolicy.Notify, group.ConsentPolicy);
    }

    [Fact]
    public async Task CreateToken_ReturnsTokenOnce_StoresOnlyHash()
    {
        var groupId = await CreateGroupAsync();

        var token = Assert.IsType<CreatedToken>(await _service.CreateTokenAsync(_admin, groupId, "rollout", TimeSpan.FromDays(7), 5));

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token.Token);
        await using var db = _database.CreateDbContext();
        var stored = await db.EnrollmentTokens.SingleAsync();
        Assert.Equal(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token.Token)), stored.TokenHash);
        Assert.Equal(5, stored.MaxUses);
        Assert.All(await db.AuditEvents.Select(a => a.Details).ToListAsync(), details => Assert.DoesNotContain(token.Token, details, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enroll_ValidToken_CreatesActiveDevice_AndConsumesUse()
    {
        var groupId = await CreateGroupAsync();
        var token = (await _service.CreateTokenAsync(_admin, groupId, null, TimeSpan.FromDays(1), 1))!;

        var result = await _service.EnrollAsync(token.Token, PublicKey(), Info, Agent);

        Assert.Null(result.ErrorCode);
        Assert.Equal(DeviceStatus.Active, result.Status);
        var device = Assert.Single(await _service.ListDevicesAsync(_admin, isAdmin: true));
        Assert.Equal("PC-0815", device.Name);
        Assert.Equal("198.51.100.30", device.LastIp);

        // single use: a second device cannot use the same token
        var second = await _service.EnrollAsync(token.Token, PublicKey(), Info, Agent);
        Assert.Equal(ErrorCodes.AuthFailed, second.ErrorCode);
    }

    [Fact]
    public async Task Enroll_GroupRequiresApproval_DeviceIsPending_UntilApproved()
    {
        var groupId = (await _service.CreateGroupAsync(_admin, "Strict", null, DeviceConsentPolicy.Ask, requireApproval: true)).Id!.Value;
        var token = (await _service.CreateTokenAsync(_admin, groupId, null, TimeSpan.FromDays(1), 1))!;

        var result = await _service.EnrollAsync(token.Token, PublicKey(), Info, Agent);

        Assert.Equal(DeviceStatus.PendingApproval, result.Status);
        Assert.Equal(DevicePermissions.None, await _service.GetPermissionsAsync(_admin, isAdmin: true, result.DeviceId!.Value));

        Assert.Null(await _service.ApproveAsync(_admin, result.DeviceId.Value));
        Assert.Equal(DevicePermissions.All, await _service.GetPermissionsAsync(_admin, isAdmin: true, result.DeviceId.Value));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    public async Task Enroll_InvalidToken_FailsAndCountsAsFailure(string scenario)
    {
        var groupId = await CreateGroupAsync();
        var token = (await _service.CreateTokenAsync(_admin, groupId, null, TimeSpan.FromHours(1), 10))!;
        var value = token.Token;
        switch (scenario)
        {
            case "expired":
                _time.Advance(TimeSpan.FromHours(2));
                break;
            case "revoked":
                Assert.True(await _service.RevokeTokenAsync(_admin, token.Id));
                break;
            default:
                value = "not-a-real-token";
                break;
        }

        _banOptions.MaxFailures = 1;
        var result = await _service.EnrollAsync(value, PublicKey(), Info, Agent);

        Assert.Equal(ErrorCodes.AuthFailed, result.ErrorCode);
        Assert.Empty(await _service.ListDevicesAsync(_admin, isAdmin: true));
        Assert.False(_ipBans.IsAllowed(Agent));
    }

    [Fact]
    public async Task Enroll_InvalidPublicKey_IsRejected()
    {
        var groupId = await CreateGroupAsync();
        var token = (await _service.CreateTokenAsync(_admin, groupId, null, TimeSpan.FromDays(1), 1))!;
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Equal(ErrorCodes.AuthFailed, (await _service.EnrollAsync(token.Token, [1, 2, 3], Info, Agent)).ErrorCode);
        Assert.Equal(ErrorCodes.AuthFailed, (await _service.EnrollAsync(token.Token, p384.ExportSubjectPublicKeyInfo(), Info, Agent)).ErrorCode);

        // the token was not consumed by the rejected attempts
        Assert.Null((await _service.EnrollAsync(token.Token, PublicKey(), Info, Agent)).ErrorCode);
    }

    [Fact]
    public async Task Authenticate_ValidSignature_ReturnsFacts_AndUpdatesLastSeen()
    {
        var deviceId = await EnrollAsync();
        var nonce = RandomNumberGenerator.GetBytes(32);
        _time.Advance(TimeSpan.FromMinutes(5));

        var facts = await _service.AuthenticateAsync(deviceId, nonce, Sign(nonce), Info with { AgentVersion = "0.2.0" }, Agent);

        Assert.NotNull(facts);
        Assert.Equal(DeviceConsentPolicy.Ask, facts.ConsentPolicy);
        var device = Assert.Single(await _service.ListDevicesAsync(_admin, isAdmin: true));
        Assert.Equal("0.2.0", device.AgentVersion);
        Assert.Equal(_time.GetUtcNow(), device.LastSeenAt);
    }

    [Fact]
    public async Task Authenticate_WrongKeyOrNonce_Fails()
    {
        var deviceId = await EnrollAsync();
        var nonce = RandomNumberGenerator.GetBytes(32);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = otherKey.SignData(nonce, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.Null(await _service.AuthenticateAsync(deviceId, nonce, foreign, null, Agent));
        Assert.Null(await _service.AuthenticateAsync(deviceId, RandomNumberGenerator.GetBytes(32), Sign(nonce), null, Agent));
        Assert.Null(await _service.AuthenticateAsync(Guid.NewGuid(), nonce, Sign(nonce), null, Agent));
    }

    [Fact]
    public async Task Authenticate_DisabledDevice_Fails()
    {
        var deviceId = await EnrollAsync();
        Assert.Null(await _service.SetDisabledAsync(_admin, deviceId, disabled: true));
        var nonce = RandomNumberGenerator.GetBytes(32);

        Assert.Null(await _service.AuthenticateAsync(deviceId, nonce, Sign(nonce), null, Agent));
    }

    [Fact]
    public async Task Permissions_AreGrantedPerGroup_AndViewIsImplied()
    {
        var deviceId = await EnrollAsync();
        var groupId = (await _service.ListGroupsAsync())[0].Id;

        Assert.Empty(await _service.ListDevicesAsync(_supporter, isAdmin: false));
        Assert.Equal(DevicePermissions.None, await _service.GetPermissionsAsync(_supporter, isAdmin: false, deviceId));

        Assert.Null(await _service.SetPermissionAsync(_admin, groupId, _supporter, DevicePermissions.Control));

        Assert.Equal(DevicePermissions.View | DevicePermissions.Control, await _service.GetPermissionsAsync(_supporter, isAdmin: false, deviceId));
        Assert.Single(await _service.ListDevicesAsync(_supporter, isAdmin: false));

        Assert.Null(await _service.SetPermissionAsync(_admin, groupId, _supporter, DevicePermissions.None));
        Assert.Empty(await _service.ListPermissionsAsync(groupId));
        Assert.Equal(DevicePermissions.None, await _service.GetPermissionsAsync(_supporter, isAdmin: false, deviceId));
    }

    [Fact]
    public async Task DeleteGroup_WithDevices_IsRefused_AfterMoveSucceeds()
    {
        var deviceId = await EnrollAsync();
        var first = (await _service.ListGroupsAsync())[0].Id;
        var second = (await _service.CreateGroupAsync(_admin, "Other", null, DeviceConsentPolicy.Silent, false)).Id!.Value;

        Assert.Equal(DeviceService.ErrorGroupNotEmpty, await _service.DeleteGroupAsync(_admin, first));
        Assert.Null(await _service.MoveAsync(_admin, deviceId, second));
        Assert.Null(await _service.DeleteGroupAsync(_admin, first));

        var facts = await _service.GetFactsAsync(deviceId);
        Assert.Equal(DeviceConsentPolicy.Silent, facts!.ConsentPolicy);
    }

    [Fact]
    public async Task Remove_DeletesDevice_AndAudits()
    {
        var deviceId = await EnrollAsync();

        Assert.Null(await _service.RemoveAsync(_admin, deviceId));

        Assert.Null(await _service.GetFactsAsync(deviceId));
        await using var db = _database.CreateDbContext();
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == AuditEventTypes.DeviceRemoved && a.TargetId == deviceId.ToString());
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == AuditEventTypes.DeviceEnrolled && a.ActorId == deviceId.ToString());
    }

    [Fact]
    public void IsValidP256Key_AcceptsOnlyP256Spki()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        Assert.True(DeviceService.IsValidP256Key(PublicKey()));
        Assert.False(DeviceService.IsValidP256Key(p384.ExportSubjectPublicKeyInfo()));
        Assert.False(DeviceService.IsValidP256Key(rsa.ExportSubjectPublicKeyInfo()));
        Assert.False(DeviceService.IsValidP256Key([.. PublicKey(), 0]));
        Assert.False(DeviceService.IsValidP256Key(null));
    }

    private byte[] PublicKey() => _deviceKey.ExportSubjectPublicKeyInfo();

    private byte[] Sign(byte[] data) => _deviceKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private async Task<Guid> CreateGroupAsync() =>
        (await _service.CreateGroupAsync(_admin, "Customer A", null, DeviceConsentPolicy.Ask, false)).Id!.Value;

    private async Task<Guid> EnrollAsync()
    {
        var groupId = await CreateGroupAsync();
        var token = (await _service.CreateTokenAsync(_admin, groupId, null, TimeSpan.FromDays(1), 1))!;
        return (await _service.EnrollAsync(token.Token, PublicKey(), Info, Agent)).DeviceId!.Value;
    }

    private Guid AddUser(string userName, string displayName)
    {
        var user = new MkitsUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            DisplayName = displayName,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        using var db = _database.CreateDbContext();
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }
}
