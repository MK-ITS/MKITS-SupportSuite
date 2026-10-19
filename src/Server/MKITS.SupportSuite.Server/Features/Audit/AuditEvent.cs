// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Audit;

/// <summary>Who caused an audit event.</summary>
public enum AuditActorType
{
    System = 0,
    User = 1,
    Guest = 2,
    Device = 3,
}

/// <summary>
/// One entry of the append-only, hash-chained audit log (docs/security-concept.md, threat T16).
/// Never update or delete rows except through retention cleanup, which is itself audited.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Sequential id; defines the order of the hash chain.</summary>
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public AuditActorType ActorType { get; set; }

    public string? ActorId { get; set; }

    public string? Ip { get; set; }

    /// <summary>Dotted identifier, e.g. <c>auth.login_failed</c>, <c>ipban.created</c>. See <see cref="AuditEventTypes"/>.</summary>
    public string EventType { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>JSON object with event-specific details. Must never contain secrets.</summary>
    public string Details { get; set; } = "{}";

    /// <summary>Hash of the previous entry (32 zero bytes for the first entry).</summary>
    public byte[] PreviousHash { get; set; } = [];

    /// <summary>SHA-256 over <see cref="PreviousHash"/> and the canonical form of this entry.</summary>
    public byte[] Hash { get; set; } = [];
}
