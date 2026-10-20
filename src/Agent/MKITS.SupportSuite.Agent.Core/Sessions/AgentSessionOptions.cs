// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Agent.Core.Sessions;

/// <summary>What an ad-hoc agent needs to join a session.</summary>
public sealed class AgentSessionOptions
{
    /// <summary>Base address of the server, e.g. <c>https://support.example.com</c>. The WebSocket URL is derived from it.</summary>
    public required Uri ServerUri { get; init; }

    /// <summary>Session code (digits; spaces and dashes are ignored).</summary>
    public required string Code { get; init; }

    /// <summary>Shown to the supporter.</summary>
    public required MachineInfo Machine { get; init; }

    /// <summary><c>windows</c>, <c>linux</c> or <c>android</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>Optional features this agent supports (e.g. <c>video.h264</c>, <c>shell.conpty</c>).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>How long the agent tries to resume after a connection loss (server grace period is 60 s).</summary>
    public TimeSpan ReconnectWindow { get; init; } = TimeSpan.FromSeconds(55);

    /// <summary>Builds options from the client's file name target (ADR-0019). Always uses TLS.</summary>
    public static AgentSessionOptions FromTarget(AdHocClientTarget target, MachineInfo machine, string platform)
    {
        ArgumentNullException.ThrowIfNull(target);

        var authority = target.Port is { } port ? $"{target.Host}:{port.ToString(CultureInfo.InvariantCulture)}" : target.Host;
        return new AgentSessionOptions
        {
            ServerUri = new Uri($"https://{authority}/"),
            Code = target.Code,
            Machine = machine,
            Platform = platform,
        };
    }

    /// <summary><c>wss://host/ws/agent</c> for https, <c>ws://…</c> for http (development only).</summary>
    public Uri AgentEndpoint
    {
        get
        {
            var builder = new UriBuilder(ServerUri)
            {
                Scheme = ServerUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
                Path = "/ws/agent",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            if (ServerUri.IsDefaultPort)
            {
                builder.Port = -1;
            }

            return builder.Uri;
        }
    }
}
