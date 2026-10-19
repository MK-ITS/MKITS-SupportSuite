// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Common.Protocol;

/// <summary>Stable values of <c>Error.code</c> (docs/protocol.md §7). Clients localize them; never change a value.</summary>
public static class ErrorCodes
{
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string AuthFailed = "AUTH_FAILED";
    public const string IpBlocked = "IP_BLOCKED";
    public const string CodeInvalid = "CODE_INVALID";
    public const string CodeExpired = "CODE_EXPIRED";
    public const string SessionEnded = "SESSION_ENDED";
    public const string ConsentDenied = "CONSENT_DENIED";
    public const string NotAuthorized = "NOT_AUTHORIZED";
    public const string RateLimited = "RATE_LIMITED";
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    public const string Internal = "INTERNAL";
}
