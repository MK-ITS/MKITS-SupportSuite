// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// Screen and input of whoever is logged on graphically right now, for the system service. Follows logins, logouts
/// and user switches: the desktop is looked up again every few seconds and whenever the X connection broke. Without
/// a desktop (nobody logged on, Wayland, headless server) there are no monitors and input is dropped.
/// </summary>
internal sealed class SessionDesktop(
    Func<LoginSession?> findSession,
    Func<LoginSession, (UserDesktop? Desktop, DesktopProblem Problem)> resolve,
    TimeProvider? timeProvider = null)
    : IScreenSource, IInputInjector, IDisposable
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Connected? _current;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private DesktopProblem _lastProblem = DesktopProblem.None;
    private bool _disposed;

    public SessionDesktop()
        : this(DesktopLocator.FindSession, DesktopLocator.Resolve)
    {
    }

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => With(c => c.Screen.GetMonitors(), []);

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken) =>
        With(c => c.Screen.Capture(monitorId, quality, cancellationToken), null);

    public void MovePointer(int x, int y) => With(c => c.Input.MovePointer(x, y));

    public void SetButton(PointerButtons button, bool down) => With(c => c.Input.SetButton(button, down));

    public void Wheel(int deltaX, int deltaY) => With(c => c.Input.Wheel(deltaX, deltaY));

    public void Key(string code, bool down) => With(c => c.Input.Key(code, down));

    public void Text(string text) => With(c => c.Input.Text(text));

    public void Special(SpecialKeyKind kind) => With(c => c.Input.Special(kind));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current?.Dispose();
            _current = null;
        }
    }

    private void With(Action<Connected> action) => With<object?>(c =>
    {
        action(c);
        return null;
    }, null);

    /// <summary>
    /// Runs <paramref name="action"/> on the current connection. One lock for capture and input: Xlib serializes calls on
    /// a display anyway (<see cref="X11Display.Gate"/>), so this adds no contention.
    /// </summary>
    private T With<T>(Func<Connected, T> action, T fallback)
    {
        lock (_gate)
        {
            if (_disposed || Current() is not { } connected)
            {
                return fallback;
            }

            return action(connected);
        }
    }

    private Connected? Current()
    {
        var now = _time.GetUtcNow();
        if (_current is { Display.IsBroken: false } current && now < _nextCheck)
        {
            return current;
        }

        _nextCheck = now + RecheckInterval;
        var session = findSession();
        if (_current is { } existing)
        {
            if (!existing.Display.IsBroken && session?.Id == existing.SessionId)
            {
                return existing;
            }

            AgentLog.Write(existing.Display.IsBroken ? "X connection lost" : "the graphical session changed");
            existing.Dispose();
            _current = null;
        }

        var (desktop, problem) = session is null ? (null, DesktopProblem.NobodyLoggedOn) : resolve(session);
        if (desktop is null)
        {
            Report(problem, problem switch
            {
                DesktopProblem.Wayland => "the logged-on user runs a Wayland session – screen and input are not available yet",
                DesktopProblem.NoDisplay => "graphical session without a usable X display",
                _ => "nobody is logged on graphically – screen and input are not available",
            });
            return null;
        }

        var display = X11Display.OpenSurvivable(desktop.Display, desktop.Cookie);
        if (display is null)
        {
            Report(DesktopProblem.NoDisplay, $"cannot open X display {desktop.Display} of {desktop.User.Name}"
                + (desktop.Cookie is null ? " (no X authority cookie found)" : string.Empty));
            return null;
        }

        AgentLog.Write($"connected to X display {desktop.Display} of {desktop.User.Name} (session {desktop.Session.Id})");
        _lastProblem = DesktopProblem.None;
        _current = new Connected(desktop.Session.Id, display);
        return _current;
    }

    /// <summary>Logs a problem once, not every few seconds.</summary>
    private void Report(DesktopProblem problem, string message)
    {
        if (problem != _lastProblem)
        {
            AgentLog.Write(message);
            _lastProblem = problem;
        }
    }

    private sealed class Connected(string sessionId, X11Display display) : IDisposable
    {
        public string SessionId { get; } = sessionId;

        public X11Display Display { get; } = display;

        public X11ScreenSource Screen { get; } = new(display);

        public X11InputInjector Input { get; } = new(display);

        public void Dispose()
        {
            Screen.Dispose();
            Display.Dispose();
        }
    }
}
