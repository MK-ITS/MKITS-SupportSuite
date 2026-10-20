// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Agent.Windows.UI;

/// <summary>Fallback when the file name does not carry server and code (renamed file, ADR-0019).</summary>
internal sealed class ConnectForm : Form
{
    private readonly TextBox _server = new() { Width = 300 };
    private readonly TextBox _code = new() { Width = 300 };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(320, 0) };

    public ConnectForm()
    {
        Text = UiText.Get("Connect_Title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var ok = new Button { Text = UiText.Get("Connect_Ok"), AutoSize = true };
        var cancel = new Button { Text = UiText.Get("Connect_Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => TryAccept(ok);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = UiText.Get("Connect_Server"), AutoSize = true });
        layout.Controls.Add(_server);
        layout.Controls.Add(new Label { Text = UiText.Get("Connect_Code"), AutoSize = true });
        layout.Controls.Add(_code);
        layout.Controls.Add(_error);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The validated input after <see cref="DialogResult.OK"/>.</summary>
    public AdHocClientTarget? Target { get; private set; }

    private void TryAccept(Button ok)
    {
        var server = _server.Text.Trim();
        int? port = null;
        var colon = server.LastIndexOf(':');
        if (colon > 0 && int.TryParse(server[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort) && parsedPort is > 0 and < 65536)
        {
            port = parsedPort;
            server = server[..colon];
        }

        var code = SessionCodeFormat.Normalize(_code.Text);
        if (!AdHocClientFileName.IsValidHost(server) || code is null)
        {
            _error.Text = UiText.Get("Connect_Invalid");
            return;
        }

        Target = new AdHocClientTarget(server.ToLowerInvariant(), port, code);
        ok.DialogResult = DialogResult.OK;
        DialogResult = DialogResult.OK;
        Close();
    }
}
