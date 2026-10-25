// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Bridge;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// The installed agent (<c>--agent-service</c>, systemd unit <c>mkits-agent.service</c>, root; ADR-0025). Holds the
/// permanent connection (<see cref="DeviceAgent"/>), the device identity and the end-to-end keys. Screen and input
/// go to the X display of whoever is logged on (<see cref="SessionDesktop"/>); chat, clipboard and files to the
/// session window in that user's session (<see cref="UserSessionBridge"/>). The shell runs here, as root – the
/// typical use on servers, which usually have no graphical session (permission "shell" in the device group).
/// </summary>
internal static class AgentService
{
    /// <summary>Exit code for problems a restart cannot fix (systemd: <c>RestartPreventExitStatus</c>).</summary>
    internal const int StopForGood = 78; // EX_CONFIG

    /// <summary>Forwarded to the user's session; the shell is not (it runs in the service).</summary>
    internal static readonly Channel[] UserChannels = [Channel.Chat, Channel.Clipboard, Channel.File];

    public static async Task<int> RunAsync()
    {
        if (!HostSystem.IsRoot)
        {
            Console.Error.WriteLine("--agent-service runs as the mkits-agent systemd service (root).");
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true; // shut down cleanly instead of being killed
            stopping.Cancel();
        });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });

        AgentLog.Write($"service starting ({Common.ProductInfo.Version})");
        var config = AgentConfig.Load(AgentPaths.ConfigFile);
        if (config is null || ServerAddress.Parse(config.ServerUri) is not { } server)
        {
            AgentLog.Write($"no valid configuration in {AgentPaths.ConfigFile} – run: sudo mkits-agent --install --server https://<host> --token <token>");
            return StopForGood;
        }

        try
        {
            Directory.CreateDirectory(AgentPaths.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var store = new EnrollmentAwareStore(
                new FileDeviceIdentityStore(AgentPaths.IdentityFile),
                onSaved: () => new AgentConfig(config.ServerUri, null).Save(AgentPaths.ConfigFile)); // the token is not needed any more

            var options = new DeviceAgentOptions
            {
                ServerUri = server,
                EnrollmentToken = config.Token,
                Machine = new MachineInfo
                {
                    MachineName = Environment.MachineName,
                    OsName = "Linux",
                    OsVersion = RuntimeInformation.OSDescription,
                },
                Platform = "linux",
            };

            using var desktop = new SessionDesktop();
            await using var bridge = new UserSessionBridge(new UserSessionConnector(), new HeadlessUserInterface(), UserChannels);
            await using var streamer = new ScreenStreamer(desktop);
            await using var shells = new ShellHandler(new PtyShellHost());
            await using var agent = new DeviceAgent(
                options, new ClientWebSocketConnector(), store, bridge,
                [streamer, new InputHandler(desktop, desktop), bridge, shells]);
            bridge.EndSessionRequested += (_, _) => _ = agent.EndSessionAsync();

            var result = await agent.RunAsync(stopping.Token).ConfigureAwait(false);
            if (result != DeviceAgentStopReason.Cancelled)
            {
                AgentLog.Write($"agent stopped: {result} – run --install again with a valid enrollment token");
                return StopForGood;
            }

            AgentLog.Write("service stopped");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Write($"agent failed: {ex.GetType().Name}: {ex.Message}");
            return 1; // systemd restarts the service (Restart=on-failure)
        }
    }

    /// <summary>Removes the enrollment token from the configuration as soon as the identity is stored.</summary>
    private sealed class EnrollmentAwareStore(IDeviceIdentityStore inner, Action onSaved) : IDeviceIdentityStore
    {
        public DeviceIdentity? Load() => inner.Load();

        public void Save(DeviceIdentity identity)
        {
            inner.Save(identity);
            AgentLog.Write($"enrolled as device {identity.DeviceId}");
            onSaved();
        }

        public void Delete() => inner.Delete();
    }
}
