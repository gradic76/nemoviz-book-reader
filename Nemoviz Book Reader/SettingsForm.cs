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

        // The loose-hint machinery that used to live here is gone with the loose
        // controls (2026-08-03). General is five GROUPS now and Misc no longer
        // exists, so every explanation in this dialog hangs off a group the
        // ordinary way — which is also why a reader now hears "Library location"
        // before the path instead of a bare text box.

        // Audio Books tab — use embedded metadata for title/author.
        private CheckBox chkUseMetadata;

        // Text Books tab — global TTS defaults (wired to AppSettings).
        // The voice NAMES behind the Voice combo's rows, which are not the same
        // strings: a stand-in row also names the language it is borrowed from.
        private readonly List<string> voiceNames = new List<string>();
        // Language → voice chosen in this visit ("" = the global default). Staged
        // rather than written through, so several languages can be set up in one
        // go and Cancel still discards the lot.
        private readonly Dictionary<string, string> stagedLanguageVoices =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // True while the combos are being filled: selecting a row then is us, not
        // the user, and must not be filed as a choice.
        private bool populating;
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
        private CheckBox chkKeepAlive;
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
            tabSettings.TabPages.Add(BuildTextBooksTab());
            tabSettings.TabPages.Add(BuildDeviceTab());

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

            // Built exactly as before, then handed over — the classic path does
            // nothing here, the new look restyles and relays out what was built.
            if (UiTheme.Current.BuildsOwnLayout) SettingsSkin.Apply(this);
        }

        /// <summary>What the skin is allowed to touch. Everything else it finds by
        /// walking the tab pages, the same way the Properties reading page does.</summary>
        internal SettingsParts SkinParts
        {
            get
            {
                return new SettingsParts
                {
                    Tabs = tabSettings,
                    ShowHints = chkShowHints,
                    Hints = hints,
                    OK = btnOK,
                    Cancel = btnCancel,
                    Apply = btnApply,
                };
            }
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

        /// <summary>General, as five groups rather than a column of loose
        /// controls (Gordan, 2026-08-03): <b>Language, Library location, Media
        /// keys, Metadata, Look</b> — and that is the tab order too, which the
        /// old page did not have in any recognisable sequence.
        ///
        /// <para>Making them GROUPS is what earns the rest. A group carries its
        /// own <c>?</c> the ordinary way, so the loose-hint machinery this page
        /// needed goes away; a group is announced on the way in, so a reader
        /// hears "Library location" before the path; and the new look already
        /// knows how to arrange groups into columns, so the three-across layout
        /// costs nothing here.</para>
        ///
        /// <para>Built at the CLASSIC width and stacked. The 560-wide dialog
        /// cannot hold three columns; the 960-wide one can, and rearranging them
        /// is the skin's job — the same way Text Books has always worked.</para>
        ///
        /// <para><b>Look moved here from Misc</b>, which is gone. It stopped
        /// being a temporary switch the moment themes became a real choice.</para></summary>
        private TabPage BuildGeneralTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.General"));
            // Read by the skin: these five are short and of a kind, so they go
            // three across and wrap, rather than down one side of a 960-wide
            // page (Gordan). The classic look ignores it and stacks them.
            page.Tag = "grid3";

            const int GW = 500, LX = 14, CX = 150, CW = 330;
            int y = 8, tab = 0;

            // ── 1. Language ──────────────────────────────────────────────
            GroupBox gLang = MakeGroup(Localization.T("Settings.General.LanguageGroup"), y, GW, 62);
            gLang.Controls.Add(MakeLabel(Localization.T("Settings.General.Language"), LX, 26));
            // NOT cmbLanguage — that field is the BOOK's language over on Speech
            // and Braille, and reusing it here would have quietly taken that page
            // apart. This one is the language NBR speaks to the user in.
            ComboBox cmbUiLanguage = MakeCombo(Localization.T("Settings.General.Language"), CX, 22, CW, tab++);
            // Only English exists until the app is feature-complete (hr.lang is a
            // final translation pass), so the combo lists the one language and is
            // not yet wired to AppSettings.SetLanguage — there is nothing to
            // switch to.
            cmbUiLanguage.Items.Add(Localization.T("LanguageName"));
            cmbUiLanguage.SelectedIndex = 0;
            gLang.Controls.Add(cmbUiLanguage);
            page.Controls.Add(gLang);
            y += gLang.Height + 8;

            // ── 2. Library location ──────────────────────────────────────
            GroupBox gLib = MakeGroup(Localization.T("Settings.General.LibraryGroup"), y, GW, 62);
            // Read-only so the path can only be changed through Browse, but
            // tabbable and carrying the folder as its value, so a reader hears
            // where the library is.
            tbLibraryLocation = new TextBox();
            tbLibraryLocation.ReadOnly = true;
            tbLibraryLocation.TabStop = true;
            tbLibraryLocation.SetBounds(LX, 24, 340, 23);
            tbLibraryLocation.Text = stagedLibraryPath;
            tbLibraryLocation.AccessibleName = Localization.T("Settings.General.LibraryLocation");
            tbLibraryLocation.TabIndex = tab++;
            gLib.Controls.Add(tbLibraryLocation);

            Button btnBrowse = new Button();
            btnBrowse.Text = Localization.T("Settings.General.Browse");
            btnBrowse.AccessibleName = Localization.T("Settings.General.Browse.Accessible");
            btnBrowse.SetBounds(LX + 350, 23, 90, 26);
            btnBrowse.TabIndex = tab++;
            btnBrowse.Click += (s, e) => BrowseLibraryLocation();
            gLib.Controls.Add(btnBrowse);
            page.Controls.Add(gLib);
            y += gLib.Height + 8;

            // ── 3. Media keys ────────────────────────────────────────────
            GroupBox gKeys = MakeGroup(Localization.T("Settings.General.MediaKeysGroup"), y, GW, 84);
            chkUseMultimediaKeys = new CheckBox();
            chkUseMultimediaKeys.Text = Localization.T("Settings.General.UseMultimediaKeys");
            chkUseMultimediaKeys.AccessibleName = Localization.T("Settings.General.UseMultimediaKeys");
            chkUseMultimediaKeys.SetBounds(LX, 22, GW - 30, 24);
            chkUseMultimediaKeys.TabIndex = tab++;
            chkUseMultimediaKeys.Checked = appSettings.MediaKeys;
            chkUseMultimediaKeys.CheckedChanged += (s, e) => UpdateMediaKeyEnabled();
            gKeys.Controls.Add(chkUseMultimediaKeys);

            chkUseMultimediaKeysGlobally = new CheckBox();
            chkUseMultimediaKeysGlobally.Text = Localization.T("Settings.General.UseMultimediaKeysGlobally");
            chkUseMultimediaKeysGlobally.AccessibleName = Localization.T("Settings.General.UseMultimediaKeysGlobally");
            chkUseMultimediaKeysGlobally.SetBounds(LX + 18, 50, GW - 48, 24);
            chkUseMultimediaKeysGlobally.TabIndex = tab++;
            chkUseMultimediaKeysGlobally.Checked = appSettings.MediaKeysGlobal;
            gKeys.Controls.Add(chkUseMultimediaKeysGlobally);
            page.Controls.Add(gKeys);
            y += gKeys.Height + 8;

            // ── 4. Metadata ──────────────────────────────────────────────
            // Moved out of Audio Books on 2026-08-02 and it never belonged
            // there: it decides where a book's title and author come from for
            // DAISY and EPUB exactly as much as for audio, and a reader of text
            // books never goes looking on a page called Audio Books.
            GroupBox gMeta = MakeGroup(Localization.T("Settings.General.MetadataGroup"), y, GW, 56);
            chkUseMetadata = new CheckBox();
            chkUseMetadata.Text = Localization.T("Settings.General.UseMetadata");
            chkUseMetadata.AccessibleName = Localization.T("Settings.General.UseMetadata");
            chkUseMetadata.SetBounds(LX, 22, GW - 30, 24);
            chkUseMetadata.TabIndex = tab++;
            chkUseMetadata.Checked = appSettings.UseMetadata;
            gMeta.Controls.Add(chkUseMetadata);
            page.Controls.Add(gMeta);
            y += gMeta.Height + 8;

            // ── 5. Look ──────────────────────────────────────────────────
            GroupBox gLook = MakeGroup(Localization.T("Settings.General.LookGroup"), y, GW, 62);
            gLook.Controls.Add(MakeLabel(Localization.T("Settings.Misc.Look"), LX, 26));
            cmbLook = MakeCombo(Localization.T("Settings.Misc.Look"), CX, 22, CW, tab++);
            // Follow Windows first, and it is where a reader who has never
            // chosen already stands: under high contrast it gives the
            // system-colours layout, otherwise the new one. The other two are
            // deliberate choices and are honoured whatever Windows is doing.
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.Follow"));
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.Classic"));
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.New"));
            cmbLook.SelectedIndex =
                string.Equals(appSettings.UiTheme, UiTheme.NewId, StringComparison.OrdinalIgnoreCase) ? 2
              : string.Equals(appSettings.UiTheme, UiTheme.ClassicId, StringComparison.OrdinalIgnoreCase) ? 1
              : 0;
            gLook.Controls.Add(cmbLook);
            page.Controls.Add(gLook);

            UpdateMediaKeyEnabled();
            return page;
        }

        /// <summary>A group box at the page's left margin, sized by its caller —
        /// the classic layout stacks them and the skin rearranges them.</summary>
        private static GroupBox MakeGroup(string text, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.AccessibleName = text;
            g.SetBounds(10, y, w, h);
            return g;
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

            // Text Books — which voice reads which language, plus how every voice
            // touched in this visit is set up (each keeps its own speed / volume /
            // pitch).
            StageCurrentPrefs();
            if (cmbLanguage != null) stagedLanguageVoices[SelectedLanguageCode()] = SelectedVoiceName();
            foreach (var kv in stagedPrefs.All())
                appSettings.SetVoicePrefs(kv.Key, kv.Value);

            string globalVoice = appSettings.TtsVoice ?? "";
            foreach (var kv in stagedLanguageVoices)
            {
                if (kv.Key.Length == 0) globalVoice = kv.Value;      // "all other languages"
                else appSettings.SetLanguageVoice(kv.Key, kv.Value);
            }
            // The global default's own numbers, not whichever voice happens to be
            // on screen: the selected row may well be another language's voice.
            VoicePrefs gp = stagedPrefs.Get(globalVoice, appSettings.PrefsFor(globalVoice));
            appSettings.SetTtsDefaults(globalVoice, gp.Wpm, gp.Pitch, gp.Volume);

            // Text Books — how a book looks on screen, when the book itself has
            // not been given a look of its own. These six wrote nowhere at all
            // until 2026-08-03.
            if (chkVisual != null && cmbVisualMode != null && cmbHighlight != null)
                appSettings.SetVisualDefaults(
                    chkVisual.Checked,
                    cmbVisualMode.SelectedIndex,
                    cmbHighlight.SelectedIndex,
                    cmbHighlightColour != null ? cmbHighlightColour.SelectedIndex : -1,
                    cmbTextColour != null ? cmbTextColour.SelectedIndex : -1,
                    cmbBackColour != null ? cmbBackColour.SelectedIndex : -1);

            // Device — persist the chosen output card (empty = system default),
            // and whether the card is held awake between sentences.
            if (cmbSoundCard != null)
            {
                int i = cmbSoundCard.SelectedIndex;
                if (i >= 0 && i < deviceIds.Count)
                    appSettings.SetAudioDevice(deviceIds[i]);
            }
            if (chkKeepAlive != null) appSettings.SetKeepDeviceAlive(chkKeepAlive.Checked);

            // Misc — the look. A window builds itself once, so the change lands
            // when NBR starts again; offer to do that now rather than leaving the
            // user wondering why nothing happened.
            if (cmbLook != null && !string.Equals(SelectedThemeId(), appSettings.UiTheme,
                                                  StringComparison.OrdinalIgnoreCase))
            {
                appSettings.SetUiTheme(SelectedThemeId());
                if (MessageForm.ShowConfirm(this, Localization.T("Settings.Misc.Look.Restart"),
                                           Localization.T("Settings.Misc.Look.RestartTitle")))
                    Application.Restart();
            }
        }

        // The Audio Books tab is GONE. Its only control — "use embedded
        // metadata" — moved to General, where it belongs, and a tab with nothing
        // on it is worse than no tab: a reader tabs onto it, finds an empty page
        // and has to work out whether that is a fault. If an audio-only setting
        // ever appears, the tab comes back with something to say.

        // Text Books is three groups: how the text is SPOKEN, how it goes to a
        // BRAILLE display, and how it is SHOWN. Speech is live; the other two are
        // the settings surface for the output branches still to be built, so their
        // controls are present but inert. Settings holds the DEFAULTS — each book
        // can override them in its own Properties.
        private TabPage BuildTextBooksTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.TextBooks"));
            page.AutoScroll = true;

            // Everything below the speech group moved up by the 34 units the
            // engine row used to take.
            page.Controls.Add(BuildSpeechGroup());
            page.Controls.Add(MakeHint("Settings.TextBooks.Speech.Hint", 14, 258, 480, 32, 1));
            page.Controls.Add(BuildBrailleGroup(8, 296));
            page.Controls.Add(MakeHint("Settings.TextBooks.Braille.Hint", 14, 388, 480, 32, 3));
            page.Controls.Add(BuildVisualGroup(8, 426));
            page.Controls.Add(MakeHint("Settings.TextBooks.Visual.Hint", 14, 656, 480, 32, 5));
            return page;
        }

        // ── Speech: language → voice, then how that voice sounds ──────────────
        // The engine step is gone (Gordan, 2026-07-29). It grouped voices by what
        // they report as their vendor, which is not a question a reader has an
        // opinion about — and since CompositeSpeechBackend already merges the
        // backends and lets the 64-bit copy win a duplicate name, there was
        // nothing left for the step to disambiguate. Two steps instead of three
        // is also two Tab stops instead of three, every time.
        //
        // What the page sets is now one thing: WHICH VOICE READS WHICH LANGUAGE.
        // The first entry in the language list is "all other languages" — that is
        // the global default, the last stop in the chain before nothing.
        private GroupBox BuildSpeechGroup()
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.SpeechGroup");
            box.Location = new Point(8, 6);
            box.Size = new Size(500, 246);

            int lx = 14, cx = 214, cw = 272, y = 26, tab = 0;

            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.BookLanguage"), lx, y + 3));
            cmbLanguage = MakeCombo(Localization.T("Settings.TextBooks.BookLanguage"), cx, y, cw, tab++);
            cmbLanguage.SelectedIndexChanged += (s, e) => LanguageChanged();
            box.Controls.Add(cmbLanguage);

            y += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Voice"), lx, y + 3));
            cmbVoice = MakeCombo(Localization.T("Settings.TextBooks.Voice"), cx, y, cw, tab++);
            cmbVoice.SelectedIndexChanged += (s, e) => VoiceChanged();
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
            // Settings.TextBooks.Dictionary.Hint no longer looks for a home here:
            // it is at the top of the window this button opens, in plain sight
            // (Gordan, 2026-08-03). A second ? never would have fitted anyway —
            // the row already uses 482 of the 486 units the box has.
            box.Controls.Add(btnDict);

            try { voiceCatalog = EnsureSpeech().GetVoiceCatalog(); }
            catch { voiceCatalog = new List<(string, string, string)>(); }

            PopulateLanguages();
            cmbLanguage.SelectedIndex = 0;      // "all other languages" — cascades
            return box;
        }

        /// <summary>Two sources, because neither is enough on its own: the
        /// languages something installed <b>speaks</b>, and the languages this
        /// library has a <b>book</b> in. A French book with no French voice is
        /// precisely the case a rule is wanted for, and it could not be set if
        /// French were not on the list — which is what "go to Settings and sort it
        /// out there" requires. Rows with no voice say so, because otherwise
        /// "French" and "Croatian" look identical and behave nothing alike.
        /// <para>Index 0 is the global default and carries the empty code: it is
        /// what a book whose language could not be worked out is read with.</para></summary>
        private void PopulateLanguages()
        {
            cmbLanguage.Items.Clear();
            languageCodes.Clear();

            languageCodes.Add("");
            cmbLanguage.Items.Add(Localization.T("Settings.TextBooks.AllOtherLanguages"));

            var codes = new List<string>();
            foreach (var c in voiceCatalog)
            {
                string p = LanguageDetector.Primary(c.Language);
                if (p.Length > 0 && !codes.Contains(p)) codes.Add(p);
            }
            foreach (string p in appSettings.SeenLanguages)
                if (p.Length > 0 && !codes.Contains(p)) codes.Add(p);
            foreach (string p in appSettings.LanguagesWithVoice)
                if (p.Length > 0 && !codes.Contains(p)) codes.Add(p);

            codes.Sort((a, b) => string.Compare(LanguageName(a), LanguageName(b),
                                                StringComparison.CurrentCultureIgnoreCase));
            foreach (string p in codes)
            {
                languageCodes.Add(p);
                string row = LanguageName(p) + " (" + p + ")";
                if (VoiceChooser.VoicesFor(voiceCatalog, p).Count == 0)
                    row = Localization.T("Settings.TextBooks.LanguageNoVoice", row);
                cmbLanguage.Items.Add(row);
            }
        }

        /// <summary>A language's name, or its bare code when Windows has none for
        /// it — it answers "Unknown Language (xx)" in that case, which read as
        /// "Unknown Language (cnr) (cnr)" once the code was appended.</summary>
        internal static string LanguageName(string code)
        {
            string name = LanguageDetector.DisplayName(code);
            if (name.Length == 0
                || name.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0)
                return code;
            return name;
        }

        /// <summary>The code behind the selected row; empty means the global
        /// default rather than a real language.</summary>
        private string SelectedLanguageCode()
        {
            int i = cmbLanguage != null ? cmbLanguage.SelectedIndex : -1;
            return i >= 0 && i < languageCodes.Count ? languageCodes[i] : "";
        }

        /// <summary>The voice NAME behind the selected row. The row's text is not
        /// it: a stand-in row carries the language it is borrowed from, and the
        /// "not set" row carries nothing.</summary>
        private string SelectedVoiceName()
        {
            int i = cmbVoice != null ? cmbVoice.SelectedIndex : -1;
            return i >= 0 && i < voiceNames.Count ? voiceNames[i] : "";
        }

        /// <summary>Language picked → the voices that could read it, and whichever
        /// one is currently set for it.</summary>
        private void LanguageChanged()
        {
            if (cmbVoice == null || voiceCatalog == null) return;
            string code = SelectedLanguageCode();

            bool wasPopulating = populating;
            populating = true;
            cmbVoice.Items.Clear();
            voiceNames.Clear();

            if (code.Length > 0)
            {
                // A language may be left without a voice on purpose: it then falls
                // through to a related language, or to the global default.
                voiceNames.Add("");
                cmbVoice.Items.Add(Localization.T("Settings.TextBooks.VoiceNotSet"));
            }

            // The voices that speak this language — and when NOTHING does, every
            // voice on the machine instead. That is not the substitution NBR
            // refuses to make: nothing is suggested, nothing is ranked by how
            // close it sounds, and the reader came here on purpose. It is the one
            // place a deliberate cross-language rule can be written, and without
            // it "go to Settings and sort it out there" has nowhere to go. If they
            // set a Mandarin voice for Russian, that is theirs to do.
            List<string> speak = VoiceChooser.VoicesFor(voiceCatalog, code);
            if (code.Length > 0 && speak.Count == 0)
                speak = VoiceChooser.VoicesFor(voiceCatalog, "");
            foreach (string name in speak)
            {
                voiceNames.Add(name);
                cmbVoice.Items.Add(name);
            }

            string want;
            if (!stagedLanguageVoices.TryGetValue(code, out want))
                want = code.Length > 0 ? appSettings.LanguageVoice(code) : (appSettings.TtsVoice ?? "");
            int wi = voiceNames.IndexOf(want ?? "");
            cmbVoice.SelectedIndex = wi >= 0 ? wi : 0;

            populating = wasPopulating;
            LoadPrefsForSelectedVoice();
        }

        /// <summary>Voice picked → it becomes this language's voice, and the three
        /// numbers below switch to how that voice is set up.</summary>
        private void VoiceChanged()
        {
            if (!populating) stagedLanguageVoices[SelectedLanguageCode()] = SelectedVoiceName();
            LoadPrefsForSelectedVoice();
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
            string voice = SelectedVoiceName();
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
            cmbVisualMode.SelectedIndex = appSettings.VisualMode;
            chkVisual.Checked = appSettings.Visual;
            box.Controls.Add(cmbVisualMode);

            yy += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Highlight"), lx, yy + 3));
            cmbHighlight = MakeCombo(Localization.T("Settings.TextBooks.Highlight"), cx, yy, cw, tab++);
            cmbHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.None"));
            cmbHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Line"));
            cmbHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Sentence"));
            cmbHighlight.SelectedIndex = appSettings.Highlight;
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
            // From the settings file, whose own defaults are the pair that used to
            // be written here: yellow on black, marked blue.
            cmbHighlightColour.SelectedIndex = appSettings.HighlightColour;
            cmbTextColour.SelectedIndex = appSettings.TextColour;
            cmbBackColour.SelectedIndex = appSettings.BackColour;

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

        /// <summary>"hr-HR" → the language's own name, so the list reads naturally.</summary>
        internal static string LanguageLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return Localization.T("Settings.TextBooks.LanguageUnknown");
            try { return new System.Globalization.CultureInfo(code).DisplayName + " (" + code + ")"; }
            catch { return code; }
        }

        /// <summary>Opens the user's dictionary for the language picked here, and
        /// hands it <b>every</b> voice that speaks that language — not only the one
        /// selected. A voice mangles a name whether or not it is the voice in use,
        /// and having to adopt a voice before correcting it would be a strange
        /// price to pay. "Try it" then speaks in the voice whose rules are open.</summary>
        private void OpenDictionary()
        {
            int li = cmbLanguage != null ? cmbLanguage.SelectedIndex : -1;
            string lang = (li >= 0 && li < languageCodes.Count) ? languageCodes[li] : "";

            List<string> voices = VoiceChooser.VoicesFor(voiceCatalog, lang);
            if (lang.Length > 0 && voices.Count == 0)
                voices = VoiceChooser.VoicesFor(voiceCatalog, "");

            using (var dlg = new SpeechDictionaryForm(lang, voices, SpeakSample))
                dlg.ShowDialog(this);
            // Whatever was edited takes effect from the next sentence read.
            SpeechDictionaries.Reload();
        }

        /// <summary>Says a line with the voice selected here — used by the
        /// dictionary's "Try it".</summary>
        private void SpeakSample(string voice, string text)
        {
            try
            {
                CompositeSpeechBackend sp = EnsureSpeech();
                // The caller's voice when it named one — the dictionary tries a
                // rule out in the voice that rule was written for. Nothing named,
                // and it falls back to whatever is selected here.
                string picked = string.IsNullOrEmpty(voice) ? SelectedVoiceName() : voice;
                if (picked.Length > 0) sp.SelectVoice(picked);
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
                string picked = SelectedVoiceName();
                if (picked.Length > 0) sp.SelectVoice(picked);
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

        /// <summary>Ctrl+Tab, Ctrl+Shift+Tab and Ctrl+1…9 move between the pages,
        /// cyclically — see <see cref="TabKeys"/> for why a TabControl does not
        /// do this by itself once focus is inside a page.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (TabKeys.Handle(tabSettings, keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
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
            // No hint on the card list (Gordan, 2026-08-02). A list of sound
            // cards under a label that says which sound card explains itself, and
            // a hint that only restates the caption costs a reader the same time
            // to hear as one that tells them something.

            // The keep-alive gets a switch (Gordan, 2026-08-03). It DOES need its
            // explanation: nothing about "keep the device awake" tells a reader
            // what it is for, and what it is for is a fault almost nobody would
            // diagnose — the first word of sentence after sentence going missing
            // because the card powered down in the gap.
            chkKeepAlive = new CheckBox();
            chkKeepAlive.Text = Localization.T("Settings.Device.KeepAlive");
            chkKeepAlive.AccessibleName = Localization.T("Settings.Device.KeepAlive");
            chkKeepAlive.SetBounds(10, 58, 470, 24);
            chkKeepAlive.TabIndex = 1;
            chkKeepAlive.Checked = appSettings.KeepDeviceAlive;
            page.Controls.Add(chkKeepAlive);
            page.Controls.Add(MakeHint("Settings.Device.KeepAlive.Hint", 28, 84, 452, 60, 2));
            return page;
        }

        // Misc is GONE (Gordan, 2026-08-03). It never held anything but the look
        // switch and a "work in progress" placeholder — a tab that existed to
        // have somewhere to put one control. The look moved to General, where it
        // is a setting among settings instead of a leftover.

        /// <summary>The chosen look, as an id for AppSettings.</summary>
        private string SelectedThemeId()
        {
            if (cmbLook == null) return UiTheme.FollowId;
            switch (cmbLook.SelectedIndex)
            {
                case 1: return UiTheme.ClassicId;
                case 2: return UiTheme.NewId;
                default: return UiTheme.FollowId;
            }
        }
    }
}
