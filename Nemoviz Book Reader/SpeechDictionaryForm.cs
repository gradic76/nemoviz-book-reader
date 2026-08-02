using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The user's speech dictionary: what NBR should say instead of what the book
    /// says. Empty until they fill it — nothing is supplied and nothing is
    /// guessed.
    ///
    /// <para>Three scopes to choose between, because a rule's reason differs: the
    /// voice that mispronounces something, the language it belongs to, or the
    /// user's own habit everywhere. The list shows one scope at a time and each is
    /// its own file, so a dictionary can be backed up or passed to someone else.</para>
    ///
    /// <para>The <b>Try it</b> box at the bottom is not decoration: without it a
    /// blind user would have to find the right place in a book to hear whether a
    /// rule works. Type a sentence, press the button, and the result is read out —
    /// <b>only</b> read out (Gordan, 2026-08-03). A second box showing the same
    /// thing in writing said nothing the ear had not already been told, and the
    /// dictionary changes what is <i>spoken</i> and nothing else: the text on
    /// screen and in braille is the author's either way.</para>
    ///
    /// <para><b>The scopes are the language and its voices</b>, and they are named
    /// from where the reader stands: "Everything I read in Croatian", "Everything
    /// I read with Dragana". The language is the one picked under Speech; the
    /// voices are the ones that speak it, all of them, not only the one Settings
    /// has chosen — fixing what a voice gets wrong should not first require making
    /// it the voice you read with.</para>
    /// </summary>
    public class SpeechDictionaryForm : Form
    {
        private readonly string language;

        private ComboBox cmbScope;
        private ListView list;
        private TextBox tbTry;
        private Button btnAdd, btnEdit, btnRemove, btnTry, btnHelp;

        // One entry per line of the scope combo: the voice that line stands for,
        // or "" for the first line, which is the language itself.
        private readonly List<string> scopeVoices = new List<string>();

        private SpeechDictionary current;             // the file being edited
        private readonly List<DictRule> working = new List<DictRule>();

        // Reads a line back, in a named voice — the voice of the SCOPE being
        // edited, not the one Settings is set to, or a rule written for Ivan
        // would be tried out in Dragana's mouth.
        private readonly Action<string, string> speak;

        public SpeechDictionaryForm(string language, IList<string> voices,
                                    Action<string, string> speak = null)
        {
            this.language = language ?? "";
            this.speak = speak;

            this.Text = Localization.T("Dict.Title");
            this.ClientSize = new Size(720, 520);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            int tab = 0;

            // What this window is for, in plain sight rather than behind a ? —
            // the reader is here on purpose and may never have met a speech
            // dictionary before. Read-only and TABBABLE, which is what a hint is
            // in this app: a Label is invisible to a screen reader driven by Tab.
            TextBox hint = new TextBox();
            hint.Multiline = true;
            hint.ReadOnly = true;
            hint.Text = Localization.T("Settings.TextBooks.Dictionary.Hint");
            hint.AccessibleName = Localization.T("GoTo.Hint.Accessible");
            hint.Location = new Point(12, 12);
            hint.Size = new Size(696, 68);
            hint.TabIndex = tab++;
            this.Controls.Add(hint);

            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Scope"), 12, 100));
            cmbScope = SettingsForm.MakeCombo(Localization.T("Dict.Scope"), 150, 96, 400, tab++);
            // The language first, then every voice that speaks it. There is no
            // "all languages" line: the one dictionary that would sit behind it
            // applies to a language the reader is not looking at, and naming it
            // "everything" while standing inside one language read as a promise
            // the file does not keep. When Settings is on "all other languages"
            // there IS no language to name, and then that same file is exactly
            // what this line means.
            scopeVoices.Add("");
            cmbScope.Items.Add(LanguageDetector.Primary(this.language).Length > 0
                ? Localization.T("Dict.Scope.Language", LanguageDetector.DisplayName(this.language))
                : Localization.T("Dict.Scope.Global"));
            if (voices != null)
                foreach (string v in voices)
                {
                    if (string.IsNullOrEmpty(v) || scopeVoices.Contains(v)) continue;
                    scopeVoices.Add(v);
                    cmbScope.Items.Add(Localization.T("Dict.Scope.Voice", v));
                }
            cmbScope.SelectedIndexChanged += (s, e) => LoadScope();
            this.Controls.Add(cmbScope);

            list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Location = new Point(12, 130);
            list.Size = new Size(696, 220);
            list.TabIndex = tab++;
            list.AccessibleName = Localization.T("Dict.List.Accessible");
            list.Columns.Add(Localization.T("Dict.Column.Pattern"), 180);
            list.Columns.Add(Localization.T("Dict.Column.Says"), 180);
            list.Columns.Add(Localization.T("Dict.Column.Match"), 110);
            list.Columns.Add(Localization.T("Dict.Column.Case"), 60);
            list.Columns.Add(Localization.T("Dict.Column.State"), 70);
            list.Columns.Add(Localization.T("Dict.Column.Comment"), 90);
            list.DoubleClick += (s, e) => EditSelected();
            list.KeyDown += List_KeyDown;
            this.Controls.Add(list);

            // Add, Edit, Remove — and no Move up / Move down (Gordan, 2026-08-03).
            // Order still decides what a rule sees, since each one works on what
            // the ones before it left behind, but rules that step on each other
            // are rare enough that two buttons for arranging them cost more room
            // than they were worth. To place one differently now: remove it and
            // add it again.
            int bx = 12, by = 358;
            btnAdd = MakeButton("Dict.Add", bx, by, tab++, () => AddRule());
            btnEdit = MakeButton("Dict.Edit", bx + 116, by, tab++, () => EditSelected());
            btnRemove = MakeButton("Dict.Remove", bx + 232, by, tab++, () => RemoveSelected());
            this.Controls.Add(btnAdd); this.Controls.Add(btnEdit); this.Controls.Add(btnRemove);

            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Try"), 12, 404));
            tbTry = new TextBox();
            tbTry.Location = new Point(150, 400);
            tbTry.Size = new Size(430, 23);
            tbTry.AccessibleName = Localization.T("Dict.Try");
            tbTry.TabIndex = tab++;
            this.Controls.Add(tbTry);

            btnTry = MakeButton("Dict.TryButton", 590, 398, tab++, () => TryIt());
            this.Controls.Add(btnTry);

            // What regular expressions are, and Gordan's advice about them, which
            // is to leave them alone unless you already know. It is prose now
            // rather than a table of symbols, so it wraps.
            btnHelp = MakeButton("Dict.Help", 12, 466, tab++, () =>
            {
                using (var h = new TextHelpForm(Localization.T("Dict.Help.Title"),
                                                Localization.T("Dict.Help.Text"), true))
                    h.ShowDialog(this);
            });
            this.Controls.Add(btnHelp);

            Button ok = MakeButton("Btn.OK", 484, 466, tab++, () => { Save(); DialogResult = DialogResult.OK; Close(); });
            Button cancel = MakeButton("Btn.Cancel", 600, 466, tab++, null);
            cancel.DialogResult = DialogResult.Cancel;
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;

            cmbScope.SelectedIndex = 0;

            // The hint is first on the page and first in the tab order, but the
            // window does not OPEN on it: a reader who came here to add a rule
            // would sit through four sentences every time. It is one Shift+Tab
            // away for whoever wants it.
            this.Shown += (s, e) => { this.ActiveControl = cmbScope; };
        }

        private Button MakeButton(string key, int x, int y, int tab, Action click)
        {
            Button b = new Button();
            b.Text = Localization.T(key);
            b.AccessibleName = Localization.T(key);
            b.Location = new Point(x, y);
            b.Size = new Size(108, 32);
            b.TabIndex = tab;
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { EditSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { ToggleSelected(); e.Handled = true; e.SuppressKeyPress = true; }
        }

        /// <summary>Switching scope keeps what was edited: the current file is
        /// written first, so nothing is lost by looking at another one.</summary>
        private void LoadScope()
        {
            Save();
            string v = ScopeVoice();
            // No language to name means Settings is on "all other languages", and
            // ForLanguage answers null there — which is precisely when the global
            // file is the one being asked for.
            current = v.Length > 0
                ? SpeechDictionaries.ForVoice(v)
                : (SpeechDictionaries.ForLanguage(language) ?? SpeechDictionaries.Global);

            working.Clear();
            foreach (DictRule r in current.Rules) working.Add(r.Copy());
            Refresh(working.Count > 0 ? 0 : -1);
        }

        private void Refresh(int select)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (DictRule r in working)
            {
                var item = new ListViewItem(r.Pattern);
                item.SubItems.Add(r.Skip ? Localization.T("Dict.Field.Skip") : r.Replacement);
                item.SubItems.Add(Localization.T(r.Match == DictMatch.Regex ? "Dict.Match.Regex"
                                  : r.Match == DictMatch.Anywhere ? "Dict.Match.Anywhere" : "Dict.Match.WholeWord"));
                item.SubItems.Add(Localization.T(r.CaseSensitive ? "Prop.On" : "Prop.Off"));
                item.SubItems.Add(Localization.T(r.Enabled ? "Dict.State.On" : "Dict.State.Off"));
                item.SubItems.Add(r.Comment);
                list.Items.Add(item);
            }
            list.EndUpdate();
            if (select >= 0 && select < list.Items.Count)
            {
                list.Items[select].Selected = true;
                list.Items[select].Focused = true;
                list.EnsureVisible(select);
            }
        }

        private int Selected
        {
            get { return list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1; }
        }

        private void AddRule()
        {
            using (var dlg = new DictRuleForm(null))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    working.Add(dlg.Result);
                    Refresh(working.Count - 1);
                }
        }

        private void EditSelected()
        {
            int i = Selected;
            if (i < 0) return;
            using (var dlg = new DictRuleForm(working[i]))
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    working[i] = dlg.Result;
                    Refresh(i);
                }
        }

        private void RemoveSelected()
        {
            int i = Selected;
            if (i < 0) return;
            working.RemoveAt(i);
            Refresh(Math.Min(i, working.Count - 1));
        }

        /// <summary>Space turns a rule off and on again — quicker than editing it
        /// when trying out whether it is the one causing trouble.</summary>
        private void ToggleSelected()
        {
            int i = Selected;
            if (i < 0) return;
            working[i].Enabled = !working[i].Enabled;
            Refresh(i);
        }

        /// <summary>The voice the chosen scope belongs to, or "" when the scope is
        /// the language itself.</summary>
        private string ScopeVoice()
        {
            int i = cmbScope != null ? cmbScope.SelectedIndex : -1;
            return (i >= 0 && i < scopeVoices.Count) ? scopeVoices[i] : "";
        }

        /// <summary>Runs the rules as they stand over a sentence the user types —
        /// the rules being edited, not the saved ones, so a rule can be judged
        /// before committing to it. The result is spoken and not written down:
        /// the dictionary changes speech and only speech.</summary>
        private void TryIt()
        {
            var temp = new SpeechDictionary(current != null ? current.Path : "");
            temp.Rules.AddRange(working);
            string result = temp.Apply(tbTry.Text ?? "");
            if (speak != null && result.Length > 0) speak(ScopeVoice(), result);
        }

        private void Save()
        {
            if (current == null) return;
            current.Rules.Clear();
            current.Rules.AddRange(working);
            current.Save();
        }
    }
}
