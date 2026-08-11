using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// "This book is pictures. Shall I read it, and with which language?"
    ///
    /// <para><b>It only appears when there is a choice to make.</b> With one
    /// recognizer installed — which is most machines, and was this one until
    /// Gordan installed English — the import asks a plain yes/no, because a
    /// picker holding a single entry is an obstacle wearing the clothes of a
    /// choice. With two or more it is a real decision and it belongs HERE, at the
    /// moment the reader is deciding, not buried in Settings: they have the book
    /// in front of them and know what language it is in.</para>
    ///
    /// <para>That reverses half of what I argued earlier. I was right that the
    /// language rarely matters — recognition goes by script, and an English page
    /// through the Croatian engine measured 0.0 % character error — and wrong to
    /// conclude the reader should not be offered it. Rarely mattering is not
    /// never mattering, and the cost of offering it is one combo box that only
    /// exists when it has something to say.</para>
    ///
    /// <para><b>Focus starts on the language.</b> Unusual for this codebase,
    /// where focus starts on the action — but the action already has Enter
    /// through <see cref="Form.AcceptButton"/>, so a reader who just wants to say
    /// yes presses Enter and hears nothing extra, while a reader who came to
    /// change the language is already standing on it.</para>
    /// </summary>
    internal class OcrAskForm : Form
    {
        /// <summary>The recognizer the reader chose: a language tag, or empty for
        /// automatic. Only meaningful when the dialog returned OK.</summary>
        public string Language { get; private set; }

        private readonly ComboBox languages;
        private readonly List<string> tags = new List<string>();

        public OcrAskForm(string question, string preselect)
        {
            Language = preselect ?? "";

            Text = Localization.T("Ocr.Ask.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 218);

            // The question is material to weigh, not an event to dismiss, so it
            // goes in something a reader can go back into and re-read (§8b) —
            // never a Label, which Tab never visits.
            var text = new TextBox
            {
                Location = new Point(12, 12),
                Size = new Size(436, 96),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Text = question,
                TabIndex = 3
            };
            text.AccessibleName = question;

            var label = new Label
            {
                Text = Localization.T("Ocr.Ask.ReadWith"),
                Location = new Point(12, 122),
                Size = new Size(140, 20)
            };

            languages = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(158, 118),
                Size = new Size(290, 24),
                TabIndex = 0
            };
            languages.AccessibleName = Localization.T("Ocr.Ask.ReadWith");

            tags.Add("");
            languages.Items.Add(Localization.T("Settings.Ocr.Automatic"));
            int selected = 0;
            foreach (var l in WindowsOcr.Languages)
            {
                if (string.Equals(l.Tag, Language, StringComparison.OrdinalIgnoreCase))
                    selected = tags.Count;
                tags.Add(l.Tag);
                languages.Items.Add(l.Name);
            }
            languages.SelectedIndex = Math.Min(selected, languages.Items.Count - 1);

            var read = new Button
            {
                Text = Localization.T("Ocr.Ask.Read"),
                Location = new Point(232, 168),
                Size = new Size(104, 32),
                TabIndex = 1,
                DialogResult = DialogResult.OK
            };
            var skip = new Button
            {
                Text = Localization.T("Ocr.Ask.Skip"),
                Location = new Point(344, 168),
                Size = new Size(104, 32),
                TabIndex = 2,
                DialogResult = DialogResult.Cancel
            };
            read.AccessibleName = read.Text;
            skip.AccessibleName = skip.Text;
            AcceptButton = read;
            CancelButton = skip;

            Controls.Add(text);
            Controls.Add(label);
            Controls.Add(languages);
            Controls.Add(read);
            Controls.Add(skip);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Focus is on the language, so without this the window opens naming a
            // combo box and says nothing about what it is being asked to read.
            ScreenReader.Announce(this, Controls.Count > 0 ? Controls[0].Text : "");
            languages.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                int i = languages.SelectedIndex;
                Language = i >= 0 && i < tags.Count ? tags[i] : "";
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ScreenReader.Forget(this);
            base.OnFormClosed(e);
        }
    }
}
