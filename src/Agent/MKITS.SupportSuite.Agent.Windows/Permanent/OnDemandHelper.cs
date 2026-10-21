// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.Security.Principal;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Elevation;
using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// Screen capture and input for the agent service, which runs in session 0 and cannot see the desktop itself
/// (ADR-0022). The privileged helper (<c>--agent-helper</c>, the same code as in the elevated ad-hoc mode) is started
/// as SYSTEM in the active console session <b>only while a session needs it</b>, and replaced when the console
/// session changes (fast user switching, RDP to console). Ctrl+Alt+Del requests of the helper are served here.
/// </summary>
internal sealed class OnDemandHelper : IScreenSource, IInputInjector, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private Running? _running;
    private DateTime _lastFailure = DateTime.MinValue;

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => Helper()?.GetMonitors() ?? [];

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken) =>
        Helper()?.Capture(monitorId, quality, cancellationToken);

    public void MovePointer(int x, int y) => Helper()?.MovePointer(x, y);

    public void SetButton(PointerButtons button, bool down) => Helper()?.SetButton(button, down);

    public void Wheel(int deltaX, int deltaY) => Helper()?.Wheel(deltaX, deltaY);

    public void Key(string code, bool down) => Helper()?.Key(code, down);

    public void Text(string text) => Helper()?.Text(text);

    public void Special(SpecialKeyKind kind) => Helper()?.Special(kind);

    /// <summary>The session ended: stop the helper (it is started again for the next session).</summary>
    public void Release()
    {
        Running? running;
        lock (_gate)
        {
            running = _running;
            _running = null;
        }

        running?.Dispose();
    }

    public void Dispose() => Release();

    /// <summary>A connected helper in the current console session, started if necessary; <c>null</c> if not possible right now.</summary>
    private HelperClient? Helper()
    {
        lock (_gate)
        {
            var console = ConsoleSessionLauncher.ActiveSessionId;
            if (_running is { } current)
            {
                if (!current.Client.IsBroken && current.SessionId == console)
                {
                    return current.Client;
                }

                AgentLog.Write(current.Client.IsBroken ? "helper connection lost" : "console session changed – restarting helper");
                current.Dispose();
                _running = null;
            }

            if (console == ConsoleSessionLauncher.NoSession || DateTime.UtcNow - _lastFailure < RetryDelay)
            {
                return null;
            }

            _running = Start();
            if (_running is null)
            {
                _lastFailure = DateTime.UtcNow;
            }

            return _running?.Client;
        }
    }

    private static Running? Start()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = AgentPaths.HelperDirectory(id);
        var config = new ElevatedSessionConfig(
            id, HelperClient.PipeNameFor(id), Environment.ProcessId, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
        config.Save(directory);

        var sas = new CancellationTokenSource();
        _ = Task.Run(() => SasServer.ServeAsync(id, sas.Token));

        var started = ConsoleSessionLauncher.Start(AgentPaths.Executable, $"--agent-helper {id}");
        if (started is not { } helper)
        {
            AgentLog.Write("could not start the helper in the console session");
            Cleanup(directory, sas);
            return null;
        }

        var client = HelperClient.ConnectAsync(config.PipeName, AgentPaths.Executable, ConnectTimeout, CancellationToken.None).GetAwaiter().GetResult();
        if (client is null)
        {
            AgentLog.Write("could not connect to the helper");
            TryKill(helper.Process);
            helper.Process.Dispose();
            Cleanup(directory, sas);
            return null;
        }

        AgentLog.Write($"helper started in console session {helper.SessionId}");
        return new Running(client, helper.Process, helper.SessionId, directory, sas);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    private static void Cleanup(string directory, CancellationTokenSource sas)
    {
        sas.Cancel();
        sas.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // removed with the next uninstall
        }
    }

    private sealed class Running(HelperClient client, Process process, uint sessionId, string directory, CancellationTokenSource sas) : IDisposable
    {
        public HelperClient Client { get; } = client;

        public uint SessionId { get; } = sessionId;

        public void Dispose()
        {
            Client.Shutdown();
            Client.Dispose();
            if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                TryKill(process);
            }

            process.Dispose();
            Cleanup(directory, sas);
        }
    }
}
