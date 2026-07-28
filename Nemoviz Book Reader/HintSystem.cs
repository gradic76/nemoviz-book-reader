using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Help where it is asked for. Every group carries a small <c>?</c> at its top
    /// left; pressing it opens one short explanation and nothing else. The old way
    /// — a hint box beside every control, all of them showing at once — cost space
    /// on every dialog and gave the user six explanations when they wanted one.
    ///
    /// <para>Two things make it work rather than merely look tidy. The button's
    /// <b>accessible name says which control it explains</b> ("Help for noise
    /// removal"), because a screen reader announcing "question mark, button" six
    /// times tells nobody anything. And <b>F1 opens the same text from wherever
    /// the focus already is</b>, so a keyboard user never has to travel to the
    /// button at all — the mouse route and the keyboard route reach the same
    /// place by different means.</para>
    /// </summary>
    internal static class HintSystem
    {
        // Which group explains what, and where its text lives.
        private static readonly Dictionary<Control, string> hints = new Dictionary<Control, string>();

        public static void Clear() { hints.Clear(); }

        /// <summary>Puts a ? on a group and remembers the text behind it.</summary>
        public static void Attach(GroupBox g, string bodyKey)
        {
            if (g == null) return;
            hints[g] = bodyKey;

            var b = new Button();
            b.Text = "?";
            b.AccessibleName = Localization.T("Hint.Button.Accessible", g.Text);
            b.SetBounds(g.Width - 30, 4, 22, 22);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = NewPlayerSkin.Silk;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = Color.FromArgb(0x12, 0x18, 0x15);
            b.ForeColor = NewPlayerSkin.Lit;
            b.Font = DialogSkin.FSilk;
            // Last in its group, never first. BringToFront is needed so the key
            // draws above the sticker, but it also makes the button the first
            // CHILD — and WinForms breaks a TabIndex tie by child order, so a
            // TabIndex of 0 here put the help key ahead of the settings it
            // explains. The dialog then opened with the reader announcing "Help
            // for Speech, button" instead of the first thing to choose, and Tab
            // hit a ? on the way into every group. A high index keeps the paint
            // and gives the order back.
            b.TabIndex = 900;
            b.Click += (s, e) => Show(g, b);
            g.Controls.Add(b);
            b.BringToFront();
        }

        /// <summary>F1 anywhere in the dialog: find the group the focus is sitting
        /// in and open its help. Walking up the parents means it works from a
        /// combo inside a group just as well as from the group itself.</summary>
        public static bool HandleF1(Form f)
        {
            Control c = f.ActiveControl;
            while (c != null)
            {
                if (hints.ContainsKey(c)) { Show(c, f.ActiveControl); return true; }
                c = c.Parent;
            }
            return false;
        }

        private static void Show(Control group, Control returnTo)
        {
            string key;
            if (!hints.TryGetValue(group, out key)) return;
            string title = group is GroupBox ? group.Text : "";
            using (var h = new HintForm(title, Localization.T(key)))
                h.ShowDialog(group.FindForm());
            // Focus goes back exactly where it came from, or the user is left
            // stranded in the middle of a dialog they did not move through.
            if (returnTo != null && returnTo.CanSelect && !returnTo.IsDisposed)
                returnTo.Focus();
        }
    }

    /// <summary>One explanation, and a way out. The text is a read-only multiline
    /// TextBox rather than a label because that is the shape a screen reader can
    /// walk line by line — the pattern this app settled on long ago.</summary>
    internal sealed class HintForm : Form
    {
        public HintForm(string title, string body)
        {
            DialogSkin.EnsureFonts();
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 240);
            BackColor = NewPlayerSkin.PanelMid;

            var t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.WordWrap = true;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = NewPlayerSkin.Glass;
            t.ForeColor = NewPlayerSkin.Lit;
            t.Font = DialogSkin.FBody;
            t.SetBounds(16, 16, 408, 168);
            t.TabIndex = 0;
            t.AccessibleName = title;
            t.Text = body;
            t.GotFocus += (s, e) => t.Select(0, 0);

            var close = new Button();
            close.Text = Localization.T("Hint.Close");
            close.AccessibleName = Localization.T("Hint.Close");
            close.SetBounds(312, 196, 112, 32);
            close.TabIndex = 1;
            close.DialogResult = DialogResult.OK;
            DialogSkin.AsKey(close, new Rectangle(312, 196, 112, 32));

            Controls.Add(t);
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;   // Esc closes it too
        }
    }
}
