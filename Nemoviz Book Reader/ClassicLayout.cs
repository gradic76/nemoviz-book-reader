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
    /// <para><b>The tab order IS copied</b>, key for key — see
    /// <see cref="SetTabRing"/>. This paragraph used to say the opposite, on the
    /// argument that §5's column-major order was what every accessible name and
    /// shortcut had been learned against. Gordan overruled it on 2026-08-16:
    /// *"sredi tab order na classic, sve mora biti identično u svim temama"*. A
    /// reader who learns one look must not have to relearn the other, and the
    /// shortcuts are untouched either way — only the order of the stops
    /// changes.</para>
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

                // The three captions that only repeat their own field — see
                // PlayerBoxes. The new look hides these same labels (plus the
                // seek one, which it redraws); classic keeps the seek label
                // because its combo names nothing.
                if (p.VolumeLabel != null) p.VolumeLabel.Visible = false;
                if (p.SpeedLabel != null) p.SpeedLabel.Visible = false;
                if (p.ProgressLabel != null) p.ProgressLabel.Visible = false;

                MakeVolumeKeys(form, p);
                LayOutControls(p);
                SetTabRing(p);
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
            //
            // NO CAPTION LABEL OVER ANY OF THE THREE, and that is not a saving of
            // space — it is a duplicate removed. Each of these fields already
            // SAYS what it is: "Volume: 80%", "Speed: 1.0x",
            // "Position: 00:00:41 / 07:58:38". They have to, because of §2 — the
            // arrow keys make a screen reader read the focused field's own line,
            // so the line has to name itself or the reader hears a bare number.
            // And Form1 writes that same finished string into the LABEL as well
            // (`lblVolume.Text = text`, `lblProgress.Text = posText`), so every
            // one of these appeared on screen twice, one above the other.
            //
            // It is as old as the project — the initial commit already does it —
            // and it went unseen because the new look hides all four labels while
            // it draws its own legends, and nobody had looked at the classic
            // panel. Gordan's describer found it: *"pozicija je ispisana dvaput"*.
            //
            // The FIELD is the one that has to keep the prefix, so the LABEL is
            // what goes. Hidden rather than re-captioned: "Position" standing
            // over "Position: 00:00:41 / 07:58:38" is still saying it twice.
            // ApplyPlayer does the hiding; nothing is placed for them here.
            // (`SeekLabel` stays — its combo reads "5 minutes" and names nothing.)
            int halfW = (w - Margin) / 2;
            int half2 = x + halfW + Margin;
            list.Add(("VolumeField", new Rectangle(x, y, halfW, RowH)));
            list.Add(("SpeedField", new Rectangle(half2, y, halfW, RowH)));
            y += RowH + 14;

            // Position, the width of the column — the longest line on the panel.
            list.Add(("ProgressField", new Rectangle(x, y, w, RowH)));

            // Column C: the eight commands in one column, in the NEW LOOK'S OWN
            // ORDER — its column A read down, then its column D. See Command().
            for (int i = 0; i < 8; i++)
                list.Add(("Cmd" + i, new Rectangle(cx, Margin + i * Pitch, cmdW, BtnH)));
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
        /// <para><b>They stand third and fourth in the tab ring</b>, where the new
        /// look's two ring arrows stand — see <see cref="SetTabRing"/>, which is
        /// what assigns it. This file used to put them last, on the argument that
        /// the keyboard has had Up and Down for volume since before either look;
        /// that argument survives, but it is no longer ours to make, because the
        /// two looks now share one keyboard.</para>
        ///
        /// <para>Built once. This runs whenever the player builds itself, and a
        /// second pair would be two invisible buttons stacked on the first.</para></summary>
        private static void MakeVolumeKeys(Form1 form, PlayerParts p)
        {
            if (volumeUp != null && volumeUp.Parent == p.Bottom) return;

            volumeUp = MakeKey(form, "Btn.VolumeUp.Accessible", +5);
            volumeDown = MakeKey(form, "Btn.VolumeDown.Accessible", -5);
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
            if (name.StartsWith("Cmd")) return Command(p, name[3] - '0');
            return null;
        }

        /// <summary>The eight command keys, in the order the new look reads them:
        /// its column A down (Library, Settings, Properties, Help) and then its
        /// column D (Go To, Bookmark, Bookmarks, Timer).
        ///
        /// <para><b>Not the order of <c>p.Left</c> and <c>p.Right</c></b>, which is
        /// the order <c>BuildUI</c> happens to declare them in — Timer sits third
        /// in <c>Left</c> and Properties first in <c>Right</c>. Reading the arrays
        /// straight through gave the classic column a different order from the new
        /// look's, which is the thing Gordan asked to be rid of: *"sve mora biti
        /// identično u svim temama"*. `NewPlayerSkin.LayOutButtons` picks the same
        /// eight the same way; the two lists have to be read together.</para></summary>
        private static Control Command(PlayerParts p, int i)
        {
            if (p.Left == null || p.Right == null) return null;
            switch (i)
            {
                case 0: return p.Left[0];    // Library
                case 1: return p.Left[1];    // Settings
                case 2: return p.Right[0];   // Properties
                case 3: return p.Left[3];    // Help
                case 4: return p.Right[1];   // Go To
                case 5: return p.Right[2];   // Set Bookmark
                case 6: return p.Right[3];   // Manage Bookmarks
                case 7: return p.Left[2];    // Sleep Timer
            }
            return null;
        }

        /// <summary>The tab ring, taken from <see cref="NewPlayerSkin"/> key for
        /// key so the two looks are one keyboard.
        ///
        /// <para><b>This reverses what this file used to say.</b> It argued that
        /// §5's column-major order should stay because every accessible name and
        /// shortcut was learned against it. Gordan overruled it on 2026-08-16 —
        /// *"sredi tab order na classic, sve mora biti identično u svim temama"* —
        /// and he is right that a reader who learns one look must not have to
        /// relearn the other. The shortcuts are untouched either way; only the
        /// order of the stops changes.</para>
        ///
        /// <para>Two of these are <b>TabStop = false</b> rather than an index, and
        /// both come straight from §8k. The volume READOUT leaves the ring because
        /// the two volume keys already speak on every step, so it would only add a
        /// stop. The INFO BOX leaves it because it is reached with F8 — twice to
        /// put focus inside it — and keeping it out means the arrows never have two
        /// owners. The F8 path sets <c>TabStop</c> back on while focus is in there,
        /// and that code is look-independent, so it works here unchanged.</para></summary>
        /// <summary>The ring as plain data, split out for the same reason
        /// <see cref="PlayerBoxes"/> is: nobody here can look at a tab order, so it
        /// has to be readable without a window. <c>-1</c> means
        /// <c>TabStop = false</c> — in the ring's list rather than left implicit,
        /// because "not a stop" is a decision and not an omission.</summary>
        internal static System.Collections.Generic.List<(string Name, int Index)> PlayerTabRing()
        {
            var r = new System.Collections.Generic.List<(string, int)>
            {
                ("PlayPause", 0), ("Forward", 1), ("Back", 2),
                ("VolumeUp", 3), ("VolumeDown", 4),
                ("Seek", 5), ("SpeedField", 6), ("ProgressField", 7),
                ("VolumeField", -1), ("Info", -1),
            };
            // 20..27, which is what NewPlayerSkin assigns as colA 20+i and
            // colD 24+i — the same eight keys in the same order.
            for (int i = 0; i < 8; i++) r.Add(("Cmd" + i, 20 + i));
            return r;
        }

        private static void SetTabRing(PlayerParts p)
        {
            foreach (var e in PlayerTabRing())
            {
                Control c = e.Name == "Info" ? p.Info : Named(p, e.Name);
                if (c == null) continue;
                if (e.Index < 0) { c.TabStop = false; continue; }
                c.TabStop = true;
                c.TabIndex = e.Index;
            }
        }

    }
}
