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
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
