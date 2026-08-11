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
    /// <para><b>I argued the language barely mattered, and I was wrong.</b> The
    /// evidence was one clean synthetic English paragraph read by the Croatian
    /// engine at 0.0 % character error — the easiest case there is, where no
    /// glyph is ambiguous. Gordan corrected it from years of real OCR'd books:
    /// the English engine turns <i>Vatikan</i> into <i>Yatikan</i>, a Serbian
    /// recognizer on a Croatian book turns <i>William</i> into <i>Vvilliam</i>.
    /// And if it were only Latin letters, Microsoft would ship one pack rather
    /// than thirty-five. The language decides the ambiguous glyphs, so the reader
    /// gets to decide the language.</para>
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

            // NO "Automatic" here, and Gordan is right that there could not be
            // one: choosing the language automatically would mean reading a page
            // to see what language it is in, and reading a page is the thing that
            // needs the language. The loop has no start. Automatic belongs in
            // Settings, where it means "whatever Windows would pick" and is a
            // default rather than an answer — here the reader is being asked
            // precisely because they know something we cannot work out.
            //
            // Which one is offered first: the Settings choice when it names a
            // real recognizer, otherwise the one Windows itself would use.
            string prefer = Language;
            if (string.IsNullOrEmpty(prefer) || !WindowsOcr.IsInstalled(prefer))
                prefer = WindowsOcr.ResolvedLanguage("");
            int selected = 0;
            foreach (var l in WindowsOcr.Languages)
            {
                if (string.Equals(l.Tag, prefer, StringComparison.OrdinalIgnoreCase))
                    selected = tags.Count;
                tags.Add(l.Tag);
                languages.Items.Add(l.Name);
            }
            if (languages.Items.Count > 0)
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
                // A real tag, never empty: the reader picked one, and passing
                // empty would hand the job back to "whatever Windows would do"
                // and quietly lose the choice they just made.
                if (i >= 0 && i < tags.Count) Language = tags[i];
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
