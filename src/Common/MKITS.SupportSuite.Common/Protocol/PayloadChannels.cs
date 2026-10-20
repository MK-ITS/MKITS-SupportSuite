// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Common.Protocol;

/// <summary>Maps session-plane payloads to their channel (docs/protocol.md §5).</summary>
public static class PayloadChannels
{
    public static Channel For(SessionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload.BodyCase switch
        {
            SessionPayload.BodyOneofCase.VideoFrame => Channel.Video,
            SessionPayload.BodyOneofCase.PointerEvent or SessionPayload.BodyOneofCase.KeyEvent or SessionPayload.BodyOneofCase.SpecialKey => Channel.Input,
            SessionPayload.BodyOneofCase.MonitorList or SessionPayload.BodyOneofCase.SelectMonitor
                or SessionPayload.BodyOneofCase.QualitySettings or SessionPayload.BodyOneofCase.FrameAck => Channel.Display,
            SessionPayload.BodyOneofCase.ClipboardText => Channel.Clipboard,
            SessionPayload.BodyOneofCase.FileOffer or SessionPayload.BodyOneofCase.FileDecision or SessionPayload.BodyOneofCase.FileChunk
                or SessionPayload.BodyOneofCase.FileComplete or SessionPayload.BodyOneofCase.FileCancel => Channel.File,
            SessionPayload.BodyOneofCase.ChatMessage => Channel.Chat,
            SessionPayload.BodyOneofCase.ShellOpen or SessionPayload.BodyOneofCase.ShellData
                or SessionPayload.BodyOneofCase.ShellResize or SessionPayload.BodyOneofCase.ShellExit => Channel.Shell,
            _ => Channel.Unspecified,
        };
    }

    /// <summary><c>true</c> if a payload arriving on <paramref name="channel"/> really belongs there (rejects re-labelled frames).</summary>
    public static bool Matches(uint channel, SessionPayload payload) => channel != 0 && (uint)For(payload) == channel;
}
