// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.Audit;

/// <summary>Data for a new audit entry. <see cref="Details"/> must never contain secrets.</summary>
public sealed record AuditEntry(
    string EventType,
    AuditActorType ActorType,
    string? ActorId = null,
    string? Ip = null,
    string? TargetType = null,
    string? TargetId = null,
    IReadOnlyDictionary<string, string?>? Details = null);

/// <summary>Result of <see cref="IAuditLog.VerifyAsync"/>.</summary>
public sealed record AuditVerificationResult(bool IsValid, long CheckedEntries, long? FirstInvalidId);

/// <summary>Append-only, hash-chained audit log (docs/security-concept.md, threat T16).</summary>
public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Recomputes the hash chain and reports the first modified or missing entry.</summary>
    Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes audit entries in their own database context (never mixed with a request's pending changes)
/// and serializes writers so the hash chain stays linear.
/// </summary>
public sealed class AuditLog(IDbContextFactory<MkitsDbContext> dbFactory, TimeProvider timeProvider) : IAuditLog, IDisposable
{
    private const int HashSize = 32;

    private static readonly JsonSerializerOptions DetailsJson = new() { WriteIndented = false };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var previousHash = await db.AuditEvents
                .OrderByDescending(e => e.Id)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? new byte[HashSize];

            var auditEvent = new AuditEvent
            {
                Timestamp = timeProvider.GetUtcNow(),
                ActorType = entry.ActorType,
                ActorId = entry.ActorId,
                Ip = entry.Ip,
                EventType = entry.EventType,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                Details = entry.Details is null
                    ? "{}"
                    : JsonSerializer.Serialize(new SortedDictionary<string, string?>(entry.Details.ToDictionary(), StringComparer.Ordinal), DetailsJson),
                PreviousHash = previousHash,
            };
            auditEvent.Hash = ComputeHash(auditEvent);

            db.AuditEvents.Add(auditEvent);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var expectedPrevious = new byte[HashSize];
        long checkedEntries = 0;

        await foreach (var auditEvent in db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            checkedEntries++;

            if (!CryptographicOperations.FixedTimeEquals(auditEvent.PreviousHash, expectedPrevious)
                || !CryptographicOperations.FixedTimeEquals(auditEvent.Hash, ComputeHash(auditEvent)))
            {
                return new AuditVerificationResult(false, checkedEntries, auditEvent.Id);
            }

            expectedPrevious = auditEvent.Hash;
        }

        return new AuditVerificationResult(true, checkedEntries, null);
    }

    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// SHA-256 over the previous hash and all fields, each length-prefixed, so that no two different entries
    /// can produce the same input (no delimiter ambiguity).
    /// </summary>
    internal static byte[] ComputeHash(AuditEvent auditEvent)
    {
        using var buffer = new MemoryStream();

        buffer.Write(auditEvent.PreviousHash);
        WriteInt64(buffer, auditEvent.Timestamp.UtcTicks);
        WriteInt64(buffer, (long)auditEvent.ActorType);
        WriteString(buffer, auditEvent.ActorId);
        WriteString(buffer, auditEvent.Ip);
        WriteString(buffer, auditEvent.EventType);
        WriteString(buffer, auditEvent.TargetType);
        WriteString(buffer, auditEvent.TargetId);
        WriteString(buffer, auditEvent.Details);

        return SHA256.HashData(buffer.ToArray());
    }

    private static void WriteInt64(MemoryStream buffer, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        buffer.Write(bytes);
    }

    private static void WriteString(MemoryStream buffer, string? value)
    {
        if (value is null)
        {
            WriteInt64(buffer, -1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt64(buffer, bytes.Length);
        buffer.Write(bytes);
    }
}
