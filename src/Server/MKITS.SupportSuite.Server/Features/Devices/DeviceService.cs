// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.Devices;

public sealed record GroupSummary(Guid Id, string Name, string? Description, DeviceConsentPolicy ConsentPolicy, bool RequireEnrollmentApproval, int DeviceCount);

public sealed record GroupPermissionSummary(Guid UserId, string DisplayName, string UserName, DevicePermissions Permissions);

public sealed record TokenSummary(Guid Id, string? Label, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int Uses, int MaxUses, bool Revoked);

/// <summary>A new enrollment token; <see cref="Token"/> is shown once and never stored.</summary>
public sealed record CreatedToken(Guid Id, string Token, DateTimeOffset ExpiresAt);

public sealed record DeviceSummary(
    Guid Id, string Name, Guid GroupId, string GroupName, string Platform, string? OsName, string? OsVersion, string? AgentVersion,
    DeviceStatus Status, DateTimeOffset EnrolledAt, DateTimeOffset? LastSeenAt, string? LastIp, DevicePermissions Permissions);

/// <summary>What the agent reports about itself.</summary>
public sealed record DeviceInfo(string? MachineName, string Platform, string? OsName, string? OsVersion, string? AgentVersion);

/// <summary>Result of an enrollment. <see cref="ErrorCode"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed record EnrollResult(Guid? DeviceId, DeviceStatus Status, string? ErrorCode);

/// <summary>Persisted facts the relay needs about a device.</summary>
public sealed record DeviceFacts(Guid Id, Guid GroupId, string Name, DeviceStatus Status, DeviceConsentPolicy ConsentPolicy);

