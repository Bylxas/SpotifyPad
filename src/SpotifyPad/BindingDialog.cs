namespace SpotifyPad;

/// <summary>Edits one binding: pick an action and record or tick the controller buttons.</summary>
sealed class BindingDialog : Form
{
    readonly Engine engine;
    readonly PadType type;
    readonly Func<int?> currentVolume;
    readonly NumericUpDown volume = new() { Minimum = 0, Maximum = 100, Value = 30, Width = 70, Anchor = AnchorStyles.Left };
    readonly FlowLayoutPanel volumeRow = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
    readonly ComboBox action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    // One checkbox per entry of PadButtons.All, same order
    readonly ModernCheckBox[] buttons;
    readonly Button record = new() { Text = RecordText, Height = 40, Dock = DockStyle.Fill, Tag = Theme.Primary };
    readonly Label hint = new()
    {
        Text = T("Klick auf „Aufnehmen“, drück die Tasten gleichzeitig am Controller und lass los.",
            "Click \"Record\", press the buttons together on your controller, then let go."),
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        Margin = new Padding(0, 8, 0, 8),
    };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 20 };
    bool recording;

    public PadBinding Result { get; private set; } = new();

    static string RecordText => T("🎮  Kombination aufnehmen", "🎮  Record combination");

    /// <param name="currentVolume">Reads the volume right now, for "take current" on volume presets.</param>
    public BindingDialog(PadBinding? existing, Engine engine, PadType type, Func<int?> currentVolume)
    {
        this.engine = engine;
        this.type = type;
        this.currentVolume = currentVolume;

        Text = (existing == null ? T("Belegung hinzufügen", "Add binding") : T("Belegung bearbeiten", "Edit binding")) + $" ({PadButtons.TypeName(type)})";
        Font = Theme.BaseFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(500, 640);

        // ---- Action card ----
        foreach (var a in PadActions.All) action.Items.Add(PadActions.Label(a));
        action.SelectedIndex = (int)(existing?.Action ?? PadAction.PlayPause);
        if (existing?.Volume is int v) volume.Value = Math.Clamp(v, 0, 100);

        // Only shown for "Set volume": the target the volume jumps to
        var takeCurrent = new Button { Text = T("Aktuelle übernehmen", "Use current"), AutoSize = true, MinimumSize = new Size(0, 30) };
        takeCurrent.Click += (_, _) =>
        {
            if (this.currentVolume() is int now) volume.Value = Math.Clamp(now, 0, 100);
            else MessageBox.Show(this, T("Die aktuelle Lautstärke konnte nicht gelesen werden.", "Couldn't read the current volume."), "SpotifyPad");
        };
        volumeRow.Controls.AddRange(
        [
            new Label { Text = T("Ziel-Lautstärke (%)", "Target volume (%)"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 10, 3) },
            volume,
            takeCurrent,
        ]);
        action.SelectedIndexChanged += (_, _) => volumeRow.Visible = (PadAction)action.SelectedIndex == PadAction.SetVolume;
        volumeRow.Visible = (PadAction)action.SelectedIndex == PadAction.SetVolume;

        var actionContent = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        actionContent.Controls.Add(action, 0, 0);
        actionContent.Controls.Add(volumeRow, 0, 1);
        var actionCard = new Card(T("Aktion", "Action"), actionContent);

        // ---- Buttons card: record button and a two-column grid of checkboxes ----
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons = new ModernCheckBox[PadButtons.All.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = new ModernCheckBox { Text = PadButtons.Long(i, type), Margin = new Padding(0, 3, 8, 3) };
            grid.Controls.Add(buttons[i], i % 2, i / 2);
        }
        if (existing != null) SetChecked(existing.Mask);

        hint.ForeColor = Theme.Subtle;
        var buttonsContent = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        buttonsContent.Controls.Add(record, 0, 0);
        buttonsContent.Controls.Add(hint, 0, 1);
        buttonsContent.Controls.Add(grid, 0, 2);
        var buttonsCard = new Card(T("Tasten", "Buttons"), buttonsContent);

        record.Click += (_, _) => ToggleRecording();
        timer.Tick += (_, _) => PollRecording();

        // ---- Footer ----
        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(100, 34), Tag = Theme.Primary };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Text = T("Abbrechen", "Cancel"), AutoSize = true, MinimumSize = new Size(100, 34), DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        footer.Controls.AddRange([cancel, ok]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 14), ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(actionCard, 0, 0);
        root.Controls.Add(buttonsCard, 0, 1);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        Theme.Apply(this);

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
        var all = PadButtons.All;
        for (int i = 0; i < buttons.Length; i++)
            buttons[i].Checked = (mask & (uint)all[i].Button) != 0;
    }

    uint CheckedMask()
    {
        var all = PadButtons.All;
        uint m = 0;
        for (int i = 0; i < buttons.Length; i++)
            if (buttons[i].Checked) m |= (uint)all[i].Button;
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
        var chosen = (PadAction)action.SelectedIndex;
        Result = new PadBinding
        {
            Action = chosen,
            Buttons = PadBinding.FromMask(mask),
            Volume = chosen == PadAction.SetVolume ? (int)volume.Value : null,
        };
        DialogResult = DialogResult.OK;
    }
}
