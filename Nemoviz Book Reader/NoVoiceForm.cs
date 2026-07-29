using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// A book has been opened that nothing installed can read. This is the one
    /// place NBR interrupts on the subject, and it interrupts deliberately: the
    /// spoken announcement it replaces was gone the moment it was said, and left
    /// a silent player with nothing on screen to explain it. A reader who was
    /// looking away, or who cannot hear it, got no message at all — which is the
    /// whole reason this is a dialog and not a notification.
    ///
    /// <para>It offers the same two steps as everywhere else, <b>language then
    /// voice</b>, and it offers them for THIS BOOK ONLY. There is deliberately no
    /// "use this for every book in that language": reading a French book with a
    /// Romanian voice is a fair thing to do once and a poor thing to make a rule
    /// of, and a rule the reader did not knowingly set is a rule they do not know
    /// they have. Making it a rule is Settings' job, and getting there should
    /// take some effort (Gordan, 2026-07-29).</para>
    /// </summary>
    public class NoVoiceForm : Form
    {
        private readonly List<(string Name, string Group, string Language)> catalog;
        private readonly List<string> languageCodes = new List<string>();
        private ComboBox cmbLanguage, cmbVoice;

        /// <summary>The voice picked, or empty when the reader left the book
        /// unread.</summary>
        public string ChosenVoice { get; private set; }

        public NoVoiceForm(string bookLanguage,
                           List<(string Name, string Group, string Language)> voices)
        {
            catalog = voices ?? new List<(string, string, string)>();
            ChosenVoice = "";
            BuildUI(bookLanguage);
        }

        private void BuildUI(string bookLanguage)
        {
            Text = Localization.T("NoVoice.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 236);

            string langName = SettingsForm.LanguageName(LanguageDetector.Primary(bookLanguage));

            // The message is a read-only TextBox, not a Label. A screen reader
            // driven by Tab — which is how this app is used — never visits a
            // label, and this is the sentence the whole dialog exists to deliver.
            var message = new TextBox();
            message.Multiline = true;
            message.ReadOnly = true;
            message.BorderStyle = BorderStyle.None;
            message.BackColor = SystemColors.Control;
            message.Text = Localization.T("NoVoice.Message", langName);
            message.AccessibleName = message.Text;
            message.SetBounds(14, 14, 492, 60);
            message.TabIndex = 0;
            Controls.Add(message);

            Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Language"), 14, 91));
            cmbLanguage = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.Language"), 190, 88, 316, 1);
            cmbLanguage.SelectedIndexChanged += (s, e) => VoicesForLanguage();
            Controls.Add(cmbLanguage);

            Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Voice"), 14, 127));
            cmbVoice = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.Voice"), 190, 124, 316, 2);
            Controls.Add(cmbVoice);

            var ok = new Button();
            ok.Text = Localization.T("NoVoice.Read");
            ok.AccessibleName = ok.Text;
            ok.SetBounds(190, 176, 150, 32);
            ok.TabIndex = 3;
            ok.DialogResult = DialogResult.OK;
            ok.Click += (s, e) =>
            {
                int i = cmbVoice.SelectedIndex;
                ChosenVoice = i >= 0 ? cmbVoice.Items[i].ToString() : "";
            };
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = Localization.T("NoVoice.Leave");
            cancel.AccessibleName = cancel.Text;
            cancel.SetBounds(352, 176, 154, 32);
            cancel.TabIndex = 4;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            // Only the languages something can actually speak. This is the "read
            // it with" list, not the "set a rule for" list — offering a language
            // with no voice under it would be offering a dead end.
            var codes = new List<string>();
            foreach (var c in catalog)
            {
                string p = LanguageDetector.Primary(c.Language);
                if (p.Length > 0 && !codes.Contains(p)) codes.Add(p);
            }
            codes.Sort((a, b) => string.Compare(SettingsForm.LanguageName(a), SettingsForm.LanguageName(b),
                                                StringComparison.CurrentCultureIgnoreCase));
            foreach (string p in codes)
            {
                languageCodes.Add(p);
                cmbLanguage.Items.Add(SettingsForm.LanguageName(p) + " (" + p + ")");
            }
            if (cmbLanguage.Items.Count > 0) cmbLanguage.SelectedIndex = 0;

            // Focus starts on the message, so it is the first thing read out: the
            // reader hears WHY before they are handed the choice. A focused
            // multiline TextBox selects all of itself, though, which paints the
            // whole message as a solid blue block — the same thing the info glass
            // had to be taught not to do. Park the caret at the start instead;
            // nothing is lost, the field is read-only.
            message.GotFocus += (s, e) => { if (message.SelectionLength > 0) message.Select(0, 0); };
            Shown += (s, e) => { message.Focus(); message.Select(0, 0); };
        }

        private void VoicesForLanguage()
        {
            int i = cmbLanguage.SelectedIndex;
            string lang = i >= 0 && i < languageCodes.Count ? languageCodes[i] : "";
            cmbVoice.Items.Clear();
            foreach (string name in VoiceChooser.VoicesFor(catalog, lang))
                cmbVoice.Items.Add(name);
            if (cmbVoice.Items.Count > 0) cmbVoice.SelectedIndex = 0;
        }
    }
}
