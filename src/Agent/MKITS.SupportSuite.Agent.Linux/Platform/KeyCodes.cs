// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>
/// W3C <c>KeyboardEvent.code</c> → Linux evdev key code. Non-extended PC scan codes (set 1) are identical to evdev
/// codes except a few international keys; the extended (<c>0xE0</c>) keys have their own evdev numbers.
/// </summary>
internal static class KeyCodes
{
    private static readonly Dictionary<ushort, int> Different = new()
    {
        [0x73] = 89,    // KEY_RO (IntlRo)
        [0x7D] = 124,   // KEY_YEN (IntlYen)
        [0xE01C] = 96,  // KEY_KPENTER
        [0xE01D] = 97,  // KEY_RIGHTCTRL
        [0xE035] = 98,  // KEY_KPSLASH
        [0xE037] = 99,  // KEY_SYSRQ (PrintScreen)
        [0xE038] = 100, // KEY_RIGHTALT
        [0xE045] = 69,  // KEY_NUMLOCK
        [0xE047] = 102, // KEY_HOME
        [0xE048] = 103, // KEY_UP
        [0xE049] = 104, // KEY_PAGEUP
        [0xE04B] = 105, // KEY_LEFT
        [0xE04D] = 106, // KEY_RIGHT
        [0xE04F] = 107, // KEY_END
        [0xE050] = 108, // KEY_DOWN
        [0xE051] = 109, // KEY_PAGEDOWN
        [0xE052] = 110, // KEY_INSERT
        [0xE053] = 111, // KEY_DELETE
        [0xE05B] = 125, // KEY_LEFTMETA
        [0xE05C] = 126, // KEY_RIGHTMETA
        [0xE05D] = 127, // KEY_COMPOSE (context menu)
    };

    public static int? FromCode(string? code)
    {
        if (ScanCodes.FromCode(code) is not { } scanCode)
        {
            return null;
        }

        if (Different.TryGetValue(scanCode, out var evdev))
        {
            return evdev;
        }

        return ScanCodes.IsExtended(scanCode) ? null : scanCode;
    }
}
