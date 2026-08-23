using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
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
        private ComboBox cmbUiLanguage;
        private readonly List<string> uiLanguageCodes = new List<string>();
        private ComboBox cmbLanguage;
        private readonly List<string> languageCodes = new List<string>();
        private NumericUpDown numRate;
        private NumericUpDown numVolume;
        private NumericUpDown numPitch;

        // Visual output: the rule a book inherits when it has not been given a
        // look of its own. (Braille has no rule of its own — see BuildVisualGroup's
        // neighbour comment on the page.)
        private CheckBox chkVisual;
        private ComboBox cmbVisualMode;
        private ComboBox cmbHighlight;
        private ComboBox cmbHighlightColour;
        private ComboBox cmbTextColour;
        private ComboBox cmbBackColour;

        private CheckBox chkAutoUpdate;

        // Misc tab — the temporary classic/new look switch.
        private ComboBox cmbLook;

        // Device tab — output sound-card picker. The combo shows human-readable
        // descriptions; deviceIds[i] is the mpv identifier for row i. A live-apply
        // callback (from the player) switches the output on selection so the user
        // hears the change immediately.
        private ComboBox cmbSoundCard;
        private CheckBox chkKeepAlive;
        private CheckBox chkOptical;
        private ComboBox cmbOptical;
        /// <summary>Drive letters parallel to cmbOptical's rows; "" is the
        /// automatic first row.</summary>
        private readonly List<string> opticalDrives = new List<string>();

        /// <summary>"F: (Audio CD)" when something is in the drive, "F:" when it
        /// is empty.
        ///
        /// <para>The model name would have been nicer to read and was written
        /// that way first — through WMI, which meant a reference to
        /// System.Management for one label, on the same morning seven unused
        /// framework references were taken out for exactly that reason, plus a
        /// WMI query while a dialog is being built. The disc's own label costs
        /// nothing and, for the job in hand, says more: a reader choosing between
        /// two drives wants to know WHICH ONE HAS THE DISC, not what it is
        /// called.</para></summary>
        private static string DriveLabel(string letter)
        {
            try
            {
                var d = new System.IO.DriveInfo(letter + "\\");
                if (d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel))
                    return letter + "  (" + d.VolumeLabel.Trim() + ")";
            }
            catch { }
            return letter;
        }
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

            // "SHOW HELP HINTS" IS GONE, and it had been dead for some time
            // (found 2026-08-22 when Gordan noticed its string in the language
            // file). It governed `hints`, a list nothing ever added to, so it
            // switched nothing on or off; and SettingsSkin took it off the form
            // in the NBR look while leaving it standing in classic, which is
            // exactly the drift §8k forbids. The hints it was written for were
            // always-visible boxes under every control; they became a `?` per
            // group, which costs a corner and needs no switch.

            tabSettings = new TabControl();
            tabSettings.Location = new Point(10, 40);
            tabSettings.Size = new Size(540, 470);
            tabSettings.TabIndex = 1;

            tabSettings.TabPages.Add(BuildGeneralTab());
            tabSettings.TabPages.Add(BuildTextBooksTab());
            // Its own tab (Gordan, 2026-08-11), and not a group on Speech and
            // Braille. Two reasons, both his: reading pictures has little to do
            // with how a book is SPOKEN or FELT, and that page had just passed a
            // visual inspection with everything sitting where it should — one more
            // group at the bottom is how a laid-out page stops being one.
            // ADVANCED LAST (Gordan, 2026-08-17). It had drifted in front of
            // Devices, which reads wrongly: everything before it is something a
            // reader sets and forgets, and Advanced is the one page that sends
            // them out to other people's services. The last page is where a
            // reader expects the deep end.
            tabSettings.TabPages.Add(BuildDeviceTab());
            tabSettings.TabPages.Add(BuildOcrTab());

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

            this.Controls.Add(tabSettings);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnApply);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            // Built exactly as before, then handed over — ONE layout pass for
            // both looks (Gordan, 2026-08-16). The classic look is this same
            // window in classic form: same controls, same places, same
            // dimensions, only unpainted. See DialogSkin.Painting.
            SettingsSkin.Apply(this);
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
                    OK = btnOK,
                    Cancel = btnCancel,
                    Apply = btnApply,
                };
            }
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
            cmbUiLanguage = MakeCombo(Localization.T("Settings.General.Language"), CX, 22, CW, tab++);
            // EVERY .lang FILE THAT IS REALLY THERE, never a list written by hand.
            // Localization already scans the folder and reads each file's own
            // LanguageName, so a translation dropped in beside the others appears
            // here without anybody editing this method -- the rule the braille
            // table catalogue follows for the same reason (a hand-written list
            // drops the one entry somebody needs, and it surfaces on their
            // machine rather than ours).
            uiLanguageCodes.Clear();
            foreach (var l in Localization.AvailableLanguages)
            {
                uiLanguageCodes.Add(l.Code);
                cmbUiLanguage.Items.Add(l.Name);
            }
            if (cmbUiLanguage.Items.Count == 0)
            {
                uiLanguageCodes.Add("en");
                cmbUiLanguage.Items.Add(Localization.T("LanguageName"));
            }
            int cur = uiLanguageCodes.FindIndex(c =>
                string.Equals(c, Localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));
            cmbUiLanguage.SelectedIndex = cur >= 0 ? cur : 0;
            gLang.Controls.Add(cmbUiLanguage);
            page.Controls.Add(gLang);
            y += gLang.Height + 8;

            // ── 2. Library location ──────────────────────────────────────
            GroupBox gLib = MakeGroup(Localization.T("Settings.General.LibraryGroup"), y, GW, 62);
            gLib.Name = "Settings.General.LibraryLocation.Hint";
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
            gKeys.Name = "Settings.General.UseMultimediaKeys.Hint";
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
            gMeta.Name = "Settings.General.UseMetadata.Hint";
            chkUseMetadata = new CheckBox();
            chkUseMetadata.Text = Localization.T("Settings.General.UseMetadata");
            chkUseMetadata.AccessibleName = Localization.T("Settings.General.UseMetadata");
            chkUseMetadata.SetBounds(LX, 22, GW - 30, 24);
            chkUseMetadata.TabIndex = tab++;
            chkUseMetadata.Checked = appSettings.UseMetadata;
            gMeta.Controls.Add(chkUseMetadata);
            page.Controls.Add(gMeta);
            y += gMeta.Height + 8;

            // ── 5. Updates ───────────────────────────────────────────────
            // The switch only; asking on the spot is Help → Check for update in
            // the Library, because that is a thing you DO and this is a rule you
            // set — the same division that put the service guides under Help and
            // left their switches here (2026-08-17).
            GroupBox gUpd = MakeGroup(Localization.T("Settings.General.UpdateGroup"), y, GW, 56);
            gUpd.Name = "Settings.General.AutoCheckUpdates.Hint";
            chkAutoUpdate = new CheckBox();
            chkAutoUpdate.Text = Localization.T("Settings.General.AutoCheckUpdates");
            chkAutoUpdate.AccessibleName = Localization.T("Settings.General.AutoCheckUpdates");
            chkAutoUpdate.SetBounds(LX, 22, GW - 30, 24);
            chkAutoUpdate.TabIndex = tab++;
            chkAutoUpdate.Checked = appSettings.AutoCheckUpdates;
            gUpd.Controls.Add(chkAutoUpdate);
            page.Controls.Add(gUpd);
            y += gUpd.Height + 8;

            // ── 6. Look ──────────────────────────────────────────────────
            GroupBox gLook = MakeGroup(Localization.T("Settings.General.LookGroup"), y, GW, 62);
            gLook.Name = "Settings.Misc.Look.Hint";
            gLook.Controls.Add(MakeLabel(Localization.T("Settings.Misc.Look"), LX, 26));
            cmbLook = MakeCombo(Localization.T("Settings.Misc.Look"), CX, 22, CW, tab++);
            // TWO CHOICES, NOT THREE (Gordan, 2026-08-20). "Follow Windows" is
            // gone because it was not a third look — it was a RULE that resolved
            // to one of these two, and for anybody without high contrast it
            // resolved to NBR design. So the list offered a third option that
            // behaved exactly like one of the other two, which is confusion for
            // nothing. The rule itself survives untouched, as the DEFAULT: see
            // UiTheme.Select. What is removed is asking the reader to pick it.
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.Classic"));
            cmbLook.Items.Add(Localization.T("Settings.Misc.Look.New"));
            // AGAINST THE THEME IN FORCE, never against the stored id. Until the
            // reader chooses, nothing is stored — and comparing a chosen "new"
            // against an empty setting would have reported a change that is not
            // one, and offered to restart NBR for it.
            cmbLook.SelectedIndex =
                string.Equals(UiTheme.Current.Id, UiTheme.ClassicId, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
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

        /// <summary>True when the reader answered "yes, restart now" to a change
        /// of look. The restart itself belongs to <see cref="Program"/>.</summary>
        public bool RestartRequested { get; private set; }

        /// <summary>Asks for a restart instead of performing one.
        ///
        /// <para><b>Why not <c>Application.Restart()</c> here</b> (Gordan,
        /// 2026-08-19: changing the look left BOTH windows on screen). That call
        /// starts the replacement FIRST and only then tries to end this process —
        /// and it was being made from inside this dialog, which is MODAL. A modal
        /// dialog runs its own nested message loop, and that loop does not unwind
        /// on <c>Application.Exit</c>, so the old player stayed up while its
        /// replacement opened in front of it.</para>
        ///
        /// <para>So the answer travels out as a flag: this window closes, the
        /// player closes, the message loop ends, mpv and the speech host are
        /// released — and only then does <see cref="Program"/> start the new one.
        /// Order that also fixes a file-and-sound-card race nobody had hit
        /// yet.</para></summary>
        private void RequestRestart()
        {
            RestartRequested = true;
            // Apply leaves this window open, and the answer was "restart now"; OK
            // would have closed it in any case.
            DialogResult = DialogResult.OK;
            Close();
        }
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

            // General -- the once-a-day update check.
            if (chkAutoUpdate != null)
                appSettings.SetAutoCheckUpdates(chkAutoUpdate.Checked);

            // General — hints and the media keys (the player re-applies the global
            // claim when the dialog closes).
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

            if (chkCloudVoices != null && appSettings != null)
                appSettings.SetUseCloudVoices(chkCloudVoices.Checked);

            // Per language and nothing else. The global default went with its row
            // (see PopulateLanguages): an empty key can no longer arrive here,
            // because there is no row that carries one. An empty VALUE still
            // does, and it means revoke — SetLanguageVoice removes the language
            // rather than storing a blank.
            foreach (var kv in stagedLanguageVoices)
                if (kv.Key.Length > 0) appSettings.SetLanguageVoice(kv.Key, kv.Value);

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
            // Which recognizer reads image documents; index 0 is Automatic, which
            // is stored as empty so a machine that later loses that language falls
            // back instead of failing.
            if (cmbOcrLanguage != null && cmbOcrLanguage.Enabled)
            {
                int i = cmbOcrLanguage.SelectedIndex;
                if (i >= 0 && i < ocrTags.Count) appSettings.SetOcrLanguage(ocrTags[i]);
            }

            if (tbTranslateNotes != null) appSettings.SetTranslationNotes(tbTranslateNotes.Text);
            // Only when the group was reachable: a disabled box reads as
            // unchecked, and saving that would quietly clear a setting made on a
            // machine that did have a drive.
            if (chkOptical != null && chkOptical.Enabled)
            {
                appSettings.SetUseOpticalDrive(chkOptical.Checked);
                int i = cmbOptical != null ? cmbOptical.SelectedIndex : -1;
                if (i >= 0 && i < opticalDrives.Count)
                    appSettings.SetOpticalDriveLetter(opticalDrives[i]);
            }

            // Misc — the look. A window builds itself once, so the change lands
            // when NBR starts again; offer to do that now rather than leaving the
            // user wondering why nothing happened.
            //
            // AGAINST THE THEME IN FORCE, not against the stored id. Until the
            // reader picks a look nothing is stored, and the combo is showing
            // whichever one the default rule resolved to — so comparing against
            // the empty setting would have called that a change and offered to
            // restart NBR for a look it is already wearing. The setting is still
            // WRITTEN in that case, which is right: it turns a default that could
            // move under them into a choice that cannot.
            // THE LANGUAGE NEEDS A RESTART FOR THE SAME REASON THE LOOK DOES: a
            // window builds itself once, and every caption, accessible name and
            // hint in it was read at that moment. Offering to reload them in
            // place would mean rebuilding six dialogs and the player from
            // scratch, and a screen reader would be standing in one of them.
            if (cmbUiLanguage != null && cmbUiLanguage.SelectedIndex >= 0
                && cmbUiLanguage.SelectedIndex < uiLanguageCodes.Count)
            {
                string picked = uiLanguageCodes[cmbUiLanguage.SelectedIndex];
                if (!string.Equals(picked, Localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                {
                    appSettings.SetLanguage(picked);
                    if (MessageForm.ShowConfirm(this, Localization.T("Settings.General.Language.Restart"),
                                               Localization.T("Settings.General.Language.RestartTitle")))
                        RequestRestart();
                }
            }

            if (cmbLook != null && !string.Equals(SelectedThemeId(), UiTheme.Current.Id,
                                                  StringComparison.OrdinalIgnoreCase))
            {
                appSettings.SetUiTheme(SelectedThemeId());
                if (MessageForm.ShowConfirm(this, Localization.T("Settings.Misc.Look.Restart"),
                                           Localization.T("Settings.Misc.Look.RestartTitle")))
                    RequestRestart();
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
            // The braille group is GONE (2026-08-04), not merely shortened. It
            // held one check box, "Use braille output", and braille does not work
            // that way: the display is fed by the screen reader following focus
            // into the reading surface, so the reading window IS the braille
            // output and this could only ever agree with it or lie. What remains
            // of braille in NBR is the table a .brf was READ with, which belongs
            // to one book and lives in Properties.
            page.Controls.Add(BuildVisualGroup(8, 296));
            return page;
        }

        /// <summary>Reading pictures, and later translating them.
        ///
        /// <para>Named for what it will hold rather than only for what it holds
        /// today — the tab exists because Gordan wanted OCR out of Speech and
        /// Braille, and translation is the other member of the same family.</para></summary>
        // "Advanced", not "OCR and Translate" (Gordan, 2026-08-15). The name had
        // to change once cloud voices joined it, and his reasoning picked the
        // word: this is where keys and tokens are fetched, which is not for
        // everyone, and "Advanced" reads as do-not-touch-unless-you-know where
        // "Extras" reads as bonuses. Misc is not it either — that was for
        // trifles, and it is gone.
        private TabPage BuildOcrTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.Advanced"));
            page.AutoScroll = true;
            page.Controls.Add(BuildOcrGroup(8, 6));
            page.Controls.Add(BuildTranslationGroup(8, 176));
            // ONE cloud group where there were three (Gordan, 2026-08-17). The
            // credential dialogs have gone to Help → Services and accounts, so what
            // is left of Google and Azure is a line each saying whether they are
            // set up — and two boxes holding one line each is not a page, it is a
            // list pretending to be one. The switch governs both, so it leads.
            page.Controls.Add(BuildCloudGroup(8, 360));
            return page;
        }

        /// <summary>The same catalogue without the cloud voices — the rule itself
        /// lives in <see cref="GoogleCloudVoices.Exclude"/>, because three places
        /// need it and none of them is a dialog.</summary>
        internal static List<(string Name, string Engine, string Language)> WithoutCloudVoices(
            List<(string Name, string Engine, string Language)> all)
        {
            int dropped;
            return CloudVoices.Exclude(all, out dropped);
        }

        private CheckBox chkCloudVoices;
        private TextBox tbCloudState, tbAzureState, tbCloudWhy, tbCloudWhere;

        /// <summary>Cloud voices: the switch, and one line per service saying
        /// whether it is set up.
        ///
        /// <para><b>What is no longer here, and why</b> (Gordan, 2026-08-17). This
        /// page had grown five groups, three of them credential dialogs, on a page a
        /// reader visits once and then never again. Setting a service up is not a
        /// setting — it is a job with steps, done once on somebody's web site — so
        /// the job went to Help → Services and accounts and this keeps only the
        /// switch. The drift being removed was TWO ways to set one service up;
        /// leaving the buttons here would have kept it.</para>
        ///
        /// <para><b>The switch leads the group, and that is his instruction.</b> It
        /// turns on <see cref="CloudVoices.Any"/> — either credential lights it — so
        /// it belongs to neither service and cannot sit inside one. It used to live
        /// in Google's box, and he caught what that meant: with Azure added he went
        /// looking for the same switch in the Azure group and there was none.</para>
        ///
        /// <para><b>Three groups became one because there was nothing left to
        /// separate.</b> What survives of Google and Azure is a sentence each; two
        /// boxes holding one sentence apiece is a list pretending to be a page.</para>
        ///
        /// <para><b>Every line here is read-only but TABBABLE, never a Label.</b> A
        /// reader driven by Tab never visits a label, and these lines are now the
        /// only way to learn from this page whether either service is set up, and
        /// where to go if not.</para></summary>
        private GroupBox BuildCloudGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.Cloud.VoicesGroup");
            box.Name = "Hint.Settings.CloudUse";
            box.Location = new Point(x, y);
            box.Size = new Size(500, 210);
            box.Tag = "span2";

            chkCloudVoices = new CheckBox();
            chkCloudVoices.Text = Localization.T("Settings.Cloud.Use");
            chkCloudVoices.AccessibleName = chkCloudVoices.Text;
            chkCloudVoices.SetBounds(14, 24, 470, 22);
            chkCloudVoices.TabIndex = 0;
            chkCloudVoices.Checked = appSettings != null && appSettings.UseCloudVoices;

            tbCloudWhy = MakeLine(1);
            tbCloudState = MakeLine(2);
            tbAzureState = MakeLine(3);

            // THE LINE THE WHOLE SPLIT RESTS ON. Without it this page says two
            // services are not set up and gives no way to set them up — which is
            // worse than the crowded page it replaced.
            tbCloudWhere = MakeLine(4);
            tbCloudWhere.Text = Localization.T("Settings.Cloud.Where");
            tbCloudWhere.AccessibleName = tbCloudWhere.Text;

            box.Controls.Add(chkCloudVoices);
            box.Controls.Add(tbCloudWhy);
            box.Controls.Add(tbCloudState);
            box.Controls.Add(tbAzureState);
            box.Controls.Add(tbCloudWhere);

            cloudBox = box;
            box.Resize += (s, e) => LayoutCloudGroup();

            ShowAzureState();
            ShowCloudState();          // which lays the group out once it has text
            return box;
        }

        private GroupBox cloudBox;

        /// <summary>One read-only, tabbable line. Four of them differ only in where
        /// they sit, which <see cref="LayoutCloudGroup"/> decides.</summary>
        private TextBox MakeLine(int tabIndex)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = SystemColors.Control;
            t.TabIndex = tabIndex;
            t.SetBounds(14, 52, 470, 32);
            return t;
        }

        /// <summary>Stacks whichever lines have something to say.
        ///
        /// <para><b>An empty line takes no room.</b> "There is nothing to switch on
        /// yet" is only true before either service is set up, and the old page left
        /// its box standing empty the rest of the time — a blank band in the middle
        /// of a group, which reads as a fault to the eye even though a screen reader
        /// steps straight over it. Laid out from the group's REAL width for the
        /// reason the two-column reflow taught: a group built at 500 lands in about
        /// 293, and anything pinned to the built width hangs off its own edge.</para></summary>
        private void LayoutCloudGroup()
        {
            if (cloudBox == null) return;
            int right = cloudBox.ClientSize.Width - 14;
            if (right < 80) return;
            int w = right - 14;

            chkCloudVoices.SetBounds(14, 24, w, 22);
            int yy = 52;
            foreach (TextBox t in new[] { tbCloudWhy, tbCloudState, tbAzureState, tbCloudWhere })
            {
                if (t == null) continue;
                bool has = !string.IsNullOrEmpty(t.Text);
                t.Visible = has;
                // AN EMPTY LINE IS MOVED, NOT MERELY HIDDEN. Left at its built
                // bounds it still sits on top of whatever took its place, which is
                // invisible to the eye and to a screen reader but is exactly what
                // check-layout reports as a collision — and it is right to: a
                // hidden control with real bounds is a fault waiting for the day
                // something makes it visible again.
                if (!has) { t.SetBounds(14, yy, w, 0); continue; }
                int h = Math.Max(20, TextRenderer.MeasureText(t.Text, t.Font,
                                     new Size(w, 0), TextFormatFlags.WordBreak).Height + 4);
                t.SetBounds(14, yy, w, h);
                yy += h + 6;
            }
            // SIZED TO WHAT IS IN IT, both ways. Growing only would leave the slack
            // the built height guessed wrong by — measured 38 units of empty band
            // below the last line, which is exactly the sort of thing that reads as
            // an unfinished group to the eye even though nothing is wrong. Safe
            // inside a Resize handler because the height it asks for is computed
            // from the WIDTH and never from the height, so it cannot chase itself;
            // the tolerance stops a one-unit rounding difference from looping.
            int want = yy + 12;
            if (Math.Abs(cloudBox.Height - want) > 1) cloudBox.Height = want;
        }

        private void ShowAzureState()
        {
            if (tbAzureState == null) return;
            string text;
            if (!AzureVoices.Have) text = Localization.T("Settings.Azure.State.None");
            else
            {
                var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int n = 0;
                foreach (var v in AzureVoices.Voices()) { n++; langs.Add(v.Language); }
                text = Localization.T("Settings.Azure.State.Have", n, langs.Count);
            }
            tbAzureState.Text = text;
            tbAzureState.AccessibleName = text;
        }

        /// <summary>Puts the group's moving parts in agreement with what is actually
        /// stored — called after every change, so nothing on it can go on claiming
        /// something that has stopped being true. It ends by laying the group out,
        /// because a line that has just gained or lost its text changes what the
        /// group is the right height for.</summary>
        private void ShowCloudState()
        {
            bool have = GoogleCloudVoices.Have;

            if (tbCloudState != null)
            {
                string text;
                if (!have) text = Localization.T("Settings.Cloud.State.None");
                else
                {
                    var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in GoogleCloudVoices.Voices())
                    {
                        speakers.Add(GoogleCloudVoices.Speaker(v.Name));
                        langs.Add(v.Language);
                    }
                    text = speakers.Count == 0
                        ? Localization.T("Settings.Cloud.State.HaveNoList")
                        : Localization.T("Settings.Cloud.State.Have", speakers.Count, langs.Count);
                }
                tbCloudState.Text = text;
                tbCloudState.AccessibleName = text;
            }

            // THE SWITCH IS ABOUT THE KIND, NOT THE VENDOR: it decides whether
            // Properties offers cloud voices at all, so EITHER credential lights
            // it. Left on Google's alone, an Azure-only reader would have had a
            // full catalogue and a dead switch, with the line below telling them
            // to load a Google service account they do not want.
            bool anyCloud = CloudVoices.Any;
            if (chkCloudVoices != null)
            {
                if (!anyCloud) chkCloudVoices.Checked = false;
                chkCloudVoices.Enabled = anyCloud;
            }
            if (tbCloudWhy != null)
            {
                // Only says something when there is something to say. A permanent
                // line explaining a control that is working is noise on every Tab
                // through the page.
                string why = anyCloud ? "" : Localization.T("Settings.Cloud.Why");
                tbCloudWhy.Text = why;
                tbCloudWhy.AccessibleName = why;
                tbCloudWhy.TabStop = why.Length > 0;
            }

            LayoutCloudGroup();
        }

        private ComboBox cmbTranslateEngine;

        /// <summary>Which services may translate a book, and whether each has a key.
        ///
        /// <para><b>A combo and ONE button, not a button per service.</b> Gordan
        /// offered either; this way the group does not grow a control every time a
        /// service is added — and Azure is already waiting in the wings for the day
        /// a book turns up that both language models refuse.</para>
        ///
        /// <para><b>The row says in TEXT whether a key is stored</b> — "Gemini, key
        /// stored" against "Gemini, no key". Without that a reader who cannot see
        /// the dialog has to open the key window to find out whether they already
        /// did this, which is the same reason a book's status on the shelf is spoken
        /// and not only coloured.</para>
        ///
        /// <para>Nothing here is a setting in <c>Settings.ini</c>: what is
        /// configured IS which keys exist, so there is no second place for the two
        /// to disagree. Keys live in their own file — see
        /// <see cref="TranslationKeys"/> for why not this one.</para></summary>
        private GroupBox BuildTranslationGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.Translate.Group");
            box.Name = "Settings.Translate.Hint";
            box.Location = new Point(x, y);
            box.Size = new Size(500, 92);
            box.Tag = "span2";

            Label lbl = new Label();
            lbl.Text = Localization.T("Settings.Translate.Service");
            lbl.Location = new Point(14, 29);
            lbl.Size = new Size(200, 20);

            cmbTranslateEngine = new ComboBox();
            cmbTranslateEngine.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbTranslateEngine.Location = new Point(224, 26);
            cmbTranslateEngine.Size = new Size(200, 24);
            cmbTranslateEngine.AccessibleName = Localization.T("Settings.Translate.Service");
            cmbTranslateEngine.TabIndex = 0;
            // NVDA says nothing when a closed DropDownList changes on the arrow
            // keys; this is the app-wide remedy, and a no-op under JAWS so nothing
            // is said twice.
            NvdaController.SpeakOnChange(cmbTranslateEngine);
            RefillTranslationEngines();

            // WHERE THE KEY BUTTON WENT (2026-08-17). Same move as the two cloud
            // voices: pasting a key is the tail end of a job that starts on a web
            // site, and that job is one place now. The combo still SAYS whether a
            // key is stored — "Gemini, key stored" against "Gemini, no key" — so
            // what this page loses is the doing, not the knowing.
            TextBox where = new TextBox();
            where.Multiline = true;
            where.ReadOnly = true;
            where.BorderStyle = BorderStyle.None;
            where.BackColor = SystemColors.Control;
            where.Text = Localization.T("Settings.Cloud.Where");
            where.AccessibleName = where.Text;
            where.SetBounds(14, 58, 470, 32);
            where.TabIndex = 1;

            // THE READER'S STANDING INSTRUCTION TO THE TRANSLATOR, and it belongs
            // here rather than beside a book because some of what one wants to say
            // is a habit rather than a property of the book. British spelling is the
            // measured case: one sentence flips all six markers on every language
            // model, and it does not change from one book to the next. Same shape as
            // the language-to-voice rule — the global rule lives in Settings, the
            // exception beside the book.
            //
            // FREE TEXT AND NOT A SET OF TICK BOXES, and Gordan's reasoning is the
            // one that settles it: our own standing rules were written from Croatian,
            // and a fixed set of questions would harden that bias — someone
            // translating into Finnish would be offered our questions and not
            // theirs. It also reaches the model as prose, so it can be written in
            // any language the reader thinks in.
            Label lblNotes = new Label();
            lblNotes.Text = Localization.T("Settings.Translate.Notes");
            // AUTOSIZE OFF, and it is not tidiness. A Label auto-sizes to its text, and
            // at 12 pt this caption measured 705 wide inside a 598 group -- over the
            // edge and across its own field. The classic look escaped it only because
            // the theme font is smaller, which is luck rather than a design.
            lblNotes.AutoSize = false;
            lblNotes.Location = new Point(14, 92);
            lblNotes.Size = new Size(470, 20);

            tbTranslateNotes = new TextBox();
            tbTranslateNotes.Multiline = true;
            tbTranslateNotes.ScrollBars = ScrollBars.Vertical;
            // 124, not 114: the caption above is 27 tall at 12 pt whatever the 20
            // it is given, so the field started five units inside it. Classic
            // escaped it with a smaller font, which is luck and not a layout.
            tbTranslateNotes.SetBounds(14, 124, 470, 44);
            tbTranslateNotes.TabIndex = 2;
            tbTranslateNotes.AccessibleName = Localization.T("Settings.Translate.Notes");
            tbTranslateNotes.Text = appSettings != null ? appSettings.TranslationNotes : "";

            box.Size = new Size(500, 176);
            box.Controls.Add(lbl);
            box.Controls.Add(cmbTranslateEngine);
            box.Controls.Add(where);
            box.Controls.Add(lblNotes);
            box.Controls.Add(tbTranslateNotes);
            box.Resize += (s, e) =>
            {
                int right = box.ClientSize.Width - 14;
                if (right > 94) where.SetBounds(14, 58, right - 14, 32);
            };
            return box;
        }

        private TextBox tbTranslateNotes;

        /// <summary>Rebuilds the service list so each row carries its current
        /// state, keeping whichever service was selected.</summary>
        private void RefillTranslationEngines()
        {
            if (cmbTranslateEngine == null) return;
            int keep = Math.Max(0, cmbTranslateEngine.SelectedIndex);
            cmbTranslateEngine.BeginUpdate();
            cmbTranslateEngine.Items.Clear();
            foreach (var e in TranslationEngines.All)
                cmbTranslateEngine.Items.Add(Localization.T(
                    e.HasKey ? "Settings.Translate.State.Have" : "Settings.Translate.State.None",
                    e.DisplayName));
            cmbTranslateEngine.EndUpdate();
            if (cmbTranslateEngine.Items.Count > 0)
                cmbTranslateEngine.SelectedIndex = Math.Min(keep, cmbTranslateEngine.Items.Count - 1);
        }

        private ComboBox cmbOcrLanguage;
        private readonly List<string> ocrTags = new List<string>();

        /// <summary>Which recognizer reads an image document.
        ///
        /// <para><b>"Automatic" is the DEFAULT, not an answer.</b> It means
        /// "whatever Windows would pick", and it lives here rather than in the
        /// import question because at the point of reading there is no such thing:
        /// working the language out automatically would mean reading a page to see
        /// what language it is in, and reading a page is what needs the language.
        /// The import asks outright whenever there is more than one recognizer —
        /// the language really does change the reading (see
        /// <see cref="WindowsOcr"/>).</para>
        ///
        /// <para><b>The group is present even with nothing installed</b>, dimmed,
        /// exactly as the optical-drive group is: a reader who has none is better
        /// told "this exists and your Windows has not got it" than left wondering
        /// whether they missed it — and the button beside it is how they fix that.
        /// NBR cannot install a language itself; that needs elevation, and
        /// elevating to add operating-system components is not something a book
        /// reader should do.</para></summary>
        private GroupBox BuildOcrGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.Ocr.Group");
            box.Name = "Settings.Ocr.Hint";
            box.Location = new Point(x, y);
            box.Size = new Size(500, 92);
            box.Tag = "span2";

            List<(string Tag, string Name)> languages = WindowsOcr.Languages;
            bool any = languages.Count > 0;

            Label lbl = new Label();
            lbl.Text = Localization.T("Settings.Ocr.Language");
            lbl.Location = new Point(14, 29);
            lbl.Size = new Size(200, 20);

            cmbOcrLanguage = new ComboBox();
            cmbOcrLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbOcrLanguage.Location = new Point(224, 26);
            cmbOcrLanguage.Size = new Size(200, 24);
            cmbOcrLanguage.AccessibleName = Localization.T("Settings.Ocr.Language");
            cmbOcrLanguage.TabIndex = 0;
            cmbOcrLanguage.Enabled = any;

            ocrTags.Clear();
            ocrTags.Add("");
            cmbOcrLanguage.Items.Add(Localization.T(any ? "Settings.Ocr.Automatic" : "Settings.Ocr.None"));
            int selected = 0;
            string current = appSettings.OcrLanguage ?? "";
            foreach (var l in languages)
            {
                if (string.Equals(l.Tag, current, StringComparison.OrdinalIgnoreCase))
                    selected = ocrTags.Count;
                ocrTags.Add(l.Tag);
                cmbOcrLanguage.Items.Add(l.Name);
            }
            cmbOcrLanguage.SelectedIndex = Math.Min(selected, cmbOcrLanguage.Items.Count - 1);

            Button add = new Button();
            add.Text = Localization.T("Settings.Ocr.AddLanguage");
            add.AccessibleName = add.Text;
            add.SetBounds(14, 58, 240, 26);
            add.TabIndex = 1;
            // The dialog, not a jump into Windows. Windows' own route installs a
            // whole display language to get one recognition pack, which was
            // Gordan's objection and a fair one — this installs the pack alone.
            // The Windows route is still in there, one button along, for anyone
            // who wants the display language too.
            add.Click += (s, e) =>
            {
                using (var dlg = new OcrLanguageForm())
                {
                    dlg.ShowDialog(this);
                    // A language that has just arrived has to appear in the combo
                    // without a restart, and the choice already made has to
                    // survive the rebuild.
                    if (dlg.Changed) RefillOcrLanguages();
                }
            };

            box.Controls.Add(lbl);
            box.Controls.Add(cmbOcrLanguage);
            box.Controls.Add(add);
            return box;
        }

        /// <summary>Rebuilds the recognizer list after one has been installed,
        /// keeping whatever was chosen if it is still there.</summary>
        private void RefillOcrLanguages()
        {
            if (cmbOcrLanguage == null) return;
            string chosen = cmbOcrLanguage.SelectedIndex >= 0 && cmbOcrLanguage.SelectedIndex < ocrTags.Count
                ? ocrTags[cmbOcrLanguage.SelectedIndex] : "";

            List<(string Tag, string Name)> languages = WindowsOcr.Languages;
            bool any = languages.Count > 0;

            cmbOcrLanguage.BeginUpdate();
            cmbOcrLanguage.Items.Clear();
            ocrTags.Clear();
            ocrTags.Add("");
            cmbOcrLanguage.Items.Add(Localization.T(any ? "Settings.Ocr.Automatic" : "Settings.Ocr.None"));
            int selected = 0;
            foreach (var l in languages)
            {
                if (string.Equals(l.Tag, chosen, StringComparison.OrdinalIgnoreCase))
                    selected = ocrTags.Count;
                ocrTags.Add(l.Tag);
                cmbOcrLanguage.Items.Add(l.Name);
            }
            cmbOcrLanguage.EndUpdate();
            cmbOcrLanguage.Enabled = any;
            cmbOcrLanguage.SelectedIndex = Math.Min(selected, cmbOcrLanguage.Items.Count - 1);
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
            box.Name = "Settings.TextBooks.Speech.Hint";
            box.Location = new Point(8, 6);
            box.Size = new Size(500, 246);
            // Two of the three columns. This is the widest group in Settings —
            // "Books in this language:" alone pushes the value column to 214, and
            // in one column that left the voice list 65 pixels wide.
            box.Tag = "span2";

            int lx = 14, cx = 214, cw = 272, y = 26, tab = 0;

            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.BookLanguage"), lx, y + 3));
            cmbLanguage = MakeCombo(Localization.T("Settings.TextBooks.BookLanguage"), cx, y, cw, tab++);
            cmbLanguage.SelectedIndexChanged += (s, e) => LanguageChanged();
            box.Controls.Add(cmbLanguage);

            y += 34;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Voice"), lx, y + 3));
            cmbVoice = MakeCombo(Localization.T("Settings.TextBooks.Voice"), cx, y, cw, tab++);
            cmbVoice.SelectedIndexChanged += (s, e) => VoiceChanged();
            // Enter opens the door; walking past it does not. And walking AWAY
            // from it puts the previous voice back, so the row can be read
            // without consequence.
            //
            // NOT wired to DropDownClosed, though that looked right: closing the
            // list is what Enter itself does, so restoring there would move the
            // selection off the door before the key was handled and the door
            // would never open. Leave is late enough to be safe.
            cmbVoice.KeyDown += OnVoiceKeyDown;
            cmbVoice.Leave += (s, e) => RestoreIfOnDoor();
            box.Controls.Add(cmbVoice);

            // Numeric fields rather than sliders: a screen reader speaks the value
            // on every step, which a track bar does not.
            y += 40;
            box.Controls.Add(MakeLabel(Localization.T("Settings.TextBooks.Speed"), lx, y + 3));
            // 0.5× to 3.0× in tenths, which is the audio player's speed range and
            // step, written the way the player writes it. It was 80..400 words a
            // minute until 2026-08-23; see currentTextSpeed in Form1 for why that
            // unit went. Stored as a whole percentage, so the conversion is here
            // and the settings file never carries a decimal point -- which would
            // read back differently on a machine with a decimal comma.
            numRate = MakeDecimal(Localization.T("Settings.TextBooks.Speed"), cx, y, 0.5m, 3.0m,
                                  Clamp(appSettings.TtsSpeed, 50, 300) / 100m, tab++, 0.1m, 1);
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

            // THE CLOUD VOICES ARE NEVER OFFERED HERE, whatever the switch on the
            // Advanced tab says. This page assigns per-language DEFAULTS and a
            // cloud voice may not be one; leaving them out is how that rule is
            // kept, rather than by a rule somebody has to remember. It also stops
            // the language list below jumping from three entries to fifty-three,
            // since thirty speakers between them read that many languages.
            voiceCatalog = WithoutCloudVoices(voiceCatalog);

            PopulateLanguages();
            cmbLanguage.SelectedIndex = 0;      // "all other languages" — cascades
            return box;
        }

        /// <summary>The languages this page can assign a default voice to:
        /// **languages something installed SPEAKS, plus any language that already
        /// has a rule**. Nothing else.
        ///
        /// <para><b>Rewritten 2026-08-15, and both things that went had become
        /// wrong for the same reason</b> — this page assigns per-language
        /// DEFAULTS, so a row that cannot lead to a default has no business on
        /// it.</para>
        ///
        /// <para><b>The languages the LIBRARY has a book in are gone.</b> They
        /// were collected append-only and never pruned, so one German book that
        /// had long since been deleted left German on this list for ever
        /// (Gordan). Deriving them instead would have needed a rebuild on every
        /// scan and a hook on every delete path — and books also vanish outside
        /// NBR. The whole question dissolves once the list is what is installed:
        /// no store, no pruning, and it cannot go stale.</para>
        ///
        /// <para><b>"All other languages" — the global default — is gone too,
        /// and Gordan found the fault by reasoning about it.</b> He put it as: if
        /// that row is set to Matej, a book in a language nobody supports opens
        /// in Matej instead of asking. Measured, he is right, though by a route
        /// he did not name: a KNOWN language with no voice already answered
        /// <see cref="VoiceSource.NoVoice"/> and asked. But an exotic language is
        /// usually not known at all — <see cref="LanguageDetector"/> covers about
        /// twenty and stays silent rather than guess — so the book arrived with
        /// an EMPTY language, and an empty language took the global default. The
        /// row's name was the lie: it read "all the other languages" and behaved
        /// as "the language could not be worked out".</para>
        ///
        /// <para>A language that keeps a rule stays listed even with nothing to
        /// speak it, because a rule you cannot see is a rule you cannot revoke —
        /// and the first row of the voice list, "(no voice chosen)", is how it
        /// is revoked.</para></summary>
        private void PopulateLanguages()
        {
            cmbLanguage.Items.Clear();
            languageCodes.Clear();

            var codes = new List<string>();
            foreach (var c in voiceCatalog)
            {
                string p = LanguageDetector.Primary(c.Language);
                if (p.Length > 0 && !codes.Contains(p)) codes.Add(p);
            }
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

            // LAST ENTRY: a way out to Windows, inside the list where the
            // absence is felt (Gordan, 2026-08-14). A reader who finds no voice
            // for their language is standing right here when they find it out,
            // and a button somewhere else on the page is a worse answer than an
            // extra row. It also keeps the Speech page from growing another
            // control, which was his other concern.
            //
            // voiceNames is NOT given a matching entry, so the row cannot be
            // mistaken for a voice by anything that reads the selection back.
            cmbVoice.Items.Add(Localization.T("Settings.TextBooks.AddVoices"));

            string want;
            if (!stagedLanguageVoices.TryGetValue(code, out want))
                want = appSettings.LanguageVoice(code);
            int wi = voiceNames.IndexOf(want ?? "");
            cmbVoice.SelectedIndex = wi >= 0 ? wi : 0;

            populating = wasPopulating;
            LoadPrefsForSelectedVoice();
        }

        /// <summary>Voice picked → it becomes this language's voice, and the three
        /// numbers below switch to how that voice is set up.</summary>
        private void VoiceChanged()
        {
            // The last row is a DOOR, not a voice — and passing over it is not
            // opening it (Gordan, 2026-08-14). Arrowing through an open list must
            // be free: a reader reads the list by walking it, and a row that acts
            // on arrival cannot be read past. So nothing happens here; the door
            // opens on Enter, in OnVoiceKeyDown.
            if (OnDoorRow())
            {
                LoadPrefsForSelectedVoice();
                return;                     // not a voice: nothing to stage
            }
            if (!populating)
            {
                lastVoiceIndex = cmbVoice.SelectedIndex;
                stagedLanguageVoices[SelectedLanguageCode()] = SelectedVoiceName();
            }
            LoadPrefsForSelectedVoice();
        }

        /// <summary>Standing on the "add languages" row, which is the one entry
        /// with no voice behind it in <c>voiceNames</c>.</summary>
        private bool OnDoorRow()
        {
            return cmbVoice != null && cmbVoice.SelectedIndex >= 0
                && cmbVoice.SelectedIndex >= voiceNames.Count;
        }

        /// <summary>The last row that WAS a voice, to fall back to when the
        /// reader walks onto the door and then leaves without opening it. Without
        /// this the language would be left with "Add new languages" for a voice,
        /// which is a language with no voice and no way to tell.</summary>
        private int lastVoiceIndex;

        private void OnVoiceKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || !OnDoorRow()) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            using (var dlg = new OcrLanguageForm(LanguagePackFamily.Voices)) dlg.ShowDialog(this);
            LanguageChanged();               // rebuild: a new voice may have arrived
        }

        /// <summary>Leaving the list while standing on the door puts the previous
        /// voice back.</summary>
        private void RestoreIfOnDoor()
        {
            if (!OnDoorRow()) return;
            if (lastVoiceIndex >= 0 && lastVoiceIndex < voiceNames.Count)
                cmbVoice.SelectedIndex = lastVoiceIndex;
            else if (cmbVoice.Items.Count > 1)
                cmbVoice.SelectedIndex = 0;
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
            SetSpeedPercent(numRate, p.Speed);
            numVolume.Value = Clamp(p.Volume, (int)numVolume.Minimum, (int)numVolume.Maximum);
            numPitch.Value = Clamp(p.Pitch, (int)numPitch.Minimum, (int)numPitch.Maximum);
        }

        /// <summary>Files what the three fields currently show under the voice they
        /// belong to.</summary>
        private void StageCurrentPrefs()
        {
            if (string.IsNullOrEmpty(prefsVoice) || numRate == null) return;
            stagedPrefs.Set(prefsVoice,
                new VoicePrefs(SpeedPercent(numRate), (int)numVolume.Value, (int)numPitch.Value));
        }

        // ── Visual output (placeholder for the on-screen branch) ──────────────
        private GroupBox BuildVisualGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.VisualGroup")
                     + (Beta.ReadingWindow ? "" : Localization.T("Beta.Suffix"));
            box.Name = "Settings.TextBooks.Visual.Hint";
            box.Location = new Point(x, y);
            // 232, not 224: the last row (Background colour) ended one pixel below
            // the box that was meant to contain it.
            box.Size = new Size(500, 232);
            // Two columns, under Speech. Five lists whose entries are phrases
            // ("Two text rows (subtitles)") and not single words.
            box.Tag = "span2";

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

        private void UpdateVisualEnabled()
        {
            // HELD BACK FROM THE FIRST BETA -- Beta.ReadingWindow. Same treatment
            // as the per-book copy in Properties: the switch greys out with the
            // group it governs, so nothing here can be set for a window that will
            // not open.
            bool on = Beta.ReadingWindow && chkVisual != null && chkVisual.Checked;
            SetEnabled(on, cmbVisualMode, cmbHighlight, cmbHighlightColour, cmbTextColour, cmbBackColour);
            if (chkVisual != null) chkVisual.Enabled = Beta.ReadingWindow;
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
            // NVDA does not announce a closed drop-down on arrow. Here rather
            // than on each combo, so the next one added is not silent by omission
            // — which is exactly how Settings came to be silent while Sound
            // processing spoke. See NvdaController.SpeakOnChange.
            NvdaController.SpeakOnChange(c);
            return c;
        }

        /// <summary>A spin box. <paramref name="increment"/> is the arrow step — it
        /// matches the player's own step for the same value (10 % speed, 5 %
        /// volume), because stepping by 1 through those ranges takes far too
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

        /// <summary>The speed spin box read as the whole percentage every other
        /// part of NBR stores — the control counts in tenths of a multiplier
        /// because that is what a reader is choosing, and 1.5 is 150.</summary>
        internal static int SpeedPercent(NumericUpDown n)
        {
            return n == null ? 100 : (int)Math.Round(n.Value * 100m);
        }

        /// <summary>The reverse, clamped to the box's own range.</summary>
        internal static void SetSpeedPercent(NumericUpDown n, int percent)
        {
            if (n == null) return;
            decimal v = percent / 100m;
            n.Value = v < n.Minimum ? n.Minimum : (v > n.Maximum ? n.Maximum : v);
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
                sp.SetRate(TtsReader.SpeedToRate(SpeedPercent(numRate)));
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
                sp.SetRate(TtsReader.SpeedToRate(SpeedPercent(numRate)));
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

            // Device goes in a group like every other page (Gordan, 2026-08-03).
            // Loose controls are not on the three-column frame, so this tab was
            // laid out unlike the two beside it — and, worse, the skin takes the
            // inline hint boxes off every page, so with nothing to carry a ? the
            // keep-alive explanation simply vanished in the new look. A group has
            // somewhere to put the ?; that is what brings the text back.
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.Device.OutputGroup");
            box.Name = "Settings.Device.KeepAlive.Hint";
            box.Location = new Point(8, 6);
            box.Size = new Size(500, 92);
            // Two columns: sound cards report themselves at length
            // ("Speakers (Realtek(R) Audio)"), and one column left the list 171
            // pixels wide — a name truncated to "Speakers (Realt...".
            box.Tag = "span2";

            Label lblSoundCard = new Label();
            lblSoundCard.Text = Localization.T("Settings.Device.SoundCard");
            lblSoundCard.Location = new Point(14, 29);
            lblSoundCard.Size = new Size(160, 20);

            cmbSoundCard = new ComboBox();
            cmbSoundCard.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSoundCard.Location = new Point(184, 26);
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

            box.Controls.Add(lblSoundCard);
            box.Controls.Add(cmbSoundCard);
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
            chkKeepAlive.SetBounds(14, 58, 470, 24);
            chkKeepAlive.TabIndex = 1;
            chkKeepAlive.Checked = appSettings.KeepDeviceAlive;
            box.Controls.Add(chkKeepAlive);

            page.Controls.Add(box);

            // ── Optical drive (Gordan, 2026-08-07) ───────────────────────────
            // Its own group, which is why the tab is "Devices" now: a sound card
            // and a CD drive are two different pieces of hardware and the page
            // was named for having only one of them.
            //
            // THE WHOLE GROUP IS DIMMED WHEN THERE IS NO DRIVE, rather than
            // hidden. A reader who has one wants to find the switch; a reader who
            // has none is better told "this exists and your machine cannot do it"
            // than left to wonder whether they missed it. Hiding answers no
            // question at all — and disabled is a state a screen reader announces.
            bool haveDrive = OpticalDrive.AnyDrive();

            GroupBox optical = new GroupBox();
            optical.Text = Localization.T("Settings.Device.OpticalGroup");
            optical.Name = "Settings.Device.UseOptical.Hint";
            optical.Location = new Point(8, 172);
            // 92, the same as the sound-card group above: a checkbox on one row
            // and a labelled combo on the next is exactly the shape that group
            // already has, and 62 was the height for the checkbox alone.
            optical.Size = new Size(500, 92);
            optical.Tag = "span2";
            optical.Enabled = haveDrive;

            chkOptical = new CheckBox();
            chkOptical.Text = Localization.T("Settings.Device.UseOptical");
            chkOptical.AccessibleName = Localization.T("Settings.Device.UseOptical");
            chkOptical.SetBounds(14, 26, 470, 24);
            chkOptical.TabIndex = 0;
            // Off by default even where a drive exists — see AppSettings.
            chkOptical.Checked = haveDrive && appSettings.UseOpticalDrive;
            // WHICH drive follows WHETHER we use one (Gordan, screen-reader pass
            // 2026-08-11). Choosing between two drives while the switch above says
            // "do not use a drive" is a control offering a decision that cannot
            // take effect — and a reader has no way to see that from the name.
            // Dimmed rather than hidden, the rule the group already follows one
            // level up: disabled is a state a screen reader announces, absent is
            // not.
            chkOptical.CheckedChanged += (s, e) =>
            {
                if (cmbOptical != null) cmbOptical.Enabled = haveDrive && chkOptical.Checked;
            };
            optical.Controls.Add(chkOptical);

            // WHICH drive, because more than one is not a museum piece (Gordan,
            // 2026-08-07): a physical drive and a virtual one side by side is an
            // ordinary setup wherever image-mounting software is installed, and
            // guessing between them is the kind of thing that works perfectly on
            // the machine it was written on.
            //
            // The first row is "whichever has a disc in it", and it stays the
            // default: for the one-drive machine that is the whole answer, and
            // for two it is still right most of the time.
            Label lblDrive = new Label();
            lblDrive.Text = Localization.T("Settings.Device.OpticalWhich");
            lblDrive.SetBounds(14, 59, 160, 20);

            cmbOptical = new ComboBox();
            cmbOptical.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbOptical.SetBounds(184, 56, 240, 24);
            cmbOptical.AccessibleName = Localization.T("Settings.Device.OpticalWhich");
            cmbOptical.TabIndex = 1;

            opticalDrives.Add("");
            cmbOptical.Items.Add(Localization.T("Settings.Device.OpticalAuto"));
            foreach (string d in OpticalDrive.Drives())
            {
                opticalDrives.Add(d);
                // The letter and, where Windows will say, what the drive calls
                // itself — "F:  Yubsoft ImgDrive" tells the two apart at a glance
                // where "F:" alone does not.
                cmbOptical.Items.Add(DriveLabel(d));
            }
            int pick = opticalDrives.IndexOf(appSettings.OpticalDriveLetter ?? "");
            cmbOptical.SelectedIndex = pick >= 0 ? pick : 0;
            // The state it opens in, which the CheckedChanged above only maintains
            // afterwards.
            cmbOptical.Enabled = haveDrive && chkOptical.Checked;

            optical.Controls.Add(lblDrive);
            optical.Controls.Add(cmbOptical);

            page.Controls.Add(optical);
            return page;
        }

        // Misc is GONE (Gordan, 2026-08-03). It never held anything but the look
        // switch and a "work in progress" placeholder — a tab that existed to
        // have somewhere to put one control. The look moved to General, where it
        // is a setting among settings instead of a leftover.

        /// <summary>The chosen look, as an id for AppSettings. Two items, and both
        /// are a real theme — there is no longer an id meaning "work it out".</summary>
        private string SelectedThemeId()
        {
            if (cmbLook == null) return UiTheme.Current.Id;
            return cmbLook.SelectedIndex == 0 ? UiTheme.ClassicId : UiTheme.NewId;
        }
    }
}
