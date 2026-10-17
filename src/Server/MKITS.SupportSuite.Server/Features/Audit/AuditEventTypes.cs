// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Audit;

/// <summary>
/// Well-known audit event types. Stable identifiers – they are stored and may be used by external tooling.
/// </summary>
public static class AuditEventTypes
{
    public const string ServerStarted = "server.started";

    public const string IpBanCreated = "ipban.created";
    public const string IpBanLifted = "ipban.lifted";
    public const string IpListChanged = "iplist.changed";

    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";

    public const string UserCreated = "user.created";
    public const string UserChanged = "user.changed";
    public const string UserDisabled = "user.disabled";
}
