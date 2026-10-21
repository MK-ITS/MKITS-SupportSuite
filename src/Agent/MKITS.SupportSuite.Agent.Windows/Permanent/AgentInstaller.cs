// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using MKITS.SupportSuite.Agent.Windows.Platform;
using static MKITS.SupportSuite.Agent.Windows.Native.ServiceNative;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>Parsed <c>--install</c> arguments.</summary>
internal sealed record InstallArguments(Uri Server, string? Token, bool Reenroll);

/// <summary>
/// Installs or removes the permanent agent (ADR-0022). Must run as Administrator (elevated prompt, software
/// deployment or RMM tool):
/// <code>
/// MKITS-Agent.exe --install --server https://support.example.com --token &lt;enrollment token&gt; [--reenroll]
/// MKITS-Agent.exe --uninstall
/// </code>
/// Exit codes: 0 ok, 2 invalid arguments, 5 not elevated, 6 service error, 7 file error.
/// </summary>
internal static partial class AgentInstaller
{
    private const uint ServiceAutoStart = 0x00000002;

    public static int Install(string[] args)
    {
        AttachToParentConsole();
        var parsed = ParseInstallArguments(args, File.Exists(AgentPaths.IdentityFile));
        if (parsed is null)
        {
            Console.Error.WriteLine("Usage: MKITS-Agent.exe --install --server https://<host> --token <enrollment token> [--reenroll]");
            Console.Error.WriteLine("The server must use https (http only for localhost). The token is required unless the device is already enrolled.");
            return 2;
        }

        if (!IsElevated())
        {
            Console.Error.WriteLine("Please run this command as Administrator.");
            return 5;
        }

        try
        {
            StopService();
            CopyExecutable();

            ProtectedFolder.Create(AgentPaths.DataDirectory);
            if (parsed.Reenroll && File.Exists(AgentPaths.IdentityFile))
            {
                File.Delete(AgentPaths.IdentityFile);
            }

            AgentConfig.Create(parsed.Server, parsed.Token).Save(AgentPaths.ConfigFile);
            CreateOrKeepService();
            Configure("description", AgentPaths.ServiceName, "Permanent remote maintenance connection to " + parsed.Server.Host);
            Configure("failure", AgentPaths.ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000");

            using var controller = new ServiceController(AgentPaths.ServiceName);
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Console.Error.WriteLine("Service error: " + ex.Message);
            return 6;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("File error: " + ex.Message);
            return 7;
        }

        Console.WriteLine($"The agent is installed and connects to {parsed.Server.Host}. Log: {AgentPaths.LogFile}");
        return 0;
    }

    public static int Uninstall()
    {
        AttachToParentConsole();
        if (!IsElevated())
        {
            Console.Error.WriteLine("Please run this command as Administrator.");
            return 5;
        }

        try
        {
            StopService();
            DeleteServiceIfPresent();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Console.Error.WriteLine("Service error: " + ex.Message);
            return 6;
        }

        DeleteDirectory(AgentPaths.DataDirectory);
        DeleteDirectory(AgentPaths.InstallDirectory);
        Console.WriteLine("The agent was removed. Remove the device in the web interface as well (Device administration).");
        return 0;
    }

    /// <summary>Returns <c>null</c> for invalid arguments.</summary>
    internal static InstallArguments? ParseInstallArguments(string[] args, bool alreadyEnrolled)
    {
        ArgumentNullException.ThrowIfNull(args);

        var server = AgentConfig.ParseServer(Value(args, "--server"));
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

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StopService()
    {
        using var controller = new ServiceController(AgentPaths.ServiceName);
        try
        {
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            // not installed
        }
    }

    private static void CopyExecutable()
    {
        var source = Environment.ProcessPath ?? throw new IOException("Unknown process path.");
        Directory.CreateDirectory(AgentPaths.InstallDirectory);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(AgentPaths.Executable), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, AgentPaths.Executable, overwrite: true);
        }
    }

    private static void CreateOrKeepService()
    {
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var existing = OpenService(manager, AgentPaths.ServiceName, ServiceAllAccess);
            if (existing != 0)
            {
                _ = CloseServiceHandle(existing); // reinstall / update: same binary path
                return;
            }

            var service = CreateService(
                manager, AgentPaths.ServiceName, AgentPaths.ServiceDisplayName, ServiceAllAccess, ServiceWin32OwnProcess, ServiceAutoStart,
                ServiceErrorNormal, $"\"{AgentPaths.Executable}\" --agent-service", null, 0, null, null, null);
            if (service == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _ = CloseServiceHandle(service);
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private static void DeleteServiceIfPresent()
    {
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var service = OpenService(manager, AgentPaths.ServiceName, Delete);
            if (service != 0)
            {
                _ = DeleteService(service);
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    /// <summary>Service settings without a dedicated API call here (description, recovery actions) via sc.exe.</summary>
    private static void Configure(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        process?.WaitForExit(10_000);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = MoveFileEx(file, null, MoveFileDelayUntilReboot); // e.g. the running EXE
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(path))
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = MoveFileEx(directory, null, MoveFileDelayUntilReboot);
            }
        }
    }

    /// <summary>The EXE is a GUI application; show messages in the console it was started from.</summary>
    private static void AttachToParentConsole() => _ = AttachConsole(-1);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
