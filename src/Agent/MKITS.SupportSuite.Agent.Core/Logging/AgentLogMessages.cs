// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Logging;

namespace MKITS.SupportSuite.Agent.Core.Logging;

/// <summary>
/// What the agent core logs (7.2): connection, enrollment, sessions, viewers and rejected input. Never secrets:
/// no tokens, session codes, keys, security codes or payload contents.
/// </summary>
internal static partial class AgentLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Server}")]
    public static partial void Connected(this ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot connect to {Endpoint}")]
    public static partial void ConnectFailed(this ILogger logger, Uri endpoint, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused the connection: {Code}")]
    public static partial void ServerRefused(this ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection lost; reconnecting in {Delay}")]
    public static partial void Reconnecting(this ILogger logger, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as device {DeviceId}")]
    public static partial void Enrolled(this ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Enrollment refused: {Code}")]
    public static partial void EnrollmentRefused(this ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} assigned by {Operator}")]
    public static partial void SessionAssigned(this ILogger logger, string sessionId, string @operator);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} ended: {Reason}")]
    public static partial void SessionEnded(this ILogger logger, string? sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Supporter {Name} joined with permissions [{Permissions}]")]
    public static partial void ViewerJoined(this ILogger logger, string name, string permissions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Supporter {Name} left")]
    public static partial void ViewerLeft(this ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consent for {Name}: {Decision}")]
    public static partial void ConsentAnswered(this ILogger logger, string name, string decision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Key exchange with {Name} failed (invalid key)")]
    public static partial void KeyExchangeFailed(this ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "End-to-end keys established with {Name}")]
    public static partial void ViewerReady(this ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped a payload from {Name} on channel {Channel}: {Reason}")]
    public static partial void PayloadDropped(this ILogger logger, string name, uint channel, string reason);
}
