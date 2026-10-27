// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Linux;
using MKITS.SupportSuite.Agent.Linux.Gui;
using MKITS.SupportSuite.Agent.Linux.Permanent;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Agent.Linux.Terminal;
using MKITS.SupportSuite.Agent.Linux.Wayland;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Common.Sessions;

// Linux ad-hoc client (ADR-0019): MKITS-Support_<host>_<code>.AppImage carries server and code in its file name.
// Developers: mkits-agent --server https://support.example.com --code 12345678 [--terminal]
// The same program is the installed agent (ADR-0025): --install, --uninstall, --agent-service, --agent-user.
switch (args.FirstOrDefault())
{
    case "--install":
        return AgentInstaller.Install(args);
    case "--uninstall":
        return AgentInstaller.Uninstall();
    case "--agent-service":
        return await AgentService.RunAsync();
    case "--agent-user":
        return UserProcess.Run(args);
    default:
        break;
}

var terminal = args.Contains("--terminal", StringComparer.Ordinal) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
var options = FromArguments(args) ?? FromFileName();

// Problems that make remote support impossible on this desktop, checked before anything connects.
// Wayland: screen and input through xdg-desktop-portal + PipeWire (the desktop asks the user which screens to share).
var wayland = WaylandDesktop.IsWaylandSession;
string? problem = null;
X11Display? display = null;
if (!JpegEncoder.IsAvailable)
{
    problem = Ui.Get("Error_NoTurboJpeg");
}
else if (!wayland && (display = X11Display.Open()) is null)
{
    problem = Ui.Get("Error_NoDisplay");
}

Func<CancellationToken, Task<IDesktopAccess>> openDesktop = wayland
    ? async cancellationToken => await WaylandDesktop.StartAsync(restoreToken: null, persist: false, cancellationToken)
    : _ => Task.FromResult<IDesktopAccess>(new X11DesktopAccess(display!));

using (display)
{
    if (terminal)
    {
        if (problem is not null || options is null)
        {
            Console.Error.WriteLine(problem ?? Ui.Get("Usage"));
            return problem is null ? 2 : 3;
        }

        return await RunTerminalAsync(options, openDesktop);
    }

    // Graphical client: errors as a message window, missing target via the connect window.
    return App.Run(() =>
    {
        if (problem is not null)
        {
            return new QuestionWindow(Ui.Get("App_Title"), problem, Ui.Get("Gui_Close"), null);
        }

        if (options is not null)
        {
            return new SessionWindow(options, openDesktop);
        }

        return new ConnectWindow(target => new SessionWindow(AgentSessionOptions.FromTarget(target, Machine(), "linux"), openDesktop).Show());
    }, args);
}

static async Task<int> RunTerminalAsync(AgentSessionOptions options, Func<CancellationToken, Task<IDesktopAccess>> openDesktop)
{
    IDesktopAccess desktop;
    try
    {
        desktop = await openDesktop(CancellationToken.None);
    }
    catch (DesktopUnavailableException ex)
    {
        Console.Error.WriteLine(Ui.ForError(ex.Code));
        return 3;
    }

    await using (desktop)
    {
        var ui = new ConsoleUserInterface();
        await using var streamer = new ScreenStreamer(desktop.Screen);
        await using var shells = new ShellHandler(new PtyShellHost());
        await using var session = new AgentSession(
            options, new ClientWebSocketConnector(), ui,
            [streamer, new InputHandler(desktop.Input, desktop.Screen), new ChatHandler(ui), shells]);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // end cleanly: the supporter sees "ended by customer"
            _ = session.EndAsync();
        };

        var result = await session.RunAsync(CancellationToken.None);
        return result.Succeeded ? 0 : 1;
    }
}

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
