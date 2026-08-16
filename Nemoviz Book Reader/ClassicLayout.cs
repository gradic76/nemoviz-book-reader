using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The classic PLAYER, arranged the way the new one is.
    ///
    /// <para><b>Gordan, 2026-08-16:</b> *"neka izgleda kao NBR default samo u
    /// classic stilu"* — the same proportions and the same places, in ordinary
    /// Windows controls and ordinary system colours. So this moves things and
    /// changes nothing else: <b>no painting, no owner-drawing, no colours.</b>
    /// That is the whole difference from <see cref="NewPlayerSkin"/>, which does
    /// both at once.</para>
    ///
    /// <para><b>THE DIALOGS ARE NOT HERE ANY MORE, and that is the point</b>
    /// (Gordan, 2026-08-16, reformulating): *"Otvoriš npr. Settings… i kompletno
    /// ga iskopiraš u klasičnoj formi… samo što nije crtan, šminkan i farban
    /// nego je classic koji izvlači stilove i boje iz windows teme."* A second
    /// layout for the same window is exactly the drift he was asking to remove —
    /// this class had already begun to differ, keeping the old always-on hint
    /// box where the new look has a <c>?</c> key. So every dialog now runs the
    /// SKIN's one layout pass in both looks, and <see cref="DialogSkin.Painting"/>
    /// is what decides whether that pass also paints. The classic path cannot be
    /// missing a control the new one has, because it is the same code that put
    /// it there. Verified: with the metal on, every dialog's geometry is
    /// byte-identical to the build before the change.</para>
    ///
    /// <para><b>The player is the one thing that genuinely differs</b>, so it
    /// stays here: the new look's transport is a DRAWN ring with sectors round a
    /// disc, which has no classic equivalent at all. Gordan's answer to that was
    /// five ordinary square buttons in a cross, and that is a different layout
    /// rather than the same one unpainted.</para>
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

    }
}
