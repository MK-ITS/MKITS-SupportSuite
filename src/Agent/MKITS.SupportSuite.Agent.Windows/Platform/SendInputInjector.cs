// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Native;
using MKITS.SupportSuite.Common.Protocol.V0;
using static MKITS.SupportSuite.Agent.Windows.Native.NativeMethods;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>
/// Injects input with <c>SendInput</c>: absolute coordinates on the virtual desktop, physical keys as scan codes
/// (the customer's layout produces the character), text as Unicode. Input into elevated windows and the secure
/// desktop is blocked by Windows (UIPI) unless the client runs elevated / as the service helper (Phase 8.4).
/// </summary>
public sealed class SendInputInjector : IInputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.Input>();

    public void MovePointer(int x, int y)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);

        Mouse(
            (int)((x - left) * 65535L / width),
            (int)((y - top) * 65535L / height),
            0,
            MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk);
    }

    public void SetButton(PointerButtons button, bool down)
    {
        var (flags, data) = button switch
        {
            PointerButtons.Left => (down ? MouseEventLeftDown : MouseEventLeftUp, 0u),
            PointerButtons.Right => (down ? MouseEventRightDown : MouseEventRightUp, 0u),
            PointerButtons.Middle => (down ? MouseEventMiddleDown : MouseEventMiddleUp, 0u),
            PointerButtons.Back => (down ? MouseEventXDown : MouseEventXUp, XButton1),
            PointerButtons.Forward => (down ? MouseEventXDown : MouseEventXUp, XButton2),
            _ => (0u, 0u),
        };

        if (flags != 0)
        {
            Mouse(0, 0, data, flags);
        }
    }

    public void Wheel(int deltaX, int deltaY)
    {
        if (deltaY != 0)
        {
            Mouse(0, 0, unchecked((uint)(deltaY * WheelDelta)), MouseEventWheel);
        }

        if (deltaX != 0)
        {
            Mouse(0, 0, unchecked((uint)(deltaX * WheelDelta)), MouseEventHWheel);
        }
    }

    public void Key(string code, bool down)
    {
        if (ScanCodes.FromCode(code) is not { } scanCode)
        {
            return;
        }

        var flags = KeyEventScanCode | (down ? 0 : KeyEventKeyUp) | (ScanCodes.IsExtended(scanCode) ? KeyEventExtendedKey : 0);
        Send([Keyboard((ushort)(scanCode & 0xFF), flags)]);
    }

    public void Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var inputs = new List<NativeMethods.Input>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(Keyboard(unit, KeyEventUnicode));
            inputs.Add(Keyboard(unit, KeyEventUnicode | KeyEventKeyUp));
        }

        Send([.. inputs]);
    }

    public void Special(SpecialKeyKind kind)
    {
        switch (kind)
        {
            case SpecialKeyKind.Win:
                Chord("MetaLeft");
                break;
            case SpecialKeyKind.AltTab:
                Chord("AltLeft", "Tab");
                break;
            case SpecialKeyKind.PrintScreen:
                Chord("PrintScreen");
                break;
            default:
                // Ctrl+Alt+Del (secure attention sequence) can only be raised by the service via SendSAS (Phase 8.4).
                break;
        }
    }

    private void Chord(params string[] codes)
    {
        foreach (var code in codes)
        {
            Key(code, down: true);
        }

        for (var i = codes.Length - 1; i >= 0; i--)
        {
            Key(codes[i], down: false);
        }
    }

    private static NativeMethods.Input Keyboard(ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = scanCode, Flags = flags } },
    };

    private static void Mouse(int x, int y, uint data, uint flags) =>
        Send([new NativeMethods.Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, MouseData = data, Flags = flags } } }]);

    private static void Send(NativeMethods.Input[] inputs)
    {
        if (inputs.Length > 0)
        {
            _ = SendInput((uint)inputs.Length, inputs, InputSize);
        }
    }
}
