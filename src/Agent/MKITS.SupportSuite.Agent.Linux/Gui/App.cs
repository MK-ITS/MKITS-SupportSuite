// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>Avalonia application (code only, no XAML). The first window comes from the factory.</summary>
internal sealed class App(Func<Window> createMainWindow) : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = createMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Runs the UI until the last window is closed.</summary>
    public static int Run(Func<Window> createMainWindow, string[] args) =>
        AppBuilder.Configure(() => new App(createMainWindow))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}
