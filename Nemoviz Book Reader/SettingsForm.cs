using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Speech.Synthesis;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Settings dialog — UI shell only (Session 9). Nothing here is wired to
    /// AppSettings, actual TTS engines, or audio devices yet; that comes once
    /// the corresponding subsystems exist. Tabs: General, Audio Books (WIP),
    /// Text Books, Device, Misc (WIP). The "Show help hints" checkbox is the
    /// planned global switch for the hint-box pattern already used in the
    /// Go To dialog (flips hint Visible/TabStop live) — not yet connected to
    /// any per-control hints here, since none exist yet.
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly AppSettings appSettings;

        private CheckBox chkShowHints;
        private TabControl tabSettings;
        private Button btnOK;
        private Button btnCancel;
        private Button btnApply;

        // Library location (General tab) — the first genuinely wired control
        // in this dialog. The textbox is read-only and shows the staged path;
        // Browse changes it; OK/Apply persist it via AppSettings.
        private TextBox tbLibraryLocation;
        private string stagedLibraryPath;

        // General tab — the media keys, and every explanatory hint line in the
        // dialog (they are shown/hidden together by the switch at the top).
        private CheckBox chkUseMultimediaKeys;
        private CheckBox chkUseMultimediaKeysGlobally;
        private readonly List<TextBox> hints = new List<TextBox>();

        // Audio Books tab — use embedded metadata for title/author.
        private CheckBox chkUseMetadata;

        // Text Books tab — global TTS defaults (wired to AppSettings).
        private ComboBox cmbSpeechEngine;
        private ComboBox cmbVoice;
        // Voice → engine-group catalog (from the merged backends), for the
        // engine/voice two-combo picker.
        private List<(string Name, string Engine, string Language)> voiceCatalog;
        private ComboBox cmbLanguage;
        private readonly List<string> languageCodes = new List<string>();
        private NumericUpDown numRate;
        private NumericUpDown numVolume;
        private NumericUpDown numPitch;

        // Braille and visual output: the settings surface for the two branches that
        // still have to be built, so the shape is agreed before the work starts.
        private CheckBox chkBraille;
        private ComboBox cmbBrailleTable;
        private CheckBox chkVisual;
        private ComboBox cmbVisualMode;
        private ComboBox cmbHighlight;
        private ComboBox cmbHighlightColour;
        private ComboBox cmbTextColour;
        private ComboBox cmbBackColour;

        // Misc tab — the temporary classic/new look switch.
        private ComboBox cmbLook;

        // Device tab — output sound-card picker. The combo shows human-readable
        // descriptions; deviceIds[i] is the mpv identifier for row i. A live-apply
        // callback (from the player) switches the output on selection so the user
        // hears the change immediately.
        private ComboBox cmbSoundCard;
        private readonly List<MpvAudioDevices.Device> audioDevices;
        private readonly List<string> deviceIds = new List<string>();
        private readonly Action<string> applyAudioDeviceLive;

        public SettingsForm(AppSettings appSettings,
            List<MpvAudioDevices.Device> audioDevices = null,
            Action<string> applyAudioDeviceLive = null)
        {
            this.appSettings = appSettings;
            this.audioDevices = audioDevices ?? new List<MpvAudioDevices.Device>();
            this.applyAudioDeviceLive = applyAudioDeviceLive;
            this.stagedLibraryPath = appSettings.LibraryPath;

            this.Text = Localization.T("Dialog.Settings.Title");
            this.ClientSize = new Size(560, 560);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            chkShowHints = new CheckBox();
            chkShowHints.Text = Localization.T("Settings.ShowHints");
            chkShowHints.AccessibleName = Localization.T("Settings.ShowHints");
            chkShowHints.Location = new Point(10, 10);
            chkShowHints.Size = new Size(440, 24);
            chkShowHints.TabIndex = 0;
            chkShowHints.Checked = appSettings.ShowHints;
            // Live, without closing the window: the hints simply appear or go.
            chkShowHints.CheckedChanged += (s, e) =>
            {
                foreach (TextBox h in hints) { h.Visible = chkShowHints.Checked; h.TabStop = chkShowHints.Checked; }
            };

            tabSettings = new TabControl();
            tabSettings.Location = new Point(10, 40);
            tabSettings.Size = new Size(540, 470);
            tabSettings.TabIndex = 1;

            tabSettings.TabPages.Add(BuildGeneralTab());
            tabSettings.TabPages.Add(BuildAudioBooksTab());
            tabSettings.TabPages.Add(BuildTextBooksTab());
            tabSettings.TabPages.Add(BuildDeviceTab());
            tabSettings.TabPages.Add(BuildMiscTab());

            btnOK = new Button();
            btnOK.Text = Localization.T("Btn.OK");
            btnOK.AccessibleName = Localization.T("Settings.OK.Accessible");
            btnOK.Size = new Size(90, 32);
            btnOK.Location = new Point(260, 518);
            btnOK.TabIndex = 2;
            btnOK.DialogResult = DialogResult.OK;
            // Click fires before the dialog closes, so this persists on OK too.
            btnOK.Click += (s, e) => SaveSettings();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("Settings.Cancel.Accessible");
            btnCancel.Size = new Size(90, 32);
            btnCancel.Location = new Point(360, 518);
            btnCancel.TabIndex = 3;
            btnCancel.DialogResult = DialogResult.Cancel;

            // No DialogResult — Apply persists without closing the dialog.
            btnApply = new Button();
            btnApply.Text = Localization.T("Settings.Apply");
            btnApply.AccessibleName = Localization.T("Settings.Apply.Accessible");
            btnApply.Size = new Size(90, 32);
            btnApply.Location = new Point(460, 518);
            btnApply.TabIndex = 4;
            btnApply.Click += (s, e) => SaveSettings();

            this.Controls.Add(chkShowHints);
            this.Controls.Add(tabSettings);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnApply);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        /// <summary>Read-only, tabbable placeholder textbox — same "hint box"
        /// control shape used elsewhere, so an otherwise-empty tab still
        /// announces something to a screen reader instead of being silent.</summary>
        private TextBox BuildPlaceholder(string text, Point location, Size size)
        {
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.TabStop = true;
            tb.Location = location;
            tb.Size = size;
            tb.Text = text;
            tb.AccessibleName = text;
            return tb;
        }

        private TabPage BuildGeneralTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.General"));

            chkUseMultimediaKeys = new CheckBox();
            chkUseMultimediaKeys.Text = Localization.T("Settings.General.UseMultimediaKeys");
            chkUseMultimediaKeys.AccessibleName = Localization.T("Settings.General.UseMultimediaKeys");
            chkUseMultimediaKeys.Location = new Point(10, 16);
            chkUseMultimediaKeys.Size = new Size(470, 24);
            chkUseMultimediaKeys.TabIndex = 0;
            chkUseMultimediaKeys.Checked = appSettings.MediaKeys;
            chkUseMultimediaKeys.CheckedChanged += (s, e) => UpdateMediaKeyEnabled();

            chkUseMultimediaKeysGlobally = new CheckBox();
            chkUseMultimediaKeysGlobally.Text = Localization.T("Settings.General.UseMultimediaKeysGlobally");
            chkUseMultimediaKeysGlobally.AccessibleName = Localization.T("Settings.General.UseMultimediaKeysGlobally");
            chkUseMultimediaKeysGlobally.Location = new Point(28, 78);
            chkUseMultimediaKeysGlobally.Size = new Size(452, 24);
            chkUseMultimediaKeysGlobally.TabIndex = 2;
            chkUseMultimediaKeysGlobally.Checked = appSettings.MediaKeysGlobal;

            Label lblLibraryLocation = new Label();
            lblLibraryLocation.Text = Localization.T("Settings.General.LibraryLocation");
            lblLibraryLocation.Location = new Point(10, 148);
            lblLibraryLocation.Size = new Size(420, 18);
            lblLibraryLocation.TabStop = false;

            // Read-only so the path can only be changed via Browse, but
            // tabbable + carrying the folder as its value so a screen reader
            // reads the current location.
            tbLibraryLocation = new TextBox();
            tbLibraryLocation.ReadOnly = true;
            tbLibraryLocation.TabStop = true;
            tbLibraryLocation.Location = new Point(10, 168);
            tbLibraryLocation.Size = new Size(330, 23);
            tbLibraryLocation.Text = stagedLibraryPath;
            tbLibraryLocation.AccessibleName = Localization.T("Settings.General.LibraryLocation");
            tbLibraryLocation.TabIndex = 4;

            Button btnBrowse = new Button();
            btnBrowse.Text = Localization.T("Settings.General.Browse");
            btnBrowse.AccessibleName = Localization.T("Settings.General.Browse.Accessible");
            btnBrowse.Location = new Point(348, 167);
            btnBrowse.Size = new Size(90, 26);
            btnBrowse.TabIndex = 5;
            btnBrowse.Click += (s, e) => BrowseLibraryLocation();

            Label lblLanguage = new Label();
            lblLanguage.Text = Localization.T("Settings.General.Language");
            lblLanguage.Location = new Point(10, 240);
            lblLanguage.Size = new Size(160, 20);
            lblLanguage.TabStop = false;

            // App UI language. Only English exists until the app is
            // feature-complete (hr.lang is a final translation pass), so the
            // combo currently lists just the one language; not yet wired to
            // AppSettings.SetLanguage since there is nothing to switch to.
            ComboBox cmbLanguage = new ComboBox();
            cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLanguage.Location = new Point(180, 237);
            cmbLanguage.Size = new Size(240, 24);
            cmbLanguage.AccessibleName = Localization.T("Settings.General.Language");
            cmbLanguage.TabIndex = 7;
            cmbLanguage.Items.Add(Localization.T("LanguageName"));
            cmbLanguage.SelectedIndex = 0;

            page.Controls.Add(chkUseMultimediaKeys);
            page.Controls.Add(MakeHint("Settings.General.UseMultimediaKeys.Hint", 28, 42, 470, 32, 1));
            page.Controls.Add(chkUseMultimediaKeysGlobally);
            page.Controls.Add(MakeHint("Settings.General.UseMultimediaKeysGlobally.Hint", 46, 104, 452, 32, 3));
            page.Controls.Add(lblLibraryLocation);
            page.Controls.Add(tbLibraryLocation);
            page.Controls.Add(btnBrowse);
            page.Controls.Add(MakeHint("Settings.General.LibraryLocation.Hint", 10, 196, 470, 32, 6));
            page.Controls.Add(lblLanguage);
            page.Controls.Add(cmbLanguage);
            page.Controls.Add(MakeHint("Settings.General.Language.Hint", 10, 266, 470, 32, 8));
            UpdateMediaKeyEnabled();
            return page;
        }

        /// <summary>The global switch only means anything while the media keys are
        /// on at all.</summary>
        private void UpdateMediaKeyEnabled()
        {
            SetEnabled(chkUseMultimediaKeys != null && chkUseMultimediaKeys.Checked,
                       chkUseMultimediaKeysGlobally);
        }

        /// <summary>An explanatory hint under a control — the same read-only,
        /// TABBABLE textbox the Go To dialog uses. It has to be tabbable: a plain
        /// label is invisible to a screen reader driven by Tab, which is how this
        /// app is used, so the first version of these hints simply could not be
        /// read. The "Show help hints" switch takes them out of the tab order and
        /// off the screen together — which is exactly why they may be tabbable in
        /// the first place.</summary>
        private TextBox MakeHint(string key, int x, int y, int w, int h, int tabIndex)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.Text = Localization.T(key);
            t.AccessibleName = Localization.T("GoTo.Hint.Accessible");
            t.Location = new Point(x, y);
            t.Size = new Size(w, h);
            t.TabIndex = tabIndex;
            t.TabStop = appSettings.ShowHints;
            t.Visible = appSettings.ShowHints;
            hints.Add(t);
            return t;
        }

        /// <summary>Browse for a new library folder; only stages the choice
        /// (updates the read-only textbox) — it isn't persisted until OK or
        /// Apply.</summary>
        private void BrowseLibraryLocation()
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = Localization.T("Settings.General.LibraryLocation.Browse");
                if (Directory.Exists(stagedLibraryPath))
                    fbd.SelectedPath = stagedLibraryPath;
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    stagedLibraryPath = fbd.SelectedPath;
                    tbLibraryLocation.Text = stagedLibraryPath;
                }
            }
        }

        /// <summary>Persists the staged settings (currently just the library
        /// path). Called by both OK and Apply. Only writes when the path
        /// actually changed, and makes sure the target folder exists.</summary>
        private void SaveSettings()
        {
            if (stagedLibraryPath != appSettings.LibraryPath)
            {
                appSettings.SetLibraryPath(stagedLibraryPath);
                appSettings.EnsureLibraryExists();
            }

            // Audio Books — metadata-vs-folder-name choice.
            if (chkUseMetadata != null)
                appSettings.SetUseMetadata(chkUseMetadata.Checked);

            // General — hints and the media keys (the player re-applies the global
            // claim when the dialog closes).
            if (chkShowHints != null) appSettings.SetShowHints(chkShowHints.Checked);
            if (chkUseMultimediaKeys != null && chkUseMultimediaKeysGlobally != null)
                appSettings.SetMediaKeys(chkUseMultimediaKeys.Checked,
                                         chkUseMultimediaKeysGlobally.Checked);

            // Text Books — the default voice, plus how every voice touched in this
            // visit is set up (each keeps its own speed / volume / pitch).
            string voice = cmbVoice != null && cmbVoice.SelectedItem != null
                ? cmbVoice.SelectedItem.ToString() : (appSettings.TtsVoice ?? "");
            StageCurrentPrefs();
            foreach (var kv in stagedPrefs.All())
                appSettings.SetVoicePrefs(kv.Key, kv.Value);
            appSettings.SetTtsDefaults(voice, (int)numRate.Value, (int)numPitch.Value, (int)numVolume.Value);

            // Device — persist the chosen output card (empty = system default).
            if (cmbSoundCard != null)
            {
                int i = cmbSoundCard.SelectedIndex;
                if (i >= 0 && i < deviceIds.Count)
                    appSettings.SetAudioDevice(deviceIds[i]);
            }

            // Misc — the look. A window builds itself once, so the change lands
            // when NBR starts again; offer to do that now rather than leaving the
            // user wondering why nothing happened.
            if (cmbLook != null && !string.Equals(SelectedThemeId(), appSettings.UiTheme,
                                                  StringComparison.OrdinalIgnoreCase))
            {
                appSettings.SetUiTheme(SelectedThemeId());
                if (MessageBox.Show(this, Localization.T("Settings.Misc.Look.Restart"),
                                    Localization.T("Settings.Misc.Look.RestartTitle"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Application.Restart();
            }
        }

        private TabPage BuildAudioBooksTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.AudioBooks"));

            chkUseMetadata = new CheckBox();
            chkUseMetadata.Text = Localization.T("Settings.Audio.UseMetadata");
            chkUseMetadata.AccessibleName = Localization.T("Settings.Audio.UseMetadata");
            chkUseMetadata.Location = new Point(10, 20);
            chkUseMetadata.Size = new Size(430, 44);
            chkUseMetadata.TabIndex = 0;
            chkUseMetadata.Checked = appSettings.UseMetadata;

            page.Controls.Add(chkUseMetadata);
            page.Controls.Add(MakeHint("Settings.Audio.UseMetadata.Hint", 28, 66, 460, 60, 1));
            return page;
        }

        // Text Books is three groups: how the text is SPOKEN, how it goes to a
        // BRAILLE display, and how it is SHOWN. Speech is live; the other two are
        // the settings surface for the output branches still to be built, so their
        // controls are present but inert. Settings holds the DEFAULTS — each book
        // can override them in its own Properties.
        private TabPage BuildTextBooksTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.TextBooks"));
            page.AutoScroll = true;

            page.Controls.Add(BuildSpeechGroup());
            page.Controls.Add(MakeHint("Settings.TextBooks.Speech.Hint", 14, 292, 480, 32, 1));
            page.Controls.Add(BuildBrailleGroup(8, 330));
            page.Controls.Add(MakeHint("Settings.TextBooks.Braille.Hint", 14, 422, 480, 32, 3));
            page.Controls.Add(BuildVisualGroup(8, 460));
            page.Controls.Add(MakeHint("Settings.TextBooks.Visual.Hint", 14, 690, 480, 32, 5));
            return page;
        }

        // ── Speech: engine → language → voice, then how it sounds ─────────────
        private GroupBox BuildSpeechGroup()
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.SpeechGroup");
            box.Location = new Point(8, 6);
            box.Size = new Size(500, 280);

            int lx = 14, cx = 214, cw = 272, y = 26, tab = 0;

            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.SpeechEngine"), lx, y + 3));
            cmbSpeechEngine = MakeCombo(Localization.T("Settings.TextBooks.SpeechEngine"), cx, y, cw, tab++);
            cmbSpeechEngine.SelectedIndexChanged += (s, e) => PopulateLanguagesForEngine();
            box.Controls.Add(cmbSpeechEngine);

            y += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Language"), lx, y + 3));
            cmbLanguage = MakeCombo(Localization.T("Settings.TextBooks.Language"), cx, y, cw, tab++);
            cmbLanguage.SelectedIndexChanged += (s, e) => PopulateVoicesForSelection();
            box.Controls.Add(cmbLanguage);

            y += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Voice"), lx, y + 3));
            cmbVoice = MakeCombo(Localization.T("Settings.TextBooks.Voice"), cx, y, cw, tab++);
            // Speed / volume / pitch belong to the VOICE, so picking one shows how
            // that voice is set up here — never the numbers of the previous voice,
            // which sound completely different on another engine.
            cmbVoice.SelectedIndexChanged += (s, e) => LoadPrefsForSelectedVoice();
            box.Controls.Add(cmbVoice);

            // Numeric fields rather than sliders: a screen reader speaks the value
            // on every step, which a track bar does not.
            y += 40;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Speed"), lx, y + 3));
            numRate = MakeNumeric(Localization.T("Settings.TextBooks.Speed"), cx, y, 80, 400,
                                  Clamp(appSettings.TtsWpm, 80, 400), tab++, 5);
            box.Controls.Add(numRate);

            y += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Volume"), lx, y + 3));
            numVolume = MakeNumeric(Localization.T("Settings.TextBooks.Volume"), cx, y, 0, 100,
                                    Clamp(appSettings.TtsVolume, 0, 100), tab++, 5);
            box.Controls.Add(numVolume);

            y += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Pitch"), lx, y + 3));
            numPitch = MakeNumeric(Localization.T("Settings.TextBooks.Pitch"), cx, y, -10, 10,
                                   Clamp(appSettings.TtsPitch, -10, 10), tab++);
            box.Controls.Add(numPitch);

            y += 36;
            Button btnTest = new Button();
            btnTest.Text = Localization.T("Settings.TextBooks.Test");
            btnTest.AccessibleName = Localization.T("Settings.TextBooks.Test");
            btnTest.Location = new Point(cx, y);
            btnTest.Size = new Size(112, 30);
            btnTest.TabIndex = tab++;
            btnTest.Click += (s, e) => TestVoice();
            box.Controls.Add(btnTest);

            Button btnDict = new Button();
            btnDict.Text = Localization.T("Dict.Open");
            btnDict.AccessibleName = Localization.T("Dict.Open.Accessible");
            // Beside Test voice, and inside the group: 214 + 118 + 150 = 482 of the
            // 486 the box has to give.
            btnDict.Location = new Point(cx + 118, y);
            btnDict.Size = new Size(150, 30);
            btnDict.TabIndex = tab++;
            btnDict.Click += (s, e) => OpenDictionary();
            box.Controls.Add(btnDict);

            // Fill the cascade and restore the saved default voice.
            try { voiceCatalog = EnsureSpeech().GetVoiceCatalog(); }
            catch { voiceCatalog = new List<(string, string, string)>(); }

            var engines = new List<string>();
            foreach (var c in voiceCatalog)
                if (!engines.Contains(c.Engine)) engines.Add(c.Engine);
            engines.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string en in engines) cmbSpeechEngine.Items.Add(en);

            string savedVoice = appSettings.TtsVoice ?? "";
            string savedEngine = null, savedLang = null;
            foreach (var c in voiceCatalog)
                if (string.Equals(c.Name, savedVoice, StringComparison.OrdinalIgnoreCase))
                { savedEngine = c.Engine; savedLang = c.Language; break; }

            int ei = savedEngine != null ? cmbSpeechEngine.Items.IndexOf(savedEngine) : -1;
            if (ei < 0 && cmbSpeechEngine.Items.Count > 0) ei = 0;
            if (ei >= 0) cmbSpeechEngine.SelectedIndex = ei;   // cascades to language + voice

            if (savedLang != null)
            {
                int li = languageCodes.IndexOf(savedLang);
                if (li >= 0) cmbLanguage.SelectedIndex = li;   // cascades to voice
            }
            int svi = cmbVoice.Items.IndexOf(savedVoice);
            if (svi >= 0) cmbVoice.SelectedIndex = svi;
            LoadPrefsForSelectedVoice();
            return box;
        }

        // Voices set up during this visit to the dialog. Held here rather than
        // written straight through, so several voices can be adjusted in one visit
        // and Cancel still discards the lot.
        private readonly VoicePrefsTable stagedPrefs = new VoicePrefsTable();
        private string prefsVoice = "";

        /// <summary>Shows the selected voice's remembered speed / volume / pitch,
        /// or the neutral default for a voice this machine hasn't set up yet. What
        /// was on screen is first filed under the voice being left, so switching
        /// back and forth doesn't lose an adjustment.</summary>
        private void LoadPrefsForSelectedVoice()
        {
            if (cmbVoice == null || numRate == null || numVolume == null || numPitch == null) return;
            string voice = cmbVoice.SelectedItem != null ? cmbVoice.SelectedItem.ToString() : "";
            if (string.IsNullOrEmpty(voice) || string.Equals(voice, prefsVoice, StringComparison.OrdinalIgnoreCase))
                return;

            StageCurrentPrefs();
            prefsVoice = voice;
            VoicePrefs p = stagedPrefs.Get(voice, appSettings.PrefsFor(voice));
            numRate.Value = Clamp(p.Wpm, (int)numRate.Minimum, (int)numRate.Maximum);
            numVolume.Value = Clamp(p.Volume, (int)numVolume.Minimum, (int)numVolume.Maximum);
            numPitch.Value = Clamp(p.Pitch, (int)numPitch.Minimum, (int)numPitch.Maximum);
        }

        /// <summary>Files what the three fields currently show under the voice they
        /// belong to.</summary>
        private void StageCurrentPrefs()
        {
            if (string.IsNullOrEmpty(prefsVoice) || numRate == null) return;
            stagedPrefs.Set(prefsVoice,
                new VoicePrefs((int)numRate.Value, (int)numVolume.Value, (int)numPitch.Value));
        }

        // ── Braille output (placeholder for the display branch) ───────────────
        private GroupBox BuildBrailleGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.BrailleGroup");
            box.Location = new Point(x, y);
            box.Size = new Size(500, 86);

            chkBraille = new CheckBox();
            chkBraille.Text = Localization.T("Settings.TextBooks.UseBraille");
            chkBraille.AccessibleName = Localization.T("Settings.TextBooks.UseBraille");
            chkBraille.Location = new Point(14, 22);
            chkBraille.Size = new Size(470, 24);
            chkBraille.TabIndex = 0;
            chkBraille.CheckedChanged += (s, e) => UpdateBrailleEnabled();
            box.Controls.Add(chkBraille);

            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.BrailleTable"), 14, 55));
            cmbBrailleTable = MakeCombo(Localization.T("Settings.TextBooks.BrailleTable"), 214, 52, 272, 1);
            // The tables NBR ships for reading .brf books; the same list serves as
            // the default for what a braille display would be sent.
            cmbBrailleTable.Items.Add(Localization.T("Settings.TextBooks.BrailleTableAuto"));
            foreach (BrailleTableInfo t in BrailleTables.All) cmbBrailleTable.Items.Add(t.Display);
            cmbBrailleTable.SelectedIndex = 0;
            box.Controls.Add(cmbBrailleTable);

            UpdateBrailleEnabled();
            return box;
        }

        // ── Visual output (placeholder for the on-screen branch) ──────────────
        private GroupBox BuildVisualGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.VisualGroup");
            box.Location = new Point(x, y);
            box.Size = new Size(500, 224);

            chkVisual = new CheckBox();
            chkVisual.Text = Localization.T("Settings.TextBooks.UseVisual");
            chkVisual.AccessibleName = Localization.T("Settings.TextBooks.UseVisual");
            chkVisual.Location = new Point(14, 22);
            chkVisual.Size = new Size(470, 24);
            chkVisual.TabIndex = 0;
            chkVisual.CheckedChanged += (s, e) => UpdateVisualEnabled();
            box.Controls.Add(chkVisual);

            int lx = 14, cx = 214, cw = 272, yy = 52, tab = 1;

            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.VisualMode"), lx, yy + 3));
            cmbVisualMode = MakeCombo(Localization.T("Settings.TextBooks.VisualMode"), cx, yy, cw, tab++);
            cmbVisualMode.Items.Add(Localization.T("Settings.TextBooks.VisualMode.TwoRows"));
            cmbVisualMode.Items.Add(Localization.T("Settings.TextBooks.VisualMode.FullInstant"));
            cmbVisualMode.Items.Add(Localization.T("Settings.TextBooks.VisualMode.FullScrolling"));
            cmbVisualMode.SelectedIndex = 0;
            box.Controls.Add(cmbVisualMode);

            yy += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Highlight"), lx, yy + 3));
            cmbHighlight = MakeCombo(Localization.T("Settings.TextBooks.Highlight"), cx, yy, cw, tab++);
            cmbHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.None"));
            cmbHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Word"));
            cmbHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Sentence"));
            cmbHighlight.SelectedIndex = 2;
            box.Controls.Add(cmbHighlight);

            yy += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.HighlightColour"), lx, yy + 3));
            cmbHighlightColour = MakeCombo(Localization.T("Settings.TextBooks.HighlightColour"), cx, yy, cw, tab++);
            box.Controls.Add(cmbHighlightColour);

            yy += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.TextColour"), lx, yy + 3));
            cmbTextColour = MakeCombo(Localization.T("Settings.TextBooks.TextColour"), cx, yy, cw, tab++);
            box.Controls.Add(cmbTextColour);

            yy += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.BackColour"), lx, yy + 3));
            cmbBackColour = MakeCombo(Localization.T("Settings.TextBooks.BackColour"), cx, yy, cw, tab++);
            box.Controls.Add(cmbBackColour);

            // High-contrast pairs first — this is the audience that needs them.
            string[] colours =
            {
                Localization.T("Settings.Colour.White"), Localization.T("Settings.Colour.Black"),
                Localization.T("Settings.Colour.Yellow"), Localization.T("Settings.Colour.Blue"),
                Localization.T("Settings.Colour.Green"), Localization.T("Settings.Colour.Red")
            };
            foreach (string c in colours) { cmbTextColour.Items.Add(c); cmbBackColour.Items.Add(c); cmbHighlightColour.Items.Add(c); }
            cmbHighlightColour.SelectedIndex = 3;   // blue highlight under yellow-on-black
            cmbTextColour.SelectedIndex = 2;   // yellow on black: the usual low-vision pair
            cmbBackColour.SelectedIndex = 1;

            UpdateVisualEnabled();
            return box;
        }

        /// <summary>Everything under "Use braille output" follows the checkbox —
        /// dimmed and out of the tab order while the feature is off.</summary>
        private void UpdateBrailleEnabled()
        {
            bool on = chkBraille != null && chkBraille.Checked;
            SetEnabled(on, cmbBrailleTable);
        }

        private void UpdateVisualEnabled()
        {
            bool on = chkVisual != null && chkVisual.Checked;
            SetEnabled(on, cmbVisualMode, cmbHighlight, cmbHighlightColour, cmbTextColour, cmbBackColour);
        }

        internal static void SetEnabled(bool on, params Control[] controls)
        {
            foreach (Control c in controls)
                if (c != null) { c.Enabled = on; c.TabStop = on; }
        }

        // ── Small builders, so the layout above stays readable ────────────────
        internal static Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            // AutoSize, not a fixed 150 px: a longer caption ("Reading speed (words
            // per minute):") was silently cut off where the field column began.
            // The field columns leave room for the longest label in each dialog.
            l.AutoSize = true;
            l.TabStop = false;
            return l;
        }

        internal static ComboBox MakeCombo(string name, int x, int y, int w, int tabIndex)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 24);
            c.AccessibleName = name;
            c.TabIndex = tabIndex;
            return c;
        }

        /// <summary>A spin box. <paramref name="increment"/> is the arrow step — it
        /// matches the player's own step for the same value (5 WPM, 5 % volume,
        /// 10 % speed), because stepping by 1 through those ranges takes far too
        /// long when every step is spoken.</summary>
        internal static NumericUpDown MakeNumeric(string name, int x, int y, int min, int max, int value, int tabIndex,
                                                  int increment = 1)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = increment;
            n.Value = Clamp(value, min, max);
            n.Location = new Point(x, y);
            n.Size = new Size(90, 24);
            n.AccessibleName = name;
            n.TabIndex = tabIndex;
            return n;
        }

        /// <summary>A spin box for a fractional value — the playback-speed
        /// multiplier, which is written the same way everywhere it appears
        /// (1,4× in the player, the library details and here), never as a
        /// percentage.</summary>
        internal static NumericUpDown MakeDecimal(string name, int x, int y, decimal min, decimal max,
                                                  decimal value, int tabIndex, decimal increment, int decimals)
        {
            NumericUpDown n = new NumericUpDown();
            n.DecimalPlaces = decimals;
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = increment;
            n.Value = value < min ? min : (value > max ? max : value);
            n.Location = new Point(x, y);
            n.Size = new Size(90, 24);
            n.AccessibleName = name;
            n.TabIndex = tabIndex;
            return n;
        }

        /// <summary>Engine chosen → list the languages that engine actually speaks.</summary>
        private void PopulateLanguagesForEngine()
        {
            if (cmbLanguage == null || cmbSpeechEngine == null || voiceCatalog == null) return;
            string engine = cmbSpeechEngine.SelectedItem as string;
            cmbLanguage.Items.Clear();
            languageCodes.Clear();
            foreach (var c in voiceCatalog)
            {
                if (c.Engine != engine) continue;
                string code = string.IsNullOrEmpty(c.Language) ? "" : c.Language;
                if (languageCodes.Contains(code)) continue;
                languageCodes.Add(code);
                cmbLanguage.Items.Add(LanguageLabel(code));
            }
            if (cmbLanguage.Items.Count > 0) cmbLanguage.SelectedIndex = 0;  // cascades to voice
            else PopulateVoicesForSelection();
        }

        /// <summary>Engine + language chosen → the voices that match both.</summary>
        private void PopulateVoicesForSelection()
        {
            if (cmbVoice == null || cmbSpeechEngine == null || voiceCatalog == null) return;
            string engine = cmbSpeechEngine.SelectedItem as string;
            int li = cmbLanguage != null ? cmbLanguage.SelectedIndex : -1;
            string lang = (li >= 0 && li < languageCodes.Count) ? languageCodes[li] : null;

            cmbVoice.Items.Clear();
            foreach (var c in voiceCatalog)
            {
                if (c.Engine != engine) continue;
                if (lang != null && (c.Language ?? "") != lang) continue;
                cmbVoice.Items.Add(c.Name);
            }
            if (cmbVoice.Items.Count > 0) cmbVoice.SelectedIndex = 0;
        }

        /// <summary>"hr-HR" → the language's own name, so the list reads naturally.</summary>
        internal static string LanguageLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return Localization.T("Settings.TextBooks.LanguageUnknown");
            try { return new System.Globalization.CultureInfo(code).DisplayName + " (" + code + ")"; }
            catch { return code; }
        }

        /// <summary>Opens the user's speech dictionary for the voice and language
        /// currently picked here, so a rule lands where they are looking. The "Try
        /// it" box speaks through the same voice.</summary>
        private void OpenDictionary()
        {
            string voice = cmbVoice != null && cmbVoice.SelectedItem != null
                ? cmbVoice.SelectedItem.ToString() : "";
            int li = cmbLanguage != null ? cmbLanguage.SelectedIndex : -1;
            string lang = (li >= 0 && li < languageCodes.Count) ? languageCodes[li] : "";

            using (var dlg = new SpeechDictionaryForm(lang, voice, SpeakSample))
                dlg.ShowDialog(this);
            // Whatever was edited takes effect from the next sentence read.
            SpeechDictionaries.Reload();
        }

        /// <summary>Says a line with the voice selected here — used by the
        /// dictionary's "Try it".</summary>
        private void SpeakSample(string text)
        {
            try
            {
                CompositeSpeechBackend sp = EnsureSpeech();
                if (cmbVoice != null && cmbVoice.SelectedItem != null)
                    sp.SelectVoice(cmbVoice.SelectedItem.ToString());
                sp.SetRate(TtsReader.WpmToRate((int)numRate.Value));
                sp.SetVolume((int)numVolume.Value);
                sp.SetPitch((int)numPitch.Value * 5);
                sp.Cancel();
                sp.Speak(text);
            }
            catch { }
        }

        /// <summary>Speaks a short sample with the currently selected voice /
        /// rate / pitch / volume so the user can preview it (asynchronous, so it
        /// doesn't block the dialog).</summary>
        private void TestVoice()
        {
            try
            {
                CompositeSpeechBackend sp = EnsureSpeech();
                if (cmbVoice != null && cmbVoice.SelectedItem != null)
                    sp.SelectVoice(cmbVoice.SelectedItem.ToString());
                sp.SetRate(TtsReader.WpmToRate((int)numRate.Value));
                sp.SetVolume((int)numVolume.Value);
                sp.SetPitch((int)numPitch.Value * 5); // -10..10 → -50..50 %
                sp.Cancel();                      // stop any still-playing sample
                sp.Speak(Localization.T("Settings.TextBooks.TestSample"));
            }
            catch { }
        }

        // Fills the Voice combo with the voices of the currently-selected engine.

        // Lazily-created merged speech backend (64-bit + 32-bit satellite), used
        // for the voice list and the Test button; disposed with the dialog.
        private CompositeSpeechBackend speech;
        private CompositeSpeechBackend EnsureSpeech()
        {
            if (speech == null) speech = new CompositeSpeechBackend();
            // The sample must come out of the card being chosen, not the system
            // default — the Device tab and the Test button are usually pressed in
            // the same visit, and every backend can follow the choice now.
            speech.SetAudioDevice(SelectedDeviceId());
            return speech;
        }

        /// <summary>The output card currently picked in the Device tab, falling
        /// back to the persisted one before that tab has been touched.</summary>
        private string SelectedDeviceId()
        {
            if (cmbSoundCard != null)
            {
                int i = cmbSoundCard.SelectedIndex;
                if (i >= 0 && i < deviceIds.Count) return deviceIds[i];
            }
            return appSettings.AudioDevice ?? "";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { speech?.Dispose(); } catch { }
            speech = null;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private TabPage BuildDeviceTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.Device"));

            Label lblSoundCard = new Label();
            lblSoundCard.Text = Localization.T("Settings.Device.SoundCard");
            lblSoundCard.Location = new Point(10, 22);
            lblSoundCard.Size = new Size(160, 20);

            cmbSoundCard = new ComboBox();
            cmbSoundCard.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSoundCard.Location = new Point(180, 19);
            cmbSoundCard.Size = new Size(240, 24);
            cmbSoundCard.AccessibleName = Localization.T("Settings.Device.SoundCard");
            cmbSoundCard.TabIndex = 0;

            // Populate from the live mpv device list. Each row's identifier is kept
            // in deviceIds parallel to the combo; mpv's own "auto" entry (system
            // default) is relabelled for clarity and always sits first.
            int selected = 0;
            foreach (MpvAudioDevices.Device dev in audioDevices)
            {
                string id = dev.Name ?? "";
                string label = id == "auto"
                    ? Localization.T("Settings.Device.Default")
                    : (!string.IsNullOrEmpty(dev.Description) ? dev.Description : id);
                if (string.Equals(id, appSettings.AudioDevice, StringComparison.OrdinalIgnoreCase))
                    selected = deviceIds.Count;
                deviceIds.Add(id);
                cmbSoundCard.Items.Add(label);
            }
            // Fallback so the tab is never empty if mpv returned nothing.
            if (cmbSoundCard.Items.Count == 0)
            {
                deviceIds.Add("");
                cmbSoundCard.Items.Add(Localization.T("Settings.Device.Default"));
            }
            cmbSoundCard.SelectedIndex = Math.Min(selected, cmbSoundCard.Items.Count - 1);

            // Live preview: switch the player's output the moment a card is picked.
            cmbSoundCard.SelectedIndexChanged += (s, e) =>
            {
                int i = cmbSoundCard.SelectedIndex;
                if (i >= 0 && i < deviceIds.Count)
                    applyAudioDeviceLive?.Invoke(deviceIds[i]);
            };

            page.Controls.Add(lblSoundCard);
            page.Controls.Add(cmbSoundCard);
            page.Controls.Add(MakeHint("Settings.Device.Hint", 10, 52, 480, 32, 1));
            return page;
        }

        // Misc — for now the one thing that lives here is the temporary switch
        // between the look the app has always had and the redesign in progress.
        private TabPage BuildMiscTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.Misc"));

            page.Controls.Add(MakeLabel(Localization.T("Settings.Misc.Look"), 10, 22));
            cmbLook = MakeCombo(Localization.T("Settings.Misc.Look"), 150, 18, 340, 0);
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.Classic"));
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.New"));
            cmbLook.SelectedIndex =
                string.Equals(appSettings.UiTheme, UiTheme.NewId, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            page.Controls.Add(cmbLook);
            page.Controls.Add(MakeHint("Settings.Misc.Look.Hint", 10, 52, 480, 46, 1));

            page.Controls.Add(BuildPlaceholder(Localization.T("Settings.WorkInProgress"),
                new Point(10, 110), new Size(480, 30)));
            return page;
        }

        /// <summary>The chosen look, as an id for AppSettings.</summary>
        private string SelectedThemeId()
        {
            return cmbLook != null && cmbLook.SelectedIndex == 1 ? UiTheme.NewId : UiTheme.ClassicId;
        }
    }
}
