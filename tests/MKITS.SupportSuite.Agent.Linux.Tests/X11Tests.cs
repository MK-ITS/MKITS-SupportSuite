// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Native;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

public sealed class X11Tests(XvfbFixture xvfb) : IClassFixture<XvfbFixture>
{
    [Fact]
    public void Monitors_AndCapture_ProduceJpegOfTheScreenSize()
    {
        using var screen = new X11ScreenSource(xvfb.RequireDisplay());

        var monitor = Assert.Single(screen.GetMonitors());
        var frame = screen.Capture(monitor.Id, 60, CancellationToken.None);

        Assert.True(monitor.Primary);
        Assert.Equal((1280, 800), (monitor.Width, monitor.Height));
        Assert.NotNull(frame);
        Assert.Equal(VideoCodec.Jpeg, frame.Codec);
        Assert.Equal((1280, 800), (frame.Width, frame.Height));
        Assert.Equal(0xFF, frame.Data[0]); // JPEG SOI marker
        Assert.Equal(0xD8, frame.Data[1]);
    }

    [Fact]
    public void Capture_UnknownMonitor_ReturnsNull()
    {
        using var screen = new X11ScreenSource(xvfb.RequireDisplay());

        Assert.Null(screen.Capture(99, 60, CancellationToken.None));
    }

    [Fact]
    public void MovePointer_MovesTheXPointer()
    {
        var display = xvfb.RequireDisplay();
        var input = new X11InputInjector(display);

        input.MovePointer(321, 123);

        lock (display.Gate)
        {
            Assert.True(X11Native.XQueryPointer(display.Handle, display.Root, out _, out _, out var x, out var y, out _, out _, out _));
            Assert.Equal((321, 123), (x, y));
        }
    }

    [Fact]
    public void KeyCodes_MatchTheServersEvdevKeycodes()
    {
        var display = xvfb.RequireDisplay();
        lock (display.Gate)
        {
            // KeyA → evdev 30 → X keycode 38, which carries the keysym 'a' (0x61) on an evdev keymap.
            Assert.Equal(38, KeyCodes.FromCode("KeyA")!.Value + X11InputInjector.EvdevOffset);
            Assert.Equal(38, X11Native.XKeysymToKeycode(display.Handle, 0x61));
            Assert.Equal(X11Native.XKeysymToKeycode(display.Handle, 0xFF51 /* Left */), KeyCodes.FromCode("ArrowLeft")!.Value + X11InputInjector.EvdevOffset);
        }
    }

    [Fact]
    public void Buttons_Keys_TextAndSpecialKeys_CauseNoXErrors()
    {
        var input = new X11InputInjector(xvfb.RequireDisplay());
        var errors = X11Display.Errors;

        input.SetButton(PointerButtons.Left, down: true);
        input.SetButton(PointerButtons.Left, down: false);
        input.Wheel(1, -2);
        input.Key("ShiftLeft", down: true);
        input.Key("KeyA", down: true);
        input.Key("KeyA", down: false);
        input.Key("ShiftLeft", down: false);
        input.Key("NoSuchKey", down: true);
        input.Text("Grüße €\n");
        input.Special(SpecialKeyKind.AltTab);

        Assert.Equal(errors, X11Display.Errors);
    }

    [Theory]
    [InlineData("KeyA", 30)]
    [InlineData("Digit1", 2)]
    [InlineData("IntlRo", 89)]
    [InlineData("IntlYen", 124)]
    [InlineData("ArrowUp", 103)]
    [InlineData("MetaLeft", 125)]
    [InlineData("NumpadEnter", 96)]
    public void KeyCodes_MapToEvdev(string code, int evdev) => Assert.Equal(evdev, KeyCodes.FromCode(code));

    [Theory]
    [InlineData("a", 0x61)]
    [InlineData("ü", 0xFC)]
    [InlineData("€", 0x010020AC)]
    [InlineData("\n", 0xFF0D)]
    public void KeysymFor_UsesLatin1AndUnicodeRanges(string text, long keysym) =>
        Assert.Equal((nuint)keysym, X11InputInjector.KeysymFor(text.EnumerateRunes().First()));
}
