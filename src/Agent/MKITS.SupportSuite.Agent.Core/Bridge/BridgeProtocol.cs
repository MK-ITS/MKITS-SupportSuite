// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Text;

namespace MKITS.SupportSuite.Agent.Core.Bridge;

/// <summary>Messages between the agent service and its process in the logged-on user's session.</summary>
public enum BridgeMessageType : byte
{
    // service → user session
    SessionAssigned = 1,
    Status = 2,
    Supporters = 3,
    SecurityCode = 4,
    ViewerReady = 5,
    ViewerGone = 6,
    ConsentRequest = 7,

    // both directions
    Payload = 8,

    // user session → service
    ConsentAnswer = 9,
    EndSession = 10,
}

/// <summary>
/// Length-prefixed binary messages (<c>u32 length (LE) ‖ type byte ‖ body</c>) over a local pipe. Both sides treat
/// every message as hostile: sizes and string lengths are limited.
/// </summary>
public static class BridgeProtocol
{
    /// <summary>Largest payload: a 1 MiB clipboard text plus headers (file chunks are 64 KiB).</summary>
    public const int MaxMessageSize = 4 * 1024 * 1024;

    public const int MaxStringLength = 4096;

    public static byte[] Build(BridgeMessageType type, Action<BinaryWriter>? body = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)type);
            body?.Invoke(writer);
        }

        return buffer.ToArray();
    }

    public static async Task WriteAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length is 0 or > MaxMessageSize)
        {
            throw new InvalidDataException("Invalid bridge message size.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Next message, or <c>null</c> when the stream ended. Throws <see cref="InvalidDataException"/> for invalid lengths.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageSize)
        {
            throw new InvalidDataException("Invalid bridge message length.");
        }

        var message = new byte[length];
        return await ReadExactlyOrEndAsync(stream, message, cancellationToken).ConfigureAwait(false) ? message : null;
    }

    public static string ReadString(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var value = reader.ReadString();
        return value.Length <= MaxStringLength ? value : throw new InvalidDataException("String too long.");
    }

    public static void WriteStrings(BinaryWriter writer, IReadOnlyCollection<string> values)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);
        writer.Write(values.Count);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    public static List<string> ReadStrings(BinaryReader reader, int maxCount = 32)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var count = reader.ReadInt32();
        if (count < 0 || count > maxCount)
        {
            throw new InvalidDataException("Too many strings.");
        }

        var values = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            values.Add(ReadString(reader));
        }

        return values;
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
