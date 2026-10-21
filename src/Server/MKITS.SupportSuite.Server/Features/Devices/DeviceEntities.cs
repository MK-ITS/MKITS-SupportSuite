// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Devices;

/// <summary>How the end user is involved when a supporter connects to a device (ADR-0014). Stored as int.</summary>
public enum DeviceConsentPolicy
{
    /// <summary>The user at the device must allow every connection.</summary>
    Ask = 1,

    /// <summary>No question; the device shows a visible banner.</summary>
    Notify = 2,

    /// <summary>No question, no banner (only where legally allowed, e.g. unattended servers).</summary>
    Silent = 3,
}

/// <summary>What a supporter may do on the devices of a group. Admins implicitly have all.</summary>
[Flags]
public enum DevicePermissions
{
    None = 0,
    View = 1,
    Control = 2,
    Files = 4,
    Shell = 8,
    Clipboard = 16,
    All = View | Control | Files | Shell | Clipboard,
}

/// <summary>Device lifecycle (data-model.md).</summary>
public enum DeviceStatus
{
    PendingApproval = 1,
    Active = 2,
    Disabled = 3,
}

/// <summary>A group of permanent devices, e.g. one customer.</summary>
public sealed class DeviceGroup
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DeviceConsentPolicy ConsentPolicy { get; set; } = DeviceConsentPolicy.Ask;

    /// <summary>New devices wait for an Admin before they can be used.</summary>
    public bool RequireEnrollmentApproval { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Access of one User to one group.</summary>
public sealed class GroupAccess
{
    public Guid UserId { get; set; }

    public Guid GroupId { get; set; }

    public DevicePermissions Permissions { get; set; }
}

/// <summary>An enrolled device with its long-term public key.</summary>
public sealed class Device
{
    public Guid Id { get; set; }

    public Guid GroupId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><c>windows</c>, <c>linux</c> or <c>android</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    public string? OsName { get; set; }

    public string? OsVersion { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>ECDSA P-256 SubjectPublicKeyInfo (DER). The private key never leaves the device.</summary>
    public byte[] PublicKey { get; set; } = [];

    public DeviceStatus Status { get; set; }

    public DateTimeOffset EnrolledAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string? LastIp { get; set; }

    public string? Notes { get; set; }
}

/// <summary>A token that lets agents enroll into a group. Only its SHA-256 is stored.</summary>
public sealed class EnrollmentToken
{
    public Guid Id { get; set; }

    public Guid GroupId { get; set; }

    public byte[] TokenHash { get; set; } = [];

    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int MaxUses { get; set; }

    public int Uses { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
