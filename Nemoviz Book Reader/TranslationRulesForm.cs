using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>What NBR tells a translation service about the language it is
    /// writing -- on screen, where a reader can read it.
    ///
    /// <para><b>Why it exists</b> (Gordan, 2026-09-01): "Korisnik fakticki ne zna
    /// sto player salje servisu." The rules were compiled into the program until
    /// that day and could not be inspected at all. They are files now, and this
    /// window is the other half of the same answer: the file is on disk for
    /// editing, and its text is here for reading, without going to look for
    /// it.</para>
    ///
    /// <para><b>No tick boxes, and that was decided rather than skipped.</b> The
    /// obvious next step is a checkbox per rule, and three things argue against it
    /// for now. The rules are prose with a stated order of priority, so switching
    /// off item three of five leaves the other four ranked against something that
    /// is gone. The translation cache is keyed on the SOURCE TEXT and not on the
    /// prompt, so a reader who unticked a rule and re-ran a book would be handed
    /// the identical old translation and conclude the tick box was broken -- that
    /// would have to be fixed first, by hashing the active rule set into the key.
    /// And two hundred check boxes is a poor thing to walk through with a screen
    /// reader, where the same text as one field is a single say-all. Gordan's
    /// answer when the three were put to him: "Bez kvacica, barem do daljnjega,
    /// treba vidjeti imaju li uopce smisla."</para>
    ///
    /// <para><b>Shape</b>, and it follows <see cref="ServicesForm"/> deliberately,
    /// because a reader who has met that window has met this one: a chooser, a
    /// read-only TABBABLE field with the whole text, and a line saying where it
    /// came from. The field is never a Label -- a reader driven by Tab never
    /// visits one, and here the text is the entire point of the window.</para>
    ///
    /// <para><b>Languages that HAVE rules come first.</b> The list is every
    /// language NBR can translate into, as Gordan asked, and today two of a
    /// hundred and thirty-eight have rules. Ordinary order would mean arrowing
    /// past a hundred entries to reach the only two with anything to show, which
    /// is worse with a screen reader than with eyes.</para></summary>
    internal sealed class TranslationRulesForm : Form
    {
        private readonly List<TranslationLanguages.Lang> order = new List<TranslationLanguages.Lang>();
        private readonly ComboBox languages;
        private readonly TextBox body, where;
        private readonly Button close;

        public TranslationRulesForm(string preferCode)
        {
            Text = Localization.T("Dialog.Rules.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(660, 500);

            BuildOrder();

            Label lbl = new Label();
            lbl.Text = Localization.T("Dialog.Rules.Language");
            lbl.AutoSize = false;
            lbl.SetBounds(14, 16, 120, 20);

            languages = new ComboBox();
            languages.DropDownStyle = ComboBoxStyle.DropDownList;
            languages.SetBounds(140, 13, 320, 24);
            languages.AccessibleName = Localization.T("Dialog.Rules.Language");
            languages.TabIndex = 0;
            foreach (TranslationLanguages.Lang l in order) languages.Items.Add(l.DisplayName);
            // NVDA says nothing when a closed DropDownList changes on the arrow
            // keys; the app-wide remedy, and a no-op under JAWS.
            NvdaController.SpeakOnChange(languages);
            languages.SelectedIndexChanged += (s, e) => ShowChosen();

            body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.TabStop = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.BorderStyle = BorderStyle.FixedSingle;
            body.BackColor = SystemColors.Window;
            body.AccessibleName = Localization.T("Dialog.Rules.Text.Accessible");
            body.SetBounds(14, 48, 632, 356);
            body.TabIndex = 1;

            // WHERE IT CAME FROM, and how to have your own. A field rather than a
            // label for the same reason as the body: Tab has to be able to reach
            // it, because for a reader who wants to change the rules this line is
            // the instruction.
            where = new TextBox();
            where.Multiline = true;
            where.ReadOnly = true;
            where.TabStop = true;
            // NO SCROLLBAR. It had one, and the screenshot showed what that looks
            // like: a dead scrollbar with two arrows and no thumb, beside a
            // message that fits on one line. Two lines is the most this ever
            // holds and both fit in the 40 it is given, so the bar was never
            // going to be used -- and with a screen reader it is one more object
            // between the reader and the next control.
            where.BorderStyle = BorderStyle.None;
            where.BackColor = SystemColors.Control;
            where.SetBounds(14, 414, 632, 40);
            where.TabIndex = 2;

            close = new Button();
            close.Text = Localization.T("Btn.Close");
            close.AccessibleName = close.Text;
            close.SetBounds(546, 462, 100, 30);
            close.TabIndex = 3;
            close.DialogResult = DialogResult.Cancel;

            Controls.Add(lbl);
            Controls.Add(languages);
            Controls.Add(body);
            Controls.Add(where);
            Controls.Add(close);
            CancelButton = close;

            int start = IndexOfCode(preferCode);
            if (languages.Items.Count > 0) languages.SelectedIndex = start >= 0 ? start : 0;
            Shown += (s, e) => { try { languages.Focus(); } catch { } };
        }

        /// <summary>Every target language, the ones with rules first. Within each
        /// half the order of <see cref="TranslationLanguages.All"/> is kept, so a
        /// reader who knows where a language sits in the translate dialog finds it
        /// in the same relative place here.</summary>
        private void BuildOrder()
        {
            List<TranslationLanguages.Lang> rest = new List<TranslationLanguages.Lang>();
            foreach (TranslationLanguages.Lang l in TranslationLanguages.All)
            {
                if (TranslationRules.Has(l.Code)) order.Add(l);
                else rest.Add(l);
            }
            order.AddRange(rest);
        }

        private int IndexOfCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return -1;
            for (int i = 0; i < order.Count; i++)
                if (string.Equals(order[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private void ShowChosen()
        {
            int i = languages.SelectedIndex;
            if (i < 0 || i >= order.Count) return;
            string code = order[i].Code;
            // Straight off disk every time the language is chosen: a reader who
            // has just edited their copy in another program and come back here
            // expects to see what they wrote.
            TranslationRules.Reload();
            string text = TranslationRules.For(code);
            string file = TranslationRules.PathFor(code);

            if (text.Length == 0)
            {
                body.Text = Localization.T("Dialog.Rules.None");
                // Caret to the top here as well as below: a screen reader that
                // reads from the caret should start at the first word of the
                // message, not after its full stop.
                body.SelectionStart = 0;
                body.SelectionLength = 0;
                where.Text = string.Format(Localization.T("Dialog.Rules.Make"),
                                           TranslationRules.UserFolder, ShortCode(code) + ".rules");
                return;
            }
            // The box wants CRLF; a file written on another machine may not have it.
            body.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            body.SelectionStart = 0;
            body.SelectionLength = 0;
            bool mine = !string.IsNullOrEmpty(file)
                        && !string.IsNullOrEmpty(TranslationRules.UserFolder)
                        && file.StartsWith(TranslationRules.UserFolder, StringComparison.OrdinalIgnoreCase);
            where.Text = string.Format(Localization.T(mine ? "Dialog.Rules.FromYours" : "Dialog.Rules.From"),
                                       file, TranslationRules.UserFolder);
        }

        /// <summary>sr-Cyrl and sr-Latn both read sr.rules, so the file a reader
        /// would make for them is sr.rules too. The same cut as the loader's.</summary>
        private static string ShortCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int dash = code.IndexOfAny(new[] { '-', '_' });
            return dash > 0 ? code.Substring(0, dash).ToLowerInvariant() : code.ToLowerInvariant();
        }
    }
}
