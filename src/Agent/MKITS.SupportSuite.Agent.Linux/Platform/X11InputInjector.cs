// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Native;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>
/// Input on X11 through the XTest extension. Keys are injected by physical position (evdev key code + 8, the keycode
/// numbering of current X servers), so the result depends on the keyboard layout of this computer – like a local key
/// press. Text mode maps each character temporarily onto a spare keycode, independent of the layout.
/// </summary>
internal sealed class X11InputInjector(X11Display display) : IInputInjector
{
    /// <summary>X keycodes are evdev codes plus 8 (xkeyboard-config "evdev" keycodes).</summary>
    internal const int EvdevOffset = 8;

    private int _spareKeycode = -1;

    public void MovePointer(int x, int y)
    {
        lock (display.Gate)
        {
            _ = X11Native.XTestFakeMotionEvent(display.Handle, -1, x, y, 0);
            _ = X11Native.XFlush(display.Handle);
        }
    }

    public void SetButton(PointerButtons button, bool down)
    {
        var x11Button = button switch
        {
            PointerButtons.Left => 1u,
            PointerButtons.Middle => 2u,
            PointerButtons.Right => 3u,
            PointerButtons.Back => 8u,
            PointerButtons.Forward => 9u,
            _ => 0u,
        };
        if (x11Button == 0)
        {
            return;
        }

        lock (display.Gate)
        {
            _ = X11Native.XTestFakeButtonEvent(display.Handle, x11Button, down, 0);
            _ = X11Native.XFlush(display.Handle);
        }
    }

    /// <summary>One click of button 4/5 (vertical) or 6/7 (horizontal) per notch; positive <paramref name="deltaY"/> scrolls up.</summary>
    public void Wheel(int deltaX, int deltaY)
    {
        lock (display.Gate)
        {
            Click(deltaY > 0 ? 4u : 5u, Math.Abs(deltaY));
            Click(deltaX > 0 ? 7u : 6u, Math.Abs(deltaX));
            _ = X11Native.XFlush(display.Handle);
        }
    }

    public void Key(string code, bool down)
    {
        if (KeyCodes.FromCode(code) is not { } evdev)
        {
            return;
        }

        lock (display.Gate)
        {
            _ = X11Native.XTestFakeKeyEvent(display.Handle, (uint)(evdev + EvdevOffset), down, 0);
            _ = X11Native.XFlush(display.Handle);
        }
    }

    public void Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (display.Gate)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                TypeKeysym(KeysymFor(rune));
            }

            _ = X11Native.XFlush(display.Handle);
        }
    }

    public void Special(SpecialKeyKind kind)
    {
        string[] chord = kind switch
        {
            SpecialKeyKind.Win => ["MetaLeft"],
            SpecialKeyKind.AltTab => ["AltLeft", "Tab"],
            SpecialKeyKind.PrintScreen => ["PrintScreen"],
            SpecialKeyKind.CtrlAltDel => ["ControlLeft", "AltLeft", "Delete"], // desktop-specific action (often log-out dialog)
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

    /// <summary>Latin-1 characters are their own keysyms; everything else uses the Unicode keysym range.</summary>
    internal static nuint KeysymFor(System.Text.Rune rune) => rune.Value switch
    {
        '\n' or '\r' => 0xFF0D, // Return
        '\t' => 0xFF09,         // Tab
        '\b' => 0xFF08,         // BackSpace
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF => (nuint)rune.Value,
        _ => (nuint)(0x01000000 | rune.Value),
    };

    private void Click(uint button, int count)
    {
        for (var i = 0; i < Math.Min(count, 10); i++)
        {
            _ = X11Native.XTestFakeButtonEvent(display.Handle, button, true, 0);
            _ = X11Native.XTestFakeButtonEvent(display.Handle, button, false, 0);
        }
    }

    /// <summary>Maps the keysym onto a spare keycode (both shift levels), presses it and restores the mapping.</summary>
    private void TypeKeysym(nuint keysym)
    {
        var keycode = SpareKeycode();
        if (keycode < 0)
        {
            return;
        }

        _ = X11Native.XChangeKeyboardMapping(display.Handle, keycode, 2, [keysym, keysym], 1);
        _ = X11Native.XSync(display.Handle, 0);
        _ = X11Native.XTestFakeKeyEvent(display.Handle, (uint)keycode, true, 0);
        _ = X11Native.XTestFakeKeyEvent(display.Handle, (uint)keycode, false, 0);
        _ = X11Native.XSync(display.Handle, 0);
        _ = X11Native.XChangeKeyboardMapping(display.Handle, keycode, 2, [0, 0], 1);
        _ = X11Native.XSync(display.Handle, 0);
    }

    /// <summary>The highest keycode without any keysym (found once).</summary>
    private int SpareKeycode()
    {
        if (_spareKeycode >= 0)
        {
            return _spareKeycode;
        }

        _ = X11Native.XDisplayKeycodes(display.Handle, out var min, out var max);
        var mapping = X11Native.XGetKeyboardMapping(display.Handle, (byte)min, max - min + 1, out var perKeycode);
        if (mapping == 0)
        {
            return -1;
        }

        try
        {
            for (var keycode = max; keycode >= min; keycode--)
            {
                var empty = true;
                for (var level = 0; level < perKeycode && empty; level++)
                {
                    empty = Marshal.ReadIntPtr(mapping, (((keycode - min) * perKeycode) + level) * IntPtr.Size) == 0;
                }

                if (empty)
                {
                    _spareKeycode = keycode;
                    break;
                }
            }
        }
        finally
        {
            _ = X11Native.XFree(mapping);
        }

        return _spareKeycode;
    }
}
