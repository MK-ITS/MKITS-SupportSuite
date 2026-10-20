// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

/// <summary>Records what handlers send.</summary>
public sealed class RecordingContext : ISessionContext
{
    public ConcurrentQueue<(string? ViewerId, SessionPayload Payload)> Sent { get; } = new();

    public Task SendAsync(string? viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        Sent.Enqueue((viewerId, payload));
        return Task.CompletedTask;
    }

    public IReadOnlyList<VideoFrame> Frames(string viewerId) =>
        Sent.Where(s => s.ViewerId == viewerId && s.Payload.BodyCase == SessionPayload.BodyOneofCase.VideoFrame).Select(s => s.Payload.VideoFrame).ToList();
}

public sealed class FakeScreenSource : IScreenSource
{
    public List<MonitorDescriptor> Monitors { get; } =
    [
        new(1, "Primary", 0, 0, 1920, 1080, Primary: true),
        new(2, "Right", 1920, 0, 1280, 1024, Primary: false),
    ];

    /// <summary>Changes the image content (otherwise identical frames are skipped).</summary>
    public byte Content { get; set; } = 1;

    public int Captures { get; private set; }

    public bool Unavailable { get; set; }

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => Monitors;

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken)
    {
        Captures++;
        if (Unavailable)
        {
            return null;
        }

        var monitor = Monitors.Single(m => m.Id == monitorId);
        return new EncodedFrame(VideoCodec.Jpeg, monitor.Width, monitor.Height, [Content, (byte)monitorId, (byte)quality], Keyframe: true);
    }
}

public sealed class RecordingInjector : IInputInjector
{
    public List<string> Calls { get; } = [];

    public void MovePointer(int x, int y) => Calls.Add($"move {x},{y}");

    public void SetButton(PointerButtons button, bool down) => Calls.Add($"{button} {(down ? "down" : "up")}");

    public void Wheel(int deltaX, int deltaY) => Calls.Add($"wheel {deltaX},{deltaY}");

    public void Key(string code, bool down) => Calls.Add($"key {code} {(down ? "down" : "up")}");

    public void Text(string text) => Calls.Add($"text {text}");

    public void Special(SpecialKeyKind kind) => Calls.Add($"special {kind}");
}

public sealed class FakeClipboard : IClipboard
{
    public string? Text { get; set; }

    public int Writes { get; private set; }

    public string? GetText() => Text;

    public void SetText(string text)
    {
        Text = text;
        Writes++;
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
