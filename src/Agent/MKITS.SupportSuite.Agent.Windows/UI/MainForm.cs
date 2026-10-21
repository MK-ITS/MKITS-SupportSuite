// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;

namespace MKITS.SupportSuite.Agent.Windows.UI;

/// <summary>
/// The always-visible session window (ADR-0014: a visible indicator and "End session" can never be hidden):
/// status, connected supporters, security code, chat and the end button. Implements the user-facing contracts
/// of the agent core; calls from background threads are marshalled to the UI thread.
/// </summary>
internal sealed class MainForm : Form, IAgentUserInterface, IChatPresenter, IClipboard, IFileTransferUi
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold) };
    private readonly Label _supporters = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Label _securityCode = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly ListBox _chatLog = new() { Height = 140, Width = 380, HorizontalScrollbar = true };
    private readonly TextBox _chatInput = new() { Width = 300 };
    private readonly Button _send = new() { Text = UiText.Get("Chat_Send"), AutoSize = true };
    private readonly Button _elevate = new() { Text = UiText.Get("Elevate_Button"), AutoSize = true };
    private readonly Label _elevationState = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Button _sendFile = new() { Text = UiText.Get("File_Send"), AutoSize = true, Enabled = false };
    private readonly Button _end = new() { Text = UiText.Get("EndSession"), AutoSize = true, BackColor = Color.Firebrick, ForeColor = Color.White };
    private readonly System.Windows.Forms.Timer _clipboardTimer = new() { Interval = 1000 };
    private readonly AgentSessionOptions _options;
    private readonly CancellationTokenSource _closing = new();
    private AgentSession? _session;
    private ChatHandler? _chat;
    private FileTransferHandler? _files;
    private ShellHandler? _shells;
    private ClipboardHandler? _clipboard;
    private GdiScreenSourceHolder? _screen;
    private Platform.SwitchableScreenSource? _screenSource;
    private Platform.SwitchableInputInjector? _inputInjector;
    private Elevation.HelperClient? _helper;
    private bool _ended;

    public MainForm(AgentSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        Text = UiText.Get("App_Title");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        StartPosition = FormStartPosition.Manual;

        var chatRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        chatRow.Controls.Add(_chatInput);
        chatRow.Controls.Add(_send);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(_status);
        layout.Controls.Add(_supporters);
        layout.Controls.Add(_securityCode);
        layout.Controls.Add(_chatLog);
        layout.Controls.Add(chatRow);
        layout.Controls.Add(_sendFile);
        layout.Controls.Add(_elevate);
        layout.Controls.Add(_elevationState);
        layout.Controls.Add(_end);
        Controls.Add(layout);

        AcceptButton = _send;
        _send.Click += async (_, _) => await SendChatAsync();
        _end.Click += async (_, _) => await EndAsync();
        _sendFile.Click += async (_, _) => await SendFileAsync();
        _elevate.Click += async (_, _) => await ElevateAsync();
        _clipboardTimer.Tick += async (_, _) => await PollClipboardAsync();
    }

    // ------------------------------------------------------------------ IAgentUserInterface

    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            using var dialog = new ConsentForm(prompt);
            using var registration = cancellationToken.Register(() => dialog.BeginInvoke(dialog.Close));
            result.TrySetResult(dialog.ShowDialog(this) == DialogResult.Yes);
        });
        return result.Task;
    }

    public void OnStatusChanged(AgentStatus status, string? detail) => OnUi(() =>
    {
        _status.Text = status switch
        {
            AgentStatus.Connecting => UiText.Format("Status_Connecting", detail),
            AgentStatus.WaitingForSupporter => UiText.Format("Status_WaitingForSupporter", detail),
            AgentStatus.AwaitingConsent => UiText.Get("Status_AwaitingConsent"),
            AgentStatus.Active => UiText.Get("Status_Active"),
            AgentStatus.Reconnecting => UiText.Get("Status_Reconnecting"),
            // Protocol error codes are upper case (CODE_INVALID); end reasons are lower case (ended_by_supporter).
            _ => detail is not null && string.Equals(detail, detail.ToUpperInvariant(), StringComparison.Ordinal)
                ? UiText.ForError(detail)
                : UiText.Get("Status_Ended"),
        };

        if (status == AgentStatus.Ended)
        {
            _helper?.Shutdown();
            _elevate.Enabled = false;
            _ended = true;
            _end.Enabled = false;
            _send.Enabled = false;
            _clipboardTimer.Stop();
        }
        else if (status == AgentStatus.Active)
        {
            _clipboardTimer.Start();
        }

        _sendFile.Enabled = status == AgentStatus.Active;
    });

    public void OnSupportersChanged(IReadOnlyList<string> displayNames) => OnUi(() =>
        _supporters.Text = displayNames.Count == 0 ? string.Empty : UiText.Format("Supporters", string.Join(", ", displayNames)));

    public void OnSecurityCode(string supporterDisplayName, string securityCode) => OnUi(() =>
        _securityCode.Text = UiText.Format("SecurityCode", supporterDisplayName, securityCode) + Environment.NewLine + UiText.Get("SecurityCodeHint"));

    // ------------------------------------------------------------------ IChatPresenter

    public void ShowMessage(string author, string text, DateTimeOffset timestamp) => OnUi(() =>
    {
        _chatLog.Items.Add($"{timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} {author}: {text}");
        _chatLog.TopIndex = _chatLog.Items.Count - 1;
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
    });

    // ------------------------------------------------------------------ IClipboard (STA: UI thread only)

    public string? GetText() => InvokeOnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <summary>Posted, not awaited: the receive loop must never wait for the UI thread.</summary>
    public void SetText(string text) => OnUi(() =>
    {
        try
        {
            Clipboard.SetText(text.Length == 0 ? " " : text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // clipboard locked by another application
        }
    });

    // ------------------------------------------------------------------ IFileTransferUi

    public string DownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public Task<bool> ConfirmIncomingAsync(string fileName, long size, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            var answer = MessageBox.Show(
                this,
                UiText.Format("File_Confirm_Text", fileName, FormatSize(size), DownloadDirectory),
                UiText.Get("File_Confirm_Title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2); // "No" is the default
            result.TrySetResult(answer == DialogResult.Yes);
        });
        return result.Task;
    }

    public void OnFileReceived(string path) => ShowMessage(UiText.Get("App_Title"), UiText.Format("File_Received", path), DateTimeOffset.Now);

    public void OnTransferFailed(string fileName, string reason) =>
        ShowMessage(UiText.Get("App_Title"), UiText.Format("File_Failed", fileName), DateTimeOffset.Now);

    // ------------------------------------------------------------------ lifecycle

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

        _screen = new GdiScreenSourceHolder();
        _chat = new ChatHandler(this);
        _files = new FileTransferHandler(this);
        _shells = new ShellHandler(new Platform.ConPtyShellHost());
        _clipboard = new ClipboardHandler(this);
        _screenSource = new Platform.SwitchableScreenSource(_screen.Source);
        _inputInjector = new Platform.SwitchableInputInjector(new Platform.SendInputInjector());
        var streamer = new ScreenStreamer(_screenSource);
        var input = new InputHandler(_inputInjector, _screenSource);

        _session = new AgentSession(_options, new Core.Connection.ClientWebSocketConnector(), this, [streamer, input, _clipboard, _chat, _files, _shells]);
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
            await streamer.DisposeAsync();
            await _shells.DisposeAsync();
            await _session.DisposeAsync();
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!_ended && _session is not null)
        {
            // Closing the window ends the session cleanly (the supporter sees "ended by customer").
            e.Cancel = true;
            await EndAsync();
            Close();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Cancel();
            _clipboardTimer.Dispose();
            _helper?.Dispose();
            _screen?.Dispose();
            _closing.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task EndAsync()
    {
        _ended = true;
        _end.Enabled = false;
        if (_session is { } session)
        {
            await session.EndAsync();
        }
    }

    private async Task SendChatAsync()
    {
        var text = _chatInput.Text.Trim();
        if (text.Length == 0 || _session is null || _chat is null)
        {
            return;
        }

        _chatInput.Clear();
        ShowMessage(UiText.Get("Chat_You"), text, DateTimeOffset.Now);
        await _chat.SendAsync(_session, Environment.UserName, text, _closing.Token);
    }

    /// <summary>
    /// "Restart with administrator rights" (ADR-0021): UAC prompt on this computer, then capture and input run through
    /// the privileged helper (secure desktop, UAC prompts, lock screen, Ctrl+Alt+Del). The session continues.
    /// </summary>
    private async Task ElevateAsync()
    {
        _elevate.Enabled = false;
        _elevationState.Text = UiText.Get("Elevate_Waiting");
        var helper = await Elevation.HelperClient.ElevateAsync(_closing.Token);
        if (helper is null || _screenSource is null || _inputInjector is null)
        {
            _elevationState.Text = UiText.Get("Elevate_Failed");
            _elevate.Enabled = !_ended;
            return;
        }

        _helper?.Dispose(); // a previous, disconnected helper
        _helper = helper;
        helper.Disconnected += (_, _) => OnUi(() =>
        {
            _screenSource.Current = _screen!.Source;
            _inputInjector.Current = new Platform.SendInputInjector();
            _elevationState.Text = UiText.Get("Elevate_Lost");
            _elevate.Enabled = !_ended;
        });
        _screenSource.Current = helper;
        _inputInjector.Current = helper;
        _elevationState.Text = UiText.Get("Elevate_Active");
        if (_session is not null && _chat is not null)
        {
            await _chat.SendAsync(_session, UiText.Get("App_Title"), UiText.Get("Elevate_ChatNotice"), _closing.Token);
        }
    }

    private async Task SendFileAsync()
    {
        if (_session is null || _files is null)
        {
            return;
        }

        using var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var name = Path.GetFileName(dialog.FileName);
        ShowMessage(UiText.Get("Chat_You"), UiText.Format("File_Offered", name), DateTimeOffset.Now);
        var sent = await _files.SendFileAsync(_session, null, dialog.FileName, _closing.Token);
        ShowMessage(UiText.Get("App_Title"), UiText.Format(sent ? "File_Sent" : "File_NotSent", name), DateTimeOffset.Now);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d),
    };

    private async Task PollClipboardAsync()
    {
        if (_session is not null && _clipboard is not null)
        {
            await _clipboard.PollAsync(_session, _closing.Token);
        }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private T? InvokeOnUi<T>(Func<T> func)
    {
        if (IsDisposed)
        {
            return default;
        }

        try
        {
            return InvokeRequired ? (T?)Invoke(func) : func();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return default; // clipboard is locked by another application – try again next time
        }
    }

    /// <summary>Owns the GDI source so it is disposed with the window.</summary>
    private sealed class GdiScreenSourceHolder : IDisposable
    {
        public Platform.GdiScreenSource Source { get; } = new();

        public void Dispose() => Source.Dispose();
    }
}
