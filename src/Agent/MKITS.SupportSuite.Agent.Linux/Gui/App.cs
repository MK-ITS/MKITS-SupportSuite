// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>Avalonia application (code only, no XAML). What it shows first comes from the start callback.</summary>
internal sealed class App(Action<IClassicDesktopStyleApplicationLifetime> start) : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Runs the UI until the last window is closed.</summary>
    public static int Run(Func<Window> createMainWindow, string[] args) => Run(desktop =>
    {
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        desktop.MainWindow = createMainWindow();
    }, args);

    /// <summary>
    /// Installed agent: the session window of the service's session, possibly hidden ("silent" policy); the UI runs
    /// until the service ends the session.
    /// </summary>
    public static int RunBridged(Stream stream, string[] args) => Run(desktop =>
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = SessionWindow.StartBridged(stream, () => desktop.Shutdown());
    }, args);

    private static int Run(Action<IClassicDesktopStyleApplicationLifetime> start, string[] args) =>
        AppBuilder.Configure(() => new App(start))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}
