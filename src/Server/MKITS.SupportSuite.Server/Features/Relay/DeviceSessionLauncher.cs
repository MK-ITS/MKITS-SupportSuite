// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Sessions;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>
/// Opens a permanent device for a supporter (docs/session-lifecycle.md §1): joins the device's running session or
/// creates a new one and binds the online device to it. The caller then opens the viewer page, which issues the
/// viewer ticket with the supporter's permissions.
/// </summary>
public sealed class DeviceSessionLauncher(DeviceService devices, SessionService sessions, SessionHub hub)
{
    public const string ErrorNotAllowed = "Devices_Error_NotAllowed";
    public const string ErrorOffline = "Devices_Error_Offline";

    public async Task<(Guid? SessionId, string? ErrorKey)> OpenAsync(Guid userId, bool isAdmin, Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (await devices.GetPermissionsAsync(userId, isAdmin, deviceId, cancellationToken).ConfigureAwait(false) == DevicePermissions.None)
        {
            return (null, ErrorNotAllowed);
        }

        if (hub.GetDeviceSessionId(deviceId) is { } running)
        {
            return (running, null); // join as an additional viewer (asks again under the "ask" policy)
        }

        var facts = await devices.GetFactsAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var operatorName = await sessions.GetActiveUserDisplayNameAsync(userId, cancellationToken).ConfigureAwait(false);
        if (facts is null || operatorName is null || !hub.IsDeviceOnline(deviceId))
        {
            return (null, ErrorOffline);
        }

        var sessionId = await sessions.CreatePermanentAsync(userId, deviceId, facts.Name, cancellationToken).ConfigureAwait(false);
        if (await hub.StartDeviceSessionAsync(sessionId, deviceId, operatorName, cancellationToken).ConfigureAwait(false))
        {
            return (sessionId, null);
        }

        // Lost a race: the device went offline, or another supporter opened it at the same moment.
        await sessions.EndBySystemAsync(sessionId, SessionEndReasons.ConnectionLost, 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        return hub.GetDeviceSessionId(deviceId) is { } other ? (other, null) : (null, ErrorOffline);
    }
}
