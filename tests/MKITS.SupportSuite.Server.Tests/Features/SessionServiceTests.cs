// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Sessions;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class SessionServiceTests : IDisposable
{
    private static readonly IPAddress Customer = IPAddress.Parse("198.51.100.20");

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SessionOptions _options = new();
    private readonly IpBanOptions _banOptions = new();
    private readonly AuditLog _auditLog;
    private readonly IpBanService _ipBans;
    private readonly ViewerTicketStore _tickets;
    private readonly SessionService _service;
    private readonly Guid _supporter;
    private readonly Guid _otherSupporter;

    public SessionServiceTests()
    {
        _auditLog = new AuditLog(_database, _time);
        _ipBans = new IpBanService(
            _database, new StaticOptionsMonitor<IpBanOptions>(_banOptions), new FailureTracker(), _auditLog, _time,
            NullLogger<IpBanService>.Instance);
        _tickets = new ViewerTicketStore(_time);
        _service = new SessionService(
            _database, new StaticOptionsMonitor<SessionOptions>(_options), _ipBans, _tickets, new DeviceService(_database, _ipBans, _auditLog, _time), _auditLog, _time);
        _supporter = AddUser("alice", "Alice Supporter");
        _otherSupporter = AddUser("bob", "Bob Supporter");
    }

    public void Dispose()
    {
        _auditLog.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task CreateAdHoc_ReturnsCode_StoresOnlyHash_AndAudits()
    {
        var result = await _service.CreateAdHocAsync(_supporter, "  Ticket 4711  ");

        var created = Assert.IsType<CreatedSession>(result.Session);
        Assert.Matches("^[0-9]{8}$", created.Code);
        Assert.Equal(_time.GetUtcNow() + _options.CodeLifetime, created.ExpiresAt);

        await using var db = _database.CreateDbContext();
        var session = await db.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.WaitingForAgent, session.Status);
        Assert.Equal("Ticket 4711", session.CustomerLabel);
        Assert.Equal(SessionService.ComputeCodeHash(created.Code), session.CodeHash);
        Assert.All(await db.AuditEvents.Select(a => a.Details).ToListAsync(), details => Assert.DoesNotContain(created.Code, details, StringComparison.Ordinal));
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == AuditEventTypes.SessionCreated && a.TargetId == session.Id.ToString());
    }

    [Fact]
    public async Task CreateAdHoc_PerUserLimit_IsEnforced()
    {
        _options.MaxActiveCodesPerUser = 2;
        await _service.CreateAdHocAsync(_supporter, null);
        await _service.CreateAdHocAsync(_supporter, null);

        var third = await _service.CreateAdHocAsync(_supporter, null);
        var otherUser = await _service.CreateAdHocAsync(_otherSupporter, null);

        Assert.Null(third.Session);
        Assert.Equal(SessionService.ErrorTooManyCodes, third.ErrorKey);
        Assert.NotNull(otherUser.Session);
    }

    [Fact]
    public async Task CreateAdHoc_ServerLimit_IsEnforced()
    {
        _options.MaxActiveCodes = 1;
        _options.MaxActiveCodesPerUser = 1;
        await _service.CreateAdHocAsync(_supporter, null);

        var second = await _service.CreateAdHocAsync(_otherSupporter, null);

        Assert.Equal(SessionService.ErrorServerBusy, second.ErrorKey);
    }

    [Fact]
    public async Task CreateAdHoc_ExpiredCodes_DoNotCountTowardsLimit()
    {
        _options.MaxActiveCodesPerUser = 1;
        await _service.CreateAdHocAsync(_supporter, null);
        _time.Advance(_options.CodeLifetime + TimeSpan.FromSeconds(1));

        var next = await _service.CreateAdHocAsync(_supporter, null);

        Assert.NotNull(next.Session);
    }

    [Fact]
    public async Task CheckCode_Valid_AcceptsFormattedInput_AndDoesNotConsume()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;

        Assert.Equal(CodeCheckResult.Valid, await _service.CheckCodeAsync(SessionCodeFormat.Format(created.Code), Customer));
        Assert.Equal(CodeCheckResult.Valid, await _service.CheckCodeAsync(created.Code, Customer));
    }

    [Fact]
    public async Task CheckCode_Unknown_CountsAsFailure_UntilBan()
    {
        for (var i = 0; i < _banOptions.MaxFailures; i++)
        {
            Assert.Equal(CodeCheckResult.Invalid, await _service.CheckCodeAsync("0000 0000", Customer));
        }

        Assert.False(_ipBans.IsAllowed(Customer));
    }

    [Fact]
    public async Task CheckCode_Expired_ReportsExpired_WithoutFailure()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        _time.Advance(_options.CodeLifetime);
        await _service.ExpireAsync();

        for (var i = 0; i < _banOptions.MaxFailures; i++)
        {
            Assert.Equal(CodeCheckResult.Expired, await _service.CheckCodeAsync(created.Code, Customer));
        }

        Assert.True(_ipBans.IsAllowed(Customer));
    }

    [Fact]
    public async Task Redeem_Valid_MovesToWaitingForViewer_AndCodeIsSingleUse()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;

        var first = await _service.RedeemCodeAsync(created.Code, new AgentInfo("CUSTOMER-PC", Customer));
        var second = await _service.RedeemCodeAsync(created.Code, new AgentInfo("ATTACKER-PC", Customer));

        Assert.Equal(created.SessionId, first.SessionId);
        Assert.Equal("Alice Supporter", first.OperatorName);
        Assert.Null(second.SessionId);
        Assert.Equal(ErrorCodes.CodeInvalid, second.ErrorCode);

        await using var db = _database.CreateDbContext();
        var session = await db.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.WaitingForViewer, session.Status);
        Assert.Null(session.CodeHash);
        Assert.Equal("CUSTOMER-PC", session.AgentMachineName);
        Assert.Equal("198.51.100.20", session.AgentIp);
    }

    [Fact]
    public async Task Redeem_Concurrent_OnlyOneWins()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => _service.RedeemCodeAsync(created.Code, new AgentInfo($"PC-{i}", Customer))));

        Assert.Single(results, r => r.SessionId is not null);
    }

    [Fact]
    public async Task Redeem_Expired_ReturnsCodeExpired()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        _time.Advance(_options.CodeLifetime);

        var result = await _service.RedeemCodeAsync(created.Code, new AgentInfo("PC", Customer));

        Assert.Equal(ErrorCodes.CodeExpired, result.ErrorCode);
    }

    [Fact]
    public async Task Redeem_WrongLength_IsInvalid()
    {
        var result = await _service.RedeemCodeAsync("123456", new AgentInfo("PC", Customer));

        Assert.Equal(ErrorCodes.CodeInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task End_ByOwner_RevokesCode()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;

        Assert.True(await _service.EndAsync(created.SessionId, _supporter, actorIsAdmin: false));
        Assert.False(await _service.EndAsync(created.SessionId, _supporter, actorIsAdmin: false));

        Assert.Equal(CodeCheckResult.Invalid, await _service.CheckCodeAsync(created.Code, Customer));
        await using var db = _database.CreateDbContext();
        var session = await db.Sessions.SingleAsync();
        Assert.Equal(SessionEndReasons.Revoked, session.EndReason);
        Assert.Null(session.CodeHash);
    }

    [Fact]
    public async Task End_ByOtherSupporter_IsRejected_ButAdminMay()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;

        Assert.False(await _service.EndAsync(created.SessionId, _otherSupporter, actorIsAdmin: false));
        Assert.True(await _service.EndAsync(created.SessionId, _otherSupporter, actorIsAdmin: true));
    }

    [Fact]
    public async Task Expire_EndsExpiredCodes_AndOverlongSessions()
    {
        var waiting = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        var connected = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        await _service.RedeemCodeAsync(connected.Code, new AgentInfo("PC", Customer));

        _time.Advance(_options.CodeLifetime);
        Assert.Equal(1, await _service.ExpireAsync());

        _time.Advance(_options.MaxDuration);
        Assert.Equal(1, await _service.ExpireAsync());

        await using var db = _database.CreateDbContext();
        var sessions = await db.Sessions.ToDictionaryAsync(s => s.Id);
        Assert.Equal(SessionEndReasons.CodeExpired, sessions[waiting.SessionId].EndReason);
        Assert.Equal(SessionEndReasons.MaxDurationReached, sessions[connected.SessionId].EndReason);
    }

    [Fact]
    public async Task EndInterruptedSessions_EndsConnectedSessions_KeepsWaitingCodes()
    {
        var waiting = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        var connected = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        await _service.RedeemCodeAsync(connected.Code, new AgentInfo("PC", Customer));

        Assert.Equal(1, await _service.EndInterruptedSessionsAsync());

        Assert.Equal(CodeCheckResult.Valid, await _service.CheckCodeAsync(waiting.Code, Customer));
    }

    [Fact]
    public async Task List_OwnSessions_AndAllForAdmins()
    {
        await _service.CreateAdHocAsync(_supporter, "mine");
        var other = (await _service.CreateAdHocAsync(_otherSupporter, "theirs")).Session!;
        await _service.EndAsync(other.SessionId, _otherSupporter, actorIsAdmin: false);

        var mine = await _service.ListAsync(_supporter, includeEnded: false);
        var allOpen = await _service.ListAsync(null, includeEnded: false);
        var all = await _service.ListAsync(null, includeEnded: true);

        Assert.Equal("mine", Assert.Single(mine).CustomerLabel);
        Assert.Equal("Alice Supporter", Assert.Single(allOpen).OwnerDisplayName);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ViewerTicket_OnlyForOwnerOrAdmin_AndSingleUse()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;

        Assert.Null(await _service.IssueViewerTicketAsync(created.SessionId, _otherSupporter, userIsAdmin: false));
        var ticket = await _service.IssueViewerTicketAsync(created.SessionId, _supporter, userIsAdmin: false);

        Assert.NotNull(ticket);
        Assert.Equal(new ViewerTicketGrant(created.SessionId, _supporter), _tickets.Redeem(ticket));
        Assert.Null(_tickets.Redeem(ticket));
    }

    [Fact]
    public async Task ViewerTicket_Expires()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        var ticket = await _service.IssueViewerTicketAsync(created.SessionId, _supporter, userIsAdmin: false);

        _time.Advance(_options.ViewerTicketLifetime);

        Assert.Null(_tickets.Redeem(ticket));
    }

    [Fact]
    public async Task ViewerTicket_EndedSession_IsRefused()
    {
        var created = (await _service.CreateAdHocAsync(_supporter, null)).Session!;
        await _service.EndAsync(created.SessionId, _supporter, actorIsAdmin: false);

        Assert.Null(await _service.IssueViewerTicketAsync(created.SessionId, _supporter, userIsAdmin: true));
    }

    [Fact]
    public void GenerateCode_HasRequestedLength_AndOnlyDigits()
    {
        for (var length = SessionCodeFormat.MinLength; length <= SessionCodeFormat.MaxLength; length++)
        {
            Assert.Matches($"^[0-9]{{{length}}}$", SessionService.GenerateCode(length));
        }
    }

    [Theory]
    [InlineData(5, 50, 10, false)]
    [InlineData(13, 50, 10, false)]
    [InlineData(8, 50, 10, true)]
    [InlineData(8, 1000, 10, true)]
    [InlineData(8, 1001, 10, false)]
    [InlineData(6, 10, 10, true)]
    [InlineData(6, 50, 10, false)]
    [InlineData(8, 50, 51, false)]
    public void OptionsValidator_CodeSpaceAndLimits(int codeLength, int maxActive, int maxPerUser, bool valid)
    {
        var options = new SessionOptions { CodeLength = codeLength, MaxActiveCodes = maxActive, MaxActiveCodesPerUser = maxPerUser };

        Assert.Equal(valid, new SessionOptionsValidator().Validate(null, options).Succeeded);
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
