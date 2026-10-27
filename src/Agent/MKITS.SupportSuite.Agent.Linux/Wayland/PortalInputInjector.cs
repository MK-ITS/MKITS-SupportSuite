// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>What <see cref="PortalInputInjector"/> sends (the portal session, or a recorder in tests).</summary>
internal interface IPortalInput
{
    IReadOnlyList<PortalStream> Streams { get; }

    void PointerMotionAbsolute(uint stream, double x, double y);

    void PointerButton(int evdevButton, bool pressed);

    void PointerAxisDiscrete(uint axis, int steps);

    void KeyboardKeycode(int evdevKey, bool pressed);

    void KeyboardKeysym(int keysym, bool pressed);
}

/// <summary>
/// Input on Wayland through the RemoteDesktop portal. Desktop coordinates (the monitors are the shared streams) are
/// turned into a position within the stream that contains them; keys go by physical position (evdev codes, as on
/// X11), text by keysym. The desktop decides what it accepts – only what the user allowed in its dialog.
/// </summary>
internal sealed class PortalInputInjector(IPortalInput portal) : IInputInjector
{
    // linux/input-event-codes.h
    internal const int ButtonLeft = 0x110;
    internal const int ButtonRight = 0x111;
    internal const int ButtonMiddle = 0x112;
    internal const int ButtonSide = 0x113;
    internal const int ButtonExtra = 0x114;

    private const uint AxisVertical = 0;
    private const uint AxisHorizontal = 1;

    public void MovePointer(int x, int y)
    {
        foreach (var stream in portal.Streams)
        {
            if (x >= stream.X && x < stream.X + stream.Width && y >= stream.Y && y < stream.Y + stream.Height)
            {
                portal.PointerMotionAbsolute(stream.NodeId, x - stream.X, y - stream.Y);
                return;
            }
        }
    }

    public void SetButton(PointerButtons button, bool down)
    {
        var code = button switch
        {
            PointerButtons.Left => ButtonLeft,
            PointerButtons.Right => ButtonRight,
            PointerButtons.Middle => ButtonMiddle,
            PointerButtons.Back => ButtonSide,
            PointerButtons.Forward => ButtonExtra,
            _ => 0,
        };
        if (code != 0)
        {
            portal.PointerButton(code, down);
        }
    }

    /// <summary>Positive <paramref name="deltaY"/> scrolls up (the portal's vertical axis points down).</summary>
    public void Wheel(int deltaX, int deltaY)
    {
        if (deltaY != 0)
        {
            portal.PointerAxisDiscrete(AxisVertical, -deltaY);
        }

        if (deltaX != 0)
        {
            portal.PointerAxisDiscrete(AxisHorizontal, deltaX);
        }
    }

    public void Key(string code, bool down)
    {
        if (KeyCodes.FromCode(code) is { } evdev)
        {
            portal.KeyboardKeycode(evdev, down);
        }
    }

    public void Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            var keysym = (int)X11InputInjector.KeysymFor(rune);
            portal.KeyboardKeysym(keysym, pressed: true);
            portal.KeyboardKeysym(keysym, pressed: false);
        }
    }

    public void Special(SpecialKeyKind kind)
    {
        string[] chord = kind switch
        {
            SpecialKeyKind.Win => ["MetaLeft"],
            SpecialKeyKind.AltTab => ["AltLeft", "Tab"],
            SpecialKeyKind.PrintScreen => ["PrintScreen"],
            SpecialKeyKind.CtrlAltDel => ["ControlLeft", "AltLeft", "Delete"],
            _ => [],
        };

        foreach (var code in chord)
        {
            Key(code, down: true);
        }

        foreach (var code in Enumerable.Reverse(chord))
        {
            Key(code, down: false);
        }
    }
}
