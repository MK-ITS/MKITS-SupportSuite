// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Notifications;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>What kind of failure is reported – used for the ban reason and the audit log.</summary>
public static class FailureKinds
{
    public const string Login = "login";
    public const string TwoFactor = "two_factor";
    public const string SessionCode = "session_code";
    public const string EnrollmentToken = "enrollment_token";
    public const string ViewerTicket = "viewer_ticket";
    public const string DeviceSignature = "device_signature";
    public const string SetupToken = "setup_token";
}

/// <summary>
/// fail2ban-style protection (docs/security-concept.md §5): counts failures per client network,
/// bans after <see cref="IpBanOptions.MaxFailures"/> failures within <see cref="IpBanOptions.Window"/>,
/// and answers "may this address connect?" from an in-memory snapshot (no database access per request).
/// </summary>
public sealed partial class IpBanService(
    IDbContextFactory<MkitsDbContext> dbFactory,
    IOptionsMonitor<IpBanOptions> options,
    FailureTracker failureTracker,
    IAuditLog auditLog,
    TimeProvider timeProvider,
    ILogger<IpBanService> logger,
    IAdminNotificationQueue? notifications = null)
{
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private volatile ConfiguredLists? _configuredLists;

    /// <summary>Loads bans and list entries from the database. Call at startup and after every change.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        var bans = await db.IpBans.AsNoTracking()
            .Where(b => b.LiftedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lists = await db.IpListEntries.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        _snapshot = new Snapshot(
            bans.Select(b => (Network: Parse(b.Network), b.ExpiresAt)).Where(b => b.Network is not null)
                .Select(b => new ActiveBan(b.Network!.Value, b.ExpiresAt)).ToImmutableArray(),
            ParseAll(lists.Where(e => e.ListType == IpListType.Allow).Select(e => e.Network)),
            ParseAll(lists.Where(e => e.ListType == IpListType.Deny).Select(e => e.Network)));
    }

    /// <summary>
    /// <c>true</c> if requests from <paramref name="address"/> are accepted.
    /// Order: allow list wins, then deny list, then active bans. Unknown addresses (<c>null</c>) are accepted.
    /// </summary>
    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        var ip = IpNetworks.Normalize(address);
        var snapshot = _snapshot;
        var settings = options.CurrentValue;

        if (IsAllowListed(ip, snapshot, settings))
        {
            return true;
        }

        if (ContainsAny(GetConfiguredLists(settings).DenyList, ip) || ContainsAny(snapshot.DenyList, ip))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var ban in snapshot.Bans)
        {
            if ((ban.ExpiresAt is null || ban.ExpiresAt > now) && ban.Network.Contains(ip))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Records a failed attempt (wrong password, code, token, …). Returns <c>true</c> if this failure caused a ban.
    /// </summary>
    public async Task<bool> RecordFailureAsync(IPAddress? address, string failureKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureKind);

        if (address is null)
        {
            return false;
        }

        var ip = IpNetworks.Normalize(address);
        var settings = options.CurrentValue;
        if (IsAllowListed(ip, _snapshot, settings))
        {
            return false;
        }

        var network = IpNetworks.ClientNetwork(ip, settings.IPv6PrefixLength);
        var key = network.ToString();
        var failures = failureTracker.RecordFailure(key, timeProvider.GetUtcNow(), settings.Window);

        LogFailure(logger, failureKind, key, failures, settings.MaxFailures);

        if (failures < settings.MaxFailures)
        {
            return false;
        }

        failureTracker.Reset(key);
        await CreateBanAsync(key, $"auto:{failureKind}", settings.BanDuration, createdByUserId: null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Forgets the failure count of a client (e.g. after a successful login).</summary>
    public void ResetFailures(IPAddress? address)
    {
        if (address is not null)
        {
            failureTracker.Reset(IpNetworks.ClientNetwork(address, options.CurrentValue.IPv6PrefixLength).ToString());
        }
    }

    /// <summary>Creates a ban (automatic when <paramref name="createdByUserId"/> is <c>null</c>). <paramref name="duration"/> <c>null</c> = permanent.</summary>
    public async Task<IpBan> CreateBanAsync(string network, string reason, TimeSpan? duration, Guid? createdByUserId, CancellationToken cancellationToken = default)
    {
        if (!IpNetworks.TryParseNetwork(network, out var parsed))
        {
            throw new ArgumentException($"Invalid network '{network}'.", nameof(network));
        }

        var now = timeProvider.GetUtcNow();
        var ban = new IpBan
        {
            Id = Guid.CreateVersion7(),
            Network = parsed.ToString(),
            Reason = reason,
            CreatedAt = now,
            ExpiresAt = duration is null ? null : now + duration.Value,
            CreatedByUserId = createdByUserId,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.IpBans.Add(ban);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);

        LogBanCreated(logger, ban.Network, ban.Reason, ban.ExpiresAt);
        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.IpBanCreated,
            createdByUserId is null ? AuditActorType.System : AuditActorType.User,
            ActorId: createdByUserId?.ToString(),
            TargetType: "network",
            TargetId: ban.Network,
            Details: new Dictionary<string, string?>
            {
                ["reason"] = ban.Reason,
                ["expiresAt"] = ban.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture),
            }), cancellationToken).ConfigureAwait(false);

        if (createdByUserId is null)
        {
            notifications?.Enqueue(AdminNotification.IpBanned(ban.Network, ban.Reason, ban.ExpiresAt));
        }

        return ban;
    }

    /// <summary>Lifts a ban. Returns <c>false</c> if it does not exist or is already lifted.</summary>
    public async Task<bool> LiftBanAsync(Guid banId, Guid liftedByUserId, CancellationToken cancellationToken = default)
    {
        string network;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var ban = await db.IpBans.FirstOrDefaultAsync(b => b.Id == banId, cancellationToken).ConfigureAwait(false);
            if (ban is null || ban.LiftedAt is not null)
            {
                return false;
            }

            ban.LiftedAt = timeProvider.GetUtcNow();
            ban.LiftedByUserId = liftedByUserId;
            network = ban.Network;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.IpBanLifted, AuditActorType.User, ActorId: liftedByUserId.ToString(),
            TargetType: "network", TargetId: network), cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Active bans, newest first (for the admin UI).</summary>
    public async Task<IReadOnlyList<IpBan>> GetActiveBansAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        return await db.IpBans.AsNoTracking()
            .Where(b => b.LiftedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Allow/deny entries managed in the admin UI.</summary>
    public async Task<IReadOnlyList<IpListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.IpListEntries.AsNoTracking()
            .OrderBy(e => e.ListType).ThenBy(e => e.Network)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Entries from configuration (<c>Mkits:IpBan:AllowList/DenyList</c>); read-only in the UI.</summary>
    public (IReadOnlyList<string> AllowList, IReadOnlyList<string> DenyList) GetConfiguredLists()
    {
        var settings = options.CurrentValue;
        return ([.. settings.AllowList], [.. settings.DenyList]);
    }

    /// <summary>Adds an allow or deny entry. Returns a resource key on failure, <c>null</c> on success.</summary>
    public async Task<string?> AddListEntryAsync(string network, IpListType listType, string? comment, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        if (!IpNetworks.TryParseNetwork(network?.Trim() ?? string.Empty, out var parsed))
        {
            return "IpBans_Error_InvalidNetwork";
        }

        var normalized = parsed.ToString();
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await db.IpListEntries.AnyAsync(e => e.ListType == listType && e.Network == normalized, cancellationToken).ConfigureAwait(false))
            {
                return "IpBans_Error_Duplicate";
            }

            db.IpListEntries.Add(new IpListEntry
            {
                Id = Guid.CreateVersion7(),
                Network = normalized,
                ListType = listType,
                Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
                CreatedAt = timeProvider.GetUtcNow(),
                CreatedByUserId = createdByUserId,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        await AuditListChangeAsync("added", listType, normalized, createdByUserId, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Removes an allow or deny entry. Returns <c>false</c> if it does not exist.</summary>
    public async Task<bool> RemoveListEntryAsync(Guid entryId, Guid removedByUserId, CancellationToken cancellationToken = default)
    {
        IpListEntry? entry;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            entry = await db.IpListEntries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            db.IpListEntries.Remove(entry);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        await AuditListChangeAsync("removed", entry.ListType, entry.Network, removedByUserId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task AuditListChangeAsync(string change, IpListType listType, string network, Guid userId, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.IpListChanged, AuditActorType.User, ActorId: userId.ToString(),
            TargetType: "network", TargetId: network,
            Details: new Dictionary<string, string?>
            {
                ["change"] = change,
                ["list"] = listType == IpListType.Allow ? "allow" : "deny",
            }), cancellationToken);

    private bool IsAllowListed(IPAddress ip, Snapshot snapshot, IpBanOptions settings) =>
        ContainsAny(GetConfiguredLists(settings).AllowList, ip) || ContainsAny(snapshot.AllowList, ip);

    /// <summary>Parsed configuration lists, re-parsed only when the options instance changes (config reload).</summary>
    private ConfiguredLists GetConfiguredLists(IpBanOptions settings)
    {
        var cached = _configuredLists;
        if (cached is not null && ReferenceEquals(cached.Source, settings))
        {
            return cached;
        }

        cached = new ConfiguredLists(settings, ParseAll(settings.AllowList), ParseAll(settings.DenyList));
        _configuredLists = cached;
        return cached;
    }

    private static bool ContainsAny(ImmutableArray<System.Net.IPNetwork> networks, IPAddress ip)
    {
        foreach (var network in networks)
        {
            if (network.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }

    private static System.Net.IPNetwork? Parse(string value) =>
        IpNetworks.TryParseNetwork(value, out var network) ? network : null;

    private static ImmutableArray<System.Net.IPNetwork> ParseAll(IEnumerable<string> values) =>
        values.Select(Parse).Where(n => n is not null).Select(n => n!.Value).ToImmutableArray();

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed {FailureKind} attempt from {Network} ({Failures}/{MaxFailures})")]
    private static partial void LogFailure(ILogger logger, string failureKind, string network, int failures, int maxFailures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Banned {Network} ({Reason}) until {ExpiresAt}")]
    private static partial void LogBanCreated(ILogger logger, string network, string reason, DateTimeOffset? expiresAt);

    private sealed record ConfiguredLists(
        IpBanOptions Source,
        ImmutableArray<System.Net.IPNetwork> AllowList,
        ImmutableArray<System.Net.IPNetwork> DenyList);

    private sealed record ActiveBan(System.Net.IPNetwork Network, DateTimeOffset? ExpiresAt);

    private sealed record Snapshot(
        ImmutableArray<ActiveBan> Bans,
        ImmutableArray<System.Net.IPNetwork> AllowList,
        ImmutableArray<System.Net.IPNetwork> DenyList)
    {
        public static readonly Snapshot Empty = new([], [], []);
    }
}
