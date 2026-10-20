// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>Mouse buttons as in <c>PointerEvent.buttons</c> (bit 0 left, 1 right, 2 middle, 3 back, 4 forward).</summary>
[Flags]
public enum PointerButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
    Back = 8,
    Forward = 16,
}

/// <summary>Platform input injection (Windows: SendInput, Linux: XTest / portal, Android: accessibility).</summary>
public interface IInputInjector
{
    /// <summary>Moves the pointer to absolute virtual-desktop coordinates.</summary>
    void MovePointer(int x, int y);

    void SetButton(PointerButtons button, bool down);

    /// <summary>Wheel in notches (positive = up / right).</summary>
    void Wheel(int deltaX, int deltaY);

    /// <summary>Physical key by W3C <c>KeyboardEvent.code</c> (e.g. <c>KeyA</c>, <c>ShiftLeft</c>).</summary>
    void Key(string code, bool down);

    /// <summary>Types text independent of the keyboard layout.</summary>
    void Text(string text);

    void Special(SpecialKeyKind kind);
}

/// <summary>
/// Turns viewer input into injected input: normalized coordinates (0..65535 per monitor) → desktop pixels,
/// button masks → press/release transitions. Keys still pressed when a viewer leaves are released, so nothing
/// stays stuck on the customer's machine. The relay only accepts input from the controlling viewer.
/// </summary>
public sealed class InputHandler(IInputInjector injector, IScreenSource screens) : ISessionChannelHandler
{
    private const int MaxTextLength = 4096;

    private static readonly PointerButtons[] AllButtons =
        [PointerButtons.Left, PointerButtons.Right, PointerButtons.Middle, PointerButtons.Back, PointerButtons.Forward];

    private readonly object _lock = new();
    private readonly Dictionary<string, ViewerInput> _viewers = new(StringComparer.Ordinal);

    public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_lock)
        {
            var state = Get(viewerId);
            switch (payload.BodyCase)
            {
                case SessionPayload.BodyOneofCase.PointerEvent:
                    OnPointer(state, payload.PointerEvent);
                    break;
                case SessionPayload.BodyOneofCase.KeyEvent:
                    OnKey(state, payload.KeyEvent);
                    break;
                case SessionPayload.BodyOneofCase.SpecialKey when payload.SpecialKey.Kind != SpecialKeyKind.Unspecified:
                    injector.Special(payload.SpecialKey.Kind);
                    break;
                default:
                    break;
            }
        }

        return Task.CompletedTask;
    }

    public void OnViewerGone(string viewerId)
    {
        lock (_lock)
        {
            if (!_viewers.Remove(viewerId, out var state))
            {
                return;
            }

            foreach (var code in state.PressedKeys)
            {
                injector.Key(code, down: false);
            }

            foreach (var button in AllButtons.Where(b => state.Buttons.HasFlag(b)))
            {
                injector.SetButton(button, down: false);
            }
        }
    }

    private void OnPointer(ViewerInput state, PointerEvent pointer)
    {
        var monitor = screens.GetMonitors().FirstOrDefault(m => m.Id == pointer.MonitorId);
        if (monitor is null)
        {
            return;
        }

        var x = monitor.Left + (int)(Math.Min(pointer.X, 65535u) * (long)(monitor.Width - 1) / 65535);
        var y = monitor.Top + (int)(Math.Min(pointer.Y, 65535u) * (long)(monitor.Height - 1) / 65535);
        injector.MovePointer(x, y);

        var buttons = (PointerButtons)(pointer.Buttons & 0x1F);
        foreach (var button in AllButtons)
        {
            var wasDown = state.Buttons.HasFlag(button);
            var isDown = buttons.HasFlag(button);
            if (wasDown != isDown)
            {
                injector.SetButton(button, isDown);
            }
        }

        state.Buttons = buttons;

        if (pointer.WheelX != 0 || pointer.WheelY != 0)
        {
            injector.Wheel(Math.Clamp(pointer.WheelX, -10, 10), Math.Clamp(pointer.WheelY, -10, 10));
        }
    }

    private void OnKey(ViewerInput state, KeyEvent key)
    {
        if (key.TextMode)
        {
            if (key.Down && !string.IsNullOrEmpty(key.Key) && key.Key.Length <= MaxTextLength)
            {
                injector.Text(key.Key);
            }

            return;
        }

        if (string.IsNullOrEmpty(key.Code) || key.Code.Length > 32)
        {
            return;
        }

        if (key.Down)
        {
            state.PressedKeys.Add(key.Code);
        }
        else if (!state.PressedKeys.Remove(key.Code))
        {
            return; // release without press – ignore (e.g. focus changes in the viewer)
        }

        injector.Key(key.Code, key.Down);
    }

    private ViewerInput Get(string viewerId)
    {
        if (!_viewers.TryGetValue(viewerId, out var state))
        {
            state = new ViewerInput();
            _viewers[viewerId] = state;
        }

        return state;
    }

    private sealed class ViewerInput
    {
        public PointerButtons Buttons { get; set; }

        public HashSet<string> PressedKeys { get; } = new(StringComparer.Ordinal);
    }
}
