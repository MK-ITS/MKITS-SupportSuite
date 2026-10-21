// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;
using ByteChannel = System.Threading.Channels.Channel;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class ShellHandlerTests : IAsyncDisposable
{
    private readonly FakeShellHost _host = new();
    private readonly RecordingContext _context = new();
    private readonly ShellHandler _handler;

    public ShellHandlerTests() => _handler = new ShellHandler(_host);

    public ValueTask DisposeAsync() => _handler.DisposeAsync();

    [Fact]
    public async Task Open_EchoesInputAsOutput_AndReportsExitCode()
    {
        await Send(new SessionPayload { ShellOpen = new ShellOpen { ShellId = "s1", Type = "bash", Cols = 120, Rows = 30 } });
        var shell = Assert.Single(_host.Started);
        Assert.Equal(("bash", 120, 30), (shell.Type, shell.Columns, shell.Rows));

        await Send(new SessionPayload { ShellData = new ShellData { ShellId = "s1", Data = ByteString.CopyFromUtf8("ls\r") } });
        Assert.Equal("ls\r", await WaitForOutputAsync("s1"));

        await Send(new SessionPayload { ShellResize = new ShellResize { ShellId = "s1", Cols = 9999, Rows = 1 } });
        Assert.Equal((500, 5), (shell.Columns, shell.Rows));

        shell.Exit(3);
        var exit = await WaitForAsync(SessionPayload.BodyOneofCase.ShellExit);
        Assert.Equal(("s1", 3), (exit.ShellExit.ShellId, exit.ShellExit.ExitCode));
    }

    [Theory]
    [InlineData("python")]
    [InlineData("/bin/rm")]
    [InlineData("powershell; calc")]
    public async Task Open_UnknownShellType_IsRefused(string type)
    {
        await Send(new SessionPayload { ShellOpen = new ShellOpen { ShellId = "s1", Type = type, Cols = 80, Rows = 24 } });

        Assert.Empty(_host.Started);
        Assert.Equal(-1, (await WaitForAsync(SessionPayload.BodyOneofCase.ShellExit)).ShellExit.ExitCode);
    }

    [Fact]
    public async Task Open_EmptyType_UsesDefault_AndLimitPerViewer()
    {
        for (var i = 0; i < ShellHandler.MaxShellsPerViewer + 1; i++)
        {
            await Send(new SessionPayload { ShellOpen = new ShellOpen { ShellId = $"s{i}", Cols = 80, Rows = 24 } });
        }

        Assert.Equal(ShellHandler.MaxShellsPerViewer, _host.Started.Count);
        Assert.All(_host.Started, s => Assert.Equal("bash", s.Type));
    }

    [Fact]
    public async Task ViewerCloses_OrLeaves_TerminatesShells()
    {
        await Send(new SessionPayload { ShellOpen = new ShellOpen { ShellId = "a", Type = "bash", Cols = 80, Rows = 24 } });
        await Send(new SessionPayload { ShellOpen = new ShellOpen { ShellId = "b", Type = "sh", Cols = 80, Rows = 24 } });

        await Send(new SessionPayload { ShellExit = new ShellExit { ShellId = "a" } });
        Assert.True(_host.Started[0].Disposed);

        _handler.OnViewerGone("v1");
        for (var i = 0; i < 100 && !_host.Started[1].Disposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_host.Started[1].Disposed);
    }

    private Task Send(SessionPayload payload) => _handler.HandleAsync(_context, "v1", payload, CancellationToken.None);

    private async Task<string> WaitForOutputAsync(string shellId)
    {
        for (var i = 0; i < 200; i++)
        {
            var output = string.Concat(_context.Sent.Where(s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.ShellData && s.Payload.ShellData.ShellId == shellId)
                .Select(s => s.Payload.ShellData.Data.ToStringUtf8()));
            if (output.Length > 0)
            {
                return output;
            }

            await Task.Delay(10);
        }

        return string.Empty;
    }

    private async Task<SessionPayload> WaitForAsync(SessionPayload.BodyOneofCase bodyCase)
    {
        for (var i = 0; i < 200; i++)
        {
            var found = _context.Sent.FirstOrDefault(s => s.Payload.BodyCase == bodyCase);
            if (found.Payload is not null)
            {
                return found.Payload;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(bodyCase.ToString());
    }

    private sealed class FakeShellHost : IShellHost
    {
        public List<FakeShell> Started { get; } = [];

        public IReadOnlyList<string> AvailableShells { get; } = ["bash", "sh"];

        public IShellProcess Start(string type, int columns, int rows)
        {
            var shell = new FakeShell(type, columns, rows);
            Started.Add(shell);
            return shell;
        }
    }

    /// <summary>Echoes input to output; exits on request.</summary>
    private sealed class FakeShell(string type, int columns, int rows) : IShellProcess
    {
        private readonly ChannelStream _output = new();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type { get; } = type;

        public int Columns { get; private set; } = columns;

        public int Rows { get; private set; } = rows;

        public bool Disposed { get; private set; }

        public Stream Output => _output;

        public Task<int> Exited => _exited.Task;

        public Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
        {
            _output.Push(input.ToArray());
            return Task.CompletedTask;
        }

        public void Resize(int columns, int rows) => (Columns, Rows) = (columns, rows);

        public void Exit(int code)
        {
            _exited.TrySetResult(code);
            _output.Complete();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Exit(-1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel = ByteChannel.CreateUnbounded<byte[]>();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Push(byte[] data) => _channel.Writer.TryWrite(data);

        public void Complete() => _channel.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_channel.Reader.TryRead(out var data))
            {
                return 0;
            }

            data.CopyTo(buffer);
            return data.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
