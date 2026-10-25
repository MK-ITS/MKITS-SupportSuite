// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using MKITS.SupportSuite.Agent.Core.Devices;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>Parsed <c>--install</c> arguments.</summary>
internal sealed record InstallArguments(Uri Server, string? Token, bool Reenroll);

/// <summary>
/// Configures the installed agent (ADR-0025). The <c>.deb</c> package installs the program and the systemd unit;
/// this connects it to a server. Must run as root:
/// <code>
/// sudo mkits-agent --install --server https://support.example.com --token &lt;enrollment token&gt; [--reenroll]
/// sudo mkits-agent --uninstall
/// </code>
/// Exit codes: 0 ok, 2 invalid arguments, 5 not root, 6 systemd error, 7 file error.
/// </summary>
internal static class AgentInstaller
{
    public static int Install(string[] args)
    {
        var parsed = ParseInstallArguments(args, File.Exists(AgentPaths.IdentityFile));
        if (parsed is null)
        {
            Console.Error.WriteLine("Usage: sudo mkits-agent --install --server https://<host> --token <enrollment token> [--reenroll]");
            Console.Error.WriteLine("The server must use https (http only for localhost). The token is required unless the device is already enrolled.");
            return 2;
        }

        if (!HostSystem.IsRoot)
        {
            Console.Error.WriteLine("Please run this command as root (sudo).");
            return 5;
        }

        if (!File.Exists(AgentPaths.Executable))
        {
            Console.Error.WriteLine($"{AgentPaths.Executable} not found – install the mkits-agent package first.");
            return 7;
        }

        try
        {
            if (parsed.Reenroll && File.Exists(AgentPaths.IdentityFile))
            {
                File.Delete(AgentPaths.IdentityFile);
            }

            new AgentConfig(parsed.Server.ToString(), parsed.Token).Save(AgentPaths.ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("File error: " + ex.Message);
            return 7;
        }

        // enable (start at boot) and (re)start with the new configuration
        if (!Systemctl("enable", AgentPaths.ServiceName) || !Systemctl("restart", AgentPaths.ServiceName))
        {
            return 6;
        }

        Console.WriteLine($"The agent is installed and connects to {parsed.Server.Host}. Log: journalctl -u mkits-agent");
        return 0;
    }

    public static int Uninstall()
    {
        if (!HostSystem.IsRoot)
        {
            Console.Error.WriteLine("Please run this command as root (sudo).");
            return 5;
        }

        // The package keeps the unit; without configuration the service would only stop again.
        _ = Systemctl("disable", "--now", AgentPaths.ServiceName);
        foreach (var folder in new[] { AgentPaths.ConfigDirectory, AgentPaths.DataDirectory })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("File error: " + ex.Message);
                return 7;
            }
        }

        Console.WriteLine("The agent was removed from this server. Remove the device in the web interface as well (Device administration).");
        Console.WriteLine("To remove the program: sudo apt remove mkits-agent");
        return 0;
    }

    /// <summary>Returns <c>null</c> for invalid arguments.</summary>
    internal static InstallArguments? ParseInstallArguments(string[] args, bool alreadyEnrolled)
    {
        ArgumentNullException.ThrowIfNull(args);

        var server = ServerAddress.Parse(Value(args, "--server"));
        var token = Value(args, "--token");
        var reenroll = args.Contains("--reenroll", StringComparer.Ordinal);
        if (server is null || token is { Length: > 100 } || (string.IsNullOrWhiteSpace(token) && (!alreadyEnrolled || reenroll)))
        {
            return null;
        }

        return new InstallArguments(server, string.IsNullOrWhiteSpace(token) ? null : token.Trim(), reenroll);
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool Systemctl(params string[] arguments)
    {
        var start = new ProcessStartInfo("systemctl") { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"systemctl {string.Join(' ', arguments)} failed ({process.ExitCode}).");
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("systemctl not available: " + ex.Message);
            return false;
        }
    }
}
