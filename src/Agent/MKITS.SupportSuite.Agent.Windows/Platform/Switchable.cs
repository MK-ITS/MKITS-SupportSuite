// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>Screen source that can be switched at runtime (own GDI capture ↔ privileged helper). A new side starts new streams (a keyframe).</summary>
public sealed class SwitchableScreenSource(IScreenSource initial) : IScreenSource
{
    private volatile IScreenSource _current = initial;

    public IScreenSource Current
    {
        get => _current;
        set => _current = value;
    }

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => _current.GetMonitors();

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken) => _current.Capture(monitorId, quality, cancellationToken);

    /// <summary>What the current side can stream; after a switch the viewer's choice is kept (see <see cref="CaptureStream"/>).</summary>
    public IReadOnlyCollection<VideoCodec> StreamCodecs => _current.StreamCodecs;

    /// <summary>
    /// The current side's stream; a side that cannot stream the codec (e.g. the helper after a switch) sends JPEG
    /// keyframes instead – the viewer decodes whatever codec a frame carries.
    /// </summary>
    public EncodedFrame? CaptureStream(uint monitorId, string streamKey, VideoCodec codec, int quality, bool keyframe, CancellationToken cancellationToken)
    {
        var current = _current;
        return current.StreamCodecs.Contains(codec)
            ? current.CaptureStream(monitorId, streamKey, codec, quality, keyframe, cancellationToken)
            : current.Capture(monitorId, quality, cancellationToken);
    }

    public void ReleaseStream(string streamKey) => _current.ReleaseStream(streamKey);
}

/// <summary>Input injector that can be switched at runtime (own SendInput ↔ privileged helper).</summary>
public sealed class SwitchableInputInjector(IInputInjector initial) : IInputInjector
{
    private volatile IInputInjector _current = initial;

    public IInputInjector Current
    {
        get => _current;
        set => _current = value;
    }

    public void MovePointer(int x, int y) => _current.MovePointer(x, y);

    public void SetButton(PointerButtons button, bool down) => _current.SetButton(button, down);

    public void Wheel(int deltaX, int deltaY) => _current.Wheel(deltaX, deltaY);

    public void Key(string code, bool down) => _current.Key(code, down);

    public void Text(string text) => _current.Text(text);

    public void Special(SpecialKeyKind kind) => _current.Special(kind);
}
