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
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Gui;

/// <summary>
/// The session window (ADR-0014: a visible indicator and "End session" can never be hidden): status, supporters,
/// security code, chat, file transfer and the end button. Behind it runs an <see cref="ISessionHost"/>: the ad-hoc
/// client's own session, or – for the installed agent – the bridge to the service (then the window stays hidden for the
/// "silent" consent policy and closes when the service ends the session). Calls from background threads are posted to
/// the UI thread.
/// </summary>
internal sealed class SessionWindow : Window, IAgentUserInterface, IChatPresenter, IClipboard, IFileTransferUi, IDisposable
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
    private readonly ChatHandler _chat;
    private readonly FileTransferHandler _files;
    private readonly ClipboardHandler _clipboard;
    private readonly bool _bridged;
    private readonly Func<SessionWindow, ISessionHost> _createHost;
    private ISessionHost? _session;
    private string? _clipboardText;
    private bool _sharing;
    private int _clipboardVersion;
    private bool _ended;
    private bool _started;

    /// <summary>Ad-hoc client: shown right away, runs its own session.</summary>
    public SessionWindow(AgentSessionOptions options, X11Display display)
        : this(window => new AdHocSessionHost(options, display, window, window.UserHandlers), bridged: false)
    {
    }

    private SessionWindow(Func<SessionWindow, ISessionHost> createHost, bool bridged)
    {
        _createHost = createHost;
        _bridged = bridged;
        _chat = new ChatHandler(this);
        _files = new FileTransferHandler(this);
        _clipboard = new ClipboardHandler(this);

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
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += (_, _) => Dispose();
    }

    /// <summary>The handlers that run with the user's rights in this process.</summary>
    private ISessionChannelHandler[] UserHandlers => [_clipboard, _chat, _files];

    /// <summary>
    /// Installed agent: runs the bridge right away without showing the window (it appears with the session assignment
    /// unless the policy is "silent"); <paramref name="ended"/> runs when the service closed the connection.
    /// </summary>
    public static SessionWindow StartBridged(Stream stream, Action ended)
    {
        var window = new SessionWindow(w => new BridgeSessionHost(stream, w, w.UserHandlers), bridged: true);
        var done = 0;
        void End()
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
            {
                ended();
            }
        }

        window.Closed += (_, _) => End();
        Dispatcher.UIThread.Post(async () =>
        {
            await window.StartAsync();
            window._ended = true;
            window.Close(); // the session is over
            End();
        });
        return window;
    }

    /// <summary>Called when the window is closed; the session itself is disposed when <see cref="StartAsync"/> ends.</summary>
    public void Dispose()
    {
        _clipboardTimer.Stop();
        _closing.Dispose();
        if (Interlocked.Exchange(ref _session, null) is { } session)
        {
            _ = session.DisposeAsync().AsTask();
        }
    }

    // ------------------------------------------------------------------ IAgentUserInterface

    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var text = Ui.Format("Consent_Text", prompt.SupporterDisplayName, prompt.OperatorName, prompt.ServerHost,
            string.Join(", ", prompt.Permissions.Select(p => Ui.Get("Permission_" + p))));
        return AskAsync(Ui.Get("Consent_Title"), text, Ui.Get("Consent_Allow"), Ui.Get("Consent_Deny"), cancellationToken);
    }

    /// <summary>Bridged window: becomes visible with the session unless the policy is "silent".</summary>
    public void OnSessionAssigned(string operatorName, ConsentPolicy policy) => Post(() =>
    {
        _status.Text = Ui.Format("Status_WaitingForSupporter", operatorName);
        if (_bridged && policy != ConsentPolicy.Silent && !IsVisible)
        {
            Show();
            PlaceBottomRight();
        }
    });

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

        _sharing = status == AgentStatus.Active;
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
        Interlocked.Increment(ref _clipboardVersion);
        Post(async () =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }

            Interlocked.Increment(ref _clipboardVersion); // also discard reads that started before the write landed
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

    private async Task StartAsync()
    {
        if (_started)
        {
            return; // Opened fires again when a hidden bridged window is shown
        }

        _started = true;
        if (!_bridged)
        {
            PlaceBottomRight();
        }

        // The clipboard is read asynchronously, so GetText() serves a cache. Prime it before the session runs and keep it
        // fresh from now on: ClipboardHandler records the current content as "already known" when a viewer becomes
        // ready, and an empty cache would make the user's existing clipboard look like a new change and leak it.
        await RefreshClipboardAsync();
        _clipboardTimer.Start();

        _session = _createHost(this);
        try
        {
            if (await _session.RunAsync(_closing.Token) is { } error)
            {
                OnStatusChanged(AgentStatus.Ended, error.Length == 0 ? null : error);
            }
        }
        catch (OperationCanceledException)
        {
            // window closed
        }
        finally
        {
            if (Interlocked.Exchange(ref _session, null) is { } session)
            {
                await session.DisposeAsync();
            }
        }
    }

    private void PlaceBottomRight()
    {
        if (Screens.Primary?.WorkingArea is { } area)
        {
            Position = new PixelPoint(area.Right - (int)Bounds.Width - 24, area.Bottom - (int)Bounds.Height - 48);
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

        _clipboardTimer.Stop();
        await _closing.CancelAsync();
    }

    private async Task EndAsync()
    {
        _ended = true;
        _end.IsEnabled = false;
        if (_session is { } session)
        {
            await session.EndAsync(_closing.Token);
        }
    }

    private async Task SendChatAsync()
    {
        var text = _chatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session is null)
        {
            return;
        }

        _chatInput.Text = string.Empty;
        ShowMessage(Ui.Get("Chat_You"), text, DateTimeOffset.Now);
        await _chat.SendAsync(_session.Context, Environment.UserName, text, _closing.Token);
    }

    private async Task SendFileAsync()
    {
        if (_session is null)
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
        var sent = await _files.SendFileAsync(_session.Context, null, path, _closing.Token);
        ShowMessage(Ui.Get("App_Title"), Ui.Format(sent ? "File_Sent" : "File_NotSent", name), DateTimeOffset.Now);
    }

    private async Task PollClipboardAsync()
    {
        await RefreshClipboardAsync();
        if (_sharing && _session is { } session)
        {
            await _clipboard.PollAsync(session.Context, _closing.Token);
        }
    }

    private async Task RefreshClipboardAsync()
    {
        if (Clipboard is { } clipboard)
        {
            // A read that overlaps SetText would restore the old content and send it back as a "change".
            var version = Volatile.Read(ref _clipboardVersion);
            var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
            if (version == Volatile.Read(ref _clipboardVersion))
            {
                _clipboardText = text;
            }
        }
    }

    private async Task<bool> AskAsync(string title, string text, string yes, string no, CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new QuestionWindow(title, text, yes, no);
            await using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
            if (IsVisible)
            {
                return await dialog.ShowDialog<bool>(this);
            }

            // A hidden bridged window ("silent" policy) cannot own a modal dialog: show it on its own.
            var answer = new TaskCompletionSource<bool>();
            dialog.Closed += (_, _) => answer.TrySetResult(dialog.Answer);
            dialog.Show();
            return await answer.Task;
        });

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d),
    };

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private static void Post(Func<Task> action) => Dispatcher.UIThread.Post(() => _ = action());
}
