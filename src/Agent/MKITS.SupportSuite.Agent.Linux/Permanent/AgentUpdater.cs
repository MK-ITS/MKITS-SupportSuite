// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Core.Updates;
using MKITS.SupportSuite.Common;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// Automatic update of the installed Linux agent (ADR-0023, ADR-0025). Same rules as on Windows: checked every few
/// hours, applied only if the manifest is signed by the release key compiled into this agent, the package matches the
/// signed SHA-256, and no session is running. The verified package is installed by the package manager in a transient
/// systemd unit – outside this service's control group, so stopping the service during the upgrade (the package's
/// postinst restarts it) does not kill the installation.
/// </summary>
internal static class AgentUpdater
{
    /// <summary>Update channel of the Debian package for this architecture (server: <c>/api/agent-updates/linux-x64-deb</c>).</summary>
    public static string Channel { get; } = ChannelFor(RuntimeInformation.ProcessArchitecture);

    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Runs in the service until cancelled.</summary>
    public static async Task RunAsync(Uri server, DeviceAgent agent, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new AgentUpdateClient(http, server, Channel, ProductInfo.BuildNumber, ProductInfo.AgentUpdatePublicKey);
        if (!client.Enabled)
        {
            AgentLog.Write("automatic updates disabled (build without release key or local build)");
            return;
        }

        TryDelete(AgentPaths.StagedUpdate);
        try
        {
            await Task.Delay(FirstCheck + Jitter(), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckOnceAsync(client, agent, cancellationToken).ConfigureAwait(false);
                await Task.Delay(Interval + Jitter(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // service stopping
        }
    }

    internal static string ChannelFor(Architecture architecture) => architecture == Architecture.Arm64 ? "linux-arm64-deb" : "linux-x64-deb";

    private static async Task CheckOnceAsync(AgentUpdateClient client, DeviceAgent agent, CancellationToken cancellationToken)
    {
        try
        {
            if (agent.SessionId is not null || await client.CheckAsync(cancellationToken).ConfigureAwait(false) is not { } manifest)
            {
                return;
            }

            AgentLog.Write($"update available: {manifest}");
            Directory.CreateDirectory(AgentPaths.UpdateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (!await client.DownloadAsync(manifest, AgentPaths.StagedUpdate, cancellationToken).ConfigureAwait(false))
            {
                AgentLog.Write("update download did not match the signed manifest – discarded");
                return;
            }

            if (agent.SessionId is not null)
            {
                return; // a session started meanwhile – next time
            }

            AgentLog.Write("update verified – installing");
            if (!StartInstallation(AgentPaths.StagedUpdate, manifest.Build))
            {
                TryDelete(AgentPaths.StagedUpdate);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            AgentLog.Write($"update check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>systemd-run --unit=mkits-agent-update-&lt;build&gt; --collect apt-get install -y &lt;package&gt;</c>
    /// (internal for tests). apt resolves new dependencies; the package's postinst restarts this service.
    /// </summary>
    internal static ProcessStartInfo InstallCommand(string package, long build)
    {
        var start = new ProcessStartInfo("systemd-run") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[]
        {
            $"--unit=mkits-agent-update-{build}", "--collect", "--quiet",
            "--setenv=DEBIAN_FRONTEND=noninteractive",
            "/usr/bin/apt-get", "install", "-y", "-q", "-o", "Dpkg::Options::=--force-confold", package,
        })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static bool StartInstallation(string package, long build)
    {
        try
        {
            using var process = Process.Start(InstallCommand(package, build));
            if (process is null)
            {
                return false;
            }

            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)) || process.ExitCode != 0)
            {
                AgentLog.Write("could not start the package installation: " + error.GetAwaiter().GetResult().Trim());
                return false;
            }

            AgentLog.Write($"package installation started (unit mkits-agent-update-{build}; log: journalctl -u mkits-agent-update-{build})");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AgentLog.Write("systemd-run not available: " + ex.Message);
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // next time
        }
    }

    /// <summary>Spreads the checks of many devices.</summary>
    private static TimeSpan Jitter() => TimeSpan.FromSeconds(Random.Shared.Next(0, 1800));
}
