// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MKITS.SupportSuite.Agent.Linux.Native;
using MKITS.SupportSuite.Agent.Linux.Permanent;
using MKITS.SupportSuite.Agent.Linux.Platform;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

/// <summary>
/// The service's access to the user's X server: an Xvfb with access control (<c>-auth</c>) stands in for the user's
/// display; the cookie is passed explicitly, as the service does it, and a lost X server must not end the process.
/// </summary>
public sealed class SessionDesktopTests : IDisposable
{
    private static int _nextDisplay;

    private readonly string _authFile = Path.GetTempFileName();
    private readonly byte[] _cookie = RandomNumberGenerator.GetBytes(16);
    private readonly string _display;
    private readonly Process _server;

    public SessionDesktopTests()
    {
        // A fresh display number per test: a killed Xvfb may leave its lock file behind.
        var number = 200 + ((Environment.ProcessId % 50) * 10) + Interlocked.Increment(ref _nextDisplay);
        _display = ":" + number.ToString(CultureInfo.InvariantCulture);
        File.WriteAllBytes(_authFile, XAuthority.Write(
            [new XAuthorityEntry(XAuthority.FamilyLocal, Encoding.ASCII.GetBytes(Environment.MachineName), number.ToString(CultureInfo.InvariantCulture), XAuthority.MitMagicCookie, _cookie)]));
        _server = Process.Start(new ProcessStartInfo("/usr/bin/Xvfb", $"{_display} -auth {_authFile} -screen 0 800x600x24 -nolisten tcp")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Xvfb is not available – install xvfb.");
        _ = _server.StandardError.ReadToEndAsync(); // keep the pipe drained

        for (var i = 0; i < 50; i++)
        {
            using var probe = X11Display.OpenSurvivable(_display, _cookie);
            if (probe is not null)
            {
                return;
            }

            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        StopServer();
        _server.Dispose();
        File.Delete(_authFile);
    }

    [Fact]
    public void OpenSurvivable_NeedsTheCookie()
    {
        using var withCookie = X11Display.OpenSurvivable(_display, _cookie);
        using var wrongCookie = X11Display.OpenSurvivable(_display, RandomNumberGenerator.GetBytes(16));

        Assert.NotNull(withCookie);
        Assert.Null(wrongCookie);
    }

    [Fact]
    public void LostXServer_MarksTheDisplayBroken_InsteadOfEndingTheProcess()
    {
        using var display = X11Display.OpenSurvivable(_display, _cookie);
        Assert.NotNull(display);

        StopServer();
        using var screen = new X11ScreenSource(display);
        _ = screen.Capture(1, 60, CancellationToken.None); // runs into the I/O error

        Assert.True(display.IsBroken);
    }

    [Fact]
    public void SessionDesktop_FollowsTheSession()
    {
        var time = new ManualTime();
        var session = new LoginSession("2", 1000, "alice", "x11", _display, true, false, "user");
        LoginSession? current = session;
        var resolves = 0;
        using var desktop = new SessionDesktop(
            () => current,
            s =>
            {
                resolves++;
                return (new UserDesktop(s, new UserAccount(1000, 1000, "alice", "/home/alice"), _display, _cookie, new Dictionary<string, string>()), DesktopProblem.None);
            },
            time);

        var monitor = Assert.Single(desktop.GetMonitors());
        Assert.Equal((800, 600), (monitor.Width, monitor.Height));
        Assert.NotNull(desktop.Capture(monitor.Id, 60, CancellationToken.None));
        _ = desktop.GetMonitors();
        Assert.Equal(1, resolves); // connected once, not per call

        time.Advance(TimeSpan.FromSeconds(10));
        _ = desktop.GetMonitors();
        Assert.Equal(1, resolves); // same session: kept

        current = null; // logged out
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(desktop.GetMonitors());
        desktop.MovePointer(10, 10); // dropped, no exception

        current = session with { Id = "3" }; // logged in again
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(desktop.GetMonitors());
        Assert.Equal(2, resolves);
    }

    private void StopServer()
    {
        if (!_server.HasExited)
        {
            _ = LibcNative.Kill(_server.Id, 15); // SIGTERM: Xvfb removes its lock file and socket
            if (!_server.WaitForExit(5000))
            {
                _server.Kill();
            }
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
