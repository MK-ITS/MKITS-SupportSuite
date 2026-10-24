// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Windows.UI;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Agent.Windows;

/// <summary>
/// Portable ad-hoc client: <c>MKITS-Support_&lt;host&gt;_&lt;code&gt;.exe</c> (ADR-0019). Server and code come from the
/// file name; if it was renamed, a dialog asks for them. Developers can pass <c>--server &lt;url&gt; --code &lt;code&gt;</c>.
/// The same EXE is the permanent agent (ADR-0022): <c>--install</c>, <c>--uninstall</c>, <c>--agent-service</c>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Roles of the elevated ad-hoc mode (ADR-0021); none of them shows a window.
        switch (args.FirstOrDefault())
        {
            case "--elevate":
                return Elevation.ElevationInstaller.Run(args);
            case "--service" when args.Length > 1:
                return Elevation.AdHocService.Run(args[1]);
            case "--helper" when args.Length > 1:
                return Elevation.PrivilegedHelper.Run(args[1]);

            // Permanent agent (ADR-0022).
            case "--install":
                return Permanent.AgentInstaller.Install(args);
            case "--uninstall":
                return Permanent.AgentInstaller.Uninstall();
            case "--agent-service":
                return Permanent.AgentService.Run();
            case "--agent-helper" when args.Length > 1:
                return Elevation.PrivilegedHelper.RunForAgent(args[1]);
            case "--agent-user" when args.Length > 1:
                return UI.SessionWindow.Run(args[1]);
            default:
                break;
        }

        ApplicationConfiguration.Initialize();

        var options = FromArguments(args) ?? FromFileName() ?? FromDialog();
        if (options is null)
        {
            return 1;
        }

        Application.Run(new MainForm(options));
        return 0;
    }

    private static AgentSessionOptions? FromFileName() =>
        AdHocClientFileName.TryParse(Environment.ProcessPath) is { } target ? AgentSessionOptions.FromTarget(target, Machine(), "windows") : null;

    private static AgentSessionOptions? FromDialog()
    {
        using var dialog = new ConnectForm();
        return dialog.ShowDialog() == DialogResult.OK && dialog.Target is { } target
            ? AgentSessionOptions.FromTarget(target, Machine(), "windows")
            : null;
    }

    /// <summary>Development only: <c>--server http://localhost:5080 --code 12345678</c>.</summary>
    private static AgentSessionOptions? FromArguments(string[] args)
    {
        var server = Value(args, "--server");
        var code = Value(args, "--code");
        if (server is null || code is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new AgentSessionOptions { ServerUri = uri, Code = code, Machine = Machine(), Platform = "windows" };
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static MachineInfo Machine() => new()
    {
        MachineName = Environment.MachineName,
        OsName = "Windows",
        OsVersion = RuntimeInformation.OSDescription,
        UserName = Environment.UserName,
    };
}
