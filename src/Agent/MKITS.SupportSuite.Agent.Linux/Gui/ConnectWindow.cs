// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>Asks for server and session code when the file name does not carry them (renamed AppImage).</summary>
internal sealed class ConnectWindow : Window
{
    private readonly TextBox _server = new() { Watermark = "support.example.com", Width = 320 };
    private readonly TextBox _code = new() { Watermark = "1234 5678", Width = 320 };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.Firebrick, IsVisible = false };

    public ConnectWindow(Action<AdHocClientTarget> connect)
    {
        Title = Ui.Get("Connect_Title");
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var button = new Button { Content = Ui.Get("Connect_Button"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) =>
        {
            if (Parse(_server.Text, _code.Text) is { } target)
            {
                connect(target);
                Close();
            }
            else
            {
                _error.Text = Ui.Get("Connect_Invalid");
                _error.IsVisible = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Ui.Get("Connect_Server") },
                _server,
                new TextBlock { Text = Ui.Get("Connect_Code") },
                _code,
                _error,
                button,
            },
        };
    }

    /// <summary>Accepts a host name (optionally with port or https:// prefix) and a numeric code.</summary>
    internal static AdHocClientTarget? Parse(string? server, string? code)
    {
        var normalizedCode = SessionCodeFormat.Normalize(code);
        if (normalizedCode is null || string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var value = server.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !AdHocClientFileName.IsValidHost(uri.Host))
        {
            return null;
        }

        return new AdHocClientTarget(uri.Host.ToLowerInvariant(), uri.IsDefaultPort ? null : uri.Port, normalizedCode);
    }
}
