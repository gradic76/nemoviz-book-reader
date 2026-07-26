using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>One rule of the user's speech dictionary, being written or edited.
    /// Every field is a labelled control a screen reader announces on Tab; nothing
    /// here needs a mouse. The rule is checked when OK is pressed — a pattern that
    /// cannot be used is said so here, not silently ignored while reading.</summary>
    public class DictRuleForm : Form
    {
        private readonly DictRule rule;

        private TextBox tbPattern, tbReplacement, tbComment;
        private ComboBox cmbMatch;
        private CheckBox chkCase, chkSkip, chkEnabled;

        /// <summary>The edited rule; valid when the dialog returns OK.</summary>
        public DictRule Result { get { return rule; } }

        public DictRuleForm(DictRule existing)
        {
            rule = existing != null ? existing.Copy() : new DictRule();

            this.Text = Localization.T(existing == null ? "Dict.Rule.TitleNew" : "Dict.Rule.TitleEdit");
            this.ClientSize = new Size(560, 330);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            int lx = 12, cx = 190, cw = 350, y = 16, tab = 0;

            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Field.Pattern"), lx, y + 4));
            tbPattern = MakeField(cx, y, cw, tab++, Localization.T("Dict.Field.Pattern"), rule.Pattern);
            this.Controls.Add(tbPattern);

            y += 34;
            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Field.Match"), lx, y + 4));
            cmbMatch = SettingsForm.MakeCombo(Localization.T("Dict.Field.Match"), cx, y, cw, tab++);
            cmbMatch.Items.Add(Localization.T("Dict.Match.WholeWord"));
            cmbMatch.Items.Add(Localization.T("Dict.Match.Anywhere"));
            cmbMatch.Items.Add(Localization.T("Dict.Match.Regex"));
            cmbMatch.SelectedIndex = (int)rule.Match;
            this.Controls.Add(cmbMatch);

            y += 34;
            chkCase = MakeCheck(Localization.T("Dict.Field.CaseSensitive"), cx, y, tab++, rule.CaseSensitive);
            this.Controls.Add(chkCase);

            y += 30;
            chkSkip = MakeCheck(Localization.T("Dict.Field.Skip"), cx, y, tab++, rule.Skip);
            chkSkip.CheckedChanged += (s, e) => UpdateEnabled();
            this.Controls.Add(chkSkip);

            y += 34;
            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Field.Replacement"), lx, y + 4));
            tbReplacement = MakeField(cx, y, cw, tab++, Localization.T("Dict.Field.Replacement"), rule.Replacement);
            this.Controls.Add(tbReplacement);

            y += 34;
            this.Controls.Add(SettingsForm.MakeLabel(Localization.T("Dict.Field.Comment"), lx, y + 4));
            tbComment = MakeField(cx, y, cw, tab++, Localization.T("Dict.Field.Comment"), rule.Comment);
            this.Controls.Add(tbComment);

            y += 34;
            chkEnabled = MakeCheck(Localization.T("Dict.Field.Enabled"), cx, y, tab++, rule.Enabled);
            this.Controls.Add(chkEnabled);

            Button ok = new Button();
            ok.Text = Localization.T("Btn.OK");
            ok.AccessibleName = Localization.T("Btn.OK");
            ok.Size = new Size(100, 32);
            ok.Location = new Point(cx, 268);
            ok.TabIndex = tab++;
            ok.Click += (s, e) => Confirm();

            Button cancel = new Button();
            cancel.Text = Localization.T("Btn.Cancel");
            cancel.AccessibleName = Localization.T("Btn.Cancel");
            cancel.Size = new Size(100, 32);
            cancel.Location = new Point(cx + 116, 268);
            cancel.TabIndex = tab++;
            cancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.AcceptButton = ok;
            this.CancelButton = cancel;
            UpdateEnabled();
        }

        private static TextBox MakeField(int x, int y, int w, int tab, string name, string value)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, 23);
            t.Text = value ?? "";
            t.AccessibleName = name;
            t.TabIndex = tab;
            return t;
        }

        private static CheckBox MakeCheck(string text, int x, int y, int tab, bool value)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AccessibleName = text;
            c.Location = new Point(x, y);
            c.Size = new Size(350, 24);
            c.Checked = value;
            c.TabIndex = tab;
            return c;
        }

        /// <summary>"Say nothing" has nothing to say, so the replacement goes
        /// quiet with it.</summary>
        private void UpdateEnabled()
        {
            SettingsForm.SetEnabled(!chkSkip.Checked, tbReplacement);
        }

        private void Confirm()
        {
            rule.Pattern = tbPattern.Text;
            rule.Replacement = tbReplacement.Text;
            rule.Comment = tbComment.Text;
            rule.Match = (DictMatch)Math.Max(0, cmbMatch.SelectedIndex);
            rule.CaseSensitive = chkCase.Checked;
            rule.Skip = chkSkip.Checked;
            rule.Enabled = chkEnabled.Checked;

            string problem = rule.Validate();
            if (problem != null)
            {
                MessageBox.Show(this, problem, Localization.T("Dict.Error.Title"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tbPattern.Focus();
                return;
            }
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
