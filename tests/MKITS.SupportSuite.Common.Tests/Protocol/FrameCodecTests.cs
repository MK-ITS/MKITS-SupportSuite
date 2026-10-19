// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Google.Protobuf;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Common.Tests.Protocol;

public sealed class FrameCodecTests
{
    [Fact]
    public void EncodeDecode_Hello_RoundTrips()
    {
        var frame = new Frame
        {
            Hello = new Hello
            {
                ProtocolMajor = ProtocolVersion.Major,
                ProtocolMinor = ProtocolVersion.Minor,
                Role = PeerRole.Agent,
                Platform = "windows",
                Capabilities = { "video.h264", "shell.conpty" },
            },
        };

        var ok = FrameCodec.TryDecode(FrameCodec.Encode(frame), out var decoded);

        Assert.True(ok);
        Assert.Equal(frame, decoded);
    }

    [Fact]
    public void TryDecode_Garbage_ReturnsFalse()
    {
        Assert.False(FrameCodec.TryDecode([0xFF, 0xFF, 0xFF, 0xFF, 0xFF], out _));
    }

    [Fact]
    public void TryDecode_Empty_ReturnsFalse()
    {
        Assert.False(FrameCodec.TryDecode([], out _));
    }

    [Fact]
    public void TryDecode_FrameWithoutBody_ReturnsFalse()
    {
        // A valid protobuf message that only contains an unknown field.
        Assert.False(FrameCodec.TryDecode([0xF8, 0x07, 0x01], out _));
    }

    [Fact]
    public void TryDecode_LargeControlMessage_ReturnsFalse()
    {
        var frame = new Frame { Error = new Error { Code = ErrorCodes.Internal, Detail = new string('x', FrameCodec.MaxControlMessageSize) } };

        Assert.False(FrameCodec.TryDecode(FrameCodec.Encode(frame), out _));
    }

    [Fact]
    public void TryDecode_LargeSessionData_IsAccepted()
    {
        var frame = new Frame
        {
            SessionData = new SessionData
            {
                SessionId = "s",
                Channel = (uint)Channel.Video,
                Sequence = 1,
                Ciphertext = ByteString.CopyFrom(new byte[FrameCodec.MaxControlMessageSize * 4]),
            },
        };

        Assert.True(FrameCodec.TryDecode(FrameCodec.Encode(frame), out var decoded));
        Assert.Equal(Frame.BodyOneofCase.SessionData, decoded.BodyCase);
    }

    [Fact]
    public void TryDecode_AboveMaxMessageSize_ReturnsFalse()
    {
        Assert.False(FrameCodec.TryDecode(new byte[FrameCodec.MaxMessageSize + 1], out _));
    }
}
