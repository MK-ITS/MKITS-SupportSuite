// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text;
using MKITS.SupportSuite.Agent.Windows.Platform;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

public sealed class ConPtyShellTests
{
    [Fact]
    public async Task Cmd_RunsCommand_AndReportsExitCode()
    {
        var host = new ConPtyShellHost();
        await using var shell = host.Start("cmd", 120, 30);
        var output = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

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

        await shell.WriteAsync(Encoding.UTF8.GetBytes("echo mkits-%COMPUTERNAME:~0,0%ok\r\n"), timeout.Token);
        while (!Contains(output, "mkits-ok"))
        {
            await Task.Delay(100, timeout.Token);
        }

        shell.Resize(100, 40);
        await shell.WriteAsync(Encoding.UTF8.GetBytes("exit 7\r\n"), timeout.Token);

        Assert.Equal(7, await shell.Exited.WaitAsync(timeout.Token));
        _ = reader;
    }

    [Fact]
    public void Host_OffersPowerShellAndCmd_AndRefusesOthers()
    {
        var host = new ConPtyShellHost();

        Assert.Equal(["powershell", "cmd"], host.AvailableShells);
        Assert.Throws<InvalidOperationException>(() => host.Start("bash", 80, 24));
    }

    private static bool Contains(StringBuilder output, string text)
    {
        lock (output)
        {
            return output.ToString().Contains(text, StringComparison.Ordinal);
        }
    }
}
