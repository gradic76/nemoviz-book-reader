using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>What NBR tells a translation service about the language it is
    /// writing -- on screen, where a reader can read it, and now where they can
    /// make one.
    ///
    /// <para><b>Why it exists</b> (Gordan, 2026-09-01): "Korisnik fakticki ne zna
    /// sto player salje servisu." The rules were compiled into the program until
    /// that day and could not be inspected at all.</para>
    ///
    /// <para><b>ADD and EDIT, and one folder behind them</b> (Gordan, 2026-09-02).
    /// The first version shipped a copy of each rulebook beside the program and
    /// read the reader's copy in preference to it; he threw the precedence out.
    /// Rules now live in the reader's own folder and nowhere else, which leaves
    /// exactly two things this window has to be able to do: make a file for a
    /// language that has none, and open the one that exists.</para>
    ///
    /// <para><b>EDIT opens Notepad by name, not the shell.</b> A .rules file has no
    /// registered handler, so ShellExecute would put up Windows' "How do you want
    /// to open this file?" chooser -- which for a reader who cannot see it is a
    /// dead end in the middle of a task. Notepad is on every Windows there is and
    /// is the most screen-reader-friendly editor on the machine.</para>
    ///
    /// <para><b>THREE states, not two, and the middle one is why Add is worth
    /// having.</b> A file holding nothing but its own header sends no rules -- so
    /// "there are no rules for this language" and "there is no file for this
    /// language" would read identically, and a reader who had just pressed Add
    /// would be told that nothing had happened.</para>
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
    /// treba vidjeti imaju li uopce smisla." A '#' in front of a line does the same
    /// job inside the file, for whoever wants it.</para>
    ///
    /// <para><b>Shape</b>, and it follows <see cref="ServicesForm"/> deliberately,
    /// because a reader who has met that window has met this one: a chooser, a
    /// read-only TABBABLE field with the whole text, and a line saying which file
    /// it came from. The field is never a Label -- a reader driven by Tab never
    /// visits one, and here the text is the entire point of the window.</para>
    ///
    /// <para><b>Languages that HAVE a rules file come first.</b> The list is every
    /// language NBR can translate into, as Gordan asked. Ordinary order would mean
    /// arrowing past a hundred entries to reach the only ones with anything to
    /// show, which is worse with a screen reader than with eyes.</para></summary>
    internal sealed class TranslationRulesForm : Form
    {
        private readonly List<TranslationLanguages.Lang> order = new List<TranslationLanguages.Lang>();
        private readonly ComboBox languages;
        private readonly TextBox body, where;
        private readonly Button add, edit, close;

        public TranslationRulesForm(string preferCode)
        {
            Text = Localization.T("Dialog.Rules.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(660, 500);

            // Make the folder before naming it: every line this window prints about
            // a rules file points at it, and Explorer answers "not found" for a
            // folder nobody has created.
            try { Directory.CreateDirectory(TranslationRules.Folder); } catch { }

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

            // WHICH FILE, AND WHERE. A field rather than a label for the same reason
            // as the body: Tab has to reach it, because for a reader who wants to
            // change the rules this line is the instruction. No scrollbar -- two
            // lines is the most it holds and both fit in the 40 it is given.
            where = new TextBox();
            where.Multiline = true;
            where.ReadOnly = true;
            where.TabStop = true;
            where.BorderStyle = BorderStyle.None;
            where.BackColor = SystemColors.Control;
            where.SetBounds(14, 414, 632, 40);
            where.TabIndex = 2;

            add = new Button();
            add.Text = Localization.T("Dialog.Rules.Add");
            add.AccessibleName = add.Text;
            add.SetBounds(14, 462, 130, 30);
            add.TabIndex = 3;
            add.Click += (s, e) => AddForChosen();

            edit = new Button();
            edit.Text = Localization.T("Dialog.Rules.Edit");
            edit.AccessibleName = edit.Text;
            edit.SetBounds(154, 462, 130, 30);
            edit.TabIndex = 4;
            edit.Click += (s, e) => EditChosen();

            close = new Button();
            close.Text = Localization.T("Btn.Close");
            close.AccessibleName = close.Text;
            close.SetBounds(546, 462, 100, 30);
            close.TabIndex = 5;
            close.DialogResult = DialogResult.Cancel;

            Controls.Add(lbl);
            Controls.Add(languages);
            Controls.Add(body);
            Controls.Add(where);
            Controls.Add(add);
            Controls.Add(edit);
            Controls.Add(close);
            CancelButton = close;

            int start = IndexOfCode(preferCode);
            if (languages.Items.Count > 0) languages.SelectedIndex = start >= 0 ? start : 0;
            Shown += (s, e) => { try { languages.Focus(); } catch { } };
        }

        /// <summary>Every target language, the ones with a rules file first. Within
        /// each half the order of <see cref="TranslationLanguages.All"/> is kept, so
        /// a reader who knows where a language sits in the translate dialog finds it
        /// in the same relative place here.</summary>
        private void BuildOrder()
        {
            List<TranslationLanguages.Lang> rest = new List<TranslationLanguages.Lang>();
            foreach (TranslationLanguages.Lang l in TranslationLanguages.All)
            {
                if (TranslationRules.FileExists(l.Code)) order.Add(l);
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

        private TranslationLanguages.Lang Chosen()
        {
            int i = languages.SelectedIndex;
            return i < 0 || i >= order.Count ? null : order[i];
        }

        private void ShowChosen()
        {
            TranslationLanguages.Lang l = Chosen();
            if (l == null) return;
            // Straight off disk every time the language is chosen: a reader who has
            // just edited the file in another program and come back here expects to
            // see what they wrote.
            TranslationRules.Reload();
            bool exists = TranslationRules.FileExists(l.Code);
            string text = TranslationRules.For(l.Code);
            string file = TranslationRules.PathFor(l.Code);

            if (!exists)
            {
                body.Text = Localization.T("Dialog.Rules.None");
                where.Text = string.Format(Localization.T("Dialog.Rules.Make"), TranslationRules.Folder);
            }
            else
            {
                body.Text = text.Length == 0
                    ? Localization.T("Dialog.Rules.Empty")
                    : text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                where.Text = string.Format(Localization.T("Dialog.Rules.File"), file);
                // "o tome cete biti obavijesteni" (Gordan, 2026-09-02). This window is
                // the channel he agreed to, and it is the right one: whoever edited
                // their rules is the person who comes back here.
                if (TranslationRules.HasPending(l.Code))
                    where.Text += Environment.NewLine + string.Format(
                        Localization.T("Dialog.Rules.Newer"), TranslationRules.PendingPath(l.Code));
            }
            body.SelectionStart = 0;
            body.SelectionLength = 0;
            add.Enabled = !exists;
            edit.Enabled = exists;
        }

        private void AddForChosen()
        {
            TranslationLanguages.Lang l = Chosen();
            if (l == null || TranslationRules.FileExists(l.Code)) return;
            // The instructions inside the file are in the language the rules are
            // FOR, because that is what whoever writes them is about to write. A
            // target language NBR is not localized into falls back to the interface
            // language -- see Localization.StringFor.
            string instructions = Localization.StringFor(l.Code, "Dialog.Rules.NewHeader");
            if (!TranslationRules.CreateEmpty(l.Code, instructions, l.Native))
            {
                MessageForm.ShowInfo(this, string.Format(Localization.T("Dialog.Rules.Make"),
                                     TranslationRules.Folder), Text);
                return;
            }
            // The language has a file now, so it belongs at the top of the list --
            // rebuilt rather than nudged, or the order would drift from what
            // BuildOrder means by it.
            string code = l.Code;
            order.Clear();
            languages.Items.Clear();
            BuildOrder();
            foreach (TranslationLanguages.Lang x in order) languages.Items.Add(x.DisplayName);
            int i = IndexOfCode(code);
            languages.SelectedIndex = i >= 0 ? i : 0;
            ShowChosen();
            try { edit.Focus(); } catch { }
        }

        private void EditChosen()
        {
            TranslationLanguages.Lang l = Chosen();
            if (l == null) return;
            string path = TranslationRules.PathFor(l.Code);
            if (path.Length == 0 || !File.Exists(path)) return;
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { MessageForm.ShowInfo(this, ex.Message, Text); }
        }
    }
}
