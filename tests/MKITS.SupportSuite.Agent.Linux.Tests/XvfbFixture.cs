// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.Globalization;
using MKITS.SupportSuite.Agent.Linux.Platform;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

/// <summary>Starts a private Xvfb server (1280x800, 24 bit) for the X11 tests.</summary>
public sealed class XvfbFixture : IDisposable
{
    private readonly Process? _server;

    public XvfbFixture()
    {
        if (!File.Exists("/usr/bin/Xvfb"))
        {
            return;
        }

        var number = 90 + (Environment.ProcessId % 100);
        Name = ":" + number.ToString(CultureInfo.InvariantCulture);
        _server = Process.Start(new ProcessStartInfo("/usr/bin/Xvfb", $"{Name} -screen 0 1280x800x24 -nolisten tcp")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        for (var i = 0; i < 50 && Display is null; i++)
        {
            Thread.Sleep(100);
            Display = X11Display.Open(Name);
        }
    }

    public string? Name { get; }

    /// <summary><c>null</c> if Xvfb is not installed or did not start (tests then fail with a clear message).</summary>
    internal X11Display? Display { get; }

    internal X11Display RequireDisplay() => Display ?? throw new InvalidOperationException("Xvfb is not available – install xvfb.");

    public void Dispose()
    {
        Display?.Dispose();
        if (_server is { HasExited: false })
        {
            _server.Kill();
            _server.WaitForExit(5000);
        }

        _server?.Dispose();
    }
}
