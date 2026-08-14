using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// What is asked before a book is sent away to be translated.
    ///
    /// <para><b>As little as possible.</b> The language the book is in NBR already
    /// knows — it detects it from the words, which has been measured over some
    /// eighty-five real books and is more reliable than what a file declares about
    /// itself (declarations are wrong 17 % of the time). So the source is shown
    /// rather than asked, and can be corrected. The target is the reader's own
    /// language, which does not change from book to book.</para>
    ///
    /// <para><b>Which services appear is decided by which have a key</b> — Gordan's
    /// rule, and the same one by which only installed voices are offered. One key
    /// means one entry and nothing to decide.</para>
    ///
    /// <para><b>The estimate is shown before anything starts</b>, in characters and
    /// minutes, because this is the longest wait in the program and a reader is
    /// entitled to know that before agreeing to it rather than after. The rate
    /// behind it is measured: about 440 characters a second through a real book,
    /// context and checks included.</para>
    /// </summary>
    internal class TranslateBookForm : Form
    {
        /// <summary>Measured over a full novel, 131 pieces: 13.7 s for 6 000
        /// characters. Not a single request timed in isolation, which is faster and
        /// would flatter the estimate — this includes the context each piece
        /// carries and the checks on the way back.</summary>
        private const double CharsPerSecond = 440;

        public string SourceLanguage { get; private set; }
        public string TargetLanguage { get; private set; }
        public TranslationEngine Primary { get; private set; }
        public TranslationEngine Fallback { get; private set; }
        public string Notes { get; private set; }

        private readonly ComboBox cmbSource = new ComboBox();
        private readonly ComboBox cmbTarget = new ComboBox();
        private readonly ComboBox cmbEngine = new ComboBox();
        private readonly ComboBox cmbFallback = new ComboBox();
        private readonly TextBox tbNotes = new TextBox();
        private readonly List<string> langCodes = new List<string>();
        private readonly List<TranslationEngine> engines;

        public TranslateBookForm(AppSettings settings, string bookTitle, string detectedLanguage,
                                 int characters, bool fromHybrid)
        {
            engines = TranslationEngines.Configured();

            Text = Localization.T("Translate.Ask.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, fromHybrid ? 372 : 348);

            int y = 12;

            // What is about to happen, and what it will cost in time. Read-only and
            // TABBABLE, because a reader driven by Tab never visits a Label.
            int secs = (int)Math.Round(characters / CharsPerSecond);
            string howLong = secs < 90
                ? Localization.T("Translate.Progress.Seconds", (secs / 5) * 5)
                : Localization.T("Translate.Progress.Minutes", (int)Math.Round(secs / 60.0));
            var info = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Location = new Point(12, y),
                Size = new Size(476, fromHybrid ? 68 : 44),
                TabIndex = 0,
                Text = Localization.T("Translate.Ask.Info", bookTitle,
                                      characters.ToString("N0", CultureInfo.CurrentCulture), howLong)
                       + (fromHybrid ? "\r\n" + Localization.T("Translate.Ask.HybridNote") : "")
            };
            info.AccessibleName = info.Text;
            Controls.Add(info);
            y += info.Height + 12;

            BuildLanguages(settings, detectedLanguage);

            y = Row(Localization.T("Translate.Ask.From"), cmbSource, y, 1);
            y = Row(Localization.T("Translate.Ask.To"), cmbTarget, y, 3);
            y = Row(Localization.T("Translate.Ask.Engine"), cmbEngine, y, 5);
            y = Row(Localization.T("Translate.Ask.Fallback"), cmbFallback, y, 7);

            var lblNotes = new Label
            {
                Text = Localization.T("Translate.Ask.Notes"),
                Location = new Point(12, y + 3),
                Size = new Size(476, 20)
            };
            Controls.Add(lblNotes);
            y += 24;
            tbNotes.Multiline = true;
            tbNotes.ScrollBars = ScrollBars.Vertical;
            tbNotes.Location = new Point(12, y);
            tbNotes.Size = new Size(476, 56);
            tbNotes.TabIndex = 9;
            tbNotes.AccessibleName = Localization.T("Translate.Ask.Notes");
            Controls.Add(tbNotes);
            y += 66;

            var ok = new Button
            {
                Text = Localization.T("Translate.Ask.Start"),
                Location = new Point(278, y),
                Size = new Size(100, 30),
                TabIndex = 10,
                DialogResult = DialogResult.OK
            };
            var cancel = new Button
            {
                Text = Localization.T("Btn.Cancel"),
                Location = new Point(388, y),
                Size = new Size(100, 30),
                TabIndex = 11,
                DialogResult = DialogResult.Cancel
            };
            ok.Enabled = engines.Count > 0;
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            // Focus on the first thing to decide, not on the explanation above it —
            // the trap the archive password prompt walked into, where focus landed
            // on the message and everything typed went nowhere.
            Shown += (s, e) => { try { (engines.Count > 0 ? (Control)cmbSource : cancel).Focus(); } catch { } };

            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                SourceLanguage = Code(cmbSource);
                TargetLanguage = Code(cmbTarget);
                Primary = cmbEngine.SelectedIndex >= 0 && cmbEngine.SelectedIndex < engines.Count
                          ? engines[cmbEngine.SelectedIndex] : null;
                // The fallback list has "leave it in the original" as its first row,
                // so everything after it is shifted by one.
                int fi = cmbFallback.SelectedIndex - 1;
                Fallback = fi >= 0 && fi < engines.Count ? engines[fi] : null;
                if (Fallback == Primary) Fallback = null;
                Notes = tbNotes.Text.Trim();
            };
        }

        private int Row(string caption, ComboBox combo, int y, int tabIndex)
        {
            var lbl = new Label { Text = caption, Location = new Point(12, y + 3), Size = new Size(190, 20) };
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Location = new Point(208, y);
            combo.Size = new Size(280, 24);
            combo.TabIndex = tabIndex;
            combo.AccessibleName = caption;
            // NVDA says nothing when a closed list changes on the arrows; this is
            // the app-wide remedy and a no-op under JAWS.
            NvdaController.SpeakOnChange(combo);
            Controls.Add(lbl);
            Controls.Add(combo);
            return y + 32;
        }

        /// <summary>The languages offered are the ones this library actually holds
        /// books in, plus the reader's own — the same set Settings uses for voices,
        /// and for the same reason: a list of three hundred and fifty languages is
        /// not a choice, it is an obstacle.</summary>
        private void BuildLanguages(AppSettings settings, string detected)
        {
            var codes = new List<string>();
            void Note(string c)
            {
                c = LanguageDetector.Primary(c ?? "");
                if (c.Length > 0 && !codes.Contains(c)) codes.Add(c);
            }

            Note(detected);
            if (settings != null) foreach (string c in settings.SeenLanguages) Note(c);
            Note(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName);
            Note("en");
            Note("hr");

            codes.Sort((a, b) => string.Compare(LanguageDetector.DisplayName(a),
                                                LanguageDetector.DisplayName(b), StringComparison.CurrentCulture));
            langCodes.Clear();
            langCodes.AddRange(codes);
            foreach (string c in langCodes)
            {
                // Every language is written in its own language, everywhere.
                string name = LanguageDetector.DisplayName(c);
                cmbSource.Items.Add(name);
                cmbTarget.Items.Add(name);
            }

            cmbSource.SelectedIndex = Math.Max(0, langCodes.IndexOf(LanguageDetector.Primary(detected ?? "")));

            // FIRST TIME the Windows display language; AFTER THAT whatever was
            // chosen last. The system decides once, and habit decides from then on.
            // ("The system language" is three different things — display language,
            // regional formats, country — and this project has already been bitten
            // by picking the wrong one, so it is named explicitly.)
            string want = settings != null && !string.IsNullOrEmpty(settings.LastTranslationTarget)
                ? settings.LastTranslationTarget
                : CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            int ti = langCodes.IndexOf(LanguageDetector.Primary(want));
            cmbTarget.SelectedIndex = ti >= 0 ? ti : Math.Max(0, langCodes.IndexOf("hr"));

            foreach (var e in engines) cmbEngine.Items.Add(e.DisplayName);
            if (engines.Count > 0) cmbEngine.SelectedIndex = 0;

            cmbFallback.Items.Add(Localization.T("Translate.Ask.NoFallback"));
            foreach (var e in engines) cmbFallback.Items.Add(e.DisplayName);
            // The second engine, when there is one: they refuse different things,
            // so the other one is usually the one that will take the passage.
            cmbFallback.SelectedIndex = engines.Count > 1 ? 2 : 0;

            if (engines.Count == 0)
            {
                cmbEngine.Items.Add(Localization.T("Translate.Ask.NoEngine"));
                cmbEngine.SelectedIndex = 0;
                cmbEngine.Enabled = false;
                cmbFallback.Enabled = false;
            }
        }

        private string Code(ComboBox c)
        {
            int i = c.SelectedIndex;
            return i >= 0 && i < langCodes.Count ? langCodes[i] : "";
        }
    }
}
