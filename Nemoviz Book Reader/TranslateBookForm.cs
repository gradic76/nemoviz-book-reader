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
        public string Notes { get; private set; }

        /// <summary>The glossary of an earlier book to start from, or null. See the
        /// combo's own comment for why this is asked rather than worked out.</summary>
        public string InheritGlossaryPath { get; private set; }

        /// <summary><b>One choice, and a chain follows from it.</b> The dialog used
        /// to ask for a fallback as well, which was asking a reader to design a
        /// retry policy — what they have an opinion about is which translation they
        /// would rather read. The rest of the order is fixed and explained by the
        /// help key beside the engine.</summary>
        public List<TranslationEngine> Chain
        {
            get { return TranslationEngines.Chain(Primary); }
        }

        private readonly ComboBox cmbSource = new ComboBox();
        private readonly ComboBox cmbTarget = new ComboBox();
        private readonly ComboBox cmbEngine = new ComboBox();
        private readonly TextBox tbNotes = new TextBox();
        private readonly List<string> langCodes = new List<string>();
        private readonly List<TranslationEngine> engines;
        private ComboBox cmbInherit;
        private readonly List<string> inheritPaths = new List<string>();
        private readonly List<KeyValuePair<string, string>> glossaries;

        public TranslateBookForm(AppSettings settings, string bookTitle, string detectedLanguage,
                                 int characters, bool fromHybrid,
                                 List<KeyValuePair<string, string>> earlierGlossaries = null)
        {
            engines = TranslationEngines.Configured();
            glossaries = earlierGlossaries;

            Text = Localization.T("Translate.Ask.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, fromHybrid ? 380 : 356);

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

            // What happens when the chosen engine will not take a passage — the
            // whole of the chain in one tabbable line, because "why does it name
            // three services when I picked one" is the question this control
            // raises and there is nowhere else to answer it.
            var chainNote = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Location = new Point(12, y),
                Size = new Size(476, 32),
                TabIndex = 7,
                Text = Localization.T("Translate.Ask.ChainNote")
            };
            chainNote.AccessibleName = chainNote.Text;
            Controls.Add(chainNote);
            y += 40;

            // THE STANDING NOTE IS SHOWN, NOT MERELY APPLIED. A rule that acts on
            // every book while being visible nowhere is the invisible dependency
            // this project keeps refusing: the reader would see a spelling or a
            // register they did not ask for here and have no way to learn where it
            // came from. Read-only and tabbable, so it is reachable.
            string standing = settings != null ? settings.TranslationNotes : "";
            if (!string.IsNullOrWhiteSpace(standing))
            {
                var std = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = SystemColors.Control,
                    Location = new Point(12, y),
                    Size = new Size(476, 30),
                    TabIndex = 8,
                    Text = Localization.T("Translate.Ask.Standing", standing.Replace("\r\n", " ").Replace("\n", " "))
                };
                std.AccessibleName = std.Text;
                Controls.Add(std);
                y += 36;
                Height += 36;
            }

            // THE GLOSSARY OF AN EARLIER BOOK, and only when there is one to offer.
            //
            // <para>Book two of a trilogy has to render Vonvalt exactly as book one
            // did, and nothing in a text says two books belong together. The reader
            // knows; NBR cannot. Same line as the narrator gender, opposite answer:
            // the gender IS in the text and is detected, series membership is not
            // and is asked. Asked ONCE, here, of somebody who already knows the
            // answer -- not a page of names to approve, which is a decision nobody
            // can make before reading the book.
            //
            // <para>The row does not appear at all when no earlier book carries a
            // glossary, which is every reader's first translation. A control that
            // can only be answered "no" is one more thing to tab past.
            if (glossaries != null && glossaries.Count > 0)
            {
                var lblInherit = new Label
                {
                    Text = Localization.T("Translate.Ask.Inherit"),
                    Location = new Point(12, y + 3),
                    Size = new Size(180, 20)
                };
                Controls.Add(lblInherit);
                cmbInherit = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(196, y),
                    Size = new Size(292, 24),
                    TabIndex = 8
                };
                cmbInherit.AccessibleName = Localization.T("Translate.Ask.Inherit");
                cmbInherit.Items.Add(Localization.T("Translate.Ask.Inherit.None"));
                inheritPaths.Add(null);
                foreach (var g in glossaries)
                {
                    cmbInherit.Items.Add(g.Key);
                    inheritPaths.Add(g.Value);
                }
                cmbInherit.SelectedIndex = 0;
                // NVDA says nothing when a closed DropDownList changes on the
                // arrow keys; the app-wide remedy, and a no-op under JAWS.
                NvdaController.SpeakOnChange(cmbInherit);
                Controls.Add(cmbInherit);
                y += 34;
                Height += 34;
            }

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
            // Explicit, as everywhere else in the app. A Button falls back to its
            // Text when nothing is set, so this changed nothing a reader hears --
            // but it is what the layout checks look for, and a false positive in a
            // check is a check people stop reading.
            ok.AccessibleName = ok.Text;
            cancel.AccessibleName = cancel.Text;
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
                Notes = tbNotes.Text.Trim();
                if (cmbInherit != null && cmbInherit.SelectedIndex > 0
                    && cmbInherit.SelectedIndex < inheritPaths.Count)
                    InheritGlossaryPath = inheritPaths[cmbInherit.SelectedIndex];
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

        /// <summary>Every language the services translate into, not the ones this
        /// library happens to hold books in.
        ///
        /// <para><b>That earlier rule was borrowed from the voice picker and does
        /// not transfer</b> — see <see cref="TranslationLanguages"/>. A voice must be
        /// installed; a translation service does not care what is on your shelf.
        /// Gordan met it with two books in the library and three languages
        /// offered.</para>
        ///
        /// <para>A long list is not the obstacle here that it is for voices either,
        /// because every row is equally reachable: there is no installed-and-not
        /// split, so no separator is needed and the type-ahead a combo already has
        /// is enough to reach any of them.</para></summary>
        private void BuildLanguages(AppSettings settings, string detected)
        {
            langCodes.Clear();
            foreach (var l in TranslationLanguages.All)
            {
                langCodes.Add(l.Code);
                cmbSource.Items.Add(l.DisplayName);
                cmbTarget.Items.Add(l.DisplayName);
            }

            int si = TranslationLanguages.IndexOf(detected);
            cmbSource.SelectedIndex = si >= 0 ? si : Math.Max(0, TranslationLanguages.IndexOf("en"));

            // FIRST TIME the Windows display language; AFTER THAT whatever was
            // chosen last. The system decides once, and habit decides from then on.
            // ("The system language" is three different things — display language,
            // regional formats, country — and this project has already been bitten
            // by picking the wrong one, so it is named explicitly.)
            string want = settings != null && !string.IsNullOrEmpty(settings.LastTranslationTarget)
                ? settings.LastTranslationTarget
                : CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            int ti = TranslationLanguages.IndexOf(want);
            cmbTarget.SelectedIndex = ti >= 0 ? ti : Math.Max(0, TranslationLanguages.IndexOf("en"));

            foreach (var e in engines) cmbEngine.Items.Add(e.DisplayName);
            if (engines.Count > 0) cmbEngine.SelectedIndex = 0;

            if (engines.Count == 0)
            {
                cmbEngine.Items.Add(Localization.T("Translate.Ask.NoEngine"));
                cmbEngine.SelectedIndex = 0;
                cmbEngine.Enabled = false;
            }
        }

        private string Code(ComboBox c)
        {
            int i = c.SelectedIndex;
            return i >= 0 && i < langCodes.Count ? langCodes[i] : "";
        }
    }
}
