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
    /// rule works. Type a sentence, press the button, and the result is both shown
    /// and read out.</para>
    /// </summary>
    public class SpeechDictionaryForm : Form
    {
        private readonly string language, voice;

        private ComboBox cmbScope;
        private ListView list;
        private TextBox tbTry, tbResult;
        private Button btnAdd, btnEdit, btnRemove, btnUp, btnDown, btnTry, btnHelp;

        private SpeechDictionary current;             // the file being edited
        private readonly List<DictRule> working = new List<DictRule>();
        private readonly Action<string> speak;        // optional "read it out" hook

        public SpeechDictionaryForm(string language, string voice, Action<string> speak = null)
        {
            this.language = language ?? "";
            this.voice = voice ?? "";
            this.speak = speak;

            this.Text = Localization.T("Dict.Title");
            this.ClientSize = new Size(720, 520);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            int tab = 0;

            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Scope"), 12, 16));
            cmbScope = SettingsForm.MakeCombo(Localization.T("Dict.Scope"), 150, 12, 400, tab++);
            cmbScope.Items.Add(Localization.T("Dict.Scope.Global"));
            if (LanguageDetector.Primary(this.language).Length > 0)
                cmbScope.Items.Add(Localization.T("Dict.Scope.Language",
                    LanguageDetector.DisplayName(this.language)));
            if (this.voice.Length > 0)
                cmbScope.Items.Add(Localization.T("Dict.Scope.Voice", this.voice));
            cmbScope.SelectedIndexChanged += (s, e) => LoadScope();
            this.Controls.Add(cmbScope);

            list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Location = new Point(12, 50);
            list.Size = new Size(696, 260);
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

            int bx = 12, by = 320;
            btnAdd = MakeButton("Dict.Add", bx, by, tab++, () => AddRule());
            btnEdit = MakeButton("Dict.Edit", bx + 116, by, tab++, () => EditSelected());
            btnRemove = MakeButton("Dict.Remove", bx + 232, by, tab++, () => RemoveSelected());
            btnUp = MakeButton("Dict.Up", bx + 348, by, tab++, () => MoveRule(-1));
            btnDown = MakeButton("Dict.Down", bx + 464, by, tab++, () => MoveRule(1));
            this.Controls.Add(btnAdd); this.Controls.Add(btnEdit); this.Controls.Add(btnRemove);
            this.Controls.Add(btnUp); this.Controls.Add(btnDown);

            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Try"), 12, 368));
            tbTry = new TextBox();
            tbTry.Location = new Point(150, 364);
            tbTry.Size = new Size(430, 23);
            tbTry.AccessibleName = Localization.T("Dict.Try");
            tbTry.TabIndex = tab++;
            this.Controls.Add(tbTry);

            btnTry = MakeButton("Dict.TryButton", 590, 362, tab++, () => TryIt());
            this.Controls.Add(btnTry);

            // The regular-expression primer. Only a page of text, but a rule
            // written from memory is where a dictionary goes wrong first.
            btnHelp = MakeButton("Dict.Help", 12, 466, tab++, () =>
            {
                using (var h = new TextHelpForm(Localization.T("Dict.Help.Title"),
                                                Localization.T("Dict.Help.Text")))
                    h.ShowDialog(this);
            });
            this.Controls.Add(btnHelp);

            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Result"), 12, 402));
            tbResult = new TextBox();
            tbResult.Location = new Point(150, 398);
            tbResult.Size = new Size(558, 23);
            tbResult.ReadOnly = true;
            tbResult.AccessibleName = Localization.T("Dict.Result");
            tbResult.TabIndex = tab++;
            this.Controls.Add(tbResult);

            Button ok = MakeButton("Btn.OK", 484, 466, tab++, () => { Save(); DialogResult = DialogResult.OK; Close(); });
            Button cancel = MakeButton("Btn.Cancel", 600, 466, tab++, null);
            cancel.DialogResult = DialogResult.Cancel;
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;

            cmbScope.SelectedIndex = 0;
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
            string label = cmbScope.SelectedItem as string ?? "";
            if (label == Localization.T("Dict.Scope.Global")) current = SpeechDictionaries.Global;
            else if (voice.Length > 0 && label == Localization.T("Dict.Scope.Voice", voice))
                current = SpeechDictionaries.ForVoice(voice);
            else current = SpeechDictionaries.ForLanguage(language) ?? SpeechDictionaries.Global;

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

        private void MoveRule(int delta)
        {
            int i = Selected;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= working.Count) return;
            DictRule r = working[i];
            working[i] = working[j];
            working[j] = r;
            Refresh(j);
        }

        /// <summary>Runs the rules as they stand over a sentence the user types —
        /// the rules being edited, not the saved ones, so a rule can be judged
        /// before committing to it.</summary>
        private void TryIt()
        {
            var temp = new SpeechDictionary(current != null ? current.Path : "");
            temp.Rules.AddRange(working);
            string result = temp.Apply(tbTry.Text ?? "");
            tbResult.Text = result;
            if (speak != null && result.Length > 0) speak(result);
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
