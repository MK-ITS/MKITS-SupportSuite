// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Agent.Linux.Terminal;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Common.Sessions;

// Linux ad-hoc client, terminal version (Phase 11A): mkits-agent --server https://support.example.com --code 12345678
// Server and code also come from the file name (MKITS-Support_<host>_<code>, ADR-0019), like on Windows.
var options = FromArguments(args) ?? FromFileName();
if (options is null)
{
    Console.Error.WriteLine(Text.Get("Usage"));
    return 2;
}

if (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(Text.Get("Error_Wayland"));
    return 3;
}

using var display = X11Display.Open();
if (display is null)
{
    Console.Error.WriteLine(Text.Get("Error_NoDisplay"));
    return 3;
}

if (!JpegEncoder.IsAvailable)
{
    Console.Error.WriteLine(Text.Get("Error_NoTurboJpeg"));
    return 4;
}

var ui = new ConsoleUserInterface();
using var screen = new X11ScreenSource(display);
await using var streamer = new ScreenStreamer(screen);
await using var shells = new ShellHandler(new PtyShellHost());
await using var session = new AgentSession(
    options, new ClientWebSocketConnector(), ui,
    [streamer, new InputHandler(new X11InputInjector(display), screen), new ChatHandler(ui), shells]);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // end cleanly: the supporter sees "ended by customer"
    _ = session.EndAsync();
};

var result = await session.RunAsync(stop.Token);
return result.Succeeded ? 0 : 1;

static AgentSessionOptions? FromArguments(string[] args)
{
    var server = Value(args, "--server");
    var code = Value(args, "--code");
    return server is not null && code is not null && Uri.TryCreate(server, UriKind.Absolute, out var uri)
        ? new AgentSessionOptions { ServerUri = uri, Code = code, Machine = Machine(), Platform = "linux" }
        : null;
}

static AgentSessionOptions? FromFileName() =>
    AdHocClientFileName.TryParse(Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath) is { } target
        ? AgentSessionOptions.FromTarget(target, Machine(), "linux")
        : null;

static string? Value(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static MachineInfo Machine() => new()
{
    MachineName = Environment.MachineName,
    OsName = "Linux",
    OsVersion = RuntimeInformation.OSDescription,
    UserName = Environment.UserName,
};
