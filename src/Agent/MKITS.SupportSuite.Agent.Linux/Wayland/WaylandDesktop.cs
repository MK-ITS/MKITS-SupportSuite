// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Platform;
using Tmds.DBus.Protocol;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>
/// Wayland desktop through xdg-desktop-portal (ADR-0026): one RemoteDesktop session with the screens the user picks
/// in the desktop's own dialog, frames via PipeWire, input via the portal. Ending the session (or the user revoking
/// it in the desktop's indicator) stops both.
/// </summary>
internal sealed class WaylandDesktop : IDesktopAccess
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);

    private readonly Connection _connection;
    private readonly PortalSession _session;
    private readonly WaylandScreenSource _screen;

    private WaylandDesktop(Connection connection, PortalSession session, WaylandScreenSource screen)
    {
        _connection = connection;
        _session = session;
        _screen = screen;
        Input = new PortalInputInjector(session);
    }

    public IScreenSource Screen => _screen;

    public IInputInjector Input { get; }

    /// <summary>Restore token for the next session (only with <c>persist</c>).</summary>
    public string? RestoreToken => _session.RestoreToken;

    /// <summary>Whether this looks like a Wayland session (the portal decides whether it actually works).</summary>
    public static bool IsWaylandSession =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Shows the desktop's sharing dialog and connects to the chosen screens. Throws <see cref="DesktopUnavailableException"/>.</summary>
    public static async Task<WaylandDesktop> StartAsync(string? restoreToken, bool persist, CancellationToken cancellationToken)
    {
        if (Address.Session is not { } address)
        {
            throw new DesktopUnavailableException("NO_PORTAL", "No D-Bus session bus.");
        }

        var connection = new Connection(address);
        PortalSession? session = null;
        var captures = new List<(PortalStream, PipeWireCapture)>();
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            session = await PortalSession.StartAsync(connection, PortalSession.Service, restoreToken, persist, cancellationToken).ConfigureAwait(false);
            foreach (var stream in session.Streams)
            {
                var remote = await session.OpenPipeWireRemoteAsync().ConfigureAwait(false);
                var capture = PipeWireCapture.Connect(remote, stream.NodeId)
                    ?? throw new DesktopUnavailableException("NO_PIPEWIRE", "libpipewire-0.3 is not installed.");
                captures.Add((stream, capture));
            }

            // The first frame proves that the stream works; an unchanged screen may still send nothing for a while.
            var first = captures[0].Item2;
            _ = await Task.Run(() => first.WaitForFrame(FirstFrameTimeout), cancellationToken).ConfigureAwait(false);
            return new WaylandDesktop(connection, session, new WaylandScreenSource(captures));
        }
        catch (Exception ex)
        {
            foreach (var (_, capture) in captures)
            {
                capture.Dispose();
            }

            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            connection.Dispose();
            if (Map(ex) is { } mapped)
            {
                throw mapped;
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _screen.Dispose();
        await _session.DisposeAsync().ConfigureAwait(false);
        _connection.Dispose();
    }

    /// <summary>Expected failures become <see cref="DesktopUnavailableException"/> (with an <c>Error_*</c> code for the UI).</summary>
    private static DesktopUnavailableException? Map(Exception ex) => ex switch
    {
        PortalRequestException or TimeoutException => new DesktopUnavailableException("PORTAL_DECLINED", ex.Message, ex),
        DBusException or ConnectException or DisconnectedException => new DesktopUnavailableException("NO_PORTAL", ex.Message, ex),
        IOException => new DesktopUnavailableException("NO_PIPEWIRE", ex.Message, ex),
        _ => null,
    };
}
