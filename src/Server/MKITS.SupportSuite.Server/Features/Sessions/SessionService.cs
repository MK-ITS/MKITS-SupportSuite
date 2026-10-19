// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Sessions;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.Sessions;

/// <summary>A freshly created ad-hoc session. <see cref="Code"/> exists only here – the database keeps its hash.</summary>
public sealed record CreatedSession(Guid SessionId, string Code, DateTimeOffset ExpiresAt);

/// <summary>Result of <see cref="SessionService.CreateAdHocAsync"/>: either a session or a resource key for the error.</summary>
public sealed record CreateSessionResult(CreatedSession? Session, string? ErrorKey);

/// <summary>Outcome of checking a code on the portal or redeeming it by an agent.</summary>
public enum CodeCheckResult
{
    Valid,
    Invalid,
    Expired,
}

/// <summary>Result of <see cref="SessionService.RedeemCodeAsync"/>. <see cref="ErrorCode"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed record RedeemResult(Guid? SessionId, string? ErrorCode, string? OperatorName = null);

/// <summary>Machine data reported by an ad-hoc agent (shown to the supporter).</summary>
public sealed record AgentInfo(string? MachineName, IPAddress? Address);

/// <summary>One row of the session list.</summary>
public sealed record SessionSummary(
    Guid Id,
    SessionKind Kind,
    SessionStatus Status,
    Guid OwnerUserId,
    string OwnerDisplayName,
    string? CustomerLabel,
    string? AgentMachineName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CodeExpiresAt,
    DateTimeOffset? EndedAt,
    string? EndReason);

