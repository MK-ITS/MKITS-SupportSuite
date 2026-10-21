// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>The end user's side of file transfers (platform UI).</summary>
public interface IFileTransferUi
{
    /// <summary>Folder for received files (e.g. the user's Downloads folder).</summary>
    string DownloadDirectory { get; }

    /// <summary>Asks the end user whether to accept an incoming file. <paramref name="fileName"/> is already sanitized.</summary>
    Task<bool> ConfirmIncomingAsync(string fileName, long size, CancellationToken cancellationToken);

    /// <summary>A file was received completely and verified.</summary>
    void OnFileReceived(string path);

    /// <summary>A transfer failed or was cancelled (for a short notice in the UI).</summary>
    void OnTransferFailed(string fileName, string reason);
}

/// <summary>
/// File transfer in both directions (docs/protocol.md §5, threat T14): incoming files are accepted only after the
/// end user agreed, names are sanitized, chunks must arrive in order and within the announced size, the SHA-256 is
/// verified, and the file only gets its final name after verification (no half-written files). Files are never
/// executed or opened automatically.
/// </summary>
public sealed class FileTransferHandler(IFileTransferUi ui, TimeProvider? timeProvider = null) : ISessionChannelHandler
{
    public const int ChunkSize = 64 * 1024;
    public const long MaxFileSize = 4L * 1024 * 1024 * 1024;
    internal const int MaxConcurrentIncoming = 3;
    internal static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(2);

    private const string PartSuffix = ".mkits-part";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Incoming> _incoming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingDecisions = new(StringComparer.Ordinal);

