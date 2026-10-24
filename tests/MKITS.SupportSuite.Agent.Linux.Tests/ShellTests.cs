// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text;
using MKITS.SupportSuite.Agent.Linux.Platform;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

public sealed class ShellTests
{
    [Fact]
    public void AvailableShells_ContainsSh()
    {
        Assert.Contains("sh", new PtyShellHost().AvailableShells);
    }

    [Fact]
    public async Task Shell_RunsOnATerminalOfTheRequestedSize_AndReportsTheExitCode()
    {
        var host = new PtyShellHost();
        var type = host.AvailableShells[0];
        await using var shell = host.Start(type, 100, 30);

        await shell.WriteAsync(Encoding.UTF8.GetBytes("stty size; tty; echo marker-$((6*7)); exit 3\n"), CancellationToken.None);
        var output = await ReadAllAsync(shell.Output);

        Assert.Equal(3, await shell.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("marker-42", output, StringComparison.Ordinal);
        Assert.Contains("30 100", output, StringComparison.Ordinal);
        Assert.Contains("/dev/pts/", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_EndsARunningShell()
    {
        var host = new PtyShellHost();
        var shell = host.Start(host.AvailableShells[0], 80, 24);
        await shell.WriteAsync("sleep 60\n"u8.ToArray(), CancellationToken.None);

        await shell.DisposeAsync();

        Assert.True(shell.Exited.IsCompleted || await Task.WhenAny(shell.Exited, Task.Delay(5000)) == shell.Exited);
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new StringBuilder();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return output.ToString();
    }
}
