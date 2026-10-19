// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Sessions;

/// <summary>How a session was started (docs/session-lifecycle.md §1).</summary>
public enum SessionKind
{
    /// <summary>Supporter created a code, the customer runs the portable client.</summary>
    AdHoc = 0,

    /// <summary>Supporter opened an enrolled device (Phase 10).</summary>
    Permanent = 1,
}

/// <summary>States of the session state machine (docs/session-lifecycle.md §3). Values are stored in the database.</summary>
public enum SessionStatus
{
    WaitingForAgent = 1,
    WaitingForViewer = 2,
    AwaitingConsent = 3,
    Active = 4,
    Suspended = 5,
    Ended = 6,
}

/// <summary>Why a session ended. Stored as text; values are stable identifiers.</summary>
public static class SessionEndReasons
{
    public const string CodeExpired = "code_expired";
    public const string Revoked = "revoked";
    public const string EndedBySupporter = "ended_by_supporter";
    public const string EndedByCustomer = "ended_by_customer";
    public const string ConsentDenied = "consent_denied";
    public const string ConsentTimeout = "consent_timeout";
    public const string NoViewer = "no_viewer";
    public const string ConnectionLost = "connection_lost";
    public const string MaxDurationReached = "max_duration";
    public const string ServerRestart = "server_restart";
}

/// <summary>A support session – ad-hoc (code) or permanent (device). See docs/data-model.md.</summary>
public sealed class SupportSession
{
    public Guid Id { get; set; }

    public SessionKind Kind { get; set; }

    public SessionStatus Status { get; set; }

    /// <summary>SHA-256 of the normalized code while it can still be redeemed; cleared once used or ended.</summary>
    public byte[]? CodeHash { get; set; }

    public DateTimeOffset? CodeExpiresAt { get; set; }

    /// <summary>Supporter who created the session.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Permanent sessions only (Phase 10).</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>Free text from the supporter, e.g. a ticket number. Never shown to the customer.</summary>
    public string? CustomerLabel { get; set; }

    public string? AgentMachineName { get; set; }

    public string? AgentIp { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>One of <see cref="SessionEndReasons"/>.</summary>
    public string? EndReason { get; set; }

    public bool Recorded { get; set; }

    public long BytesRelayed { get; set; }

    public bool IsEnded => Status == SessionStatus.Ended;
}
