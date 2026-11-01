// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Bridge;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Agent.Linux.Wayland;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>What runs behind the session window: an own ad-hoc session, or the bridge to the installed agent's service.</summary>
internal interface ISessionHost : IAsyncDisposable
{
    /// <summary>Where chat messages, files and clipboard changes from this window are sent (<c>null</c> before the session runs).</summary>
    ISessionContext? Context { get; }

    /// <summary>Runs until the session is over; returns an error code if it failed.</summary>
    Task<string?> RunAsync(CancellationToken cancellationToken);

    /// <summary>The user ends the session.</summary>
    Task EndAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Ad-hoc client: the window's own connection to the server with the desktop's screen/input (X11, or Wayland via the
/// portal – opened first, so the desktop's sharing dialog comes before the connection) and the PTY shell (user's rights).
/// </summary>
internal sealed class AdHocSessionHost(
    AgentSessionOptions options, Func<CancellationToken, Task<IDesktopAccess>> openDesktop, IAgentUserInterface ui, IEnumerable<ISessionChannelHandler> userHandlers)
    : ISessionHost
{
    private AgentSession? _session;
    private IDesktopAccess? _desktop;

    public ISessionContext? Context => _session;

    public async Task<string?> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _desktop = await openDesktop(cancellationToken).ConfigureAwait(false);
        }
        catch (DesktopUnavailableException ex)
        {
            return ex.Code;
        }

        await using var streamer = new ScreenStreamer(_desktop.Screen);
        await using var shells = new ShellHandler(new PtyShellHost());
        _session = new AgentSession(
            options, new ClientWebSocketConnector(), ui,
            [streamer, new InputHandler(_desktop.Input, _desktop.Screen), .. userHandlers, shells]);
        var result = await _session.RunAsync(cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? null : result.ErrorCode ?? string.Empty;
    }

    public Task EndAsync(CancellationToken cancellationToken) => _session?.EndAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        if (_desktop is not null)
        {
            await _desktop.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Installed agent (ADR-0025): the window runs in the user's session, started by the service, and talks to it over
/// the given stream. Chat, clipboard and files run here with the user's rights; screen, input and the shell
/// stay in the service – except on Wayland, where only this process can reach the portal: then the given <see cref="LazyWaylandDesktop"/>
/// provides screen and input here (opened when the session is assigned) and the service forwards those channels (ADR-0026).
/// </summary>
internal sealed class BridgeSessionHost : ISessionHost
{
    private readonly Stream _stream;
    private readonly LazyWaylandDesktop? _desktop;
    private readonly ScreenStreamer? _streamer;
    private readonly BridgeClient _client;

    public BridgeSessionHost(Stream stream, IAgentUserInterface ui, IReadOnlyList<ISessionChannelHandler> userHandlers, LazyWaylandDesktop? desktop)
    {
        _stream = stream;
        _desktop = desktop;
        List<ISessionChannelHandler> handlers = [.. userHandlers];
        if (desktop is not null)
        {
            _streamer = new ScreenStreamer(desktop);
            handlers.Add(_streamer);
            handlers.Add(new InputHandler(desktop, desktop));
            ui = new AssignmentHook(ui, desktop.Start);
        }

        _client = new BridgeClient(stream, ui, handlers);
    }

    public ISessionContext? Context => _client;

    public async Task<string?> RunAsync(CancellationToken cancellationToken)
    {
        // The pipe is read on the thread pool; the UI thread only receives the posted results.
        await Task.Run(() => _client.RunAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return null;
    }

    public Task EndAsync(CancellationToken cancellationToken) => _client.EndSessionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_streamer is not null)
        {
            await _streamer.DisposeAsync().ConfigureAwait(false);
        }

        if (_desktop is not null)
        {
            await _desktop.DisposeAsync().ConfigureAwait(false);
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Opens the desktop as soon as a session is assigned (before consent and viewers), then passes everything on.</summary>
    private sealed class AssignmentHook(IAgentUserInterface inner, Action assigned) : IAgentUserInterface
    {
        public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken) => inner.RequestConsentAsync(prompt, cancellationToken);

        public void OnStatusChanged(AgentStatus status, string? detail) => inner.OnStatusChanged(status, detail);

        public void OnSupportersChanged(IReadOnlyList<string> displayNames) => inner.OnSupportersChanged(displayNames);

        public void OnSecurityCode(string supporterDisplayName, string securityCode) => inner.OnSecurityCode(supporterDisplayName, securityCode);

        public void OnSessionAssigned(string operatorName, ConsentPolicy policy)
        {
            assigned();
            inner.OnSessionAssigned(operatorName, policy);
        }
    }
}
