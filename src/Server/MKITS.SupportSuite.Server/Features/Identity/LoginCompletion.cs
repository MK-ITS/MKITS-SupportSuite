// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Identity;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// Shared steps after a successful or failed sign-in attempt (password, TOTP, recovery code):
/// IP ban bookkeeping, audit log, language cookie and the mandatory 2FA set-up for Admins.
/// </summary>
public sealed class LoginCompletion(UserManager<MkitsUser> userManager, IpBanService ipBans, IAuditLog auditLog, TimeProvider timeProvider)
{
    /// <summary>Path of the two-factor set-up page; Admins without 2FA are sent there after login.</summary>
    public const string TwoFactorSetupPath = "/account/manage/two-factor";

    private const int MaxLoggedUserNameLength = 100;

    /// <summary>Completes a successful sign-in and returns the local URL to redirect to.</summary>
    public async Task<string> SucceededAsync(MkitsUser user, HttpContext context, string? returnUrl, string method)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(context);

        var ip = context.Connection.RemoteIpAddress;
        ipBans.ResetFailures(ip);

        user.LastLoginAt = timeProvider.GetUtcNow();
        await userManager.UpdateAsync(user).ConfigureAwait(false);

        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.LoginSucceeded, AuditActorType.User, ActorId: user.Id.ToString(), Ip: ip?.ToString(),
            Details: new Dictionary<string, string?> { ["method"] = method })).ConfigureAwait(false);

        if (CultureCookie.IsSupported(user.UiCulture))
        {
            CultureCookie.Append(context, user.UiCulture!);
        }

        if (await userManager.IsInRoleAsync(user, Roles.Admin).ConfigureAwait(false)
            && !await userManager.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
        {
            return TwoFactorSetupPath + "?required=true";
        }

        return LocalUrl.Sanitize(returnUrl);
    }

    /// <summary>Records a failed attempt for the IP ban service and the audit log.</summary>
    public async Task FailedAsync(HttpContext context, string failureKind, string? userName, string reason)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ip = context.Connection.RemoteIpAddress;
        await ipBans.RecordFailureAsync(ip, failureKind).ConfigureAwait(false);

        var loggedName = userName is null ? null : userName.Length > MaxLoggedUserNameLength ? userName[..MaxLoggedUserNameLength] : userName;
        await auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.LoginFailed, AuditActorType.Guest, Ip: ip?.ToString(),
            Details: new Dictionary<string, string?> { ["userName"] = loggedName, ["reason"] = reason, ["kind"] = failureKind })).ConfigureAwait(false);
    }
}
