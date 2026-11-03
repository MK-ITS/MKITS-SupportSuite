// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>
/// The helper's side of <see cref="HelperProtocol"/>: decodes one request, runs it on the screen source or the input
/// injector and builds the response. Runs as SYSTEM, so every field is checked: monitor ids are looked up by the
/// source, quality, wheel deltas and terminal sizes are clamped, strings are length-limited, H.264 streams are limited
/// in number and key length (each holds an encoder), and only the known shell types can be started. Not thread-safe – the helper calls it from its desktop thread.
/// </summary>
/// <param name="screen">Capture for the client.</param>
/// <param name="input">Input injection for the client.</param>
/// <param name="requestCtrlAltDel">Asks the service for Ctrl+Alt+Del (SendSAS only works there).</param>
/// <param name="shells">Elevated shells for the client (ADR-0028); <c>null</c> refuses them.</param>
internal sealed class HelperRequestHandler(IScreenSource screen, IInputInjector input, Action requestCtrlAltDel, IHelperShells? shells = null)
{
    /// <summary>Stream keys are viewer ids (GUID-like); anything longer is refused.</summary>
    internal const int MaxStreamKeyLength = 64;

    /// <summary>More open streams than viewers a session can have is a misbehaving client.</summary>
    internal const int MaxStreams = 8;

    private readonly HashSet<string> _streams = new(StringComparer.Ordinal);

    /// <summary>Returned for <see cref="HelperRequest.Shutdown"/>: the helper ends.</summary>
    public static byte[] Shutdown { get; } = [];

    /// <summary>The response to send, <c>null</c> for requests without one, or <see cref="Shutdown"/>.</summary>
    /// <exception cref="InvalidDataException">Malformed request (the helper then drops the connection).</exception>
    public byte[]? Handle(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var reader = new BinaryReader(new MemoryStream(message));
        var type = (HelperRequest)reader.ReadByte();
        switch (type)
        {
            case HelperRequest.GetMonitors:
                return Monitors();
            case HelperRequest.Capture:
                var frame = screen.Capture(reader.ReadUInt32(), Math.Clamp(reader.ReadInt32(), 1, 100), CancellationToken.None);
                return Frame(HelperRequest.Capture, frame);
            case HelperRequest.StreamCodecs:
                var codecs = screen.StreamCodecs;
                return HelperProtocol.Build(HelperRequest.StreamCodecs, w =>
                {
                    w.Write(codecs.Count);
                    foreach (var codec in codecs)
                    {
                        w.Write((int)codec);
                    }
                });
            case HelperRequest.CaptureStream:
                return Frame(HelperRequest.CaptureStream, CaptureStream(reader));
            case HelperRequest.ReleaseStream:
                var key = StreamKey(reader);
                if (_streams.Remove(key))
                {
                    screen.ReleaseStream(key);
                }

                return null;
            case HelperRequest.MovePointer:
                input.MovePointer(reader.ReadInt32(), reader.ReadInt32());
                return null;
            case HelperRequest.SetButton:
                input.SetButton((PointerButtons)reader.ReadInt32(), reader.ReadBoolean());
                return null;
            case HelperRequest.Wheel:
                input.Wheel(Math.Clamp(reader.ReadInt32(), -10, 10), Math.Clamp(reader.ReadInt32(), -10, 10));
                return null;
            case HelperRequest.Key:
                input.Key(HelperProtocol.ReadString(reader), reader.ReadBoolean());
                return null;
            case HelperRequest.Text:
                input.Text(HelperProtocol.ReadString(reader));
                return null;
            case HelperRequest.Special:
                var kind = (SpecialKeyKind)reader.ReadInt32();
                if (kind == SpecialKeyKind.CtrlAltDel)
                {
                    requestCtrlAltDel(); // SendSAS only works from the service
                }
                else
                {
                    input.Special(kind);
                }

                return null;
            case HelperRequest.ShellStart:
                var shellType = HelperProtocol.ReadString(reader);
                var columns = Math.Clamp(reader.ReadInt32(), 20, 500);
                var rows = Math.Clamp(reader.ReadInt32(), 5, 200);
                var started = shells is not null && shells.Types.Contains(shellType, StringComparer.Ordinal) ? shells.Start(shellType, columns, rows) : null;
                return HelperProtocol.Build(HelperRequest.ShellStart, w =>
                {
                    w.Write(started is not null);
                    if (started is { } handles)
                    {
                        w.Write(handles.Id);
                        w.Write(handles.Input);
                        w.Write(handles.Output);
                        w.Write(handles.Process);
                    }
                });
            case HelperRequest.ShellResize:
                var resizeId = reader.ReadInt32();
                shells?.Resize(resizeId, Math.Clamp(reader.ReadInt32(), 20, 500), Math.Clamp(reader.ReadInt32(), 5, 200));
                return null;
            case HelperRequest.ShellClose:
                shells?.Close(reader.ReadInt32());
                return null;
            case HelperRequest.Shutdown:
                return Shutdown;
            default:
                throw new InvalidDataException("Unknown request.");
        }
    }

    private static string StreamKey(BinaryReader reader)
    {
        var key = HelperProtocol.ReadString(reader);
        return key.Length is > 0 and <= MaxStreamKeyLength ? key : throw new InvalidDataException("Invalid stream key.");
    }

    private static byte[] Frame(HelperRequest type, EncodedFrame? frame) => HelperProtocol.Build(type, w =>
    {
        w.Write(frame is not null);
        if (frame is not null)
        {
            w.Write((int)frame.Codec);
            w.Write(frame.Width);
            w.Write(frame.Height);
            w.Write(frame.Keyframe);
            w.Write(frame.Data.Length);
            w.Write(frame.Data);
        }
    });

    private byte[] Monitors()
    {
        var monitors = screen.GetMonitors();
        return HelperProtocol.Build(HelperRequest.GetMonitors, w =>
        {
            w.Write(monitors.Count);
            foreach (var m in monitors)
            {
                w.Write(m.Id);
                w.Write(m.Name);
                w.Write(m.Left);
                w.Write(m.Top);
                w.Write(m.Width);
                w.Write(m.Height);
                w.Write(m.Primary);
            }
        });
    }

    private EncodedFrame? CaptureStream(BinaryReader reader)
    {
        var monitorId = reader.ReadUInt32();
        var key = StreamKey(reader);
        var codec = (VideoCodec)reader.ReadInt32();
        var quality = Math.Clamp(reader.ReadInt32(), 1, 100);
        var keyframe = reader.ReadBoolean();
        if (!screen.StreamCodecs.Contains(codec) || (!_streams.Contains(key) && _streams.Count >= MaxStreams))
        {
            return null;
        }

        _streams.Add(key);
        return screen.CaptureStream(monitorId, key, codec, quality, keyframe, CancellationToken.None);
    }
}
