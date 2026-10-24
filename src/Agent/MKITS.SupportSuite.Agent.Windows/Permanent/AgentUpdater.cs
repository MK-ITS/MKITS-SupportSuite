// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.ServiceProcess;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Core.Updates;
using MKITS.SupportSuite.Common;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// Automatic update of the installed agent (ADR-0023). The service checks the server every few hours; a newer build
/// is applied only if its manifest is signed by the release key compiled into this agent, the file matches the signed
/// SHA-256, and no session is running. The new EXE then replaces the installed one (<c>--apply-update</c>) and the
/// service restarts; if it does not come up, the previous EXE is restored.
/// </summary>
internal static class AgentUpdater
{
    public const string Platform = "windows-x64";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Runs in the service until cancelled.</summary>
    public static async Task RunAsync(Uri server, DeviceAgent agent, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new AgentUpdateClient(http, server, Platform, ProductInfo.BuildNumber, ProductInfo.AgentUpdatePublicKey);
        if (!client.Enabled)
        {
            AgentLog.Write("automatic updates disabled (build without release key or local build)");
            return;
        }

        CleanUp();
        await Task.Delay(FirstCheck + Jitter(), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (agent.SessionId is null && await client.CheckAsync(cancellationToken).ConfigureAwait(false) is { } manifest)
                {
                    AgentLog.Write($"update available: {manifest}");
                    Directory.CreateDirectory(AgentPaths.UpdateDirectory);
                    if (await client.DownloadAsync(manifest, AgentPaths.StagedUpdate, cancellationToken).ConfigureAwait(false) && agent.SessionId is null)
                    {
                        AgentLog.Write("update verified – applying");
                        Process.Start(new ProcessStartInfo(AgentPaths.StagedUpdate, "--apply-update") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                        return; // the updater stops this service
                    }

                    AgentLog.Write("update download did not match the signed manifest – discarded");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                AgentLog.Write($"update check failed: {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(Interval + Jitter(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>--apply-update</c>: started by the service from the protected update folder. Stops the service, replaces the
    /// installed EXE (keeping the previous one), starts the service and rolls back if it does not reach <i>Running</i>.
    /// </summary>
    public static int Apply()
    {
        AgentLog.Path = AgentPaths.LogFile;
        if (!string.Equals(Environment.ProcessPath, AgentPaths.StagedUpdate, StringComparison.OrdinalIgnoreCase))
        {
            return 2; // only the staged, verified copy may replace the installed agent
        }

        using var service = new ServiceController(AgentPaths.ServiceName);
        try
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
            }

            File.Copy(AgentPaths.Executable, AgentPaths.BackupExecutable, overwrite: true);
            File.Copy(AgentPaths.StagedUpdate, AgentPaths.Executable, overwrite: true);
            if (TryStart(service))
            {
                AgentLog.Write($"updated to build {ProductInfo.BuildNumber} ({ProductInfo.Version})");
                return 0;
            }

            AgentLog.Write("updated service did not start – restoring the previous version");
            File.Copy(AgentPaths.BackupExecutable, AgentPaths.Executable, overwrite: true);
            return TryStart(service) ? 3 : 4;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ServiceProcess.TimeoutException)
        {
            AgentLog.Write($"update failed: {ex.GetType().Name}: {ex.Message}");
            if (File.Exists(AgentPaths.BackupExecutable))
            {
                try
                {
                    File.Copy(AgentPaths.BackupExecutable, AgentPaths.Executable, overwrite: true);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    AgentLog.Write("restoring the previous version failed: " + restoreError.Message);
                }
            }

            return TryStart(service) ? 5 : 6;
        }
    }

    private static bool TryStart(ServiceController service)
    {
        try
        {
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
            Thread.Sleep(TimeSpan.FromSeconds(10)); // still running a little later (no immediate crash)?
            service.Refresh();
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Leftovers of a previous update (the updater cannot delete its own EXE while it runs).</summary>
    private static void CleanUp()
    {
        foreach (var file in new[] { AgentPaths.StagedUpdate, AgentPaths.StagedUpdate + ".download", AgentPaths.BackupExecutable })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still in use – next time
            }
        }
    }

    /// <summary>Spreads the checks of many devices.</summary>
    private static TimeSpan Jitter() => TimeSpan.FromSeconds(Random.Shared.Next(0, 1800));
}
