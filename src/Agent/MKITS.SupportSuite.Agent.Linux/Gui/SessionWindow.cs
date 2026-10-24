// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Linux.Platform;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>
/// The always-visible session window of the Linux ad-hoc client (ADR-0014: a visible indicator and "End session"
/// can never be hidden): status, supporters, security code, chat, file transfer and the end button. Runs the session
/// with X11 capture/input, clipboard, files and the PTY shell. Calls from background threads are posted to the UI thread.
/// </summary>
internal sealed class SessionWindow : Window, IAgentUserInterface, IChatPresenter, IClipboard, IFileTransferUi
{
    private readonly TextBlock _status = new() { FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _supporters = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _securityCode = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ObservableCollection<string> _chatLines = [];
    private readonly ListBox _chatLog = new() { Height = 160 };
    private readonly TextBox _chatInput = new() { Width = 300 };
    private readonly Button _send = new() { Content = Ui.Get("Chat_Send"), IsDefault = true };
    private readonly Button _sendFile = new() { Content = Ui.Get("File_Send"), IsEnabled = false };
    private readonly Button _end = new() { Content = Ui.Get("EndSession"), Background = Brushes.Firebrick, Foreground = Brushes.White };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _closing = new();
    private readonly AgentSessionOptions _options;
    private readonly X11Display _display;
    private AgentSession? _session;
    private ChatHandler? _chat;
    private FileTransferHandler? _files;
    private ClipboardHandler? _clipboard;
    private string? _clipboardText;
    private bool _ended;

    public SessionWindow(AgentSessionOptions options, X11Display display)
    {
        _options = options;
        _display = display;

        Title = Ui.Get("App_Title");
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = 420;

        _chatLog.ItemsSource = _chatLines;
        var chatRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _chatInput, _send } };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children = { _status, _supporters, _securityCode, _chatLog, chatRow, _sendFile, _end },
        };

        _send.Click += async (_, _) => await SendChatAsync();
        _sendFile.Click += async (_, _) => await SendFileAsync();
        _end.Click += async (_, _) => await EndAsync();
        _clipboardTimer.Tick += async (_, _) => await PollClipboardAsync();
        Opened += async (_, _) => await RunAsync();
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ IAgentUserInterface

    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var text = Ui.Format("Consent_Text", prompt.SupporterDisplayName, prompt.OperatorName, prompt.ServerHost,
            string.Join(", ", prompt.Permissions.Select(p => Ui.Get("Permission_" + p))));
        return AskAsync(Ui.Get("Consent_Title"), text, Ui.Get("Consent_Allow"), Ui.Get("Consent_Deny"), cancellationToken);
    }

    public void OnStatusChanged(AgentStatus status, string? detail) => Post(() =>
    {
        _status.Text = status switch
        {
            AgentStatus.Connecting => Ui.Format("Status_Connecting", detail),
            AgentStatus.WaitingForSupporter => Ui.Format("Status_WaitingForSupporter", detail),
            AgentStatus.AwaitingConsent => Ui.Get("Status_AwaitingConsent"),
            AgentStatus.Active => Ui.Get("Gui_Status_Active"),
            AgentStatus.Reconnecting => Ui.Get("Status_Reconnecting"),
            _ => detail is not null && string.Equals(detail, detail.ToUpperInvariant(), StringComparison.Ordinal)
                ? Ui.ForError(detail)
                : Ui.Get("Gui_Status_Ended"),
        };

        if (status == AgentStatus.Ended)
        {
            _ended = true;
            _end.IsEnabled = false;
            _send.IsEnabled = false;
            _clipboardTimer.Stop();
        }
        else if (status == AgentStatus.Active)
        {
            _clipboardTimer.Start();
        }

        _sendFile.IsEnabled = status == AgentStatus.Active;
    });

    public void OnSupportersChanged(IReadOnlyList<string> displayNames) => Post(() =>
        _supporters.Text = displayNames.Count == 0 ? string.Empty : Ui.Format("Supporters", string.Join(", ", displayNames)));

    public void OnSecurityCode(string supporterDisplayName, string securityCode) => Post(() =>
        _securityCode.Text = Ui.Format("SecurityCode", supporterDisplayName, securityCode));

    // ------------------------------------------------------------------ IChatPresenter

    public void ShowMessage(string author, string text, DateTimeOffset timestamp) => Post(() =>
    {
        _chatLines.Add($"{timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} {author}: {text}");
        _chatLog.ScrollIntoView(_chatLines.Count - 1);
    });

    // ------------------------------------------------------------------ IClipboard

    /// <summary>
    /// Returns the text read by the clipboard timer: X11 clipboard access is asynchronous and needs the UI thread, so it
    /// is never done synchronously from the handler.
    /// </summary>
    public string? GetText() => _clipboardText;

    public void SetText(string text)
    {
        _clipboardText = text; // no echo back to the supporter
        Post(async () =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        });
    }

    // ------------------------------------------------------------------ IFileTransferUi

    public string DownloadDirectory
    {
        get
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public Task<bool> ConfirmIncomingAsync(string fileName, long size, CancellationToken cancellationToken) =>
        AskAsync(Ui.Get("File_Confirm_Title"), Ui.Format("File_Confirm_Text", fileName, FormatSize(size), DownloadDirectory),
            Ui.Get("File_Accept"), Ui.Get("File_Decline"), cancellationToken);

    public void OnFileReceived(string path) => ShowMessage(Ui.Get("App_Title"), Ui.Format("File_Received", path), DateTimeOffset.Now);

    public void OnTransferFailed(string fileName, string reason) =>
        ShowMessage(Ui.Get("App_Title"), Ui.Format("File_Failed", fileName), DateTimeOffset.Now);

    // ------------------------------------------------------------------ lifecycle

    private async Task RunAsync()
    {
        var screens = Screens.Primary?.WorkingArea;
        if (screens is { } area)
        {
            Position = new PixelPoint(area.Right - (int)Bounds.Width - 24, area.Bottom - (int)Bounds.Height - 48);
        }

        using var screen = new X11ScreenSource(_display);
        await using var streamer = new ScreenStreamer(screen);
        await using var shells = new ShellHandler(new PtyShellHost());
        _chat = new ChatHandler(this);
        _files = new FileTransferHandler(this);
        _clipboard = new ClipboardHandler(this);
        _session = new AgentSession(
            _options, new ClientWebSocketConnector(), this,
            [streamer, new InputHandler(new X11InputInjector(_display), screen), _clipboard, _chat, _files, shells]);
        try
        {
            var result = await _session.RunAsync(_closing.Token);
            if (!result.Succeeded)
            {
                OnStatusChanged(AgentStatus.Ended, result.ErrorCode);
            }
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
        finally
        {
            await _session.DisposeAsync();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_ended && _session is not null)
        {
            // Closing the window ends the session cleanly (the supporter sees "ended by customer").
            e.Cancel = true;
            await EndAsync();
            Close();
            return;
        }

        await _closing.CancelAsync();
        _clipboardTimer.Stop();
    }

    private async Task EndAsync()
    {
        _ended = true;
        _end.IsEnabled = false;
        if (_session is { } session)
        {
            await session.EndAsync();
        }
    }

    private async Task SendChatAsync()
    {
        var text = _chatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session is null || _chat is null)
        {
            return;
        }

        _chatInput.Text = string.Empty;
        ShowMessage(Ui.Get("Chat_You"), text, DateTimeOffset.Now);
        await _chat.SendAsync(_session, Environment.UserName, text, _closing.Token);
    }

    private async Task SendFileAsync()
    {
        if (_session is null || _files is null)
        {
            return;
        }

        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        var name = Path.GetFileName(path);
        ShowMessage(Ui.Get("Chat_You"), Ui.Format("File_Offered", name), DateTimeOffset.Now);
        var sent = await _files.SendFileAsync(_session, null, path, _closing.Token);
        ShowMessage(Ui.Get("App_Title"), Ui.Format(sent ? "File_Sent" : "File_NotSent", name), DateTimeOffset.Now);
    }

    private async Task PollClipboardAsync()
    {
        if (_session is null || _clipboard is null || Clipboard is not { } clipboard)
        {
            return;
        }

        _clipboardText = await clipboard.GetTextAsync();
        await _clipboard.PollAsync(_session, _closing.Token);
    }

    private async Task<bool> AskAsync(string title, string text, string yes, string no, CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new QuestionWindow(title, text, yes, no);
            await using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
            return await dialog.ShowDialog<bool>(this);
        });

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d),
    };

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private static void Post(Func<Task> action) => Dispatcher.UIThread.Post(() => _ = action());
}
