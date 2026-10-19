// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class IpBanServiceTests : IDisposable
{
    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.7");

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly IpBanOptions _options = new();
    private readonly AuditLog _auditLog;
    private readonly RecordingNotificationQueue _notifications = new();
    private readonly IpBanService _service;

    public IpBanServiceTests()
    {
        _auditLog = new AuditLog(_database, _time);
        _service = new IpBanService(
            _database, new StaticOptionsMonitor<IpBanOptions>(_options), new FailureTracker(), _auditLog, _time,
            NullLogger<IpBanService>.Instance, _notifications);
    }

    public void Dispose()
    {
        _auditLog.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task RecordFailure_BelowThreshold_DoesNotBan()
    {
        for (var i = 0; i < _options.MaxFailures - 1; i++)
        {
            Assert.False(await _service.RecordFailureAsync(Attacker, FailureKinds.Login));
        }

        Assert.True(_service.IsAllowed(Attacker));
    }

    [Fact]
    public async Task RecordFailure_AtThreshold_BansForConfiguredDuration_AndAudits()
    {
        await FailManyTimesAsync(Attacker);

        Assert.False(_service.IsAllowed(Attacker));
        Assert.True(_service.IsAllowed(IPAddress.Parse("203.0.113.8")));

        var bans = await _service.GetActiveBansAsync();
        var ban = Assert.Single(bans);
        Assert.Equal("203.0.113.7/32", ban.Network);
        Assert.Equal("auto:login", ban.Reason);
        Assert.Equal(_time.GetUtcNow() + _options.BanDuration, ban.ExpiresAt);

        await using var db = _database.CreateDbContext();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EventType == AuditEventTypes.IpBanCreated && e.TargetId == "203.0.113.7/32"));
    }

    [Fact]
    public async Task Ban_Expires_AfterBanDuration()
    {
        await FailManyTimesAsync(Attacker);

        _time.Advance(_options.BanDuration + TimeSpan.FromSeconds(1));

        Assert.True(_service.IsAllowed(Attacker));
    }

    [Fact]
    public async Task FailuresOutsideWindow_DoNotAccumulate()
    {
        for (var i = 0; i < _options.MaxFailures * 2; i++)
        {
            await _service.RecordFailureAsync(Attacker, FailureKinds.Login);
            _time.Advance(_options.Window / (_options.MaxFailures - 1) + TimeSpan.FromSeconds(1));
        }

        Assert.True(_service.IsAllowed(Attacker));
    }

    [Fact]
    public async Task IPv6_IsBannedPerPrefix()
    {
        await FailManyTimesAsync(IPAddress.Parse("2001:db8:1:2::1"));

        Assert.False(_service.IsAllowed(IPAddress.Parse("2001:db8:1:2:ffff::99")));
        Assert.True(_service.IsAllowed(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public async Task AllowListedNetwork_IsNeverBanned()
    {
        _options.AllowList.Add("203.0.113.0/24");

        await FailManyTimesAsync(Attacker);

        Assert.True(_service.IsAllowed(Attacker));
        Assert.Empty(await _service.GetActiveBansAsync());
    }

    [Fact]
    public void DenyListedNetwork_IsAlwaysRejected()
    {
        _options.DenyList.Add("198.51.100.0/24");

        Assert.False(_service.IsAllowed(IPAddress.Parse("198.51.100.20")));
        Assert.True(_service.IsAllowed(IPAddress.Parse("198.51.101.20")));
    }

    [Fact]
    public async Task DatabaseListEntries_ApplyAfterReload()
    {
        await using (var db = _database.CreateDbContext())
        {
            db.IpListEntries.Add(new IpListEntry { Id = Guid.NewGuid(), Network = "192.0.2.0/24", ListType = IpListType.Deny, CreatedAt = _time.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        await _service.ReloadAsync();

        Assert.False(_service.IsAllowed(IPAddress.Parse("192.0.2.1")));
    }

    [Fact]
    public async Task LiftBan_AllowsAgain_AndCannotBeRepeated()
    {
        await FailManyTimesAsync(Attacker);
        var ban = Assert.Single(await _service.GetActiveBansAsync());
        var admin = Guid.NewGuid();

        Assert.True(await _service.LiftBanAsync(ban.Id, admin));
        Assert.True(_service.IsAllowed(Attacker));
        Assert.False(await _service.LiftBanAsync(ban.Id, admin));
    }

    [Fact]
    public async Task ResetFailures_AfterSuccess_StartsCountingAgain()
    {
        for (var i = 0; i < _options.MaxFailures - 1; i++)
        {
            await _service.RecordFailureAsync(Attacker, FailureKinds.Login);
        }

        _service.ResetFailures(Attacker);

        Assert.False(await _service.RecordFailureAsync(Attacker, FailureKinds.Login));
        Assert.True(_service.IsAllowed(Attacker));
    }

    [Fact]
    public async Task AutomaticBan_NotifiesAdmins_ManualBanDoesNot()
    {
        await _service.CreateBanAsync("192.0.2.9", "manual", TimeSpan.FromDays(1), createdByUserId: Guid.NewGuid());
        Assert.Empty(_notifications.Items);

        await FailManyTimesAsync(Attacker);

        var notification = Assert.Single(_notifications.Items);
        Assert.Equal("Mail_IpBanned_Subject", notification.SubjectKey);
        Assert.Equal("203.0.113.7/32", notification.Arguments[0]);
    }

    [Fact]
    public async Task ListEntries_AddAndRemove_TakeEffectImmediately()
    {
        var admin = Guid.NewGuid();

        Assert.Null(await _service.AddListEntryAsync("192.0.2.0/24", IpListType.Deny, "test", admin));
        Assert.False(_service.IsAllowed(IPAddress.Parse("192.0.2.10")));

        var entry = Assert.Single(await _service.GetListEntriesAsync());
        Assert.True(await _service.RemoveListEntryAsync(entry.Id, admin));
        Assert.True(_service.IsAllowed(IPAddress.Parse("192.0.2.10")));
        Assert.False(await _service.RemoveListEntryAsync(entry.Id, admin));
    }

    [Fact]
    public async Task ListEntries_InvalidOrDuplicate_AreRejected()
    {
        var admin = Guid.NewGuid();

        Assert.Equal("IpBans_Error_InvalidNetwork", await _service.AddListEntryAsync("not-a-network", IpListType.Allow, null, admin));
        Assert.Null(await _service.AddListEntryAsync("198.51.100.4", IpListType.Allow, null, admin));
        Assert.Equal("IpBans_Error_Duplicate", await _service.AddListEntryAsync("198.51.100.4/32", IpListType.Allow, null, admin));
    }

    [Fact]
    public async Task AllowListEntryFromDatabase_PreventsBans()
    {
        await _service.AddListEntryAsync("203.0.113.0/24", IpListType.Allow, "office", Guid.NewGuid());

        await FailManyTimesAsync(Attacker);

        Assert.True(_service.IsAllowed(Attacker));
    }

    [Fact]
    public void UnknownAddress_IsAllowed()
    {
        Assert.True(_service.IsAllowed(null));
    }

    private async Task FailManyTimesAsync(IPAddress address)
    {
        for (var i = 0; i < _options.MaxFailures; i++)
        {
            await _service.RecordFailureAsync(address, FailureKinds.Login);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