/// <summary>
/// Device groups, permissions, enrollment tokens and the device registry (ADR-0013, data-model.md).
/// Long-term device keys are ECDSA P-256; agents prove possession by signing the server's handshake nonce.
/// Callers (UI) enforce that only Admins manage groups, tokens and devices.
/// </summary>
public sealed class DeviceService(
    IDbContextFactory<MkitsDbContext> dbFactory,
    IpBanService ipBans,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    public const string ErrorNameRequired = "Devices_Error_NameRequired";
    public const string ErrorNameTaken = "Devices_Error_NameTaken";
    public const string ErrorGroupNotEmpty = "Devices_Error_GroupNotEmpty";
    public const string ErrorNotFound = "Devices_Error_NotFound";

    internal const int MaxNameLength = 100;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    // ------------------------------------------------------------------ groups

    public async Task<IReadOnlyList<GroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DeviceGroups.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new GroupSummary(g.Id, g.Name, g.Description, g.ConsentPolicy, g.RequireEnrollmentApproval, db.Devices.Count(d => d.GroupId == g.Id)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a group. Returns the new id, or an error resource key.</summary>
    public async Task<(Guid? Id, string? ErrorKey)> CreateGroupAsync(
        Guid actorId, string? name, string? description, DeviceConsentPolicy policy, bool requireApproval, CancellationToken cancellationToken = default)
    {
        var cleanName = Clean(name, MaxNameLength);
        if (cleanName is null)
        {
            return (null, ErrorNameRequired);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.DeviceGroups.AnyAsync(g => g.Name == cleanName, cancellationToken).ConfigureAwait(false))
        {
            return (null, ErrorNameTaken);
        }

        var group = new DeviceGroup
        {
            Id = Guid.CreateVersion7(timeProvider.GetUtcNow()),
            Name = cleanName,
            Description = Clean(description, 500),
            ConsentPolicy = Enum.IsDefined(policy) ? policy : DeviceConsentPolicy.Ask,
            RequireEnrollmentApproval = requireApproval,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.DeviceGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.GroupCreated, actorId, "group", group.Id, new() { ["name"] = group.Name, ["consent"] = group.ConsentPolicy.ToString() }, cancellationToken).ConfigureAwait(false);
        return (group.Id, null);
    }

    public async Task<string?> UpdateGroupAsync(
        Guid actorId, Guid groupId, string? name, string? description, DeviceConsentPolicy policy, bool requireApproval, CancellationToken cancellationToken = default)
    {
        var cleanName = Clean(name, MaxNameLength);
        if (cleanName is null)
        {
            return ErrorNameRequired;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return ErrorNotFound;
        }

        if (await db.DeviceGroups.AnyAsync(g => g.Name == cleanName && g.Id != groupId, cancellationToken).ConfigureAwait(false))
        {
            return ErrorNameTaken;
        }

        group.Name = cleanName;
        group.Description = Clean(description, 500);
        group.ConsentPolicy = Enum.IsDefined(policy) ? policy : group.ConsentPolicy;
        group.RequireEnrollmentApproval = requireApproval;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.GroupChanged, actorId, "group", groupId, new() { ["name"] = group.Name, ["consent"] = group.ConsentPolicy.ToString() }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Deletes an empty group (with its tokens and permissions).</summary>
    public async Task<string?> DeleteGroupAsync(Guid actorId, Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var group = await db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return ErrorNotFound;
        }

        if (await db.Devices.AnyAsync(d => d.GroupId == groupId, cancellationToken).ConfigureAwait(false))
        {
            return ErrorGroupNotEmpty;
        }

        await db.EnrollmentTokens.Where(t => t.GroupId == groupId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.GroupPermissions.Where(p => p.GroupId == groupId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.DeviceGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.GroupDeleted, actorId, "group", groupId, new() { ["name"] = group.Name }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    // ------------------------------------------------------------------ permissions

    public async Task<IReadOnlyList<GroupPermissionSummary>> ListPermissionsAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GroupPermissions.AsNoTracking()
            .Where(p => p.GroupId == groupId)
            .Join(db.Users, p => p.UserId, u => u.Id, (p, u) => new GroupPermissionSummary(u.Id, u.DisplayName, u.UserName ?? string.Empty, p.Permissions))
            .OrderBy(p => p.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets a user's permissions for a group; <see cref="DevicePermissions.None"/> removes the access.</summary>
    public async Task<string?> SetPermissionAsync(Guid actorId, Guid groupId, Guid userId, DevicePermissions permissions, CancellationToken cancellationToken = default)
    {
        permissions &= DevicePermissions.All;
        if (permissions != DevicePermissions.None)
        {
            permissions |= DevicePermissions.View; // every other permission implies viewing
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.DeviceGroups.AnyAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false)
            || !await db.Users.AnyAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false))
        {
            return ErrorNotFound;
        }

        var existing = await db.GroupPermissions.FirstOrDefaultAsync(p => p.GroupId == groupId && p.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (permissions == DevicePermissions.None)
        {
            if (existing is not null)
            {
                db.GroupPermissions.Remove(existing);
            }
        }
        else if (existing is null)
        {
            db.GroupPermissions.Add(new GroupPermission { GroupId = groupId, UserId = userId, Permissions = permissions });
        }
        else
        {
            existing.Permissions = permissions;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.GroupPermissionChanged, actorId, "group", groupId,
            new() { ["user"] = userId.ToString(), ["permissions"] = permissions.ToString() }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    // ------------------------------------------------------------------ enrollment tokens

    public async Task<CreatedToken?> CreateTokenAsync(
        Guid actorId, Guid groupId, string? label, TimeSpan lifetime, int maxUses, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.DeviceGroups.AnyAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entity = new EnrollmentToken
        {
            Id = Guid.CreateVersion7(now),
            GroupId = groupId,
            TokenHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)),
            Label = Clean(label, 100),
            CreatedAt = now,
            ExpiresAt = now + TimeSpan.FromTicks(Math.Clamp(lifetime.Ticks, TimeSpan.FromHours(1).Ticks, TimeSpan.FromDays(365).Ticks)),
            MaxUses = Math.Clamp(maxUses, 1, 10_000),
            CreatedByUserId = actorId,
        };
        db.EnrollmentTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.EnrollmentTokenCreated, actorId, "group", groupId,
            new() { ["token_id"] = entity.Id.ToString(), ["max_uses"] = entity.MaxUses.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        return new CreatedToken(entity.Id, token, entity.ExpiresAt);
    }

    public async Task<IReadOnlyList<TokenSummary>> ListTokensAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.EnrollmentTokens.AsNoTracking()
            .Where(t => t.GroupId == groupId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TokenSummary(t.Id, t.Label, t.CreatedAt, t.ExpiresAt, t.Uses, t.MaxUses, t.RevokedAt != null))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeTokenAsync(Guid actorId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return false;
        }

        token.RevokedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.EnrollmentTokenRevoked, actorId, "group", token.GroupId, new() { ["token_id"] = tokenId.ToString() }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ enrollment & device authentication

    /// <summary>
    /// Registers a new device with its public key. The token is consumed atomically (one use). Invalid tokens count
    /// as failures for the IP ban service.
    /// </summary>
    public async Task<EnrollResult> EnrollAsync(string? token, byte[] publicKey, DeviceInfo info, IPAddress? address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            await ipBans.RecordFailureAsync(address, FailureKinds.EnrollmentToken, cancellationToken).ConfigureAwait(false);
            return new EnrollResult(null, default, ErrorCodes.AuthFailed);
        }

        if (!IsValidP256Key(publicKey))
        {
            return new EnrollResult(null, default, ErrorCodes.AuthFailed);
        }

        var now = timeProvider.GetUtcNow();
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tokenEntity = await db.EnrollmentTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now && t.Uses < t.MaxUses, cancellationToken).ConfigureAwait(false);

        // Consume one use atomically (two agents racing for the last use: only one wins).
        var consumed = tokenEntity is not null && await db.EnrollmentTokens
            .Where(t => t.Id == tokenEntity.Id && t.Uses < t.MaxUses && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Uses, t => t.Uses + 1), cancellationToken).ConfigureAwait(false) == 1;
        if (!consumed)
        {
            await ipBans.RecordFailureAsync(address, FailureKinds.EnrollmentToken, cancellationToken).ConfigureAwait(false);
            return new EnrollResult(null, default, ErrorCodes.AuthFailed);
        }

        var group = await db.DeviceGroups.AsNoTracking().FirstAsync(g => g.Id == tokenEntity!.GroupId, cancellationToken).ConfigureAwait(false);
        var device = new Device
        {
            Id = Guid.CreateVersion7(now),
            GroupId = group.Id,
            Name = Clean(info.MachineName, MaxNameLength) ?? "device",
            Platform = Clean(info.Platform, 20) ?? "unknown",
            OsName = Clean(info.OsName, 100),
            OsVersion = Clean(info.OsVersion, 100),
            AgentVersion = Clean(info.AgentVersion, 50),
            PublicKey = publicKey,
            Status = group.RequireEnrollmentApproval ? DeviceStatus.PendingApproval : DeviceStatus.Active,
            EnrolledAt = now,
            LastSeenAt = now,
            LastIp = address is null ? null : IpNetworks.Normalize(address).ToString(),
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.DeviceEnrolled, AuditActorType.Device, ActorId: device.Id.ToString(), Ip: device.LastIp,
            TargetType: "group", TargetId: group.Id.ToString(),
            Details: new Dictionary<string, string?> { ["name"] = device.Name, ["status"] = device.Status.ToString(), ["token_id"] = tokenEntity!.Id.ToString() }),
            cancellationToken).ConfigureAwait(false);
        return new EnrollResult(device.Id, device.Status, null);
    }

    /// <summary>
    /// Verifies the device's signature (ECDSA P-256 / SHA-256, IEEE P1363 r‖s) over the handshake nonce and updates
    /// what the device reports. Returns <c>null</c> for unknown devices, bad signatures and disabled devices.
    /// </summary>
    public async Task<DeviceFacts?> AuthenticateAsync(
        Guid deviceId, byte[] nonce, byte[] signature, DeviceInfo? info, IPAddress? address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(signature);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || !Verify(device.PublicKey, nonce, signature))
        {
            await ipBans.RecordFailureAsync(address, FailureKinds.DeviceSignature, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (device.Status == DeviceStatus.Disabled)
        {
            return null;
        }

        device.LastSeenAt = timeProvider.GetUtcNow();
        device.LastIp = address is null ? device.LastIp : IpNetworks.Normalize(address).ToString();
        if (info is not null)
        {
            device.OsName = Clean(info.OsName, 100) ?? device.OsName;
            device.OsVersion = Clean(info.OsVersion, 100) ?? device.OsVersion;
            device.AgentVersion = Clean(info.AgentVersion, 50) ?? device.AgentVersion;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var policy = await db.DeviceGroups.Where(g => g.Id == device.GroupId).Select(g => g.ConsentPolicy).FirstAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceFacts(device.Id, device.GroupId, device.Name, device.Status, policy);
    }

    public async Task<DeviceFacts?> GetFactsAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Join(db.DeviceGroups, d => d.GroupId, g => g.Id, (d, g) => new DeviceFacts(d.Id, d.GroupId, d.Name, d.Status, g.ConsentPolicy))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ registry

    /// <summary>Devices visible to the user: all for Admins, otherwise those of groups with a permission.</summary>
    public async Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Devices.AsNoTracking()
            .Join(db.DeviceGroups, d => d.GroupId, g => g.Id, (d, g) => new { Device = d, GroupName = g.Name })
            .OrderBy(r => r.GroupName).ThenBy(r => r.Device.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var permissions = isAdmin
            ? []
            : await db.GroupPermissions.AsNoTracking().Where(p => p.UserId == userId)
                .ToDictionaryAsync(p => p.GroupId, p => p.Permissions, cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => (Row: r, Permissions: isAdmin ? DevicePermissions.All : permissions.GetValueOrDefault(r.Device.GroupId)))
            .Where(x => x.Permissions != DevicePermissions.None)
            .Select(x => new DeviceSummary(
                x.Row.Device.Id, x.Row.Device.Name, x.Row.Device.GroupId, x.Row.GroupName, x.Row.Device.Platform, x.Row.Device.OsName,
                x.Row.Device.OsVersion, x.Row.Device.AgentVersion, x.Row.Device.Status, x.Row.Device.EnrolledAt, x.Row.Device.LastSeenAt,
                x.Row.Device.LastIp, x.Permissions))
            .ToList();
    }

    /// <summary>The user's permissions on an active device (none for unknown, pending or disabled devices).</summary>
    public async Task<DevicePermissions> GetPermissionsAsync(Guid userId, bool isAdmin, Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => new { d.GroupId, d.Status }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (device is null || device.Status != DeviceStatus.Active)
        {
            return DevicePermissions.None;
        }

        if (isAdmin)
        {
            return DevicePermissions.All;
        }

        return await db.GroupPermissions.AsNoTracking()
            .Where(p => p.UserId == userId && p.GroupId == device.GroupId)
            .Select(p => p.Permissions)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> ApproveAsync(Guid actorId, Guid deviceId, CancellationToken cancellationToken = default) =>
        ChangeAsync(actorId, deviceId, d => d.Status = d.Status == DeviceStatus.PendingApproval ? DeviceStatus.Active : d.Status, AuditEventTypes.DeviceApproved, cancellationToken);

    public Task<string?> SetDisabledAsync(Guid actorId, Guid deviceId, bool disabled, CancellationToken cancellationToken = default) =>
        ChangeAsync(actorId, deviceId, d => d.Status = disabled ? DeviceStatus.Disabled : DeviceStatus.Active,
            disabled ? AuditEventTypes.DeviceDisabled : AuditEventTypes.DeviceEnabled, cancellationToken);

    public Task<string?> RenameAsync(Guid actorId, Guid deviceId, string? name, CancellationToken cancellationToken = default)
    {
        var clean = Clean(name, MaxNameLength);
        return clean is null
            ? Task.FromResult<string?>(ErrorNameRequired)
            : ChangeAsync(actorId, deviceId, d => d.Name = clean, AuditEventTypes.DeviceChanged, cancellationToken);
    }

    public async Task<string?> MoveAsync(Guid actorId, Guid deviceId, Guid groupId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await db.DeviceGroups.AnyAsync(g => g.Id == groupId, cancellationToken).ConfigureAwait(false))
            {
                return ErrorNotFound;
            }
        }

        return await ChangeAsync(actorId, deviceId, d => d.GroupId = groupId, AuditEventTypes.DeviceChanged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the device; the agent must be enrolled again.</summary>
    public async Task<string?> RemoveAsync(Guid actorId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return ErrorNotFound;
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.DeviceRemoved, actorId, "device", deviceId, new() { ["name"] = device.Name }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>ECDSA P-256 public key as SubjectPublicKeyInfo.</summary>
    internal static bool IsValidP256Key(byte[]? spki)
    {
        if (spki is null || spki.Length is < 64 or > 200)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            return read == spki.Length && key.ExportParameters(false).Curve.Oid.Value == P256Oid;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static bool Verify(byte[] spki, byte[] data, byte[] signature)
    {
        if (signature.Length != 64 || data.Length is 0 or > 1024)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<string?> ChangeAsync(Guid actorId, Guid deviceId, Action<Device> change, string auditType, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return ErrorNotFound;
        }

        change(device);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(auditType, actorId, "device", deviceId,
            new() { ["name"] = device.Name, ["status"] = device.Status.ToString(), ["group"] = device.GroupId.ToString() }, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private Task AuditAsync(string type, Guid actorId, string targetType, Guid targetId, Dictionary<string, string?> details, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(new AuditEntry(type, AuditActorType.User, ActorId: actorId.ToString(), TargetType: targetType, TargetId: targetId.ToString(), Details: details), cancellationToken);

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
