using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The classic look, arranged the way the new one is.
    ///
    /// <para><b>Gordan, 2026-08-16:</b> *"neka izgleda kao NBR default samo u
    /// classic stilu"* — the same proportions and the same places, in ordinary
    /// Windows controls and ordinary system colours. So this moves things and
    /// changes nothing else: <b>no painting, no owner-drawing, no colours.</b>
    /// That is the whole difference from <see cref="NewPlayerSkin"/> and the
    /// dialog skins, which do both at once.</para>
    ///
    /// <para><b>Nothing here touches the new look.</b> §8k closed that design;
    /// splitting its skins into a geometry half and a paint half would have been
    /// a rework of finished code to serve unfinished code. This is a separate
    /// pass over the same <c>SkinParts</c> surface the skins already use, so the
    /// two can never interfere.</para>
    ///
    /// <para><b>What is deliberately NOT copied is the tab order.</b> §5's
    /// column-major order — app column, then playback, then book tools — is what
    /// the classic look has always had, and every accessible name and shortcut
    /// was learned against it. Moving controls on screen costs a sighted reader
    /// nothing; renumbering them would move the ground under everybody
    /// else.</para>
    /// </summary>
    internal static class ClassicLayout
    {
        // The new look's own outside measurements, so the two windows are the
        // same size on the desktop and one can be recognised as the other.
        private const int W = 960, H = 480;

        // The left third is the information, the rest is the controls — which is
        // the arrangement the new look reads as, with its glass on the left and
        // its keys to the right of it.
        private const int LeftW = 424;

        private const int Margin = 12;
        private const int RowH = 24, LabelH = 18, BtnH = 36, Pitch = 46;

        public static void ApplyPlayer(Form1 form)
        {
            if (form == null) return;
            PlayerParts p = form.SkinParts;
            if (p.Top == null || p.Bottom == null) return;

            form.SuspendLayout();
            try
            {
                form.ClientSize = new Size(W, H);

                // Two panels side by side rather than one above the other. They
                // keep their borders and their names; only their shape changes.
                p.Top.SetBounds(0, 0, LeftW, H);
                p.Bottom.SetBounds(LeftW, 0, W - LeftW, H);

                if (p.Info != null)
                    p.Info.SetBounds(8, 8, LeftW - 16, H - 16);

                MakeVolumeKeys(form, p);
                LayOutControls(p);
            }
            finally { form.ResumeLayout(true); }
        }

        /// <summary>Where every control on the right-hand panel goes, as plain
        /// numbers.
        ///
        /// <para><b>Separated from the placing on purpose.</b> This is a layout
        /// nobody here can look at — Gordan reads by ear and the eyes-and-hands
        /// pass is somebody else's — so it has to be checkable without a screen:
        /// a list of named boxes can be tested for overlaps, for anything hanging
        /// off the panel, and for the bands landing in the order they were meant
        /// to. Applying them is then three lines that cannot go wrong.</para>
        ///
        /// <para>Top to bottom: what you are listening to, then how you move
        /// through it, then the eight commands in two columns — the same three
        /// bands the new look has, in the same order.</para></summary>
        internal static System.Collections.Generic.List<(string Name, Rectangle R)>
            PlayerBoxes(int panelW, int panelH)
        {
            var list = new System.Collections.Generic.List<(string, Rectangle)>();

            // THREE COLUMNS, the same shape the dialogs take: the information on
            // the left (its own panel), then the transport, then the commands.
            // The two on this panel are B and C.
            int cmdW = 150;
            int cx = panelW - Margin - cmdW;          // column C
            int x = Margin;                           // column B
            int w = cx - Margin - x;
            int y = Margin;

            // Seek step — a label over the combo.
            list.Add(("SeekLabel", new Rectangle(x, y, w, LabelH)));
            y += LabelH + 2;
            list.Add(("Seek", new Rectangle(x, y, w, RowH)));
            y += RowH + 14;

            // THE CROSS (Gordan, 2026-08-16): five square keys of one size, the
            // middle one Play/Pause and the other four on its sides. It is the
            // ring's own mapping in ordinary buttons — up and down are volume,
            // left and right the seek step — so a reader who learns one look has
            // learned the other, which was the whole reason for asking.
            //
            // Three across and three down at this size do not fit beside
            // everything else in one column, which is what put the commands into
            // a column of their own and gave this panel the three-column shape.
            int cell = 64, gap = 8;
            int span = 3 * cell + 2 * gap;
            int cxL = x + (w - span) / 2;             // the cross's own left edge
            list.Add(("VolumeUp", new Rectangle(cxL + cell + gap, y, cell, cell)));
            list.Add(("Back", new Rectangle(cxL, y + cell + gap, cell, cell)));
            list.Add(("PlayPause", new Rectangle(cxL + cell + gap, y + cell + gap, cell, cell)));
            list.Add(("Forward", new Rectangle(cxL + 2 * (cell + gap), y + cell + gap, cell, cell)));
            list.Add(("VolumeDown", new Rectangle(cxL + cell + gap, y + 2 * (cell + gap), cell, cell)));
            y += span + 14;

            // Volume and speed side by side: each is one number and neither needs
            // the width.
            int halfW = (w - Margin) / 2;
            int half2 = x + halfW + Margin;
            list.Add(("VolumeLabel", new Rectangle(x, y, halfW, LabelH)));
            list.Add(("SpeedLabel", new Rectangle(half2, y, halfW, LabelH)));
            y += LabelH + 2;
            list.Add(("VolumeField", new Rectangle(x, y, halfW, RowH)));
            list.Add(("SpeedField", new Rectangle(half2, y, halfW, RowH)));
            y += RowH + 14;

            // Position, the width of the column — the longest line on the panel.
            list.Add(("ProgressLabel", new Rectangle(x, y, w, LabelH)));
            y += LabelH + 2;
            list.Add(("ProgressField", new Rectangle(x, y, w, RowH)));

            // Column C: the eight commands in one column, the app above the book —
            // the order they already had across the classic panel's outer columns.
            for (int i = 0; i < 4; i++)
            {
                list.Add(("Left" + i, new Rectangle(cx, Margin + i * Pitch, cmdW, BtnH)));
                list.Add(("Right" + i, new Rectangle(cx, Margin + (i + 4) * Pitch, cmdW, BtnH)));
            }
            return list;
        }

        /// <summary>The cross's two extra arms.
        ///
        /// <para>The classic panel has never had a volume button — volume was the
        /// Up and Down keys and a read-only field. The cross needs four arms, so
        /// these two are built here, exactly as <see cref="NewPlayerSkin"/> builds
        /// its own: the same accessible names, the same <c>SkinVolume</c> hook,
        /// the same five-per-press step. Two looks, one set of controls, which is
        /// the point of the cross.</para>
        ///
        /// <para><b>Last in the tab order, after everything BuildUI made.</b> The
        /// keyboard has had Up and Down for volume since long before either look,
        /// and a reader who tabs does not need two more stops in the middle of the
        /// transport to reach what a key already does. Put at the end they can be
        /// found by anyone hunting, and are in nobody's way.</para>
        ///
        /// <para>Built once. This runs whenever the player builds itself, and a
        /// second pair would be two invisible buttons stacked on the first.</para></summary>
        private static void MakeVolumeKeys(Form1 form, PlayerParts p)
        {
            if (volumeUp != null && volumeUp.Parent == p.Bottom) return;

            volumeUp = MakeKey(form, "Btn.VolumeUp.Accessible", +5);
            volumeDown = MakeKey(form, "Btn.VolumeDown.Accessible", -5);
            int tab = 0;
            foreach (Control c in p.Bottom.Controls) if (c.TabIndex > tab) tab = c.TabIndex;
            volumeUp.TabIndex = tab + 1;
            volumeDown.TabIndex = tab + 2;
            p.Bottom.Controls.Add(volumeUp);
            p.Bottom.Controls.Add(volumeDown);
        }

        private static Button volumeUp, volumeDown;

        private static Button MakeKey(Form1 form, string nameKey, int step)
        {
            var b = new Button();
            b.Text = step > 0 ? "+" : "−";
            b.AccessibleName = Localization.T(nameKey);
            b.UseVisualStyleBackColor = true;
            b.Click += delegate { form.SkinVolume(step); };
            return b;
        }

        private static void LayOutControls(PlayerParts p)
        {
            var boxes = PlayerBoxes(p.Bottom.Width, p.Bottom.Height);
            foreach (var b in boxes)
            {
                Control c = Named(p, b.Name);
                if (c != null) c.SetBounds(b.R.X, b.R.Y, b.R.Width, b.R.Height);
            }
        }

        private static Control Named(PlayerParts p, string name)
        {
            switch (name)
            {
                case "SeekLabel": return p.SeekLabel;
                case "Seek": return p.Seek;
                case "Back": return p.Back;
                case "PlayPause": return p.PlayPause;
                case "Forward": return p.Forward;
                case "VolumeLabel": return p.VolumeLabel;
                case "SpeedLabel": return p.SpeedLabel;
                case "VolumeField": return p.VolumeField;
                case "SpeedField": return p.SpeedField;
                case "ProgressLabel": return p.ProgressLabel;
                case "ProgressField": return p.ProgressField;
                case "VolumeUp": return volumeUp;
                case "VolumeDown": return volumeDown;
            }
            if (name.StartsWith("Left") && p.Left != null)
            {
                int i = name[4] - '0';
                return i >= 0 && i < p.Left.Length ? p.Left[i] : null;
            }
            if (name.StartsWith("Right") && p.Right != null)
            {
                int i = name[5] - '0';
                return i >= 0 && i < p.Right.Length ? p.Right[i] : null;
            }
            return null;
        }

        // ── The dialogs ───────────────────────────────────────────────────────
        //
        // Same policy as the player: the sizes and the places the skinned look
        // uses, in the controls and colours the classic look already has. Every
        // number below is the skin's own, so the two windows are the same window
        // in two coats — which is the whole request.

        private const int BtnW = 112, BtnFullH = 36;

        /// <summary>Go To, at the large work-dialog size.
        ///
        /// <para><b>The hint box stays.</b> The skinned look replaced it with a
        /// <c>?</c> key, which is a painted control; here there is nothing to
        /// paint it with, and the always-on hint is what the classic look has
        /// always shown. So it keeps its place above the check box and the list
        /// gives up the height — the one place these two layouts differ, and it
        /// differs because the classic one cannot draw.</para></summary>
        public static void ApplyGoTo(GoToForm f)
        {
            GoToParts p = f.SkinParts;
            if (p == null || p.List == null) return;

            f.SuspendLayout();
            try
            {
                f.ClientSize = new Size(WorkDialogSkin.LargeW, WorkDialogSkin.LargeH);
                DialogSkin.AnchorToOwner(f, DialogAnchor.BottomRight);

                int w = WorkDialogSkin.LargeW - 2 * Margin;
                int buttonsY = WorkDialogSkin.LargeH - Margin - BtnFullH;
                int checkY = buttonsY - 12 - 24;
                // NOT p.AutoPlayHint.Visible, and this cost a measurement to find:
                // a control whose form has not been shown yet reports Visible =
                // false whatever its own setting, because Visible is the EFFECTIVE
                // visibility and the parent is not up. This runs from the
                // constructor, so the test was false every time — the hint kept
                // its original place across the middle of the list, and the list
                // took the height meant for both. §10b records the same trap for a
                // tab page that is not the selected one.
                int hintH = p.AutoPlayHint != null ? p.AutoPlayHint.Height : 0;
                int hintY = checkY - (hintH > 0 ? hintH + 8 : 0);

                // The list takes what is left, and never less than nothing: the
                // hint box sizes itself to its text, and a longer translation of
                // it could otherwise eat past the top of the window and hand
                // SetBounds a negative height.
                Place(p.List, Margin, Margin, w, Math.Max(80, hintY - 12 - Margin));
                if (hintH > 0) Place(p.AutoPlayHint, Margin, hintY, w, hintH);
                Place(p.AutoPlay, Margin, checkY, w, 24);
                Place(p.OK, WorkDialogSkin.LargeW - Margin - 2 * BtnW - 12, buttonsY, BtnW, BtnFullH);
                Place(p.Cancel, WorkDialogSkin.LargeW - Margin - BtnW, buttonsY, BtnW, BtnFullH);
            }
            finally { f.ResumeLayout(true); }
        }

        /// <summary>Manage Bookmarks, the same size and the same three keys along
        /// the foot — Delete out on the left where it cannot be hit by somebody
        /// reaching for OK.</summary>
        public static void ApplyBookmarks(ManageBookmarksForm f)
        {
            BookmarksParts p = f.SkinParts;
            if (p == null || p.List == null) return;

            f.SuspendLayout();
            try
            {
                f.ClientSize = new Size(WorkDialogSkin.LargeW, WorkDialogSkin.LargeH);
                DialogSkin.AnchorToOwner(f, DialogAnchor.BottomRight);

                int w = WorkDialogSkin.LargeW - 2 * Margin;
                int buttonsY = WorkDialogSkin.LargeH - Margin - BtnFullH;

                Place(p.List, Margin, Margin, w, buttonsY - 12 - Margin);
                Place(p.Delete, Margin, buttonsY, BtnW, BtnFullH);
                Place(p.OK, WorkDialogSkin.LargeW - Margin - 2 * BtnW - 12, buttonsY, BtnW, BtnFullH);
                Place(p.Cancel, WorkDialogSkin.LargeW - Margin - BtnW, buttonsY, BtnW, BtnFullH);
            }
            finally { f.ResumeLayout(true); }
        }

        /// <summary>The sleep timer, at the small size — its two groups keep
        /// whatever height they built themselves at, since what is in them is
        /// radio buttons and those size their own box.</summary>
        public static void ApplyTimer(SleepTimerForm f)
        {
            TimerParts p = f.SkinParts;
            if (p == null || p.Duration == null) return;

            f.SuspendLayout();
            try
            {
                f.ClientSize = new Size(WorkDialogSkin.SmallW, WorkDialogSkin.TimerHeight);
                DialogSkin.AnchorToOwner(f, DialogAnchor.BottomLeft);

                int w = WorkDialogSkin.SmallW - 2 * Margin;
                int buttonsY = WorkDialogSkin.TimerHeight - Margin - BtnFullH;

                Place(p.Duration, Margin, Margin, w, p.Duration.Height);
                int actionY = Margin + p.Duration.Height + 12;
                Place(p.Action, Margin, actionY, w, p.Action.Height);
                if (p.Bookmark != null)
                    Place(p.Bookmark, Margin, actionY + p.Action.Height + 14, w, 24);

                Place(p.Start, WorkDialogSkin.SmallW - Margin - 2 * BtnW - 12, buttonsY, BtnW, BtnFullH);
                Place(p.Cancel, WorkDialogSkin.SmallW - Margin - BtnW, buttonsY, BtnW, BtnFullH);
            }
            finally { f.ResumeLayout(true); }
        }

        /// <summary>Moves one control, and only if it is really there. A layout
        /// that throws on a control somebody removed would take the whole player
        /// down with it — and this one runs before anything is on screen to say
        /// so.
        ///
        /// <para><b>The anchor goes first, and without that nothing here works.</b>
        /// These windows grow: Go To from 420×380 to 580×600. A control anchored
        /// to more than its top left is re-computed by the next layout pass from
        /// the offsets it was BUILT with, so <c>ResumeLayout</c> quietly undoes
        /// the bounds just set. Measured before it was understood — Go To's list
        /// came out 485 tall where it had been told 442, and its hint box never
        /// moved at all and sat across the middle of the list.</para></summary>
        private static void Place(Control c, int x, int y, int w, int h)
        {
            if (c == null) return;
            c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            c.SetBounds(x, y, w, h);
        }

    }
}