/// <summary>
/// Creates, redeems and ends support sessions (docs/session-lifecycle.md). Live connections are handled by the
/// relay (Phase 5B); this service owns the persisted state.
/// </summary>
public sealed class SessionService(
    IDbContextFactory<MkitsDbContext> dbFactory,
    IOptionsMonitor<SessionOptions> options,
    IpBanService ipBans,
    ViewerTicketStore viewerTickets,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    public const string ErrorTooManyCodes = "Sessions_Error_TooManyCodes";
    public const string ErrorServerBusy = "Sessions_Error_ServerBusy";

    /// <summary>Expired codes are still recognized as "expired" (not "invalid") for this long, so customers get a helpful message.</summary>
    private static readonly TimeSpan ExpiredCodeMemory = TimeSpan.FromDays(1);

    private const int MaxLabelLength = 200;

    /// <summary>Creates an ad-hoc session with a new code (WaitingForAgent).</summary>
    public async Task<CreateSessionResult> CreateAdHocAsync(Guid ownerUserId, string? customerLabel, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        var now = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var openCodes = db.Sessions.Where(s => s.Status == SessionStatus.WaitingForAgent && s.CodeExpiresAt > now);
        if (await openCodes.CountAsync(s => s.OwnerUserId == ownerUserId, cancellationToken).ConfigureAwait(false) >= settings.MaxActiveCodesPerUser)
        {
            return new CreateSessionResult(null, ErrorTooManyCodes);
        }

        if (await openCodes.CountAsync(cancellationToken).ConfigureAwait(false) >= settings.MaxActiveCodes)
        {
            return new CreateSessionResult(null, ErrorServerBusy);
        }

        // Codes must be unique among the currently valid ones; with the enforced density (≤ 1 : 100,000) a retry is rare.
        string code;
        byte[] hash;
        var attempts = 0;
        do
        {
            code = GenerateCode(settings.CodeLength);
            hash = ComputeCodeHash(code);
            var candidate = hash;
            if (!await openCodes.AnyAsync(s => s.CodeHash == candidate, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
        while (++attempts < 10);

        var session = new SupportSession
        {
            Id = Guid.CreateVersion7(now),
            Kind = SessionKind.AdHoc,
            Status = SessionStatus.WaitingForAgent,
            CodeHash = hash,
            CodeExpiresAt = now + settings.CodeLifetime,
            OwnerUserId = ownerUserId,
            CustomerLabel = NormalizeLabel(customerLabel),
            CreatedAt = now,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.SessionCreated, AuditActorType.User, ActorId: ownerUserId.ToString(),
            TargetType: "session", TargetId: session.Id.ToString(),
            Details: new Dictionary<string, string?> { ["kind"] = nameof(SessionKind.AdHoc), ["label"] = session.CustomerLabel }),
            cancellationToken).ConfigureAwait(false);

        return new CreateSessionResult(new CreatedSession(session.Id, code, session.CodeExpiresAt.Value), null);
    }

    /// <summary>
    /// Checks a code typed on the portal <b>without</b> consuming it. Unknown codes count as a failure for the
    /// IP ban service; malformed input as well (it can only come from guessing or typos).
    /// </summary>
    public async Task<CodeCheckResult> CheckCodeAsync(string? input, IPAddress? address, CancellationToken cancellationToken = default)
    {
        var result = await LookupAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.Result == CodeCheckResult.Invalid)
        {
            await ipBans.RecordFailureAsync(address, FailureKinds.SessionCode, cancellationToken).ConfigureAwait(false);
        }

        return result.Result;
    }

    /// <summary>
    /// Consumes a code when an ad-hoc agent authenticates: the session moves to WaitingForViewer and the code
    /// can never be used again. Safe against two agents racing for the same code.
    /// </summary>
    public async Task<RedeemResult> RedeemCodeAsync(string? input, AgentInfo agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var lookup = await LookupAsync(input, cancellationToken).ConfigureAwait(false);
        if (lookup.Result != CodeCheckResult.Valid)
        {
            if (lookup.Result == CodeCheckResult.Invalid)
            {
                await ipBans.RecordFailureAsync(agent.Address, FailureKinds.SessionCode, cancellationToken).ConfigureAwait(false);
            }

            return new RedeemResult(null, lookup.Result == CodeCheckResult.Expired ? ErrorCodes.CodeExpired : ErrorCodes.CodeInvalid);
        }

        var now = timeProvider.GetUtcNow();
        var sessionId = lookup.SessionId!.Value;
        var machineName = Truncate(agent.MachineName, 100);
        var ip = agent.Address is null ? null : IpNetworks.Normalize(agent.Address).ToString();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await db.Sessions
            .Where(s => s.Id == sessionId && s.Status == SessionStatus.WaitingForAgent && s.CodeHash != null && s.CodeExpiresAt > now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(s => s.Status, SessionStatus.WaitingForViewer)
                    .SetProperty(s => s.CodeHash, (byte[]?)null)
                    .SetProperty(s => s.AgentMachineName, machineName)
                    .SetProperty(s => s.AgentIp, ip),
                cancellationToken).ConfigureAwait(false);

        if (updated == 0)
        {
            // Another agent won the race, or the code expired a moment ago.
            return new RedeemResult(null, ErrorCodes.CodeInvalid);
        }

        var ownerName = await db.Users.Where(u => u.Id == lookup.OwnerUserId)
            .Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.SessionAgentConnected, AuditActorType.Guest, Ip: ip,
            TargetType: "session", TargetId: sessionId.ToString(),
            Details: new Dictionary<string, string?> { ["machine"] = machineName }),
            cancellationToken).ConfigureAwait(false);

        return new RedeemResult(sessionId, null, ownerName);
    }

    /// <summary>
    /// Ends a session (revoking an unused code is the same operation). Only the owner or an Admin may do this.
    /// Returns <c>false</c> if the session does not exist, is already ended or the actor is not allowed.
    /// </summary>
    public async Task<bool> EndAsync(Guid sessionId, Guid actorUserId, bool actorIsAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.IsEnded || (session.OwnerUserId != actorUserId && !actorIsAdmin))
        {
            return false;
        }

        var reason = session.Status == SessionStatus.WaitingForAgent ? SessionEndReasons.Revoked : SessionEndReasons.EndedBySupporter;
        MarkEnded(session, reason, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteEndedAsync(session, AuditActorType.User, actorUserId.ToString(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Issues a one-time ticket that lets the browser or desktop viewer join the session over the WebSocket.
    /// Returns <c>null</c> if the user may not join.
    /// </summary>
    public async Task<string?> IssueViewerTicketAsync(Guid sessionId, Guid userId, bool userIsAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);

        // Phase 10 adds device-group permissions for permanent sessions; until then: owner or Admin.
        if (session is null || session.IsEnded || (session.OwnerUserId != userId && !userIsAdmin))
        {
            return null;
        }

        return viewerTickets.Issue(sessionId, userId, options.CurrentValue.ViewerTicketLifetime);
    }

    /// <summary>Sessions for the supporter list, newest first. <paramref name="ownerUserId"/> = <c>null</c> lists everyone's (Admins).</summary>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(Guid? ownerUserId, bool includeEnded, int maxCount = 100, CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow() - TimeSpan.FromDays(7);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Sessions.AsNoTracking();
        if (ownerUserId is { } owner)
        {
            query = query.Where(s => s.OwnerUserId == owner);
        }

        query = includeEnded
            ? query.Where(s => s.Status != SessionStatus.Ended || s.EndedAt > since)
            : query.Where(s => s.Status != SessionStatus.Ended);

        var rows = await query
            .OrderByDescending(s => s.CreatedAt)
            .Take(Math.Clamp(maxCount, 1, 500))
            .Join(db.Users, s => s.OwnerUserId, u => u.Id, (s, u) => new { Session = s, u.DisplayName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new SessionSummary(
            r.Session.Id, r.Session.Kind, r.Session.Status, r.Session.OwnerUserId, r.DisplayName, r.Session.CustomerLabel,
            r.Session.AgentMachineName, r.Session.CreatedAt, r.Session.CodeExpiresAt, r.Session.EndedAt, r.Session.EndReason)).ToList();
    }

    /// <summary>
    /// Ends sessions whose code expired or that exceeded the maximum duration. Called periodically.
    /// </summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var maxStart = now - options.CurrentValue.MaxDuration;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var due = await db.Sessions
            .Where(s => (s.Status == SessionStatus.WaitingForAgent && s.CodeExpiresAt <= now)
                || (s.Status != SessionStatus.Ended && s.CreatedAt <= maxStart))
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var session in due)
        {
            MarkEnded(session, session.Status == SessionStatus.WaitingForAgent && session.CodeExpiresAt <= now
                ? SessionEndReasons.CodeExpired
                : SessionEndReasons.MaxDurationReached, now);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var session in due)
        {
            await WriteEndedAsync(session, AuditActorType.System, null, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    /// <summary>
    /// After a restart no connection survives: sessions that had a connected agent are ended.
    /// Sessions still waiting for an agent keep their code.
    /// </summary>
    public async Task<int> EndInterruptedSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var interrupted = await db.Sessions
            .Where(s => s.Status != SessionStatus.Ended && s.Status != SessionStatus.WaitingForAgent)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var session in interrupted)
        {
            MarkEnded(session, SessionEndReasons.ServerRestart, now);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var session in interrupted)
        {
            await WriteEndedAsync(session, AuditActorType.System, null, cancellationToken).ConfigureAwait(false);
        }

        return interrupted.Count;
    }

    /// <summary>SHA-256 over the normalized digits (docs/session-lifecycle.md §2).</summary>
    internal static byte[] ComputeCodeHash(string normalizedCode) => SHA256.HashData(Encoding.ASCII.GetBytes(normalizedCode));

    internal static string GenerateCode(int length)
    {
        Span<char> digits = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(digits);
    }

    private async Task<(CodeCheckResult Result, Guid? SessionId, Guid OwnerUserId)> LookupAsync(string? input, CancellationToken cancellationToken)
    {
        var code = SessionCodeFormat.Normalize(input);
        if (code is null || code.Length != options.CurrentValue.CodeLength)
        {
            return (CodeCheckResult.Invalid, null, Guid.Empty);
        }

        var hash = ComputeCodeHash(code);
        var now = timeProvider.GetUtcNow();
        var expiredSince = now - ExpiredCodeMemory;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var match = await db.Sessions.AsNoTracking()
            .Where(s => s.CodeHash == hash && (s.Status == SessionStatus.WaitingForAgent
                || (s.EndReason == SessionEndReasons.CodeExpired && s.EndedAt > expiredSince)))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Status, s.CodeExpiresAt, s.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (match is null)
        {
            return (CodeCheckResult.Invalid, null, Guid.Empty);
        }

        return match.Status == SessionStatus.WaitingForAgent && match.CodeExpiresAt > now
            ? (CodeCheckResult.Valid, match.Id, match.OwnerUserId)
            : (CodeCheckResult.Expired, match.Id, match.OwnerUserId);
    }

    private static void MarkEnded(SupportSession session, string reason, DateTimeOffset now)
    {
        session.Status = SessionStatus.Ended;
        session.EndReason = reason;
        session.EndedAt = now;

        // Keep the hash of expired codes for a day (friendly "expired" message); forget all others immediately.
        if (reason != SessionEndReasons.CodeExpired)
        {
            session.CodeHash = null;
        }
    }

    private Task WriteEndedAsync(SupportSession session, AuditActorType actorType, string? actorId, CancellationToken cancellationToken)
    {
        var duration = session.StartedAt is { } started && session.EndedAt is { } ended ? ended - started : (TimeSpan?)null;
        return auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.SessionEnded, actorType, ActorId: actorId,
            TargetType: "session", TargetId: session.Id.ToString(),
            Details: new Dictionary<string, string?>
            {
                ["reason"] = session.EndReason,
                ["duration_seconds"] = duration?.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture),
                ["bytes_relayed"] = session.BytesRelayed.ToString(CultureInfo.InvariantCulture),
            }),
            cancellationToken);
    }

    private static string? NormalizeLabel(string? label) => Truncate(string.IsNullOrWhiteSpace(label) ? null : label.Trim(), MaxLabelLength);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
