// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>A small modal question (consent, incoming file) or message (errors). Closing it counts as "no".</summary>
internal sealed class QuestionWindow : Window
{
    public QuestionWindow(string title, string text, string yes, string? no)
    {
        Title = title;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var yesButton = new Button { Content = yes, IsDefault = no is null };
        yesButton.Click += (_, _) => Close(true);
        buttons.Children.Add(yesButton);
        if (no is not null)
        {
            var noButton = new Button { Content = no, IsCancel = true, IsDefault = true }; // "no" is the safe default
            noButton.Click += (_, _) => Close(false);
            buttons.Children.Add(noButton);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            MaxWidth = 460,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
    }
}
