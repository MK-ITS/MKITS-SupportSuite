// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using static MKITS.SupportSuite.Agent.Windows.Native.ServiceNative;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>
/// The temporary service of an elevated ad-hoc session (<c>--service &lt;id&gt;</c>, LocalSystem). It starts the privileged
/// helper in the active console session, restarts it when the console session changes, raises Ctrl+Alt+Del on request
/// (SendSAS only works from a service) and removes itself and its files when the session is over: when the helper
/// reports that the client is gone, or the client process no longer exists.
/// </summary>
internal sealed class AdHocService(ElevatedSessionConfig config) : ServiceBase
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public static int Run(string id)
    {
        using var service = new AdHocService(ElevatedSessionConfig.Load(id)) { ServiceName = ElevatedSessionConfig.ServicePrefix + id };
        Run(service);
        return 0;
    }

    protected override void OnStart(string[] args)
    {
        _worker = Task.Run(() => SuperviseAsync(_stopping.Token));
        _ = Task.Run(() => ServeSasAsync(_stopping.Token));
    }

    protected override void OnStop()
    {
        _stopping.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // already logged by the worker
        }

        Uninstall();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested && ClientAlive() && failures < 5)
        {
            using var helper = StartHelperInConsoleSession();
            if (helper is null)
            {
                failures++;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await helper.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (helper.ExitCode == 0)
            {
                break; // the client ended the elevated session
            }

            failures++; // e.g. console session switched (fast user switching) – start a new helper
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Stop();
        }
    }

    private bool ClientAlive()
    {
        try
        {
            using var client = Process.GetProcessById(config.ClientProcessId);
            return !client.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Starts <c>--helper</c> as SYSTEM on the interactive desktop of the console session.</summary>
    private Process? StartHelperInConsoleSession() =>
        Platform.ConsoleSessionLauncher.Start(ElevatedSessionConfig.ExecutableFor(config.Id), $"--helper {config.Id}")?.Process;

    private Task ServeSasAsync(CancellationToken cancellationToken) => SasServer.ServeAsync(config.Id, cancellationToken);

    /// <summary>Marks the service for deletion and removes the copied files (the running EXE is deleted at the next reboot).</summary>
    private void Uninstall()
    {
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager != 0)
        {
            var service = OpenService(manager, ServiceName, Delete);
            if (service != 0)
            {
                _ = DeleteService(service);
                _ = CloseServiceHandle(service);
            }

            _ = CloseServiceHandle(manager);
        }

        var directory = ElevatedSessionConfig.DirectoryFor(config.Id);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = MoveFileEx(file, null, MoveFileDelayUntilReboot);
            }
        }

        _ = MoveFileEx(directory, null, MoveFileDelayUntilReboot);
    }
}

/// <summary>Raises Ctrl+Alt+Del for a helper (SendSAS only works from a LocalSystem service). The pipe is SYSTEM-only.</summary>
internal static class SasServer
{
    public static async Task ServeAsync(string id, CancellationToken cancellationToken)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(SasClient.PipeName(id), PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 1)
                {
                    SendSAS(false);
                }
            }
            catch (IOException)
            {
                // client disconnected early – wait for the next request
            }
        }
    }
}

/// <summary>The helper's side of the Ctrl+Alt+Del request.</summary>
internal static class SasClient
{
    public static string PipeName(string id) => $"MKITS-SAS-{id}";

    public static string? CurrentId { get; set; }

    public static void Request()
    {
        if (CurrentId is null)
        {
            return;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(CurrentId), PipeDirection.Out);
            pipe.Connect(2000);
            pipe.WriteByte(1);
        }
        catch (Exception ex) when (ex is IOException or System.TimeoutException or UnauthorizedAccessException)
        {
            // the service is not reachable – nothing else can raise the SAS
        }
    }
}
