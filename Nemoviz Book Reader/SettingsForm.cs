using System;
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

        // Audio Books tab — use embedded metadata for title/author.
        private CheckBox chkUseMetadata;

        // Text Books tab — global TTS defaults (wired to AppSettings).
        private ComboBox cmbVoice;
        private TrackBar trkRate;
        private TrackBar trkVolume;
        private TrackBar trkPitch;

        public SettingsForm(AppSettings appSettings)
        {
            this.appSettings = appSettings;
            this.stagedLibraryPath = appSettings.LibraryPath;

            this.Text = Localization.T("Dialog.Settings.Title");
            this.ClientSize = new Size(480, 460);
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

            tabSettings = new TabControl();
            tabSettings.Location = new Point(10, 40);
            tabSettings.Size = new Size(460, 370);
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
            btnOK.Location = new Point(180, 420);
            btnOK.TabIndex = 2;
            btnOK.DialogResult = DialogResult.OK;
            // Click fires before the dialog closes, so this persists on OK too.
            btnOK.Click += (s, e) => SaveSettings();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("Settings.Cancel.Accessible");
            btnCancel.Size = new Size(90, 32);
            btnCancel.Location = new Point(280, 420);
            btnCancel.TabIndex = 3;
            btnCancel.DialogResult = DialogResult.Cancel;

            // No DialogResult — Apply persists without closing the dialog.
            btnApply = new Button();
            btnApply.Text = Localization.T("Settings.Apply");
            btnApply.AccessibleName = Localization.T("Settings.Apply.Accessible");
            btnApply.Size = new Size(90, 32);
            btnApply.Location = new Point(380, 420);
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

            CheckBox chkUseMultimediaKeys = new CheckBox();
            chkUseMultimediaKeys.Text = Localization.T("Settings.General.UseMultimediaKeys");
            chkUseMultimediaKeys.AccessibleName = Localization.T("Settings.General.UseMultimediaKeys");
            chkUseMultimediaKeys.Location = new Point(10, 20);
            chkUseMultimediaKeys.Size = new Size(420, 24);
            chkUseMultimediaKeys.TabIndex = 0;

            CheckBox chkUseMultimediaKeysGlobally = new CheckBox();
            chkUseMultimediaKeysGlobally.Text = Localization.T("Settings.General.UseMultimediaKeysGlobally");
            chkUseMultimediaKeysGlobally.AccessibleName = Localization.T("Settings.General.UseMultimediaKeysGlobally");
            chkUseMultimediaKeysGlobally.Location = new Point(10, 50);
            chkUseMultimediaKeysGlobally.Size = new Size(420, 24);
            chkUseMultimediaKeysGlobally.TabIndex = 1;

            Label lblLibraryLocation = new Label();
            lblLibraryLocation.Text = Localization.T("Settings.General.LibraryLocation");
            lblLibraryLocation.Location = new Point(10, 88);
            lblLibraryLocation.Size = new Size(420, 18);
            lblLibraryLocation.TabStop = false;

            // Read-only so the path can only be changed via Browse, but
            // tabbable + carrying the folder as its value so a screen reader
            // reads the current location.
            tbLibraryLocation = new TextBox();
            tbLibraryLocation.ReadOnly = true;
            tbLibraryLocation.TabStop = true;
            tbLibraryLocation.Location = new Point(10, 108);
            tbLibraryLocation.Size = new Size(330, 23);
            tbLibraryLocation.Text = stagedLibraryPath;
            tbLibraryLocation.AccessibleName = Localization.T("Settings.General.LibraryLocation");
            tbLibraryLocation.TabIndex = 2;

            Button btnBrowse = new Button();
            btnBrowse.Text = Localization.T("Settings.General.Browse");
            btnBrowse.AccessibleName = Localization.T("Settings.General.Browse.Accessible");
            btnBrowse.Location = new Point(348, 107);
            btnBrowse.Size = new Size(90, 26);
            btnBrowse.TabIndex = 3;
            btnBrowse.Click += (s, e) => BrowseLibraryLocation();

            Label lblLanguage = new Label();
            lblLanguage.Text = Localization.T("Settings.General.Language");
            lblLanguage.Location = new Point(10, 148);
            lblLanguage.Size = new Size(160, 20);
            lblLanguage.TabStop = false;

            // App UI language. Only English exists until the app is
            // feature-complete (hr.lang is a final translation pass), so the
            // combo currently lists just the one language; not yet wired to
            // AppSettings.SetLanguage since there is nothing to switch to.
            ComboBox cmbLanguage = new ComboBox();
            cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLanguage.Location = new Point(180, 145);
            cmbLanguage.Size = new Size(240, 24);
            cmbLanguage.AccessibleName = Localization.T("Settings.General.Language");
            cmbLanguage.TabIndex = 4;
            cmbLanguage.Items.Add(Localization.T("LanguageName"));
            cmbLanguage.SelectedIndex = 0;

            page.Controls.Add(chkUseMultimediaKeys);
            page.Controls.Add(chkUseMultimediaKeysGlobally);
            page.Controls.Add(lblLibraryLocation);
            page.Controls.Add(tbLibraryLocation);
            page.Controls.Add(btnBrowse);
            page.Controls.Add(lblLanguage);
            page.Controls.Add(cmbLanguage);
            return page;
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

            // Text Books — global TTS defaults.
            string voice = cmbVoice != null && cmbVoice.SelectedItem != null
                ? cmbVoice.SelectedItem.ToString() : (appSettings.TtsVoice ?? "");
            appSettings.SetTtsDefaults(voice, trkRate.Value, trkPitch.Value, trkVolume.Value);
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

            Label lblHint = new Label();
            lblHint.Text = Localization.T("Settings.Audio.UseMetadata.Hint");
            lblHint.Location = new Point(28, 66);
            lblHint.Size = new Size(410, 60);
            lblHint.TabStop = false;

            page.Controls.Add(chkUseMetadata);
            page.Controls.Add(lblHint);
            return page;
        }

        private TabPage BuildTextBooksTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.TextBooks"));

            Label lblLanguage = new Label();
            lblLanguage.Text = Localization.T("Settings.TextBooks.Language");
            lblLanguage.Location = new Point(10, 22);
            lblLanguage.Size = new Size(160, 20);

            ComboBox cmbLanguage = new ComboBox();
            cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLanguage.Location = new Point(180, 19);
            cmbLanguage.Size = new Size(240, 24);
            cmbLanguage.AccessibleName = Localization.T("Settings.TextBooks.Language");
            cmbLanguage.TabIndex = 0;

            Label lblSpeechEngine = new Label();
            lblSpeechEngine.Text = Localization.T("Settings.TextBooks.SpeechEngine");
            lblSpeechEngine.Location = new Point(10, 56);
            lblSpeechEngine.Size = new Size(160, 20);

            ComboBox cmbSpeechEngine = new ComboBox();
            cmbSpeechEngine.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSpeechEngine.Location = new Point(180, 53);
            cmbSpeechEngine.Size = new Size(240, 24);
            cmbSpeechEngine.AccessibleName = Localization.T("Settings.TextBooks.SpeechEngine");
            cmbSpeechEngine.TabIndex = 1;

            Label lblVoice = new Label();
            lblVoice.Text = Localization.T("Settings.TextBooks.Voice");
            lblVoice.Location = new Point(10, 90);
            lblVoice.Size = new Size(160, 20);

            cmbVoice = new ComboBox();
            cmbVoice.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbVoice.Location = new Point(180, 87);
            cmbVoice.Size = new Size(240, 24);
            cmbVoice.AccessibleName = Localization.T("Settings.TextBooks.Voice");
            cmbVoice.TabIndex = 2;
            // List every voice from every backend — in-process 64-bit SAPI5 plus
            // the 32-bit satellite (eSpeak / RHVoice) — merged, 64-bit winning
            // duplicates. Select the saved default.
            try
            {
                foreach (string name in EnsureSpeech().GetVoices())
                    cmbVoice.Items.Add(name);
            }
            catch { }
            if (cmbVoice.Items.Count > 0)
            {
                int vi = cmbVoice.Items.IndexOf(appSettings.TtsVoice ?? "");
                cmbVoice.SelectedIndex = vi >= 0 ? vi : 0;
            }

            Label lblSpeed = new Label();
            lblSpeed.Text = Localization.T("Settings.TextBooks.Speed");
            lblSpeed.Location = new Point(10, 128);
            lblSpeed.Size = new Size(420, 20);

            trkRate = new TrackBar();
            trkRate.Minimum = 80;
            trkRate.Maximum = 400;
            trkRate.Value = Clamp(appSettings.TtsWpm, 80, 400);
            trkRate.TickFrequency = 20;
            trkRate.Location = new Point(10, 150);
            trkRate.Size = new Size(420, 40);
            trkRate.AccessibleName = Localization.T("Settings.TextBooks.Speed");
            trkRate.TabIndex = 3;

            Label lblVolume = new Label();
            lblVolume.Text = Localization.T("Settings.TextBooks.Volume");
            lblVolume.Location = new Point(10, 194);
            lblVolume.Size = new Size(420, 20);

            trkVolume = new TrackBar();
            trkVolume.Minimum = 0;
            trkVolume.Maximum = 100;
            trkVolume.Value = Clamp(appSettings.TtsVolume, 0, 100);
            trkVolume.TickFrequency = 10;
            trkVolume.Location = new Point(10, 216);
            trkVolume.Size = new Size(420, 40);
            trkVolume.AccessibleName = Localization.T("Settings.TextBooks.Volume");
            trkVolume.TabIndex = 4;

            Label lblPitch = new Label();
            lblPitch.Text = Localization.T("Settings.TextBooks.Pitch");
            lblPitch.Location = new Point(10, 260);
            lblPitch.Size = new Size(420, 20);

            trkPitch = new TrackBar();
            trkPitch.Minimum = -10;
            trkPitch.Maximum = 10;
            trkPitch.Value = Clamp(appSettings.TtsPitch, -10, 10);
            trkPitch.TickFrequency = 1;
            trkPitch.Location = new Point(10, 282);
            trkPitch.Size = new Size(420, 40);
            trkPitch.AccessibleName = Localization.T("Settings.TextBooks.Pitch");
            trkPitch.TabIndex = 5;

            Button btnTest = new Button();
            btnTest.Text = Localization.T("Settings.TextBooks.Test");
            btnTest.AccessibleName = Localization.T("Settings.TextBooks.Test");
            btnTest.Location = new Point(10, 326);
            btnTest.Size = new Size(160, 30);
            btnTest.TabIndex = 6;
            btnTest.Click += (s, e) => TestVoice();

            page.Controls.Add(lblLanguage);
            page.Controls.Add(cmbLanguage);
            page.Controls.Add(lblSpeechEngine);
            page.Controls.Add(cmbSpeechEngine);
            page.Controls.Add(lblVoice);
            page.Controls.Add(cmbVoice);
            page.Controls.Add(lblSpeed);
            page.Controls.Add(trkRate);
            page.Controls.Add(lblVolume);
            page.Controls.Add(trkVolume);
            page.Controls.Add(lblPitch);
            page.Controls.Add(trkPitch);
            page.Controls.Add(btnTest);
            return page;
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
                sp.SetRate(TtsReader.WpmToRate(trkRate.Value));
                sp.SetVolume(trkVolume.Value);
                sp.SetPitch(trkPitch.Value * 5); // -10..10 → -50..50 %
                sp.Cancel();                      // stop any still-playing sample
                sp.Speak(Localization.T("Settings.TextBooks.TestSample"));
            }
            catch { }
        }

        // Lazily-created merged speech backend (64-bit + 32-bit satellite), used
        // for the voice list and the Test button; disposed with the dialog.
        private CompositeSpeechBackend speech;
        private CompositeSpeechBackend EnsureSpeech()
        {
            if (speech == null) speech = new CompositeSpeechBackend();
            return speech;
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

            ComboBox cmbSoundCard = new ComboBox();
            cmbSoundCard.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSoundCard.Location = new Point(180, 19);
            cmbSoundCard.Size = new Size(240, 24);
            cmbSoundCard.AccessibleName = Localization.T("Settings.Device.SoundCard");
            cmbSoundCard.TabIndex = 0;

            page.Controls.Add(lblSoundCard);
            page.Controls.Add(cmbSoundCard);
            return page;
        }

        private TabPage BuildMiscTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.Misc"));
            page.Controls.Add(BuildPlaceholder(Localization.T("Settings.WorkInProgress"),
                new Point(10, 20), new Size(420, 30)));
            return page;
        }
    }
}
