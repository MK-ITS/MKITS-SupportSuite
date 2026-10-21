// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>
/// The client's connection to the privileged helper. Implements screen capture and input by forwarding them, so the
/// session can switch to "administrator mode" without reconnecting. If the pipe breaks, <see cref="Disconnected"/> is
/// raised and the client falls back to its own (unprivileged) capture and input.
/// </summary>
internal sealed partial class HelperClient : IScreenSource, IInputInjector, IDisposable
{
    private static readonly TimeSpan MonitorCacheTime = TimeSpan.FromSeconds(2);

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<MonitorDescriptor> _monitors = [];
    private DateTime _monitorsLoaded;
    private int _broken;

    private HelperClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public event EventHandler? Disconnected;

    /// <summary>The pipe broke; a new helper is needed.</summary>
    public bool IsBroken => Volatile.Read(ref _broken) != 0;

    public static string PipeNameFor(string id) => $"MKITS-Helper-{id}";

    /// <summary>
    /// Shows the UAC prompt (on the customer's screen), lets the elevated copy install the temporary service and connects
    /// to the helper. Returns <c>null</c> if the user declined or anything failed.
    /// </summary>
    public static async Task<HelperClient?> ElevateAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null || Environment.ProcessPath is not { } exe)
        {
            return null;
        }

        Process? installer;
        try
        {
            installer = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                // Only hex, digits and a SID – nothing that needs quoting.
                Arguments = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"--elevate {id} {Environment.ProcessId} {sid}"),
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // the user clicked "No" in the UAC prompt
        }

        if (installer is null)
        {
            return null;
        }

        using (installer)
        {
            await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (installer.ExitCode != 0)
            {
                return null;
            }
        }

        return await ConnectAsync(PipeNameFor(id), ElevatedSessionConfig.ExecutableFor(id), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Connects to a helper whose process must run <paramref name="expectedExecutable"/> (guards against a squatted pipe name).</summary>
    public static async Task<HelperClient?> ConnectAsync(string pipeName, string expectedExecutable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            if (!ServerIsOurHelper(pipe, expectedExecutable))
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new HelperClient(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        if (DateTime.UtcNow - _monitorsLoaded < MonitorCacheTime)
        {
            return _monitors;
        }

        var response = Request(HelperProtocol.Build(HelperRequest.GetMonitors));
        if (response is null)
        {
            return _monitors;
        }

        using var reader = new BinaryReader(new MemoryStream(response), Encoding.UTF8);
        _ = reader.ReadByte();
        var count = Math.Clamp(reader.ReadInt32(), 0, 32);
        var monitors = new List<MonitorDescriptor>(count);
        for (var i = 0; i < count; i++)
        {
            monitors.Add(new MonitorDescriptor(reader.ReadUInt32(), reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean()));
        }

        _monitors = monitors;
        _monitorsLoaded = DateTime.UtcNow;
        return monitors;
    }

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken)
    {
        var response = Request(HelperProtocol.Build(HelperRequest.Capture, w =>
        {
            w.Write(monitorId);
            w.Write(quality);
        }));
        if (response is null)
        {
            return null;
        }

        using var reader = new BinaryReader(new MemoryStream(response));
        _ = reader.ReadByte();
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var codec = (VideoCodec)reader.ReadInt32();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var data = reader.ReadBytes(reader.ReadInt32());
        return new EncodedFrame(codec, width, height, data, Keyframe: true);
    }

    public void MovePointer(int x, int y) => Send(HelperProtocol.Build(HelperRequest.MovePointer, w =>
    {
        w.Write(x);
        w.Write(y);
    }));

    public void SetButton(PointerButtons button, bool down) => Send(HelperProtocol.Build(HelperRequest.SetButton, w =>
    {
        w.Write((int)button);
        w.Write(down);
    }));

    public void Wheel(int deltaX, int deltaY) => Send(HelperProtocol.Build(HelperRequest.Wheel, w =>
    {
        w.Write(deltaX);
        w.Write(deltaY);
    }));

    public void Key(string code, bool down) => Send(HelperProtocol.Build(HelperRequest.Key, w =>
    {
        w.Write(code);
        w.Write(down);
    }));

    public void Text(string text) => Send(HelperProtocol.Build(HelperRequest.Text, w => w.Write(text)));

    public void Special(SpecialKeyKind kind) => Send(HelperProtocol.Build(HelperRequest.Special, w => w.Write((int)kind)));

    /// <summary>Ends administrator mode: the helper exits and the service removes itself.</summary>
    public void Shutdown() => Send(HelperProtocol.Build(HelperRequest.Shutdown));

    public void Dispose()
    {
        _pipe.Dispose();
        _lock.Dispose();
    }

    private void Send(byte[] message) => _ = Request(message, expectResponse: false);

    private byte[]? Request(byte[] message, bool expectResponse = true)
    {
        if (Volatile.Read(ref _broken) != 0)
        {
            return null;
        }

        _lock.Wait();
        try
        {
            HelperProtocol.WriteAsync(_pipe, message, CancellationToken.None).GetAwaiter().GetResult();
            return expectResponse ? HelperProtocol.ReadAsync(_pipe, CancellationToken.None).GetAwaiter().GetResult() ?? Break() : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or EndOfStreamException)
        {
            return Break();
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[]? Break()
    {
        if (Interlocked.Exchange(ref _broken, 1) == 0)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    /// <summary>The pipe server must be the helper EXE from the protected folder (guards against a squatted pipe name).</summary>
    private static bool ServerIsOurHelper(NamedPipeClientStream pipe, string expectedExecutable)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid))
        {
            return false;
        }

        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, serverPid);
        if (process == 0)
        {
            return false;
        }

        try
        {
            var buffer = new char[1024];
            var length = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref length)
                && string.Equals(new string(buffer, 0, length), expectedExecutable, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _ = Native.ConPtyNative.CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] buffer, ref int size);
}
