// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>Screen and input of the desktop the ad-hoc client runs on: X11 directly, or Wayland through the portal.</summary>
internal interface IDesktopAccess : IAsyncDisposable
{
    IScreenSource Screen { get; }

    IInputInjector Input { get; }
}

/// <summary>Why the desktop could not be opened; <see cref="Code"/> maps to an <c>Error_*</c> text.</summary>
internal sealed class DesktopUnavailableException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>X11: capture and XTest input on the given display (owned by the caller).</summary>
internal sealed class X11DesktopAccess(X11Display display) : IDesktopAccess
{
    private readonly X11ScreenSource _screen = new(display);

    public IScreenSource Screen => _screen;

    public IInputInjector Input { get; } = new X11InputInjector(display);

    public ValueTask DisposeAsync()
    {
        _screen.Dispose();
        return ValueTask.CompletedTask;
    }
}
