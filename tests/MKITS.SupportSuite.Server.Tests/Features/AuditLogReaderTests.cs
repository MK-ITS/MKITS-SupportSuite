// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class AuditLogReaderTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly AuditLog _auditLog;
    private readonly AuditLogReader _reader;

    public AuditLogReaderTests()
    {
        _auditLog = new AuditLog(_database, new ManualTimeProvider());
        _reader = new AuditLogReader(_database);
    }

    public void Dispose()
    {
        _auditLog.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task Query_FiltersByEventTypePrefix_NewestFirst()
    {
        await SeedAsync();

        var page = await _reader.QueryAsync(new AuditQuery(EventTypePrefix: "ipban."));

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, e => Assert.StartsWith("ipban.", e.EventType, StringComparison.Ordinal));
        Assert.True(page.Items[0].Id > page.Items[1].Id);
    }

    [Fact]
    public async Task Query_TextSearch_MatchesIpTargetAndDetails()
    {
        await SeedAsync();

        Assert.Equal(1, (await _reader.QueryAsync(new AuditQuery(Text: "198.51.100.1"))).TotalCount);
        Assert.Equal(1, (await _reader.QueryAsync(new AuditQuery(Text: "203.0.113.0/24"))).TotalCount);
        Assert.Equal(1, (await _reader.QueryAsync(new AuditQuery(Text: "wrong_password"))).TotalCount);
    }

    [Fact]
    public async Task Query_WildcardsInSearchText_AreLiteral()
    {
        await SeedAsync();

        // Unescaped, "%done" would match "100% done"; as a literal it occurs nowhere.
        Assert.Equal(0, (await _reader.QueryAsync(new AuditQuery(Text: "%done"))).TotalCount);
        Assert.Equal(1, (await _reader.QueryAsync(new AuditQuery(Text: "100%"))).TotalCount);
    }

    [Fact]
    public async Task Query_Pages()
    {
        await SeedAsync();

        var page = await _reader.QueryAsync(new AuditQuery(Page: 2, PageSize: 2));

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.PageCount);
        Assert.Equal(2, page.Items.Count);
    }

    private async Task SeedAsync()
    {
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.ServerStarted, AuditActorType.System));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.LoginFailed, AuditActorType.Guest, Ip: "198.51.100.1",
            Details: new Dictionary<string, string?> { ["reason"] = "wrong_password" }));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.IpBanCreated, AuditActorType.System, TargetType: "network", TargetId: "203.0.113.0/24"));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.IpBanLifted, AuditActorType.User, TargetType: "network", TargetId: "192.0.2.1/32"));
        await _auditLog.WriteAsync(new AuditEntry(AuditEventTypes.UserChanged, AuditActorType.User,
            Details: new Dictionary<string, string?> { ["note"] = "100% done" }));
    }
}
