// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using System.ServiceProcess;
using MKITS.SupportSuite.Agent.Core.Bridge;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// The installed agent (<c>--agent-service</c>, LocalSystem, automatic start; ADR-0022). Holds the permanent connection
/// to the server (<see cref="DeviceAgent"/>), the device identity (DPAPI, protected folder) and the end-to-end keys.
/// Screen and input go through the on-demand helper in the console session; chat, clipboard, files and shell through
/// the session window in the logged-on user's session (<see cref="UserSessionBridge"/>).
/// </summary>
internal sealed class AgentService : ServiceBase
{
    private const string IdentityPurpose = "agent-device-identity";

    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public AgentService() => ServiceName = AgentPaths.ServiceName;

    public static int Run()
    {
        using var service = new AgentService();
        Run(service);
        return 0;
    }

    protected override void OnStart(string[] args)
    {
        AgentLog.Path = AgentPaths.LogFile;
        AgentLog.Write($"service starting ({Common.ProductInfo.Version})");
        _worker = Task.Run(() => RunAsync(_stopping.Token));
    }

    protected override void OnStop()
    {
        AgentLog.Write("service stopping");
        _stopping.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException)
        {
            // logged by the worker
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = AgentConfig.Load(AgentPaths.ConfigFile);
            if (config is null || AgentConfig.ParseServer(config.ServerUri) is not { } server)
            {
                AgentLog.Write("no valid configuration – reinstall the agent with --install");
                StopForGood();
                return;
            }

            var store = new EnrollmentAwareStore(
                new FileDeviceIdentityStore(AgentPaths.IdentityFile, data => Dpapi.Protect(data, IdentityPurpose), data => Dpapi.Unprotect(data, IdentityPurpose)),
                onSaved: () => new AgentConfig(config.ServerUri, null).Save(AgentPaths.ConfigFile)); // the token is not needed any more

            using var helper = new OnDemandHelper();
            var options = new DeviceAgentOptions
            {
                ServerUri = server,
                EnrollmentToken = config.ReadToken(),
                Machine = new MachineInfo
                {
                    MachineName = Environment.MachineName,
                    OsName = "Windows",
                    OsVersion = RuntimeInformation.OSDescription,
                },
                Platform = "windows",
            };

            // Chat, clipboard, files and shell run in the user's session with the user's rights; the bridge also shows the
            // session window there. Without a logged-on user the service's message boxes are the fallback.
            await using var bridge = new UserSessionBridge(new UserSessionConnector(), new ServiceUserInterface(helper));
            await using var streamer = new ScreenStreamer(helper);
            await using var agent = new DeviceAgent(
                options, new ClientWebSocketConnector(), store, bridge,
                [streamer, new InputHandler(helper, helper), bridge]);
            bridge.EndSessionRequested += (_, _) => _ = agent.EndSessionAsync();

            var result = await agent.RunAsync(cancellationToken).ConfigureAwait(false);
            if (result != DeviceAgentStopReason.Cancelled)
            {
                AgentLog.Write($"agent stopped: {result} – reinstall with a valid enrollment token");
                StopForGood();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentLog.Write($"agent failed: {ex.GetType().Name}: {ex.Message}");
            ExitCode = 1;
            Stop(); // non-zero exit code: the recovery actions (failureflag) restart the service
        }
    }

    /// <summary>
    /// Stops without triggering the recovery actions (exit code 0): retrying cannot help (invalid token, no
    /// configuration) and would hammer the server – and invalid tokens count for its IP ban.
    /// </summary>
    private void StopForGood()
    {
        ExitCode = 0;
        Stop();
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
