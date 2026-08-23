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

        // What each hint is ABOUT, which is what the ? is called and what the
        // pop-up is titled. Usually the control's own name; see Attach.
        private static readonly Dictionary<Control, string> titles = new Dictionary<Control, string>();

        public static void Clear() { hints.Clear(); titles.Clear(); helpKeys.Clear(); }

        // The ? buttons this system created, so a layout pass can tell them from
        // the dialog's own buttons — theirs move, this one is pinned to a corner.
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
        /// itself, when the anchor can hold children — a GroupBox can, a
        /// CheckBox cannot). Used for hint text that used to sit in its own
        /// always-visible box beside a single control, e.g. Go To's "start
        /// playing" checkbox, which has no group of its own to carry a corner
        /// button.</summary>
        public static void Attach(Control anchor, string bodyKey, Control parent, Rectangle buttonBounds,
                                  string subject = null)
        {
            if (anchor == null || parent == null) return;

            // AN EMPTY HINT IS AN UNWRITTEN HINT, and gets no key (§10c's rule,
            // which until now was only half implemented). That rule was built for
            // a MISSING key — an unwritten one renders as the key itself, and
            // "Hint.Settings.General.0" is worse than no button. A key that is
            // present but EMPTY slipped through: Localization.T hands back the
            // empty string rather than the key, so the `?` was attached and
            // opened a window with nothing in it. Gordan cleared a hint's text in
            // docs\Help hints.txt on 2026-08-17 expecting the button to go with
            // it, which is the only reading that makes sense.
            if (string.IsNullOrWhiteSpace(Localization.T(bodyKey))) return;

            hints[anchor] = bodyKey;

            // A group names itself, and a control usually does too. But a caption
            // written as an instruction to the user makes a clumsy name for the
            // help behind it — "Help for Use sound processing" — so the caller
            // may say what the subject IS instead of what its switch does.
            string forName = subject;
            if (string.IsNullOrEmpty(forName)) forName = (anchor as GroupBox)?.Text;
            if (string.IsNullOrEmpty(forName)) forName = anchor.AccessibleName ?? anchor.Text ?? "";
            titles[anchor] = forName;

            DialogSkin.EnsureFonts();
            var b = new Button();
            b.Text = "?";
            b.AccessibleName = Localization.T("Hint.Button.Accessible", forName);
            b.SetBounds(buttonBounds.X, buttonBounds.Y, buttonBounds.Width, buttonBounds.Height);
            // Only the colours are the new look's; the key itself is an ordinary
            // Button in both, which is why the classic look gets the SAME help
            // keys rather than doing without them (Gordan, 2026-08-16).
            if (DialogSkin.Painting)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = NewPlayerSkin.Silk;
                b.FlatAppearance.BorderSize = 1;
                b.BackColor = Color.FromArgb(0x12, 0x18, 0x15);
                b.ForeColor = NewPlayerSkin.Lit;
            }
            // NOT FSilk. That is 11 pt, whose line box is about 20 units, and the
            // key is 22 with a 1-unit border on each side — so the glyph had 20
            // units to live in and its tail sat on the frame. Gordan, 2026-08-10:
            // "the ? in the hh buttons is cut off all over the dialogs." Bold at
            // 9 pt fits with room to spare and reads better small, and it changes
            // no layout at all, which a bigger key would have done in every
            // dialog at once.
            b.Font = new Font(DialogSkin.FSilk.FontFamily, 9f, FontStyle.Bold);
            // Last in its group, never first. BringToFront is needed so the key
            // draws above the sticker, but it also makes the button the first
            // CHILD — and WinForms breaks a TabIndex tie by child order, so a
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
        /// key never does nothing — the failure people actually notice is
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
        /// like it — none of which a window we drew ourselves would match.
        ///
        /// <para>A local file, so it works with no internet. If it is missing the
        /// user is told rather than left wondering why F1 did nothing.</para></summary>
        public static void OpenManual(IWin32Window owner)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);

                // THE MANUAL IS PER LANGUAGE, and it falls back rather than
                // failing. Help\<code>\index.html for the language in force,
                // then English, then the flat Help\index.html the app shipped
                // with before the manual was translated. A reader whose
                // language has no manual yet gets the English one, which is
                // the whole point of a chain: F1 must never lead nowhere, and
                // that was already this method's reason for existing.
                foreach (string page in new[] {
                             System.IO.Path.Combine(dir, "Help", Localization.CurrentLanguageCode, "index.html"),
                             System.IO.Path.Combine(dir, "Help", "en", "index.html"),
                             System.IO.Path.Combine(dir, "Help", "index.html") })
                {
                    if (System.IO.File.Exists(page))
                    {
                        System.Diagnostics.Process.Start(page);
                        return;
                    }
                }
            }
            catch { }
            MessageForm.ShowHint(owner, Localization.T("Dialog.Help.ComingSoon"),
                                 Localization.T("Dialog.Help.Title"));
        }

        /// <summary>About NBR — the window it will be, standing empty until
        /// somebody writes what goes in it (Gordan, 2026-08-03). It is wired now
        /// rather than left dead so the menu item leads somewhere: what is
        /// missing is the text, and that is obvious the moment it opens.
        ///
        /// <para>The same shape as every other page of words in the app —
        /// <see cref="TextHelpForm"/>, read-only and tabbable, which is what a
        /// screen reader can walk line by line.</para></summary>
        public static void ShowAbout(IWin32Window owner)
        {
            // THE RELEASE LABEL IS A STRING, THE BUILD DATE IS NOT (Gordan,
            // 2026-08-21). His convention: "Alpha" while it is internal, then
            // "Beta 1", "Beta 2", and from the first public release the DATE --
            // "Nemoviz Book Reader 26.08.21" -- because "applications with fifteen
            // decimals after the name" tell nobody anything. So the label is one
            // editable string and nothing computes it.
            //
            // The build date beneath it is computed, and it is not the same thing:
            // it is what a tester needs when they report a fault, and it is exactly
            // the field nobody remembers to bump by hand.
            string release = Localization.T("Dialog.About.Release");
            string built = "?";
            try
            {
                built = System.IO.File.GetLastWriteTime(
                            System.Reflection.Assembly.GetExecutingAssembly().Location)
                        .ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            string body = Localization.T("Dialog.About.Text", release, built,
                                         Localization.T("Dialog.About.Licence"));
            using (var f = new TextHelpForm(Localization.T("Dialog.About.Title"), body, true))
                f.ShowDialog(owner);
        }

        /// <summary>Check for update, as the reader asked for it.
        ///
        /// <para><b>Off the UI thread, and the reason is on record.</b> §11 has a
        /// bulk import that blocked the window for a minute and a file dialog
        /// that blocked it on a network read; a request to a service the machine
        /// may not be able to reach belongs in neither. So the window stays live
        /// and the answer arrives when it arrives.</para>
        ///
        /// <para>That leaves a silence of up to ten seconds with nothing on
        /// screen and nothing said, which for this audience is the whole
        /// problem — so the check announces that it has started. It is a
        /// transient fact and there is no state to sit and read, which is exactly
        /// what <see cref="ScreenReader.Announce"/> is for.</para></summary>
        public static void CheckForUpdate(Control owner)
        {
            if (owner == null) return;
            ScreenReader.Announce(owner, Localization.T("Update.Checking"));

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateCheck.Result r = UpdateCheck.Ask();
                try
                {
                    // The window can be gone by now -- ten seconds is long enough
                    // to close the Library -- and posting to a dead handle throws.
                    if (owner.IsDisposed || !owner.IsHandleCreated) return;
                    owner.BeginInvoke((Action)(() => Report(owner, r, true)));
                }
                catch { }
            });
        }

        /// <summary>The once-a-day check nobody asked for. Same request, and a
        /// deliberately quieter mouth: it speaks only when there is something to
        /// say, because a reader who did not ask has no use for "checked, all
        /// well" and less for "the check failed".</summary>
        public static void CheckForUpdateQuietly(Control owner, AppSettings settings)
        {
            if (owner == null || !UpdateCheck.DueNow(settings)) return;
            settings.NoteUpdateCheck();

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateCheck.Result r = UpdateCheck.Ask();
                if (r.Outcome != UpdateCheck.Outcome.Newer) return;
                try
                {
                    if (owner.IsDisposed || !owner.IsHandleCreated) return;
                    owner.BeginInvoke((Action)(() => Report(owner, r, false)));
                }
                catch { }
            });
        }

        private static void Report(Control owner, UpdateCheck.Result r, bool manual)
        {
            switch (r.Outcome)
            {
                case UpdateCheck.Outcome.Newer:
                    // A QUESTION, NOT A NOTICE. There is somewhere to go, and the
                    // reader is the one who decides whether to go now -- which
                    // matters more here than usual, since the automatic check can
                    // raise this in the middle of a book.
                    if (MessageForm.ShowConfirm(owner,
                            Localization.T("Update.Available", r.Latest),
                            Localization.T("Update.Title")))
                    {
                        try { System.Diagnostics.Process.Start(UpdateCheck.ReleasesPage); }
                        catch { MessageForm.ShowInfo(owner, UpdateCheck.ReleasesPage,
                                                     Localization.T("Update.Title")); }
                    }
                    break;

                case UpdateCheck.Outcome.UpToDate:
                    if (manual)
                        MessageForm.ShowInfo(owner, Localization.T("Update.UpToDate"),
                                             Localization.T("Update.Title"));
                    break;

                default:
                    if (manual)
                        MessageForm.ShowInfo(owner, Localization.T("Update.Failed"),
                                             Localization.T("Update.Title"));
                    break;
            }
        }

        private static void Show(Control anchor, Control returnTo)
        {
            string key;
            if (!hints.TryGetValue(anchor, out key)) return;
            string title;
            if (!titles.TryGetValue(anchor, out title) || string.IsNullOrEmpty(title))
            {
                title = (anchor as GroupBox)?.Text;
                if (string.IsNullOrEmpty(title)) title = anchor.AccessibleName ?? anchor.Text ?? "";
            }
            // One shared design for every "here is a sentence or two, and a way
            // out" dialog in the app (Gordan, 2026-07-29) — the hint pop-up is
            // simply MessageForm's info variant with this control's own text.
            MessageForm.ShowHint(anchor.FindForm(), Localization.T(key), title);
            // Focus goes back exactly where it came from, or the user is left
            // stranded in the middle of a dialog they did not move through.
            if (returnTo != null && returnTo.CanSelect && !returnTo.IsDisposed)
                returnTo.Focus();
        }
    }
}

