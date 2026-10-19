// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Server.Features.Audit;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>One row of the user list in the admin UI.</summary>
public sealed record UserSummary(
    Guid Id,
    string UserName,
    string Email,
    string DisplayName,
    string Role,
    bool TwoFactorEnabled,
    bool IsDisabled,
    bool IsLockedOut,
    DateTimeOffset? LastLoginAt);

/// <summary>Outcome of an administrative action. <see cref="Secret"/> is shown once (e.g. a generated password).</summary>
public sealed record AdminActionResult(bool Succeeded, IReadOnlyList<string> Errors, string? Secret = null)
{
    public static AdminActionResult Ok(string? secret = null) => new(true, [], secret);

    public static AdminActionResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>
/// User management for Admins. Every action is audited; Admins cannot lock themselves out
/// (no self-disable/-demotion, the last active Admin is protected).
/// </summary>
/// <remarks>Error values are resource keys (prefix <c>Admin_Error_</c>) or already localized Identity error texts.</remarks>
public sealed class UserAdministration(UserManager<MkitsUser> userManager, IAuditLog auditLog, ClientConnectionInfo client, TimeProvider timeProvider)
{
    public const string ErrorSelf = "Admin_Error_Self";
    public const string ErrorLastAdmin = "Admin_Error_LastAdmin";
    public const string ErrorNotFound = "Admin_Error_NotFound";
    public const string ErrorInvalidRole = "Admin_Error_InvalidRole";

    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private const int GeneratedPasswordLength = 20;

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var users = await userManager.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var result = new List<UserSummary>(users.Count);
        foreach (var user in users)
        {
            var isAdmin = await userManager.IsInRoleAsync(user, Roles.Admin).ConfigureAwait(false);
            result.Add(new UserSummary(
                user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty, user.DisplayName,
                isAdmin ? Roles.Admin : Roles.User, user.TwoFactorEnabled, user.IsDisabled,
                user.LockoutEnd is { } end && end > now, user.LastLoginAt));
        }

        return result;
    }

    /// <summary>Creates a user with a generated initial password (returned as <see cref="AdminActionResult.Secret"/>).</summary>
    public async Task<AdminActionResult> CreateAsync(Guid actorId, string userName, string email, string displayName, string role, string? uiCulture)
    {
        if (!Roles.All.Contains(role))
        {
            return AdminActionResult.Fail(ErrorInvalidRole);
        }

        var user = new MkitsUser
        {
            Id = Guid.CreateVersion7(),
            UserName = userName.Trim(),
            Email = email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            UiCulture = CultureCookie.IsSupported(uiCulture) ? uiCulture : null,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        var password = GeneratePassword();
        var created = await userManager.CreateAsync(user, password).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            return FromIdentity(created);
        }

        var roleResult = await userManager.AddToRoleAsync(user, role).ConfigureAwait(false);
        if (!roleResult.Succeeded)
        {
            return FromIdentity(roleResult);
        }

        await AuditAsync(AuditEventTypes.UserCreated, actorId, user.Id, ("role", role)).ConfigureAwait(false);
        return AdminActionResult.Ok(password);
    }

    public async Task<AdminActionResult> SetDisabledAsync(Guid actorId, Guid userId, bool disabled)
    {
        if (actorId == userId)
        {
            return AdminActionResult.Fail(ErrorSelf);
        }

        var user = await userManager.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return AdminActionResult.Fail(ErrorNotFound);
        }

        if (disabled && await IsLastActiveAdminAsync(user).ConfigureAwait(false))
        {
            return AdminActionResult.Fail(ErrorLastAdmin);
        }

        user.IsDisabled = disabled;
        var result = await userManager.UpdateAsync(user).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return FromIdentity(result);
        }

        // Invalidates cookies and interactive circuits of the user.
        await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await AuditAsync(disabled ? AuditEventTypes.UserDisabled : AuditEventTypes.UserChanged, actorId, userId, ("disabled", disabled ? "true" : "false")).ConfigureAwait(false);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetRoleAsync(Guid actorId, Guid userId, string role)
    {
        if (!Roles.All.Contains(role))
        {
            return AdminActionResult.Fail(ErrorInvalidRole);
        }

        if (actorId == userId)
        {
            return AdminActionResult.Fail(ErrorSelf);
        }

        var user = await userManager.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return AdminActionResult.Fail(ErrorNotFound);
        }

        if (role != Roles.Admin && await IsLastActiveAdminAsync(user).ConfigureAwait(false))
        {
            return AdminActionResult.Fail(ErrorLastAdmin);
        }

        var currentRoles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
        await userManager.RemoveFromRolesAsync(user, currentRoles).ConfigureAwait(false);
        var result = await userManager.AddToRoleAsync(user, role).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return FromIdentity(result);
        }

        await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.UserChanged, actorId, userId, ("role", role)).ConfigureAwait(false);
        return AdminActionResult.Ok();
    }

    /// <summary>Removes the authenticator; the user has to set up 2FA again at the next login.</summary>
    public async Task<AdminActionResult> ResetTwoFactorAsync(Guid actorId, Guid userId)
    {
        if (actorId == userId)
        {
            return AdminActionResult.Fail(ErrorSelf);
        }

        var user = await userManager.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return AdminActionResult.Fail(ErrorNotFound);
        }

        await userManager.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
        await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.UserChanged, actorId, userId, ("twoFactor", "reset")).ConfigureAwait(false);
        return AdminActionResult.Ok();
    }

    /// <summary>Sets a new generated password (returned once) and unlocks the account.</summary>
    public async Task<AdminActionResult> ResetPasswordAsync(Guid actorId, Guid userId)
    {
        if (actorId == userId)
        {
            return AdminActionResult.Fail(ErrorSelf);
        }

        var user = await userManager.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return AdminActionResult.Fail(ErrorNotFound);
        }

        var password = GeneratePassword();
        var token = await userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
        var result = await userManager.ResetPasswordAsync(user, token, password).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return FromIdentity(result);
        }

        await userManager.SetLockoutEndDateAsync(user, null).ConfigureAwait(false);
        await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        await AuditAsync(AuditEventTypes.UserChanged, actorId, userId, ("password", "reset")).ConfigureAwait(false);
        return AdminActionResult.Ok(password);
    }

    internal static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedPasswordLength);

    private async Task<bool> IsLastActiveAdminAsync(MkitsUser user)
    {
        if (!await userManager.IsInRoleAsync(user, Roles.Admin).ConfigureAwait(false))
        {
            return false;
        }

        var admins = await userManager.GetUsersInRoleAsync(Roles.Admin).ConfigureAwait(false);
        return admins.Count(a => !a.IsDisabled) <= 1;
    }

    private Task AuditAsync(string eventType, Guid actorId, Guid targetId, params (string Key, string? Value)[] details) =>
        auditLog.WriteAsync(new AuditEntry(
            eventType, AuditActorType.User, ActorId: actorId.ToString(), Ip: client.RemoteIp?.ToString(),
            TargetType: "user", TargetId: targetId.ToString(),
            Details: details.ToDictionary(d => d.Key, d => d.Value)));

    private static AdminActionResult FromIdentity(IdentityResult result) =>
        AdminActionResult.Fail(result.Errors.Select(e => e.Description).ToArray());
}