    public async Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);

        switch (payload.BodyCase)
        {
            case SessionPayload.BodyOneofCase.FileOffer:
                // The confirmation dialog may stay open for a while: never block the receive loop with it.
                _ = OfferInBackgroundAsync(context, viewerId, payload.FileOffer, cancellationToken);
                break;
            case SessionPayload.BodyOneofCase.FileChunk:
                await OnChunkAsync(context, viewerId, payload.FileChunk, cancellationToken).ConfigureAwait(false);
                break;
            case SessionPayload.BodyOneofCase.FileComplete:
                await OnCompleteAsync(context, viewerId, payload.FileComplete, cancellationToken).ConfigureAwait(false);
                break;
            case SessionPayload.BodyOneofCase.FileCancel:
                if (TryTake(viewerId, payload.FileCancel.TransferId, out var cancelled))
                {
                    cancelled.Discard();
                    ui.OnTransferFailed(cancelled.FileName, "cancelled");
                }

                if (_pendingDecisions.TryRemove(Key(viewerId, payload.FileCancel.TransferId), out var waiting))
                {
                    waiting.TrySetResult(false);
                }

                break;
            case SessionPayload.BodyOneofCase.FileDecision:
                // Only the viewer that was asked can answer for itself.
                if (_pendingDecisions.TryRemove(Key(viewerId, payload.FileDecision.TransferId), out var decision))
                {
                    decision.TrySetResult(payload.FileDecision.Accepted);
                }

                break;
            default:
                break;
        }
    }

    public void OnViewerGone(string viewerId)
    {
        foreach (var key in _incoming.Keys.Where(k => k.StartsWith(viewerId + "/", StringComparison.Ordinal)).ToList())
        {
            if (_incoming.TryRemove(key, out var transfer))
            {
                transfer.Discard();
            }
        }
    }

    /// <summary>
    /// Sends a local file chosen by the end user to one viewer, or to every ready viewer when <paramref name="viewerId"/>
    /// is <c>null</c>. Each viewer decides on its own; the content is sent only to viewers that accepted.
    /// Returns <c>true</c> if at least one viewer received the file.
    /// </summary>
    public async Task<bool> SendFileAsync(ISessionContext context, string? viewerId, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var info = new FileInfo(path);
        var targets = viewerId is null ? context.ReadyViewerIds.ToList() : [viewerId];
        if (!info.Exists || info.Length > MaxFileSize || targets.Count == 0)
        {
            return false;
        }

        var transferId = Guid.NewGuid().ToString("N");
        var decisions = targets.ToDictionary(t => t, t => RegisterDecision(t, transferId), StringComparer.Ordinal);
        try
        {
            var offer = new SessionPayload
            {
                FileOffer = new FileOffer { TransferId = transferId, Name = SanitizeFileName(info.Name), Size = (ulong)info.Length },
            };
            foreach (var target in targets)
            {
                await context.SendAsync(target, offer, cancellationToken).ConfigureAwait(false);
            }

            var sent = false;
            foreach (var (target, decision) in decisions)
            {
                bool accepted;
                try
                {
                    accepted = await decision.Task.WaitAsync(DecisionTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    accepted = false;
                }

                if (accepted)
                {
                    await StreamFileAsync(context, target, transferId, path, cancellationToken).ConfigureAwait(false);
                    sent = true;
                }
            }

            return sent;
        }
        finally
        {
            foreach (var target in targets)
            {
                _pendingDecisions.TryRemove(Key(target, transferId), out _);
            }
        }
    }

    private TaskCompletionSource<bool> RegisterDecision(string viewerId, string transferId)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDecisions[Key(viewerId, transferId)] = decision;
        return decision;
    }

    private static async Task StreamFileAsync(ISessionContext context, string viewerId, string transferId, string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            ulong offset = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await context.SendAsync(viewerId, new SessionPayload
                {
                    FileChunk = new FileChunk { TransferId = transferId, Offset = offset, Data = ByteString.CopyFrom(buffer, 0, read) },
                }, cancellationToken).ConfigureAwait(false);
                offset += (ulong)read;
            }
        }

        await context.SendAsync(viewerId, new SessionPayload
        {
            FileComplete = new FileComplete { TransferId = transferId, Sha256 = ByteString.CopyFrom(hash.GetHashAndReset()) },
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a received name safe: no directories, no characters invalid on Windows or Linux, no reserved device
    /// names, no trailing dots/spaces, at most 200 characters. Never returns an empty name.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        var candidate = (name ?? string.Empty).Replace('\\', '/');
        candidate = candidate[(candidate.LastIndexOf('/') + 1)..];

        var builder = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
        {
            builder.Append(char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > 200)
        {
            var extension = Path.GetExtension(result);
            result = extension.Length is > 0 and <= 20 ? result[..(200 - extension.Length)] + extension : result[..200];
        }

        if (result.Length == 0 || result.All(c => c == '.'))
        {
            return "file";
        }

        var stem = result.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + result : result;
    }

    private async Task OfferInBackgroundAsync(ISessionContext context, string viewerId, FileOffer offer, CancellationToken cancellationToken)
    {
        try
        {
            await OnOfferAsync(context, viewerId, offer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ui.OnTransferFailed(SanitizeFileName(offer.Name), "io");
        }
    }

    private async Task OnOfferAsync(ISessionContext context, string viewerId, FileOffer offer, CancellationToken cancellationToken)
    {
        var key = Key(viewerId, offer.TransferId);
        var name = SanitizeFileName(offer.Name);
        if (!IsValidTransferId(offer.TransferId) || _incoming.ContainsKey(key)
            || offer.Size > MaxFileSize || _incoming.Count >= MaxConcurrentIncoming)
        {
            await DecideAsync(context, viewerId, offer.TransferId, accepted: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var accepted = await ui.ConfirmIncomingAsync(name, (long)offer.Size, cancellationToken).ConfigureAwait(false);
        if (accepted)
        {
            var directory = ui.DownloadDirectory;
            Directory.CreateDirectory(directory);
            // The temporary name never contains peer-supplied characters beyond the sanitized file name.
            var partPath = Path.Combine(directory, $"{name}.{Guid.NewGuid():N}{PartSuffix}");
            var stream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true);
            _incoming[key] = new Incoming(name, (long)offer.Size, partPath, stream, directory);
        }

        await DecideAsync(context, viewerId, offer.TransferId, accepted, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnChunkAsync(ISessionContext context, string viewerId, FileChunk chunk, CancellationToken cancellationToken)
    {
        if (!_incoming.TryGetValue(Key(viewerId, chunk.TransferId), out var transfer))
        {
            return;
        }

        if ((long)chunk.Offset != transfer.Received || chunk.Data.Length > ChunkSize || transfer.Received + chunk.Data.Length > transfer.Size)
        {
            await FailAsync(context, viewerId, chunk.TransferId, transfer, "invalid_chunk", cancellationToken).ConfigureAwait(false);
            return;
        }

        await transfer.WriteAsync(chunk.Data.Memory, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnCompleteAsync(ISessionContext context, string viewerId, FileComplete complete, CancellationToken cancellationToken)
    {
        if (!TryTake(viewerId, complete.TransferId, out var transfer))
        {
            return;
        }

        var digest = await transfer.FinishAsync().ConfigureAwait(false);
        if (transfer.Received != transfer.Size || !CryptographicOperations.FixedTimeEquals(digest, complete.Sha256.Span))
        {
            transfer.Discard();
            ui.OnTransferFailed(transfer.FileName, "checksum");
            await context.SendAsync(viewerId, new SessionPayload { FileCancel = new FileCancel { TransferId = complete.TransferId, Reason = "checksum" } }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var finalPath = UniquePath(transfer.Directory, transfer.FileName);
            File.Move(transfer.PartPath, finalPath);
            transfer.Dispose();
            ui.OnFileReceived(finalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            transfer.Discard();
            ui.OnTransferFailed(transfer.FileName, "io");
        }
    }

    private async Task FailAsync(ISessionContext context, string viewerId, string transferId, Incoming transfer, string reason, CancellationToken cancellationToken)
    {
        _incoming.TryRemove(Key(viewerId, transferId), out _);
        transfer.Discard();
        ui.OnTransferFailed(transfer.FileName, reason);
        await context.SendAsync(viewerId, new SessionPayload { FileCancel = new FileCancel { TransferId = transferId, Reason = reason } }, cancellationToken).ConfigureAwait(false);
    }

    private static Task DecideAsync(ISessionContext context, string viewerId, string transferId, bool accepted, CancellationToken cancellationToken) =>
        context.SendAsync(viewerId, new SessionPayload { FileDecision = new FileDecision { TransferId = transferId, Accepted = accepted } }, cancellationToken);

    private bool TryTake(string viewerId, string transferId, out Incoming transfer) =>
        _incoming.TryRemove(Key(viewerId, transferId), out transfer!);

    private static string Key(string viewerId, string transferId) => viewerId + "/" + transferId;

    /// <summary>Transfer ids are opaque tokens: 1–64 letters, digits, '-' or '_'.</summary>
    internal static bool IsValidTransferId(string? transferId) =>
        !string.IsNullOrEmpty(transferId) && transferId.Length <= 64 && transferId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary><c>name.ext</c>, then <c>name (1).ext</c>, <c>name (2).ext</c>, … – never overwrites.</summary>
    internal static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; File.Exists(path) && i < 10_000; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
        }

        return path;
    }

    private sealed class Incoming(string fileName, long size, string partPath, FileStream stream, string directory) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly SemaphoreSlim _lock = new(1, 1);

        public string FileName { get; } = fileName;

        public long Size { get; } = size;

        public string PartPath { get; } = partPath;

        public string Directory { get; } = directory;

        public long Received { get; private set; }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                _hash.AppendData(data.Span);
                Received += data.Length;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<byte[]> FinishAsync()
        {
            await stream.FlushAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            return _hash.GetHashAndReset();
        }

        public void Dispose()
        {
            stream.Dispose();
            _hash.Dispose();
            _lock.Dispose();
        }

        public void Discard()
        {
            Dispose();
            try
            {
                File.Delete(PartPath);
            }
            catch (IOException)
            {
                // best effort; the .mkits-part file is harmless
            }
        }
    }
}
