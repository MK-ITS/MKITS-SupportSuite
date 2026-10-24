// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>
/// Shells on a pseudo terminal (docs/protocol.md §5, channel SHELL). The shell is started through
/// <c>setsid --ctty</c>, so it becomes a session leader with the PTY as controlling terminal (job control works).
/// It runs with the rights of the agent process.
/// </summary>
internal sealed class PtyShellHost : IShellHost
{
    private static readonly (string Type, string Path)[] Candidates =
    [
        ("bash", "/bin/bash"),
        ("zsh", "/bin/zsh"),
        ("sh", "/bin/sh"),
    ];

    public IReadOnlyList<string> AvailableShells { get; } =
        Candidates.Where(c => File.Exists(c.Path)).Select(c => c.Type).ToList();

    public IShellProcess Start(string type, int columns, int rows)
    {
        var path = Candidates.FirstOrDefault(c => c.Type == type).Path
            ?? throw new InvalidOperationException("Unknown shell type.");
        return PtyShellProcess.Start(path, columns, rows);
    }
}

/// <summary>A shell on a PTY: master side as stream, resize via <c>TIOCSWINSZ</c>, exit code via <c>waitpid</c>.</summary>
internal sealed class PtyShellProcess : IShellProcess
{
    private const string SetSid = "/usr/bin/setsid";

    private readonly int _master;
    private readonly int _pid;
    private readonly FileStream _stream;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private PtyShellProcess(int master, int pid)
    {
        _master = master;
        _pid = pid;
        _stream = new FileStream(new SafeFileHandle(master, ownsHandle: true), FileAccess.ReadWrite, bufferSize: 0);
        Output = new PtyOutputStream(_stream);
        var waiter = new Thread(Wait) { IsBackground = true, Name = "MKITS shell wait" };
        waiter.Start();
    }

    public Stream Output { get; }

    public Task<int> Exited => _exited.Task;

    public static PtyShellProcess Start(string shell, int columns, int rows)
    {
        var master = LibcNative.PosixOpenPt(LibcNative.ReadWrite | LibcNative.NoControllingTerminal);
        if (master < 0 || LibcNative.GrantPt(master) != 0 || LibcNative.UnlockPt(master) != 0)
        {
            throw new IOException($"Cannot create a pseudo terminal ({Marshal.GetLastPInvokeError()}).");
        }

        var nameBuffer = new byte[256];
        if (LibcNative.PtsNameR(master, nameBuffer, (nuint)nameBuffer.Length) != 0)
        {
            _ = LibcNative.Close(master);
            throw new IOException("Cannot resolve the pseudo terminal.");
        }

        var slaveName = Encoding.UTF8.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0));
        var slave = LibcNative.Open(slaveName, LibcNative.ReadWrite | LibcNative.NoControllingTerminal | LibcNative.CloseOnExec);
        if (slave < 0)
        {
            _ = LibcNative.Close(master);
            throw new IOException("Cannot open the pseudo terminal.");
        }

        try
        {
            Resize(master, columns, rows);
            var pid = Spawn(master, slave, [SetSid, "--ctty", "--wait", shell, "-l"]);
            return new PtyShellProcess(master, pid);
        }
        catch
        {
            _ = LibcNative.Close(master);
            throw;
        }
        finally
        {
            _ = LibcNative.Close(slave);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows) => Resize(_master, columns, rows);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_exited.Task.IsCompleted)
        {
            _ = LibcNative.Kill(_pid, LibcNative.SignalHangUp);
            if (await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != _exited.Task)
            {
                _ = LibcNative.Kill(_pid, LibcNative.SignalKill);
            }
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static void Resize(int master, int columns, int rows)
    {
        var size = new LibcNative.WindowSize { Columns = (ushort)columns, Rows = (ushort)rows };
        _ = LibcNative.IoctlWindowSize(master, LibcNative.SetWindowSize, ref size);
    }

    /// <summary><c>posix_spawn</c> with the slave as stdin/stdout/stderr (no fork of the .NET process).</summary>
    private static int Spawn(int master, int slave, string[] argv)
    {
        var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k is not "TERM" and not "COLUMNS" and not "LINES")
            .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}")
            .Append("TERM=xterm-256color")
            .ToArray();

        var actions = Marshal.AllocHGlobal(256); // posix_spawn_file_actions_t is 80 bytes on glibc
        var argvBlock = ToNativeArray(argv);
        var envBlock = ToNativeArray(environment);
        try
        {
            _ = LibcNative.FileActionsInit(actions);
            _ = LibcNative.FileActionsAddDup2(actions, slave, 0);
            _ = LibcNative.FileActionsAddDup2(actions, slave, 1);
            _ = LibcNative.FileActionsAddDup2(actions, slave, 2);
            _ = LibcNative.FileActionsAddClose(actions, master);
            var result = LibcNative.PosixSpawn(out var pid, argv[0], actions, 0, argvBlock, envBlock);
            return result == 0 ? pid : throw new IOException($"Cannot start the shell ({result}).");
        }
        finally
        {
            _ = LibcNative.FileActionsDestroy(actions);
            Marshal.FreeHGlobal(actions);
            FreeNativeArray(argvBlock, argv.Length);
            FreeNativeArray(envBlock, environment.Length);
        }
    }

    private static nint ToNativeArray(string[] values)
    {
        var block = Marshal.AllocHGlobal((values.Length + 1) * IntPtr.Size);
        for (var i = 0; i < values.Length; i++)
        {
            Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
        }

        Marshal.WriteIntPtr(block, values.Length * IntPtr.Size, 0);
        return block;
    }

    private static void FreeNativeArray(nint block, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(block, i * IntPtr.Size));
        }

        Marshal.FreeHGlobal(block);
    }

    private void Wait()
    {
        int status;
        while (LibcNative.WaitPid(_pid, out status, 0) < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */)
        {
        }

        // WIFEXITED → WEXITSTATUS; killed by a signal → 128 + signal (shell convention)
        _exited.TrySetResult((status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F));
    }

    /// <summary>Reading the master after the shell exited fails with EIO; that is the normal end of output.</summary>
    private sealed class PtyOutputStream(FileStream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
