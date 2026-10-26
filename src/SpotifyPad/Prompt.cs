namespace SpotifyPad;

/// <summary>Small text input dialog (WinForms has none built in).</summary>
sealed class Prompt : Form
{
    readonly TextBox box;

    Prompt(string title, string label, string value, bool multiline, bool readOnly)
    {
        Text = title;
        Font = Theme.BaseFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(460, multiline ? 300 : 180);

        box = new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            Multiline = multiline,
            ReadOnly = readOnly,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            Font = multiline ? new Font("Consolas", 9.5f) : Font,
        };

        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(100, 34), DialogResult = DialogResult.OK, Tag = Theme.Primary };
        var cancel = new Button { Text = T("Abbrechen", "Cancel"), AutoSize = true, MinimumSize = new Size(100, 34), DialogResult = DialogResult.Cancel };
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        if (readOnly)
        {
            footer.Controls.Add(ok);
            CancelButton = ok;
        }
        else
        {
            footer.Controls.AddRange([cancel, ok]);
            CancelButton = cancel;
        }
        if (!multiline) AcceptButton = ok;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(multiline ? SizeType.Percent : SizeType.AutoSize, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 0, 3, 6) }, 0, 0);
        root.Controls.Add(box, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        Theme.Apply(this);

        Shown += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }

    /// <returns>The entered text, or null if cancelled.</returns>
    public static string? Ask(IWin32Window owner, string title, string label, string value = "", bool multiline = false)
    {
        using var dlg = new Prompt(title, label, value, multiline, readOnly: false);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.box.Text : null;
    }

    /// <summary>Shows text that the user can copy.</summary>
    public static void ShowText(IWin32Window owner, string title, string label, string value)
    {
        using var dlg = new Prompt(title, label, value, multiline: true, readOnly: true);
        dlg.ShowDialog(owner);
    }
}
