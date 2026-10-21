// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Devices;

/// <summary>Configuration of an installed (permanent) agent.</summary>
public sealed class DeviceAgentOptions
{
    /// <summary>Base address of the server, e.g. <c>https://support.example.com</c>.</summary>
    public required Uri ServerUri { get; init; }

    /// <summary>Only needed until the device is enrolled; the installer passes it once.</summary>
    public string? EnrollmentToken { get; init; }

    /// <summary>Reported at enrollment (machine name becomes the device name).</summary>
    public required MachineInfo Machine { get; init; }

    /// <summary><c>windows</c>, <c>linux</c> or <c>android</c>.</summary>
    public required string Platform { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>First retry delay after a connection loss; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan MinReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often a device waiting for approval asks again.</summary>
    public TimeSpan ApprovalPollInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Retry interval when the server rejects the device (disabled or removed – an Admin may re-enable it).</summary>
    public TimeSpan RejectedRetryInterval { get; init; } = TimeSpan.FromMinutes(5);

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
