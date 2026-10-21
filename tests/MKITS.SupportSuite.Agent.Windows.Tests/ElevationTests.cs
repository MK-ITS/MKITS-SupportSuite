// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Windows.Elevation;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

public sealed class ElevationTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("..\\..\\Windows\\System32\\aaaaaaa", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData(null, false)]
    public void IsValidId_OnlyAcceptsGuidHex(string? id, bool valid)
    {
        Assert.Equal(valid, ElevatedSessionConfig.IsValidId(id));
    }

    [Fact]
    public async Task Protocol_RoundTrip_AndRejectsOversizedMessages()
    {
        using var stream = new MemoryStream();
        var message = HelperProtocol.Build(HelperRequest.Key, w =>
        {
            w.Write("KeyA");
            w.Write(true);
        });

        await HelperProtocol.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(message, await HelperProtocol.ReadAsync(stream, CancellationToken.None));
        Assert.Null(await HelperProtocol.ReadAsync(stream, CancellationToken.None)); // end of stream

        using var hostile = new MemoryStream(BitConverter.GetBytes(HelperProtocol.MaxMessageSize + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync(hostile, CancellationToken.None));
    }

    [Fact]
    public void Installer_RejectsInvalidArguments()
    {
        Assert.NotEqual(0, ElevationInstaller.Run(["--elevate", "not-an-id", "1", "S-1-5-18"]));
        Assert.NotEqual(0, ElevationInstaller.Run(["--elevate", Guid.NewGuid().ToString("N"), "abc", "S-1-5-18"]));
        Assert.NotEqual(0, ElevationInstaller.Run(["--elevate", Guid.NewGuid().ToString("N"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "not-a-sid"]));
    }

    [Fact]
    public void Installer_RejectsProcessOfAnotherAccount()
    {
        // LocalSystem is never the account of the test process.
        Assert.Equal(12, ElevationInstaller.Run(["--elevate", Guid.NewGuid().ToString("N"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "S-1-5-18"]));
    }
}
