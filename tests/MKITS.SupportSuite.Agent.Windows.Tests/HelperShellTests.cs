// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text;
using MKITS.SupportSuite.Agent.Windows.Elevation;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

/// <summary>Elevated shells through the helper (ADR-0028). In tests the "helper" is this process, so no rights change.</summary>
public sealed class HelperShellTests
{
    [Fact]
    public async Task ShellThroughHelper_RunsCommands_AndReportsTheExitCode()
    {
        using var shells = new HelperShells(Environment.ProcessId);
        await using var helper = await LoopbackHelper.StartAsync(Handler(shells));
        var host = new HelperShellHost(() => helper.Client);
        await using var shell = host.Start("cmd", 120, 30);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            int read;
            while ((read = await shell.Output.ReadAsync(buffer, timeout.Token)) > 0)
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
        });

        await shell.WriteAsync(Encoding.UTF8.GetBytes("echo helper-^ok\r\n"), timeout.Token);
        while (!Snapshot(output).Contains("helper-ok", StringComparison.Ordinal))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, CancellationToken.None);
        }

        shell.Resize(100, 40); // goes through the helper pipe
        await shell.WriteAsync(Encoding.UTF8.GetBytes("exit 5\r\n"), timeout.Token);

        Assert.Equal(5, await shell.Exited.WaitAsync(timeout.Token));
        await reader.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Helper_RefusesUnknownTypes_AndTooManyShells()
    {
        using var shells = new HelperShells(Environment.ProcessId);
        await using var helper = await LoopbackHelper.StartAsync(Handler(shells));

        Assert.Null(helper.Client.StartShell("bash", 80, 24));
        Assert.Null(helper.Client.StartShell("cmd.exe /c calc", 80, 24));

        var started = new List<HelperShellProcess>();
        try
        {
            for (var i = 0; i < HelperShells.MaxShells; i++)
            {
                started.Add(helper.Client.StartShell("cmd", 80, 24) ?? throw new InvalidOperationException($"shell {i} not started"));
            }

            Assert.Null(helper.Client.StartShell("cmd", 80, 24));

            // Closing one frees a slot.
            await started[0].DisposeAsync();
            _ = helper.Client.GetMonitors(); // a request with a response: the close before it has been handled
            started[0] = helper.Client.StartShell("powershell", 80, 24) ?? throw new InvalidOperationException("slot not freed");
        }
        finally
        {
            foreach (var shell in started)
            {
                await shell.DisposeAsync();
            }
        }
    }

    [Fact]
    public void Host_WithoutHelper_Refuses()
    {
        var host = new HelperShellHost(() => null);

        Assert.Equal(["powershell", "cmd"], host.AvailableShells);
        Assert.Throws<InvalidOperationException>(() => host.Start("cmd", 80, 24));
    }

    private static HelperRequestHandler Handler(IHelperShells shells) =>
        new(new HelperStreamingTests.FakeSource(), new HelperStreamingTests.FakeInput(), () => { }, shells);

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}
