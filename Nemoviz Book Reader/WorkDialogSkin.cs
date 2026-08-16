using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    internal sealed class GoToParts
    {
        public ListBox List;
        public CheckBox AutoPlay;
        public TextBox AutoPlayHint;
        public Button OK, Cancel;
    }

    internal sealed class BookmarksParts
    {
        public ListBox List;
        public Button Delete, OK, Cancel;
    }

    internal sealed class TimerParts
    {
        public GroupBox Duration, Action;
        /// <summary>The bookmark switch, which belongs to neither group and so
        /// has to be placed by name. Added 2026-08-07 — the skin lays this dialog
        /// out by hand, so a control it has never heard of stays wherever the
        /// classic layout left it, which in the new look is off the glass.</summary>
        public CheckBox Bookmark;
        public Button Start, Cancel;
    }

    /// <summary>
    /// The player's working dialogs — Go To, Manage Bookmarks, Sleep Timer, the
    /// archive password prompt — wearing the same casing as Properties, Settings
    /// and the Library, at a size that fits what each one actually holds rather
    /// than borrowing the big dialogs' 960×640.
    ///
    /// <para><b>Two size families, not four, and not one</b> (Gordan, 2026-07-29).
    /// Go To and Manage Bookmarks are lists that want real height; Sleep Timer and
    /// the password prompt are a handful of controls that would rattle around in
    /// that much room. One size for all four would force the short ones to carry
    /// empty space they do not need; four unique sizes would mean four things to
    /// learn. Two families is the compromise, and the size is learned once per
    /// family rather than once per dialog.</para>
    ///
    /// <para><b>Anchored to a corner of the player, not centered</b> — see <see
    /// cref="DialogSkin.AnchorToOwner"/>. Large family settles bottom-right, small
    /// family bottom-left, so the two families also occupy their own part of the
    /// screen and the split reads as "kinds of dialog", not just "sizes of
    /// dialog".</para>
    /// </summary>
    internal static class WorkDialogSkin
    {
        internal const int LargeW = 580, LargeH = 600;
        // Sized to the TALLER of the two small dialogs (the timer's two groups
        // plus a button row), so the family fits its worst case rather than
        // leaving the timer to rattle around in a square. The password prompt is
        // shorter and carries the slack — that is the cost of one size per
        // family, and it is the cheaper way round.
        internal const int SmallW = 420, SmallH = 360;

        /// <summary>The Sleep Timer's own height. It outgrew the shared one when
        /// the bookmark switch arrived: 12 + 150 (duration) + 12 + 110 (action) +
        /// 14 + 24 puts the switch at 322, and the buttons on a 360-high dialog
        /// start at 312 — they would have overlapped. Its own constant rather
        /// than a bigger SmallH, because Manage Bookmarks and the password prompt
        /// share that one and neither grew.</summary>
        private const int TimerH = 396;
        /// <summary>The same number, for the classic layout — so the two looks
        /// cannot drift apart by one of them being edited.</summary>
        internal static int TimerHeight { get { return TimerH; } }
        private const int Margin = 12;

        public static void ApplyGoTo(GoToForm f)
        {
            GoToParts p = f.SkinParts;
            if (p == null || p.List == null) return;

            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, LargeW, LargeH);
            DialogSkin.AnchorToOwner(f, DialogAnchor.BottomRight);

            int buttonsY = LargeH - Margin - DialogSkin.ButtonH;
            int checkY = buttonsY - 12 - 24;
            int listBottom = checkY - 12;

            p.List.Font = DialogSkin.FBody;
            if (DialogSkin.Painting)
            {
                p.List.BorderStyle = BorderStyle.None;
                p.List.BackColor = NewPlayerSkin.Glass;
                p.List.ForeColor = NewPlayerSkin.Lit;
            }
            p.List.SetBounds(Margin, Margin, LargeW - 2 * Margin, listBottom - Margin);

            // The always-on hint box is gone — the checkbox gets a ? like every
            // other hinted control does now, and F1 reaches the same text.
            int qX = LargeW - Margin - 22;
            if (p.AutoPlayHint != null)
            {
                p.AutoPlayHint.Visible = false;
                p.AutoPlayHint.TabStop = false;
            }
            p.AutoPlay.SetBounds(Margin, checkY, qX - Margin - 8, 24);
            DialogSkin.OnGlass(p.AutoPlay);
            HintSystem.Clear();
            HintSystem.Attach(p.AutoPlay, "GoTo.AutoPlay.Hint", f, new Rectangle(qX, checkY - 1, 22, 22));

            DialogSkin.AsKey(p.Cancel, new Rectangle(LargeW - Margin - DialogSkin.ButtonW, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(LargeW - Margin - 2 * DialogSkin.ButtonW - 12, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            // Re-parenting is not needed here (nothing changes containers), but
            // AcceptButton/CancelButton still point at real Button instances that
            // never moved — only their bounds and paint changed — so they need no
            // re-binding. Kept explicit anyway, since a future change that DOES
            // re-parent these should not have to rediscover the Library lesson.
            f.AcceptButton = p.OK;
            f.CancelButton = p.Cancel;

            f.ResumeLayout();
            canvas.Rebuild();
        }

        public static void ApplyBookmarks(ManageBookmarksForm f)
        {
            BookmarksParts p = f.SkinParts;
            if (p == null || p.List == null) return;

            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, LargeW, LargeH);
            DialogSkin.AnchorToOwner(f, DialogAnchor.BottomRight);

            int buttonsY = LargeH - Margin - DialogSkin.ButtonH;
            int listBottom = buttonsY - 12;

            p.List.Font = DialogSkin.FBody;
            if (DialogSkin.Painting)
            {
                p.List.BorderStyle = BorderStyle.None;
                p.List.BackColor = NewPlayerSkin.Glass;
                p.List.ForeColor = NewPlayerSkin.Lit;
            }
            p.List.SetBounds(Margin, Margin, LargeW - 2 * Margin, listBottom - Margin);

            DialogSkin.AsKey(p.Delete, new Rectangle(Margin, buttonsY, DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.Cancel, new Rectangle(LargeW - Margin - DialogSkin.ButtonW, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(LargeW - Margin - 2 * DialogSkin.ButtonW - 12, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            f.CancelButton = p.Cancel;   // the one Form-property binding this dialog had

            f.ResumeLayout();
            canvas.Rebuild();
        }

        public static void ApplyTimer(SleepTimerForm f)
        {
            TimerParts p = f.SkinParts;
            if (p == null || p.Duration == null) return;

            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, SmallW, TimerH);
            DialogSkin.AnchorToOwner(f, DialogAnchor.BottomLeft);

            int buttonsY = TimerH - Margin - DialogSkin.ButtonH;
            int w = SmallW - 2 * Margin;

            DialogSkin.AsSticker(p.Duration, new Rectangle(Margin, Margin, w, p.Duration.Height));
            foreach (Control c in p.Duration.Controls) DialogSkin.OnGlass(c);
            WidenLabels(p.Duration);

            int actionY = Margin + p.Duration.Height + 12;
            DialogSkin.AsSticker(p.Action, new Rectangle(Margin, actionY, w, p.Action.Height));
            foreach (Control c in p.Action.Controls) DialogSkin.OnGlass(c);
            WidenLabels(p.Action);

            // Between the last group and the buttons, on the metal rather than in
            // a sticker: it is not part of either question above it.
            // Between the last group and the buttons, styled the way Go To's
            // "start playing after jump" already is — the same shape of question
            // in the same place on the same kind of dialog.
            if (p.Bookmark != null)
            {
                int y = actionY + p.Action.Height + 14;
                p.Bookmark.SetBounds(Margin, y, w, 24);
                DialogSkin.OnGlass(p.Bookmark);
            }

            DialogSkin.AsKey(p.Start, new Rectangle(SmallW - Margin - 2 * DialogSkin.ButtonW - 12, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.Cancel, new Rectangle(SmallW - Margin - DialogSkin.ButtonW, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            f.AcceptButton = p.Start;
            f.CancelButton = p.Cancel;

            f.ResumeLayout();
            canvas.Rebuild();
        }

        /// <summary>Lets a radio or check button use the width its group now has.
        /// These were sized by hand for a narrower dialog, and a caption that no
        /// longer fits is simply cut off — "…close the program and shut" was the
        /// one that showed it. Only the width changes: the caption text, the
        /// position and everything a screen reader is handed stay as they
        /// were.</summary>
        private static void WidenLabels(GroupBox g)
        {
            int limit = g.Width - 14;
            foreach (Control c in g.Controls)
            {
                if (!(c is RadioButton) && !(c is CheckBox)) continue;
                if (c.Right >= limit) continue;          // already reaches the edge
                if (c.Left + c.Width >= limit) continue;
                // Leave a control that shares its row with something else (the
                // Custom radio sits beside its spin box) exactly where it is.
                bool sharesRow = false;
                foreach (Control other in g.Controls)
                {
                    if (ReferenceEquals(other, c)) continue;
                    if (other.Top < c.Bottom && c.Top < other.Bottom) { sharesRow = true; break; }
                }
                if (sharesRow) continue;
                c.Width = limit - c.Left;
            }
        }

        /// <summary>The archive password prompt is built ad hoc (a static method,
        /// no dedicated Form subclass to hang a SkinParts property off), so it
        /// takes its controls directly rather than through a Parts object.
        ///
        /// <para><b>The explanation was a Label, and stays one under the classic
        /// theme</b> — this pass only reskins, it does not go fixing accessibility
        /// behaviour on a path nobody asked to touch. Under the new theme it
        /// becomes a read-only message box instead, in the same rectangle: a Label
        /// is never visited by Tab, and this sentence is the one that names the
        /// archive and says whether this is a retry, which matters enough that a
        /// keyboard user has to be able to reach it, not just glance at it.</para>
        /// </summary>
        public static void ApplyPassword(Form f, Label originalLabel, TextBox password, Button ok, Button cancel)
        {
            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, SmallW, SmallH);
            DialogSkin.AnchorToOwner(f, DialogAnchor.BottomLeft);

            int buttonsY = SmallH - Margin - DialogSkin.ButtonH;

            originalLabel.Visible = false;
            var well = new Rectangle(Margin, Margin, SmallW - 2 * Margin, 92);
            canvas.Wells.Add(well);
            TextBox message = DialogSkin.NewMessageBox(originalLabel.Text);
            message.TabIndex = 0;
            DialogSkin.AsGlass(message, new Rectangle(well.X + 12, well.Y + 12, well.Width - 24, well.Height - 24));
            f.Controls.Add(message);

            password.SetBounds(Margin, well.Bottom + 16, SmallW - 2 * Margin, 26);
            DialogSkin.OnGlass(password);
            if (DialogSkin.Painting) password.BorderStyle = BorderStyle.FixedSingle;
            password.TabIndex = 1;

            DialogSkin.AsKey(cancel, new Rectangle(SmallW - Margin - DialogSkin.ButtonW, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(ok, new Rectangle(SmallW - Margin - 2 * DialogSkin.ButtonW - 12, buttonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            f.AcceptButton = ok;
            f.CancelButton = cancel;

            f.ResumeLayout();
            canvas.Rebuild();
        }
    }
}
