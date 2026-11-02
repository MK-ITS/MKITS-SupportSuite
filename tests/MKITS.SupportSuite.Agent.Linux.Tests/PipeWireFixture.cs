// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

/// <summary>
/// A private session bus and PipeWire (own runtime folder) with WirePlumber and a GStreamer test source
/// (<c>videotestsrc pattern=red</c>, 320x240 BGRx) as node <see cref="SourceNode"/> – a stand-in for a compositor's screen stream.
/// </summary>
public sealed class PipeWireFixture : IDisposable
{
    public const string SourceNode = "mkits-test-source";

    private readonly List<Process> _processes = [];

    public PipeWireFixture()
    {
        RuntimeDirectory = Path.Combine(Path.GetTempPath(), "mkits-pw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Socket = Path.Combine(RuntimeDirectory, "pipewire-0");
        BusAddress = "unix:path=" + Path.Combine(RuntimeDirectory, "bus");

        string[] tools = ["/usr/bin/pipewire", "/usr/bin/pw-cli", "/usr/bin/wireplumber", "/usr/bin/gst-launch-1.0", "/usr/bin/dbus-daemon"];
        if (!tools.All(File.Exists))
        {
            return; // the tests then fail with a clear message
        }

        var bus = BusAddress;
        Start("/usr/bin/dbus-daemon", "--session", "--nofork", "--nopidfile", "--address=" + bus);
        BusReady = Wait(() => File.Exists(Path.Combine(RuntimeDirectory, "bus")), 10);
        Start("/usr/bin/pipewire");
        Wait(() => File.Exists(Socket), 30);
        Start("/usr/bin/wireplumber");
        Wait(() => PipeWireList("Client").Contains("wireplumber", StringComparison.OrdinalIgnoreCase), 30); // the session manager links streams
        Start("/usr/bin/gst-launch-1.0", "videotestsrc", "is-live=true", "pattern=red", "!",
            "video/x-raw,format=BGRx,width=320,height=240,framerate=30/1", "!",
            "pipewiresink", "mode=provide", "stream-properties=p,node.name=" + SourceNode + ",media.class=Video/Source");
        Available = Wait(() => PipeWireList("Node").Contains(SourceNode, StringComparison.Ordinal), 60); // busy CI runners can be slow

        string PipeWireList(string type)
        {
            var list = new ProcessStartInfo("/usr/bin/pw-cli") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            list.ArgumentList.Add("ls");
            list.ArgumentList.Add(type);
            list.Environment["XDG_RUNTIME_DIR"] = RuntimeDirectory;
            list.Environment["PIPEWIRE_RUNTIME_DIR"] = RuntimeDirectory;
            try
            {
                using var process = Process.Start(list)!;
                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                return process.WaitForExit(5000) ? output.Result : string.Empty;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return string.Empty; // pw-cli missing (pipewire-bin)
            }
        }

        void Start(string file, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["XDG_RUNTIME_DIR"] = RuntimeDirectory;
            start.Environment["PIPEWIRE_RUNTIME_DIR"] = RuntimeDirectory;
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = bus;
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _processes.Add(process);
        }
    }

    /// <summary>The PipeWire test source is running (and with it the session bus).</summary>
    public bool Available { get; }

    /// <summary>The private session bus is running.</summary>
    public bool BusReady { get; }

    /// <summary>The private session bus (also used for the fake portal).</summary>
    public string BusAddress { get; }

    public string RuntimeDirectory { get; }

    /// <summary>The daemon's socket (passed as <c>remote.name</c>: the test process keeps its own environment).</summary>
    public string Socket { get; }

    public void Dispose()
    {
        foreach (var process in Enumerable.Reverse(_processes))
        {
            if (!process.HasExited)
            {
                _ = LibcNative.Kill(process.Id, 15);
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                }
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(RuntimeDirectory, recursive: true);
        }
        catch (IOException)
        {
            // a socket still being removed
        }
    }

    private static bool Wait(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Thread.Sleep(200);
        }

        return true;
    }
}
