// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Text;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>Request types between the client (user process) and the privileged helper (SYSTEM, user's session).</summary>
internal enum HelperRequest : byte
{
    GetMonitors = 1,
    Capture = 2,
    MovePointer = 3,
    SetButton = 4,
    Wheel = 5,
    Key = 6,
    Text = 7,
    Special = 8,
    Shutdown = 9,
}

/// <summary>
/// Length-prefixed binary messages over the helper pipe: <c>u32 length (LE) ‖ payload</c>, payload starts with a
/// <see cref="HelperRequest"/> byte. Only <see cref="HelperRequest.GetMonitors"/> and <see cref="HelperRequest.Capture"/>
/// have responses; responses arrive in request order. Everything is size-limited – the helper runs as SYSTEM and treats
/// every message as hostile.
/// </summary>
internal static class HelperProtocol
{
    internal const int MaxMessageSize = 64 * 1024 * 1024;
    internal const int MaxStringLength = 4096;

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Next message, or <c>null</c> when the pipe closed.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageSize)
        {
            throw new InvalidDataException("Invalid helper message length.");
        }

        var payload = new byte[length];
        return await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false) ? payload : null;
    }

    public static byte[] Build(HelperRequest type, Action<BinaryWriter>? body = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)type);
            body?.Invoke(writer);
        }

        return buffer.ToArray();
    }

    /// <summary>Reads a length-checked UTF-8 string.</summary>
    public static string ReadString(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string value;
        try
        {
            value = reader.ReadString();
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Malformed string.", ex); // malformed 7-bit length prefix
        }

        return value.Length <= MaxStringLength ? value : throw new InvalidDataException("String too long.");
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
