// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class FileTransferTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mkits-ft-{Guid.NewGuid():N}");
    private readonly RecordingContext _context = new();
    private readonly FakeUi _ui;
    private readonly FileTransferHandler _handler;

    public FileTransferTests()
    {
        _ui = new FakeUi(_directory);
        _handler = new FileTransferHandler(_ui);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
    [InlineData("name. . .", "name")]
    [InlineData("", "file")]
    [InlineData("...", "file")]
    [InlineData("tab\there", "tab_here")]
    public void SanitizeFileName_RemovesDangerousParts(string input, string expected)
    {
        Assert.Equal(expected, FileTransferHandler.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_LimitsLength_KeepsExtension()
    {
        var name = FileTransferHandler.SanitizeFileName(new string('a', 300) + ".txt");

        Assert.Equal(200, name.Length);
        Assert.EndsWith(".txt", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_Accepted_WritesVerifiedFile_WithoutOverwriting()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.txt"), "existing");
        var content = RandomNumberGenerator.GetBytes(150_000);

        await OfferAsync("t1", "notes.txt", content.Length);
        Assert.True(Decision("t1"));
        await SendContentAsync("t1", content);
        await CompleteAsync("t1", SHA256.HashData(content));

        var received = Assert.Single(_ui.Received);
        Assert.Equal(Path.Combine(_directory, "notes (1).txt"), received);
        Assert.Equal(content, await File.ReadAllBytesAsync(received));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_directory, "notes.txt")));
        Assert.Empty(Directory.GetFiles(_directory, "*.mkits-part"));
    }

    [Fact]
    public async Task Receive_Declined_WritesNothing()
    {
        _ui.Accept = false;

        await OfferAsync("t1", "x.bin", 10);

        Assert.False(Decision("t1"));
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any());
    }

    [Fact]
    public async Task Receive_WrongChecksum_DiscardsFile()
    {
        var content = new byte[1000];
        await OfferAsync("t1", "x.bin", content.Length);
        await SendContentAsync("t1", content);
        await CompleteAsync("t1", new byte[32]);

        Assert.Empty(_ui.Received);
        Assert.Contains(("x.bin", "checksum"), _ui.Failures);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Contains(_context.Sent, s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.FileCancel);
    }

    [Fact]
    public async Task Receive_ChunkOutOfOrderOrTooBig_Cancels()
    {
        await OfferAsync("t1", "x.bin", 100);
        await _handler.HandleAsync(_context, "v1", new SessionPayload { FileChunk = new FileChunk { TransferId = "t1", Offset = 50, Data = ByteString.CopyFrom(new byte[10]) } }, CancellationToken.None);

        Assert.Contains(("x.bin", "invalid_chunk"), _ui.Failures);
        Assert.Empty(Directory.GetFiles(_directory));

        await OfferAsync("t2", "y.bin", 10);
        await _handler.HandleAsync(_context, "v1", new SessionPayload { FileChunk = new FileChunk { TransferId = "t2", Offset = 0, Data = ByteString.CopyFrom(new byte[11]) } }, CancellationToken.None);
        Assert.Contains(("y.bin", "invalid_chunk"), _ui.Failures);
    }

    [Fact]
    public async Task Receive_TooLarge_IsRefusedWithoutAsking()
    {
        await OfferAsync("t1", "huge.iso", FileTransferHandler.MaxFileSize + 1);

        Assert.False(Decision("t1"));
        Assert.Equal(0, _ui.Questions);
    }

    [Fact]
    public async Task ViewerGone_DiscardsPartialFiles()
    {
        await OfferAsync("t1", "x.bin", 100);
        await SendContentAsync("t1", new byte[50]);

        _handler.OnViewerGone("v1");

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Send_WaitsForDecision_ThenStreamsChunksAndChecksum()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "log.txt");
        var content = RandomNumberGenerator.GetBytes(FileTransferHandler.ChunkSize + 10);
        await File.WriteAllBytesAsync(path, content);

        var sending = _handler.SendFileAsync(_context, "v1", path, CancellationToken.None);
        var offer = await WaitForAsync(SessionPayload.BodyOneofCase.FileOffer);
        Assert.Equal("log.txt", offer.FileOffer.Name);
        Assert.Equal((ulong)content.Length, offer.FileOffer.Size);

        await _handler.HandleAsync(_context, "v1", new SessionPayload { FileDecision = new FileDecision { TransferId = offer.FileOffer.TransferId, Accepted = true } }, CancellationToken.None);
        Assert.True(await sending);

        var chunks = _context.Sent.Where(s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.FileChunk).Select(s => s.Payload.FileChunk).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(content, chunks.SelectMany(c => c.Data.ToByteArray()).ToArray());
        var complete = _context.Sent.Single(s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.FileComplete).Payload.FileComplete;
        Assert.Equal(SHA256.HashData(content), complete.Sha256.ToByteArray());
    }

    [Fact]
    public async Task Send_Declined_SendsNoData()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "a.txt");
        await File.WriteAllTextAsync(path, "x");

        var sending = _handler.SendFileAsync(_context, "v1", path, CancellationToken.None);
        var offer = await WaitForAsync(SessionPayload.BodyOneofCase.FileOffer);
        await _handler.HandleAsync(_context, "v1", new SessionPayload { FileDecision = new FileDecision { TransferId = offer.FileOffer.TransferId, Accepted = false } }, CancellationToken.None);

        Assert.False(await sending);
        Assert.DoesNotContain(_context.Sent, s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.FileChunk);
    }

    private async Task OfferAsync(string transferId, string name, long size)
    {
        await _handler.HandleAsync(_context, "v1", new SessionPayload { FileOffer = new FileOffer { TransferId = transferId, Name = name, Size = (ulong)size } }, CancellationToken.None);
        await WaitForDecisionAsync(transferId);
    }

    private async Task SendContentAsync(string transferId, byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += FileTransferHandler.ChunkSize)
        {
            var length = Math.Min(FileTransferHandler.ChunkSize, content.Length - offset);
            await _handler.HandleAsync(_context, "v1", new SessionPayload
            {
                FileChunk = new FileChunk { TransferId = transferId, Offset = (ulong)offset, Data = ByteString.CopyFrom(content, offset, length) },
            }, CancellationToken.None);
        }
    }

    private Task CompleteAsync(string transferId, byte[] sha256) =>
        _handler.HandleAsync(_context, "v1", new SessionPayload { FileComplete = new FileComplete { TransferId = transferId, Sha256 = ByteString.CopyFrom(sha256) } }, CancellationToken.None);

    private bool Decision(string transferId) =>
        _context.Sent.Single(s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.FileDecision && s.Payload.FileDecision.TransferId == transferId).Payload.FileDecision.Accepted;

    private async Task WaitForDecisionAsync(string transferId)
    {
        for (var i = 0; i < 200 && !_context.Sent.Any(s => s.Payload.BodyCase == SessionPayload.BodyOneofCase.FileDecision && s.Payload.FileDecision.TransferId == transferId); i++)
        {
            await Task.Delay(10);
        }
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

    private sealed class FakeUi(string directory) : IFileTransferUi
    {
        public bool Accept { get; set; } = true;

        public int Questions { get; private set; }

        public List<string> Received { get; } = [];

        public List<(string Name, string Reason)> Failures { get; } = [];

        public string DownloadDirectory => directory;

        public Task<bool> ConfirmIncomingAsync(string fileName, long size, CancellationToken cancellationToken)
        {
            Questions++;
            return Task.FromResult(Accept);
        }

        public void OnFileReceived(string path) => Received.Add(path);

        public void OnTransferFailed(string fileName, string reason) => Failures.Add((fileName, reason));
    }
}
