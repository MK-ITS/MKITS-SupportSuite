// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>
/// The Wayland desktop of the installed agent's session window (ADR-0026): opened through the portal when a session
/// is assigned (with the stored restore token, so the desktop may skip its dialog), used by the screen streamer and
/// input handler of that process. Until it is open there are no monitors and input is dropped.
/// </summary>
internal sealed class LazyWaylandDesktop(Func<CancellationToken, Task<WaylandDesktop>> open) : IScreenSource, IInputInjector, IAsyncDisposable
{
    /// <summary>How long the monitor list waits for the portal (the user may have to answer the desktop's dialog).</summary>
    private static readonly TimeSpan MonitorWait = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();
    private Task<WaylandDesktop?>? _opening;

    /// <summary>Installed agent: portal with persistence, restore token in the user's state folder.</summary>
    public static LazyWaylandDesktop ForInstalledAgent()
    {
        var store = PortalTokenStore.ForCurrentUser();
        return new LazyWaylandDesktop(async cancellationToken =>
        {
            var desktop = await WaylandDesktop.StartAsync(store.Load(), persist: true, cancellationToken).ConfigureAwait(false);
            store.Save(desktop.RestoreToken);
            return desktop;
        });
    }

    /// <summary>Starts opening the portal session (once).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _opening ??= OpenAsync();
        }
    }

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => Ready(MonitorWait)?.Screen.GetMonitors() ?? [];

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken) =>
        Ready(TimeSpan.Zero)?.Screen.Capture(monitorId, quality, cancellationToken);

    public void MovePointer(int x, int y) => Ready(TimeSpan.Zero)?.Input.MovePointer(x, y);

    public void SetButton(PointerButtons button, bool down) => Ready(TimeSpan.Zero)?.Input.SetButton(button, down);

    public void Wheel(int deltaX, int deltaY) => Ready(TimeSpan.Zero)?.Input.Wheel(deltaX, deltaY);

    public void Key(string code, bool down) => Ready(TimeSpan.Zero)?.Input.Key(code, down);

    public void Text(string text) => Ready(TimeSpan.Zero)?.Input.Text(text);

    public void Special(SpecialKeyKind kind) => Ready(TimeSpan.Zero)?.Input.Special(kind);

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        Task<WaylandDesktop?>? opening;
        lock (_gate)
        {
            opening = _opening;
        }

        if (opening is not null && await opening.ConfigureAwait(false) is { } desktop)
        {
            await desktop.DisposeAsync().ConfigureAwait(false);
        }

        _closing.Dispose();
    }

    private async Task<WaylandDesktop?> OpenAsync()
    {
        try
        {
            return await open(_closing.Token).ConfigureAwait(false);
        }
        catch (Platform.DesktopUnavailableException ex)
        {
            await Console.Error.WriteLineAsync($"Wayland desktop not available: {ex.Code} – {ex.Message}").ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // never fault the task: every capture and input call looks at it
            await Console.Error.WriteLineAsync($"Wayland desktop failed: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private WaylandDesktop? Ready(TimeSpan wait)
    {
        Task<WaylandDesktop?>? opening;
        lock (_gate)
        {
            opening = _opening;
        }

        return opening is not null && opening.Wait(wait) ? opening.Result : null;
    }
}
