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

    public const string SessionCreated = "session.created";
    public const string SessionAgentConnected = "session.agent_connected";
    public const string SessionViewerJoined = "session.viewer_joined";
    public const string SessionViewerLeft = "session.viewer_left";
    public const string SessionConsent = "session.consent";
    public const string SessionStarted = "session.started";
    public const string SessionSuspended = "session.suspended";
    public const string SessionResumed = "session.resumed";
    public const string SessionEnded = "session.ended";

    public const string GroupCreated = "group.created";
    public const string GroupChanged = "group.changed";
    public const string GroupDeleted = "group.deleted";
    public const string GroupPermissionChanged = "group.permission_changed";
    public const string EnrollmentTokenCreated = "enrollment_token.created";
    public const string EnrollmentTokenRevoked = "enrollment_token.revoked";

    public const string DeviceEnrolled = "device.enrolled";
    public const string DeviceApproved = "device.approved";
    public const string DeviceChanged = "device.changed";
    public const string DeviceDisabled = "device.disabled";
    public const string DeviceEnabled = "device.enabled";
    public const string DeviceRemoved = "device.removed";
}
