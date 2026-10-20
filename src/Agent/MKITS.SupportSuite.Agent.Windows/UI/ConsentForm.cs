// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Sessions;

namespace MKITS.SupportSuite.Agent.Windows.UI;

/// <summary>Asks the end user whether a supporter may connect. Closing the window means "deny".</summary>
internal sealed class ConsentForm : Form
{
    public ConsentForm(ConsentPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        Text = UiText.Get("Consent_Title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var text = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Text = UiText.Format("Consent_Text", prompt.SupporterDisplayName, prompt.ServerHost),
            Margin = new Padding(0, 0, 0, 16),
        };

        var allow = new Button { Text = UiText.Get("Consent_Allow"), DialogResult = DialogResult.Yes, AutoSize = true };
        var deny = new Button { Text = UiText.Get("Consent_Deny"), DialogResult = DialogResult.No, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(allow);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(text);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        // Deny is the default: pressing Enter by accident must not grant access.
        AcceptButton = deny;
        CancelButton = deny;
    }
}
