// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Core.Logging;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class AgentLoggingTests
{
    [Fact]
    public void Lines_HaveLevelCategoryAndExceptionText_BelowTheMinimumNothing()
    {
        var lines = new List<string>();
        using var logs = new LineLoggerProvider(lines.Add, LogLevel.Information, timestamps: false);
        var logger = logs.CreateLogger("MKITS.SupportSuite.Agent.Core.Devices.DeviceAgent");

        logger.LogDebug("hidden");
        logger.LogInformation("Connected to {Server}", "support.example.test");
        logger.LogWarning(new IOException("connection reset"), "Cannot connect");

        Assert.Equal(["INFO DeviceAgent: Connected to support.example.test", "WARN DeviceAgent: Cannot connect (IOException: connection reset)"], lines);
    }

    [Fact]
    public void Timestamps_AreUtc_AndAFailingSinkIsIgnored()
    {
        var lines = new List<string>();
        var time = new FixedTime(new DateTimeOffset(2026, 11, 4, 12, 30, 5, TimeSpan.FromHours(1)));
        using var logs = new LineLoggerProvider(lines.Add, timeProvider: time);
        logs.CreateLogger("X").LogError("boom");
        Assert.Equal("2026-11-04 11:30:05Z ERROR X: boom", Assert.Single(lines));

        using var broken = new LineLoggerProvider(_ => throw new IOException("disk full"));
        broken.CreateLogger("X").LogError("still fine"); // must not throw
        Assert.Throws<NotSupportedException>(() => broken.AddProvider(broken));
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData(null, LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData("2", LogLevel.Information)] // numbers are not accepted – only names
    [InlineData("verbose", LogLevel.Information)]
    public void ParseLevel_AcceptsNamesOnly(string? value, LogLevel expected)
    {
        Assert.Equal(expected, LineLoggerProvider.ParseLevel(value));
    }

    [Fact]
    public async Task DeviceAgent_ConnectionFailure_IsLoggedWithTheReason()
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var logs = new LineLoggerProvider(lines.Enqueue, timestamps: false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new DeviceAgentOptions
        {
            ServerUri = new Uri("https://support.example.test"),
            EnrollmentToken = "token-never-logged",
            Machine = new MachineInfo { MachineName = "pc", OsName = "Test", OsVersion = "1" },
            Platform = "linux",
            MinReconnectDelay = TimeSpan.FromMinutes(5),
            LoggerFactory = logs,
        };
        await using var agent = new DeviceAgent(options, new FailingConnector(stop), new EmptyStore(), new SilentUi(), []);

        await agent.RunAsync(stop.Token);

        var line = Assert.Single(lines);
        Assert.StartsWith("WARN DeviceAgent: Cannot connect to wss://support.example.test/ws/agent", line, StringComparison.Ordinal);
        Assert.Contains("WebSocketException", line, StringComparison.Ordinal);
        Assert.DoesNotContain("token-never-logged", line, StringComparison.Ordinal);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>Fails like an unreachable server, then stops the agent (it would wait for the retry).</summary>
    private sealed class FailingConnector(CancellationTokenSource stop) : IWebSocketConnector
    {
        public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            stop.CancelAfter(TimeSpan.FromMilliseconds(100));
            throw new WebSocketException("Unable to connect to the remote server");
        }
    }

    private sealed class EmptyStore : IDeviceIdentityStore
    {
        public DeviceIdentity? Load() => null;

        public void Save(DeviceIdentity identity)
        {
        }

        public void Delete()
        {
        }
    }

    private sealed class SilentUi : IAgentUserInterface
    {
        public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken) => Task.FromResult(false);

        public void OnStatusChanged(AgentStatus status, string? detail)
        {
        }

        public void OnSupportersChanged(IReadOnlyList<string> displayNames)
        {
        }

        public void OnSecurityCode(string supporterDisplayName, string securityCode)
        {
        }
    }
}
