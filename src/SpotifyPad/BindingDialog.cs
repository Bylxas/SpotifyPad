namespace SpotifyPad;

/// <summary>Edits one binding: pick an action and record or tick the controller buttons.</summary>
sealed class BindingDialog : Form
{
    readonly Engine engine;
    readonly PadType type;
    readonly ComboBox action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly CheckedListBox buttons = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Button record = new() { Text = RecordText, Height = 36, Dock = DockStyle.Fill };
    readonly Label hint = new()
    {
        Text = T("Klick auf „Aufnehmen“, drück die Tasten gleichzeitig am Controller und lass los.",
            "Click \"Record\", press the buttons together on your controller, then let go."),
        AutoSize = true,
        MaximumSize = new Size(390, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(3, 4, 3, 8),
    };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 20 };
    bool recording;

    public PadBinding Result { get; private set; } = new();

    static string RecordText => T("🎮  Kombination aufnehmen", "🎮  Record combination");

    public BindingDialog(PadBinding? existing, Engine engine, PadType type)
    {
        this.engine = engine;
        this.type = type;

        Text = (existing == null ? T("Belegung hinzufügen", "Add binding") : T("Belegung bearbeiten", "Edit binding")) + $" ({PadButtons.TypeName(type)})";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(440, 620);

        foreach (var a in PadActions.All) action.Items.Add(PadActions.Label(a));
        action.SelectedIndex = (int)(existing?.Action ?? PadAction.PlayPause);

        for (int i = 0; i < PadButtons.All.Length; i++) buttons.Items.Add(PadButtons.Long(i, type));
        if (existing != null) SetChecked(existing.Mask);

        record.Click += (_, _) => ToggleRecording();
        timer.Tick += (_, _) => PollRecording();

        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 30) };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Text = T("Abbrechen", "Cancel"), AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;

        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        footer.Controls.AddRange([cancel, ok]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = T("Aktion", "Action"), AutoSize = true }, 0, 0);
        root.Controls.Add(action, 0, 1);
        root.Controls.Add(new Label { Text = T("Tasten", "Buttons"), AutoSize = true, Margin = new Padding(3, 12, 3, 3) }, 0, 2);
        root.Controls.Add(record, 0, 3);
        root.Controls.Add(hint, 0, 4);
        root.Controls.Add(buttons, 0, 5);
        root.Controls.Add(footer, 0, 6);
        Controls.Add(root);

        // Don't trigger music actions while the user is pressing buttons to record a combo
        engine.Suspended = true;
        timer.Start();
        FormClosed += (_, _) =>
        {
            engine.Suspended = false;
            timer.Dispose();
        };
    }

    void SetChecked(uint mask)
    {
        for (int i = 0; i < PadButtons.All.Length; i++)
            buttons.SetItemChecked(i, (mask & (uint)PadButtons.All[i].Button) != 0);
    }

    uint CheckedMask()
    {
        uint m = 0;
        foreach (int i in buttons.CheckedIndices) m |= (uint)PadButtons.All[i].Button;
        return m;
    }

    void ToggleRecording()
    {
        recording = !recording;
        if (recording)
        {
            engine.ResetRecording();
            record.Text = T("⏺  Warte auf Controller… (Klick = abbrechen)", "⏺  Waiting for controller… (click = cancel)");
        }
        else
        {
            record.Text = RecordText;
        }
    }

    void PollRecording()
    {
        if (!recording) return;
        uint rec = engine.RecordedOn(type);
        // Only listen to the controller type this profile is for
        if (type == PadType.Xbox ? engine.ConnectedXbox == 0 : engine.ConnectedPlayStation == 0)
        {
            hint.Text = T($"Kein {PadButtons.TypeName(type)}-Controller erkannt – bitte verbinden.", $"No {PadButtons.TypeName(type)} controller detected – please connect one.");
            return;
        }
        if (rec == 0) return;

        hint.Text = T("Erkannt: ", "Detected: ") + PadButtons.Format(rec, type);
        // Finished once everything is released again
        if (engine.PressedOn(type) == 0)
        {
            SetChecked(rec);
            ToggleRecording();
        }
    }

    void Confirm()
    {
        uint mask = CheckedMask();
        if (mask == 0)
        {
            MessageBox.Show(this, T("Bitte mindestens eine Taste auswählen.", "Please select at least one button."), "SpotifyPad");
            return;
        }
        Result = new PadBinding { Action = (PadAction)action.SelectedIndex, Buttons = PadBinding.FromMask(mask) };
        DialogResult = DialogResult.OK;
    }
}
