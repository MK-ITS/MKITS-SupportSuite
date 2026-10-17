// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class AuditLogTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AuditLog _auditLog;

    public AuditLogTests()
    {
        _auditLog = new AuditLog(_database, _time);
    }

    public void Dispose()
    {
        _auditLog.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task VerifyAsync_EmptyLog_IsValid()
    {
        var result = await _auditLog.VerifyAsync();

        Assert.True(result.IsValid);
        Assert.Equal(0, result.CheckedEntries);
    }

    [Fact]
    public async Task WriteAsync_ChainsEntries_VerifyIsValid()
    {
        await WriteSampleEntriesAsync();

        var result = await _auditLog.VerifyAsync();

        Assert.True(result.IsValid);
        Assert.Equal(3, result.CheckedEntries);

        await using var db = _database.CreateDbContext();
        var entries = await db.AuditEvents.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new byte[32], entries[0].PreviousHash);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(entries[1].Hash, entries[2].PreviousHash);
        Assert.Equal("{\"a\":\"1\",\"b\":null}", entries[1].Details);
    }

    [Fact]
    public async Task VerifyAsync_ModifiedEntry_IsDetected()
    {
        await WriteSampleEntriesAsync();
        await using (var db = _database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE AuditEvents SET Ip = '198.51.100.99' WHERE Id = 2");
        }

        var result = await _auditLog.VerifyAsync();

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstInvalidId);
    }

    [Fact]
    public async Task VerifyAsync_DeletedEntry_IsDetected()
    {
        await WriteSampleEntriesAsync();
        await using (var db = _database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents WHERE Id = 2");
        }

        var result = await _auditLog.VerifyAsync();

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstInvalidId);
    }

    [Fact]
    public async Task VerifyAsync_TimestampsWithSubMillisecondTicks_StayValid()
    {
        // Real clocks produce arbitrary ticks; storage must be lossless or the hash chain breaks.
        _time.Advance(TimeSpan.FromTicks(1_234_567));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.ServerStarted, AuditActorType.System));
        _time.Advance(TimeSpan.FromTicks(7));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.ServerStarted, AuditActorType.System));

        var result = await _auditLog.VerifyAsync();

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWriters_KeepChainLinear()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            _auditLog.WriteAsync(new AuditEntry("test.concurrent", AuditActorType.System, ActorId: i.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        var result = await _auditLog.VerifyAsync();

        Assert.True(result.IsValid);
        Assert.Equal(20, result.CheckedEntries);
    }

    private async Task WriteSampleEntriesAsync()
    {
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.ServerStarted, AuditActorType.System));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.LoginFailed, AuditActorType.Guest, Ip: "203.0.113.7",
            Details: new Dictionary<string, string?> { ["b"] = null, ["a"] = "1" }));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.IpBanCreated, AuditActorType.System, TargetType: "network", TargetId: "203.0.113.7/32"));
    }
}
