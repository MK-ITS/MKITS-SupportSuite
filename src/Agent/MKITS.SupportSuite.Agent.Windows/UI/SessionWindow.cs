// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.IO.Pipes;
using MKITS.SupportSuite.Agent.Core.Bridge;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.UI;

/// <summary>
/// The permanent agent's window in the logged-on user's session (<c>--agent-user &lt;pipe&gt;</c>, ADR-0022), started by
/// the service for the duration of a session. It runs chat, clipboard, file transfer and the shell with the user's
/// rights and shows status, supporters, security code, chat and "End session". Under the <i>silent</i> policy the window
/// stays hidden; consent dialogs and incoming-file questions still appear. Exits when the service disconnects.
/// </summary>
internal sealed class SessionWindow : Form, IAgentUserInterface, IChatPresenter, IClipboard, IFileTransferUi
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold) };
    private readonly Label _supporters = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Label _securityCode = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly ListBox _chatLog = new() { Height = 140, Width = 380, HorizontalScrollbar = true };
    private readonly TextBox _chatInput = new() { Width = 300 };
    private readonly Button _send = new() { Text = UiText.Get("Chat_Send"), AutoSize = true };
    private readonly Button _sendFile = new() { Text = UiText.Get("File_Send"), AutoSize = true, Enabled = false };
    private readonly Button _end = new() { Text = UiText.Get("EndSession"), AutoSize = true, BackColor = Color.Firebrick, ForeColor = Color.White };
    private readonly System.Windows.Forms.Timer _clipboardTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _closing = new();
    private readonly NamedPipeClientStream _pipe;
    private readonly ChatHandler _chat;
    private readonly ClipboardHandler _clipboard;
    private readonly FileTransferHandler _files;
    private readonly ShellHandler _shells;
    private readonly BridgeClient _client;
    private bool _visibleAllowed;
    private bool _endRequested;

    public SessionWindow(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _chat = new ChatHandler(this);
        _clipboard = new ClipboardHandler(this);
        _files = new FileTransferHandler(this);
        _shells = new ShellHandler(new Platform.ConPtyShellHost());
        _client = new BridgeClient(pipe, this, [_chat, _clipboard, _files, _shells]);

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
        layout.Controls.Add(_end);
        Controls.Add(layout);

        AcceptButton = _send;
        _send.Click += async (_, _) => await SendChatAsync();
        _end.Click += async (_, _) => await EndAsync();
        _sendFile.Click += async (_, _) => await SendFileAsync();
        _clipboardTimer.Tick += async (_, _) => await _clipboard.PollAsync(_client, _closing.Token);

        // The window may stay hidden (silent policy), so the handle – and with it the bridge loop – is created right away.
        _ = Handle;
    }

    /// <summary><c>--agent-user &lt;pipe&gt;</c>: connects to the service and runs the window until the session is over.</summary>
    public static int Run(string pipeName)
    {
        if (!pipeName.StartsWith("MKITS-User-", StringComparison.Ordinal) || pipeName.Length > 64)
        {
            return 2;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10_000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return 3;
        }

        // Only talk to the installed agent service – never to a process that squatted the pipe name.
        if (!Elevation.HelperClient.ServerIsOurHelper(pipe, Permanent.AgentPaths.Executable))
        {
            return 4;
        }

        ApplicationConfiguration.Initialize();
        using var window = new SessionWindow(pipe);
        Application.Run(window);
        return 0;
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(value && _visibleAllowed);

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            await _client.RunAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            // closing
        }

        // The service closed the connection: the session is over.
        _endRequested = true;
        if (!IsDisposed)
        {
            Close();
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!_endRequested && e.CloseReason == CloseReason.UserClosing)
        {
            // Closing the window ends the session (ADR-0014: the user can always end it).
            e.Cancel = true;
            await EndAsync();
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
            _shells.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client.Dispose();
            _closing.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ IAgentUserInterface (from the service)

    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            using var dialog = new ConsentForm(prompt) { TopMost = true };
            using var registration = cancellationToken.Register(() => dialog.BeginInvoke(dialog.Close));
            result.TrySetResult(dialog.ShowDialog() == DialogResult.Yes);
        });
        return result.Task;
    }

    public void OnSessionAssigned(string operatorName, ConsentPolicy policy) => OnUi(() =>
    {
        _status.Text = UiText.Format("Status_WaitingForSupporter", operatorName);
        if (policy != ConsentPolicy.Silent && !_visibleAllowed)
        {
            _visibleAllowed = true;
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            Show();
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }
    });

    public void OnStatusChanged(AgentStatus status, string? detail) => OnUi(() =>
    {
        _status.Text = status switch
        {
            AgentStatus.WaitingForSupporter => UiText.Format("Status_WaitingForSupporter", detail),
            AgentStatus.AwaitingConsent => UiText.Get("Status_AwaitingConsent"),
            AgentStatus.Active => UiText.Get("Status_Active"),
            AgentStatus.Reconnecting => UiText.Get("Status_Reconnecting"),
            _ => _status.Text,
        };

        if (status == AgentStatus.Active)
        {
            _clipboardTimer.Start();
        }

        _sendFile.Enabled = status == AgentStatus.Active;
    });

    public void OnSupportersChanged(IReadOnlyList<string> displayNames) => OnUi(() =>
        _supporters.Text = displayNames.Count == 0 ? string.Empty : UiText.Format("Supporters", string.Join(", ", displayNames)));

    public void OnSecurityCode(string supporterDisplayName, string securityCode) => OnUi(() =>
        _securityCode.Text = UiText.Format("SecurityCode", supporterDisplayName, securityCode) + Environment.NewLine + UiText.Get("SecurityCodeHint"));

    // ------------------------------------------------------------------ IChatPresenter, IClipboard, IFileTransferUi

    public void ShowMessage(string author, string text, DateTimeOffset timestamp) => OnUi(() =>
    {
        _chatLog.Items.Add($"{timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} {author}: {text}");
        _chatLog.TopIndex = _chatLog.Items.Count - 1;
    });

    public string? GetText() => InvokeOnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

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

    public string DownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public Task<bool> ConfirmIncomingAsync(string fileName, long size, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            var answer = MessageBox.Show(
                UiText.Format("File_Confirm_Text", fileName, FormatSize(size), DownloadDirectory),
                UiText.Get("File_Confirm_Title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2, // "No" is the default
                MessageBoxOptions.DefaultDesktopOnly);
            result.TrySetResult(answer == DialogResult.Yes);
        });
        return result.Task;
    }

    public void OnFileReceived(string path) => ShowMessage(UiText.Get("App_Title"), UiText.Format("File_Received", path), DateTimeOffset.Now);

    public void OnTransferFailed(string fileName, string reason) =>
        ShowMessage(UiText.Get("App_Title"), UiText.Format("File_Failed", fileName), DateTimeOffset.Now);

    // ------------------------------------------------------------------ actions

    private async Task EndAsync()
    {
        _end.Enabled = false;
        await _client.EndSessionAsync(_closing.Token);
    }

    private async Task SendChatAsync()
    {
        var text = _chatInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _chatInput.Clear();
        ShowMessage(UiText.Get("Chat_You"), text, DateTimeOffset.Now);
        await _chat.SendAsync(_client, Environment.UserName, text, _closing.Token);
    }

    private async Task SendFileAsync()
    {
        using var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var name = Path.GetFileName(dialog.FileName);
        ShowMessage(UiText.Get("Chat_You"), UiText.Format("File_Offered", name), DateTimeOffset.Now);
        var sent = await _files.SendFileAsync(_client, null, dialog.FileName, _closing.Token);
        ShowMessage(UiText.Get("App_Title"), UiText.Format(sent ? "File_Sent" : "File_NotSent", name), DateTimeOffset.Now);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d),
    };

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
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
        if (IsDisposed || !IsHandleCreated)
        {
            return default;
        }

        try
        {
            return InvokeRequired ? (T?)Invoke(func) : func();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return default;
        }
    }
}
