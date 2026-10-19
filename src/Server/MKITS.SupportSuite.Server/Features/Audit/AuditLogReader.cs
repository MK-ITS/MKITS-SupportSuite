// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.Audit;

/// <summary>Filter for the audit log view. All criteria are optional and combined with AND.</summary>
public sealed record AuditQuery(string? EventTypePrefix = null, string? Text = null, int Page = 1, int PageSize = 50);

/// <summary>One page of audit entries plus the total number of matches.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
}

/// <summary>Read access to the audit log for the admin UI (newest first).</summary>
public sealed class AuditLogReader(IDbContextFactory<MkitsDbContext> dbFactory)
{
    public const int MaxPageSize = 200;

    private const string LikeEscape = "\\";

    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, query.Page);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<AuditEvent> events = db.AuditEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.EventTypePrefix))
        {
            var prefix = EscapeLike(query.EventTypePrefix.Trim()) + "%";
            events = events.Where(e => EF.Functions.Like(e.EventType, prefix, LikeEscape));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var pattern = "%" + EscapeLike(query.Text.Trim()) + "%";
            events = events.Where(e =>
                EF.Functions.Like(e.Ip, pattern, LikeEscape) ||
                EF.Functions.Like(e.ActorId, pattern, LikeEscape) ||
                EF.Functions.Like(e.TargetId, pattern, LikeEscape) ||
                EF.Functions.Like(e.Details, pattern, LikeEscape));
        }

        var total = await events.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await events
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new AuditPage(items, total, page, pageSize);
    }

    /// <summary>User input is matched literally: LIKE wildcards (% _) and the escape character are escaped.</summary>
    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
