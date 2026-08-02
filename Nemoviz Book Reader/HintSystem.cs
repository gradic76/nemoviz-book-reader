using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Help where it is asked for. Every group carries a small <c>?</c> at its top
    /// left; pressing it opens one short explanation and nothing else. The old way
    /// â€” a hint box beside every control, all of them showing at once â€” cost space
    /// on every dialog and gave the user six explanations when they wanted one.
    ///
    /// <para>Two things make it work rather than merely look tidy. The button's
    /// <b>accessible name says which control it explains</b> ("Help for noise
    /// removal"), because a screen reader announcing "question mark, button" six
    /// times tells nobody anything. And <b>F1 opens the same text from wherever
    /// the focus already is</b>, so a keyboard user never has to travel to the
    /// button at all â€” the mouse route and the keyboard route reach the same
    /// place by different means.</para>
    /// </summary>
    internal static class HintSystem
    {
        // Which group explains what, and where its text lives.
        private static readonly Dictionary<Control, string> hints = new Dictionary<Control, string>();

        public static void Clear() { hints.Clear(); helpKeys.Clear(); }

        // The ? buttons this system created, so a layout pass can tell them from
        // the dialog's own buttons â€” theirs move, this one is pinned to a corner.
        private static readonly List<Button> helpKeys = new List<Button>();
        public static bool IsHelpKey(Button b) { return b != null && helpKeys.Contains(b); }

        /// <summary>Puts a ? on a group and remembers the text behind it. The
        /// button goes in the group's own top-right corner.</summary>
        public static void Attach(GroupBox g, string bodyKey)
        {
            if (g == null) return;
            Attach(g, bodyKey, g, new Rectangle(g.Width - 30, 4, 22, 22));
        }

        /// <summary>The general form: a ? for any control, wherever the caller's
        /// layout wants it, added to a PARENT the caller chooses (the anchor
        /// itself, when the anchor can hold children â€” a GroupBox can, a
        /// CheckBox cannot). Used for hint text that used to sit in its own
        /// always-visible box beside a single control, e.g. Go To's "start
        /// playing" checkbox, which has no group of its own to carry a corner
        /// button.</summary>
        public static void Attach(Control anchor, string bodyKey, Control parent, Rectangle buttonBounds)
        {
            if (anchor == null || parent == null) return;
            hints[anchor] = bodyKey;

            string forName = (anchor as GroupBox)?.Text;
            if (string.IsNullOrEmpty(forName)) forName = anchor.AccessibleName ?? anchor.Text ?? "";

            var b = new Button();
            b.Text = "?";
            b.AccessibleName = Localization.T("Hint.Button.Accessible", forName);
            b.SetBounds(buttonBounds.X, buttonBounds.Y, buttonBounds.Width, buttonBounds.Height);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = NewPlayerSkin.Silk;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = Color.FromArgb(0x12, 0x18, 0x15);
            b.ForeColor = NewPlayerSkin.Lit;
            b.Font = DialogSkin.FSilk;
            // Last in its group, never first. BringToFront is needed so the key
            // draws above the sticker, but it also makes the button the first
            // CHILD â€” and WinForms breaks a TabIndex tie by child order, so a
            // TabIndex of 0 here put the help key ahead of the settings it
            // explains. The dialog then opened with the reader announcing "Help
            // for Speech, button" instead of the first thing to choose, and Tab
            // hit a ? on the way into every group. A high index keeps the paint
            // and gives the order back.
            b.TabIndex = 900;
            b.Click += (s, e) => Show(anchor, b);
            parent.Controls.Add(b);
            b.BringToFront();
            helpKeys.Add(b);
        }

        /// <summary>F1 anywhere in the dialog: find the group the focus is sitting
        /// in and open its help. Walking up the parents means it works from a
        /// combo inside a group just as well as from the group itself.
        ///
        /// <para><b>Otherwise it opens the manual</b> (Gordan, 2026-07-31): F1
        /// means help in the player and in every window, and only a group that
        /// has something specific to say gets to answer instead. That way the
        /// key never does nothing â€” the failure people actually notice is
        /// pressing F1 and getting silence, not getting the wrong page.</para></summary>
        public static bool HandleF1(Form f)
        {
            Control c = f.ActiveControl;
            while (c != null)
            {
                if (hints.ContainsKey(c)) { Show(c, f.ActiveControl); return true; }
                c = c.Parent;
            }
            OpenManual(f);
            return true;
        }

        /// <summary>The manual, in the reader's OWN browser (Gordan's call, and
        /// the accessible one): they get heading navigation, find-on-page, their
        /// own fonts and colours, and their screen reader set up the way they
        /// like it â€” none of which a window we drew ourselves would match.
        ///
        /// <para>A local file, so it works with no internet. If it is missing the
        /// user is told rather than left wondering why F1 did nothing.</para></summary>
        public static void OpenManual(IWin32Window owner)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string page = System.IO.Path.Combine(dir, "Help", "index.html");
                if (System.IO.File.Exists(page))
                {
                    System.Diagnostics.Process.Start(page);
                    return;
                }
            }
            catch { }
            MessageForm.ShowHint(owner, Localization.T("Dialog.Help.ComingSoon"),
                                 Localization.T("Dialog.Help.Title"));
        }

        private static void Show(Control anchor, Control returnTo)
        {
            string key;
            if (!hints.TryGetValue(anchor, out key)) return;
            string title = (anchor as GroupBox)?.Text;
            if (string.IsNullOrEmpty(title)) title = anchor.AccessibleName ?? anchor.Text ?? "";
            // One shared design for every "here is a sentence or two, and a way
            // out" dialog in the app (Gordan, 2026-07-29) â€” the hint pop-up is
            // simply MessageForm's info variant with this control's own text.
            MessageForm.ShowHint(anchor.FindForm(), Localization.T(key), title);
            // Focus goes back exactly where it came from, or the user is left
            // stranded in the middle of a dialog they did not move through.
            if (returnTo != null && returnTo.CanSelect && !returnTo.IsDisposed)
                returnTo.Focus();
        }
    }
}

