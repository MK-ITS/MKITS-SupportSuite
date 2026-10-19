// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Google.Protobuf;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Common.Protocol;

/// <summary>
/// Converts between WebSocket binary messages and protocol <see cref="Frame"/>s (docs/protocol.md §1).
/// Every message holds exactly one frame.
/// </summary>
public static class FrameCodec
{
    /// <summary>Maximum size of one WebSocket message (1 MiB). Larger video frames are split by the sender.</summary>
    public const int MaxMessageSize = 1024 * 1024;

    /// <summary>Maximum size of a control-plane message (everything except <see cref="SessionData"/>).</summary>
    public const int MaxControlMessageSize = 64 * 1024;

    /// <summary>WebSocket sub-protocol announced by all peers.</summary>
    public const string SubProtocol = "mkits.v0";

    public static byte[] Encode(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.ToByteArray();
    }

    /// <summary>
    /// Parses one message. Returns <c>false</c> for malformed input, an empty frame, a message above
    /// <see cref="MaxMessageSize"/> or a control message above <see cref="MaxControlMessageSize"/>.
    /// Never throws for hostile input.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> message, out Frame frame)
    {
        frame = new Frame();
        if (message.IsEmpty || message.Length > MaxMessageSize)
        {
            return false;
        }

        try
        {
            frame = Frame.Parser.ParseFrom(message);
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }

        if (frame.BodyCase == Frame.BodyOneofCase.None)
        {
            return false;
        }

        return frame.BodyCase == Frame.BodyOneofCase.SessionData || message.Length <= MaxControlMessageSize;
    }
}
