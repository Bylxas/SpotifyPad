namespace SpotifyPad;

sealed class SettingsForm : Form
{
    readonly Engine engine;
    readonly ListView list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly NumericUpDown step = new() { Minimum = 1, Maximum = 25, Width = 70 };
    readonly ComboBox volTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly CheckBox direct = new() { Text = T("Spotify direkt steuern (aus = globale Medientasten)", "Control Spotify directly (off = global media keys)"), AutoSize = true };
    readonly CheckBox rumble = new() { Text = T("Kurz vibrieren als Bestätigung", "Short rumble as confirmation"), AutoSize = true };
    readonly CheckBox autostart = new() { Text = T("Mit Windows starten", "Start with Windows"), AutoSize = true };
    readonly CheckBox patchSpotify = new() { Text = T("Spotify immer mit Lautstärke-Steuerung starten (Autostart + Verknüpfungen)", "Always start Spotify with volume control (autostart + shortcuts)"), AutoSize = true };
    readonly CheckBox autoRestart = new() { Text = T("Spotify automatisch neu starten, wenn die Lautstärke-Steuerung fehlt (Musik läuft danach weiter)", "Restart Spotify automatically when volume control is missing (music keeps playing)"), AutoSize = true };
    readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly Label spotifyStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2000 };
    readonly Label live = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
    readonly ComboBox profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Left };
    readonly Label detected = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 3, 3, 3) };
    readonly Label header = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 6) };
    // Follow the detected controller until the user picks a profile by hand
    bool profileChosenByUser;
    bool settingProfile;
    readonly ComboBox preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Anchor = AnchorStyles.Left };
    bool settingPreset;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    readonly ProgressBar volumeBar = new() { Maximum = 100, Width = 220, Height = 16, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) };
    readonly Label volumeText = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
    readonly System.Windows.Forms.Timer volumeTimer = new() { Interval = 400 };
    bool readingVolume;

    public AppConfig Config { get; }
    public bool Saved { get; private set; }

    public SettingsForm(AppConfig cfg, Engine engine)
    {
        this.engine = engine;
        Config = cfg.Clone();

        Text = T("SpotifyPad – Einstellungen", "SpotifyPad – Settings");
        Icon = TrayIcons.On;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(660, 800);
        MinimumSize = new Size(520, 520);

        list.Columns.Add(T("Aktion", "Action"), 190);
        list.Columns.Add(T("Tasten (gleichzeitig drücken)", "Buttons (press together)"), 360);
        list.DoubleClick += (_, _) => EditSelected();

        // Order matches PadType
        profile.Items.AddRange(["Xbox", "PlayStation"]);
        profile.SelectedIndexChanged += (_, _) =>
        {
            if (!settingProfile) profileChosenByUser = true;
            RefreshList();
        };
        detected.Font = new Font(Font, FontStyle.Bold);
        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        top.Controls.AddRange([OptLabel(T("Profil für", "Profile for")), profile, detected]);

        preset.SelectedIndexChanged += (_, _) =>
        {
            if (settingPreset || preset.SelectedIndex < 0) return;
            Config.ActivePreset = Config.Presets[preset.SelectedIndex].Name;
            RefreshList();
        };
        var more = new ContextMenuStrip();
        more.Items.Add(T("Duplizieren", "Duplicate"), null, (_, _) => DuplicatePreset());
        more.Items.Add(T("Umbenennen…", "Rename…"), null, (_, _) => RenamePreset());
        more.Items.Add(T("Löschen", "Delete"), null, (_, _) => DeletePreset());
        var moreButton = new Button { Text = "⋯", AutoSize = true, MinimumSize = new Size(40, 30) };
        moreButton.Click += (_, _) => more.Show(moreButton, new Point(0, moreButton.Height));
        var presetRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        presetRow.Controls.AddRange(
        [
            OptLabel("Preset"),
            preset,
            SmallButton(T("Neu", "New"), (_, _) => NewPreset()),
            SmallButton(T("Teilen", "Share"), (_, _) => SharePreset()),
            SmallButton(T("Importieren", "Import"), (_, _) => ImportPreset()),
            moreButton,
        ]);

        var volumeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        volumeRow.Controls.AddRange([OptLabel(T("🔊 Aktuelle Lautstärke", "🔊 Current volume")), volumeBar, volumeText]);

        var bindButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        bindButtons.Controls.AddRange(
        [
            MakeButton(T("Hinzufügen", "Add"), (_, _) => AddBinding()),
            MakeButton(T("Bearbeiten", "Edit"), (_, _) => EditSelected()),
            MakeButton(T("Entfernen", "Remove"), (_, _) => RemoveSelected()),
            MakeButton(T("Standard", "Defaults"), (_, _) => ResetToDefaults()),
        ]);

        // Order matches VolumeTarget
        volTarget.Items.AddRange([T("Spotify-Regler in der App", "Spotify's own slider"), T("Systemlautstärke (alles)", "System volume (everything)"), T("Spotify im Windows-Mixer", "Spotify in the Windows mixer")]);
        // Order matches AppLanguage
        language.Items.AddRange([T("Automatisch (Windows-Sprache)", "Automatic (Windows language)"), "Deutsch", "English"]);

        var opts = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        opts.Controls.Add(OptLabel(T("Lautstärke-Schritt pro Druck (%)", "Volume step per press (%)")), 0, 0);
        opts.Controls.Add(step, 1, 0);
        opts.Controls.Add(OptLabel(T("Lauter/Leiser ändert", "Volume up/down changes")), 0, 1);
        opts.Controls.Add(volTarget, 1, 1);
        opts.Controls.Add(direct, 0, 2);
        opts.SetColumnSpan(direct, 2);
        opts.Controls.Add(rumble, 0, 3);
        opts.SetColumnSpan(rumble, 2);
        opts.Controls.Add(autostart, 0, 4);
        opts.SetColumnSpan(autostart, 2);
        opts.Controls.Add(patchSpotify, 0, 5);
        opts.SetColumnSpan(patchSpotify, 2);
        var restart = MakeButton(T("Spotify jetzt neu starten", "Restart Spotify now"), (_, _) => RestartSpotify());
        opts.Controls.Add(autoRestart, 0, 6);
        opts.SetColumnSpan(autoRestart, 2);
        opts.Controls.Add(spotifyStatus, 0, 7);
        opts.Controls.Add(restart, 1, 7);
        opts.Controls.Add(OptLabel("Sprache / Language"), 0, 8);
        opts.Controls.Add(language, 1, 8);

        var group = new GroupBox
        {
            Text = T("Optionen", "Options"),
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 6),
        };
        group.Controls.Add(opts);

        var save = MakeButton(T("Speichern", "Save"), (_, _) => Save());
        var cancel = MakeButton(T("Abbrechen", "Cancel"), (_, _) => Close());
        AcceptButton = save;
        CancelButton = cancel;

        var bottom = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(live, 0, 0);
        bottom.Controls.Add(save, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(presetRow, 0, 0);
        root.Controls.Add(top, 0, 1);
        root.Controls.Add(header, 0, 2);
        root.Controls.Add(list, 0, 3);
        root.Controls.Add(bindButtons, 0, 4);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(volumeRow, 0, 5);
        root.Controls.Add(group, 0, 6);
        root.Controls.Add(bottom, 0, 7);
        Controls.Add(root);

        step.Value = Math.Clamp(Config.VolumeStep, 1, 25);
        volTarget.SelectedIndex = (int)Config.VolumeTarget;
        direct.Checked = Config.SpotifyDirect;
        rumble.Checked = Config.Rumble;
        autostart.Checked = Config.StartWithWindows ?? Autostart.IsEnabled;
        patchSpotify.Checked = Config.PatchSpotifyLaunch;
        autoRestart.Checked = Config.AutoRestartSpotify;
        language.SelectedIndex = (int)Config.Language;
        RefreshPresets();
        SetProfile(DetectedType() ?? PadType.Xbox);

        timer.Tick += (_, _) => UpdateLive();
        timer.Start();
        UpdateLive();
        statusTimer.Tick += (_, _) => UpdateSpotifyStatus();
        statusTimer.Start();
        UpdateSpotifyStatus();
        volumeTimer.Tick += (_, _) => UpdateVolume();
        volumeTimer.Start();
        UpdateVolume();
        FormClosed += (_, _) =>
        {
            timer.Dispose();
            statusTimer.Dispose();
            volumeTimer.Dispose();
        };
    }

    static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 30) };
        b.Click += onClick;
        return b;
    }

    static Button SmallButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 30) };
        b.Click += onClick;
        return b;
    }

    void RefreshPresets()
    {
        settingPreset = true;
        preset.Items.Clear();
        foreach (var p in Config.Presets) preset.Items.Add(p.Name);
        preset.SelectedIndex = Config.Presets.IndexOf(Config.Active);
        settingPreset = false;
        RefreshList();
    }

    void ActivatePreset(Preset p)
    {
        Config.ActivePreset = p.Name;
        RefreshPresets();
    }

    /// <summary>Asks for a preset name; null if cancelled, empty or already taken.</summary>
    string? AskName(string title, string value, Preset? renaming = null)
    {
        var name = Prompt.Ask(this, title, T("Name des Presets:", "Preset name:"), value)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Length > 40) name = name[..40];
        if (Config.Presets.Any(p => p.Name == name && p != renaming))
        {
            MessageBox.Show(this, T($"Ein Preset „{name}“ gibt es schon.", $"A preset named \"{name}\" already exists."), "SpotifyPad");
            return null;
        }
        return name;
    }

    void NewPreset()
    {
        if (AskName(T("Neues Preset", "New preset"), Config.UniqueName(T("Mein Preset", "My preset"))) is not { } name) return;
        var p = new Preset { Name = name };
        Config.Presets.Add(p);
        ActivatePreset(p);
    }

    void DuplicatePreset()
    {
        var copy = ShareCode.Import(ShareCode.Export(Config.Active)).Preset;
        copy.Name = Config.UniqueName(Config.Active.Name + T(" (Kopie)", " (copy)"));
        Config.Presets.Add(copy);
        ActivatePreset(copy);
    }

    void RenamePreset()
    {
        var p = Config.Active;
        if (AskName(T("Preset umbenennen", "Rename preset"), p.Name, p) is not { } name) return;
        p.Name = name;
        ActivatePreset(p);
    }

    void DeletePreset()
    {
        if (Config.Presets.Count == 1)
        {
            MessageBox.Show(this, T("Das letzte Preset kann nicht gelöscht werden.", "The last preset can't be deleted."), "SpotifyPad");
            return;
        }
        var p = Config.Active;
        if (MessageBox.Show(this, T($"Preset „{p.Name}“ löschen?", $"Delete preset \"{p.Name}\"?"), "SpotifyPad",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Config.Presets.Remove(p);
        ActivatePreset(Config.Presets[0]);
    }

    void SharePreset()
    {
        // Profile follows the detected controller, so this records which controller the bindings are for
        string code = ShareCode.Export(Config.Active, Profile);
        try { Clipboard.SetText(code); } catch { }
        Prompt.ShowText(this, T("Code teilen", "Share code"),
            T($"Der Code für „{Config.Active.Name}“ wurde in die Zwischenablage kopiert. Schick ihn einem Freund – " +
                "er kann ihn in SpotifyPad unter „Importieren“ einfügen.",
              $"The code for \"{Config.Active.Name}\" was copied to the clipboard. Send it to a friend – " +
                "they can paste it in SpotifyPad under \"Import\"."), code);
    }

    void ImportPreset()
    {
        string clip = "";
        try
        {
            var text = Clipboard.GetText().Trim();
            if (text.StartsWith(ShareCode.Prefix, StringComparison.OrdinalIgnoreCase)) clip = text;
        }
        catch { }

        var code = Prompt.Ask(this, T("Code importieren", "Import code"),
            T("Füge den SpotifyPad-Code ein (beginnt mit ", "Paste the SpotifyPad code (starts with ") + ShareCode.Prefix + "):", clip, multiline: true);
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            var imported = ShareCode.Import(code);
            var p = imported.Preset;
            var target = DetectedType() ?? Profile;
            bool converted = ShareCode.ConvertTo(imported, target);
            p.Name = Config.UniqueName(p.Name);
            Config.Presets.Add(p);
            ActivatePreset(p);
            SetProfile(target);

            string message = T($"Preset „{p.Name}“ importiert und ausgewählt. Mit „Speichern“ übernehmen.",
                $"Preset \"{p.Name}\" imported and selected. Click \"Save\" to keep it.");
            if (converted)
            {
                string from = PadButtons.TypeName(imported.Source!.Value), to = PadButtons.TypeName(target);
                string mapping = target == PadType.PlayStation ? " (LB → L1, A → ✕, Y → △ …)." : " (L1 → LB, ✕ → A, △ → Y …).";
                message = T($"Der Code ist für {from}-Controller – die Tasten wurden auf deinen {to}-Controller übertragen",
                              $"This code was made for {from} controllers – the buttons were converted to your {to} controller") +
                    mapping + "\n\n" + message;
            }
            MessageBox.Show(this, message, "SpotifyPad");
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, T("Code ungültig", "Invalid code"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static Label OptLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) };

    PadType Profile => (PadType)profile.SelectedIndex;
    List<PadBinding> Bindings => Config.BindingsFor(Profile);

    void SetProfile(PadType type)
    {
        settingProfile = true;
        profile.SelectedIndex = (int)type;
        settingProfile = false;
        RefreshList();
    }

    /// <summary>The connected controller type, or null if none (Xbox wins if both are connected).</summary>
    PadType? DetectedType() =>
        engine.ConnectedXbox > 0 ? PadType.Xbox
        : engine.ConnectedPlayStation > 0 ? PadType.PlayStation
        : null;

    void UpdateLive()
    {
        int xbox = engine.ConnectedXbox, ps = engine.ConnectedPlayStation;
        string psName = engine.PlayStationModel is { Length: > 0 } model ? $"PlayStation ({model})" : "PlayStation";
        detected.Text = (xbox > 0, ps > 0) switch
        {
            (true, true) => T("🎮 Xbox + " + psName + " erkannt", "🎮 Xbox + " + psName + " detected"),
            (true, false) => T("🎮 Xbox-Controller erkannt", "🎮 Xbox controller detected"),
            (false, true) => T("🎮 " + psName + "-Controller erkannt", "🎮 " + psName + " controller detected"),
            _ => T("Kein Controller erkannt", "No controller detected"),
        };
        detected.ForeColor = xbox + ps > 0 ? Color.SeaGreen : Color.DarkOrange;

        if (!profileChosenByUser && DetectedType() is { } type && type != Profile)
            SetProfile(type);

        uint p = engine.PressedOn(Profile);
        live.Text = xbox + ps == 0 ? "" : T("Gedrückt: ", "Pressed: ") + (p == 0 ? "–" : PadButtons.Format(p, Profile));
    }

    VolumeTarget SelectedVolumeTarget => (VolumeTarget)volTarget.SelectedIndex;

    /// <summary>Shows the live volume of whatever "Volume up/down changes" points at.</summary>
    async void UpdateVolume()
    {
        if (readingVolume) return;
        readingVolume = true;
        var target = SelectedVolumeTarget;
        int? v = await Task.Run(() => SpotifyControl.GetVolume(target));
        readingVolume = false;
        if (IsDisposed) return;

        volumeBar.Value = v ?? 0;
        string source = target switch
        {
            VolumeTarget.Spotify => T("Spotify-Regler", "Spotify slider"),
            VolumeTarget.Mixer => T("Windows-Mixer", "Windows mixer"),
            _ => T("System", "System"),
        };
        volumeText.Text = v is int p
            ? $"{p} %  ({source})"
            : target == VolumeTarget.Spotify
                ? T("–  (Spotify bzw. Lautstärke-Steuerung nicht aktiv)", "–  (Spotify or volume control not active)")
                : T("–  (Spotify spielt gerade nichts ab)", "–  (Spotify isn't playing)");
    }

    async void UpdateSpotifyStatus()
    {
        bool ok = await Task.Run(SpotifyDevTools.IsAvailable);
        if (IsDisposed) return;
        spotifyStatus.Text = ok
            ? T("Lautstärke-Steuerung: aktiv ✓", "Volume control: active ✓")
            : T("Lautstärke-Steuerung: Spotify neu starten nötig", "Volume control: restart Spotify");
        spotifyStatus.ForeColor = ok ? Color.SeaGreen : Color.DarkOrange;
    }

    void RestartSpotify()
    {
        try
        {
            SpotifyLauncher.Restart();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Spotify konnte nicht neu gestartet werden: ", "Couldn't restart Spotify: ") + ex.Message, "SpotifyPad");
        }
    }

    void RefreshList(int select = -1)
    {
        list.BeginUpdate();
        list.Items.Clear();
        header.Text = Profile == PadType.Xbox
            ? T("Belegung für Xbox-Controller – mehrere Tasten = Kombination (z. B. LB + RB + A).",
                "Bindings for Xbox controllers – several buttons = combination (e.g. LB + RB + A).")
            : T("Belegung für PlayStation-Controller – mehrere Tasten = Kombination (z. B. L1 + R1 + ✕).",
                "Bindings for PlayStation controllers – several buttons = combination (e.g. L1 + R1 + ✕).");
        foreach (var b in Bindings)
            list.Items.Add(new ListViewItem([PadActions.Describe(b), PadButtons.Format(b.Mask, Profile)]));
        list.EndUpdate();
        if (select >= 0 && select < list.Items.Count)
        {
            list.Items[select].Selected = true;
            list.Items[select].EnsureVisible();
        }
    }

    int SelectedIndex => list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1;

    void AddBinding()
    {
        using var dlg = new BindingDialog(null, engine, Profile, () => SpotifyControl.GetVolume(SelectedVolumeTarget));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Bindings.Add(dlg.Result);
        RefreshList(Bindings.Count - 1);
    }

    void EditSelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;
        using var dlg = new BindingDialog(Bindings[i], engine, Profile, () => SpotifyControl.GetVolume(SelectedVolumeTarget));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Bindings[i] = dlg.Result;
        RefreshList(i);
    }

    void RemoveSelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;
        Bindings.RemoveAt(i);
        RefreshList(Math.Min(i, Bindings.Count - 1));
    }

    void ResetToDefaults()
    {
        if (MessageBox.Show(this, T($"Alle {PadButtons.TypeName(Profile)}-Belegungen auf Standard zurücksetzen?",
                $"Reset all {PadButtons.TypeName(Profile)} bindings to the defaults?"), "SpotifyPad",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Config.SetBindings(Profile, AppConfig.DefaultBindings());
        RefreshList();
    }

    void Save()
    {
        Config.VolumeStep = (int)step.Value;
        Config.VolumeTarget = (VolumeTarget)volTarget.SelectedIndex;
        Config.SpotifyDirect = direct.Checked;
        Config.Rumble = rumble.Checked;
        Config.AutoRestartSpotify = autoRestart.Checked;
        Config.Language = (AppLanguage)language.SelectedIndex;
        if (patchSpotify.Checked != Config.PatchSpotifyLaunch)
        {
            try
            {
                SpotifyLauncher.Patch(patchSpotify.Checked);
                Config.PatchSpotifyLaunch = patchSpotify.Checked;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, T("Spotify-Start konnte nicht angepasst werden: ", "Couldn't change how Spotify starts: ") + ex.Message, "SpotifyPad");
            }
        }
        try
        {
            Config.StartWithWindows = autostart.Checked;
            Autostart.Set(autostart.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Autostart konnte nicht gesetzt werden: ", "Couldn't set autostart: ") + ex.Message, "SpotifyPad");
        }
        Saved = true;
        Close();
    }
}
