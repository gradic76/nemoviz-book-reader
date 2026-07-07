using System;
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
        private CheckBox chkShowHints;
        private TabControl tabSettings;
        private Button btnOK;
        private Button btnCancel;
        private Button btnApply;

        public SettingsForm()
        {
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

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("Settings.Cancel.Accessible");
            btnCancel.Size = new Size(90, 32);
            btnCancel.Location = new Point(280, 420);
            btnCancel.TabIndex = 3;
            btnCancel.DialogResult = DialogResult.Cancel;

            // No DialogResult — Apply does not close the dialog. Currently a
            // placeholder with nothing to apply yet (see class remarks).
            btnApply = new Button();
            btnApply.Text = Localization.T("Settings.Apply");
            btnApply.AccessibleName = Localization.T("Settings.Apply.Accessible");
            btnApply.Size = new Size(90, 32);
            btnApply.Location = new Point(380, 420);
            btnApply.TabIndex = 4;

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

            page.Controls.Add(chkUseMultimediaKeys);
            page.Controls.Add(chkUseMultimediaKeysGlobally);
            return page;
        }

        private TabPage BuildAudioBooksTab()
        {
            TabPage page = new TabPage(Localization.T("Settings.Tab.AudioBooks"));
            page.Controls.Add(BuildPlaceholder(Localization.T("Settings.WorkInProgress"),
                new Point(10, 20), new Size(420, 30)));
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

            ComboBox cmbVoice = new ComboBox();
            cmbVoice.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbVoice.Location = new Point(180, 87);
            cmbVoice.Size = new Size(240, 24);
            cmbVoice.AccessibleName = Localization.T("Settings.TextBooks.Voice");
            cmbVoice.TabIndex = 2;

            Label lblSpeed = new Label();
            lblSpeed.Text = Localization.T("Settings.TextBooks.Speed");
            lblSpeed.Location = new Point(10, 128);
            lblSpeed.Size = new Size(420, 20);

            TrackBar trkSpeed = new TrackBar();
            trkSpeed.Minimum = 100;
            trkSpeed.Maximum = 400;
            trkSpeed.Value = 200;
            trkSpeed.TickFrequency = 25;
            trkSpeed.Location = new Point(10, 150);
            trkSpeed.Size = new Size(420, 40);
            trkSpeed.AccessibleName = Localization.T("Settings.TextBooks.Speed");
            trkSpeed.TabIndex = 3;

            Label lblVolume = new Label();
            lblVolume.Text = Localization.T("Settings.TextBooks.Volume");
            lblVolume.Location = new Point(10, 194);
            lblVolume.Size = new Size(420, 20);

            TrackBar trkVolume = new TrackBar();
            trkVolume.Minimum = 0;
            trkVolume.Maximum = 100;
            trkVolume.Value = 100;
            trkVolume.TickFrequency = 10;
            trkVolume.Location = new Point(10, 216);
            trkVolume.Size = new Size(420, 40);
            trkVolume.AccessibleName = Localization.T("Settings.TextBooks.Volume");
            trkVolume.TabIndex = 4;

            Label lblPitch = new Label();
            lblPitch.Text = Localization.T("Settings.TextBooks.Pitch");
            lblPitch.Location = new Point(10, 260);
            lblPitch.Size = new Size(420, 20);

            TrackBar trkPitch = new TrackBar();
            trkPitch.Minimum = -10;
            trkPitch.Maximum = 10;
            trkPitch.Value = 0;
            trkPitch.TickFrequency = 1;
            trkPitch.Location = new Point(10, 282);
            trkPitch.Size = new Size(420, 40);
            trkPitch.AccessibleName = Localization.T("Settings.TextBooks.Pitch");
            trkPitch.TabIndex = 5;

            TextBox tbComingSoon = BuildPlaceholder(Localization.T("Settings.TextBooks.ComingSoon"),
                new Point(10, 326), new Size(420, 30));
            tbComingSoon.TabIndex = 6;

            page.Controls.Add(lblLanguage);
            page.Controls.Add(cmbLanguage);
            page.Controls.Add(lblSpeechEngine);
            page.Controls.Add(cmbSpeechEngine);
            page.Controls.Add(lblVoice);
            page.Controls.Add(cmbVoice);
            page.Controls.Add(lblSpeed);
            page.Controls.Add(trkSpeed);
            page.Controls.Add(lblVolume);
            page.Controls.Add(trkVolume);
            page.Controls.Add(lblPitch);
            page.Controls.Add(trkPitch);
            page.Controls.Add(tbComingSoon);
            return page;
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
