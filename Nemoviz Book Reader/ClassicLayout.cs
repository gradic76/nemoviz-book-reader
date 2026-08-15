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
            int x = Margin;
            int w = panelW - 2 * Margin;
            int colW = (w - Margin) / 2;
            int col2 = x + colW + Margin;
            int y = Margin;

            // Seek step — a label over a full-width combo.
            list.Add(("SeekLabel", new Rectangle(x, y, colW, LabelH)));
            y += LabelH + 2;
            list.Add(("Seek", new Rectangle(x, y, w, RowH)));
            y += RowH + 14;

            // Transport, three across, Play/Pause in the middle as it is in the ring.
            int tw = (w - 2 * 13) / 3;
            list.Add(("Back", new Rectangle(x, y, tw, BtnH + 4)));
            list.Add(("PlayPause", new Rectangle(x + tw + 13, y, tw, BtnH + 4)));
            list.Add(("Forward", new Rectangle(x + 2 * (tw + 13), y, tw, BtnH + 4)));
            y += BtnH + 4 + 14;

            // Volume and speed side by side: each is one number and neither needs
            // the width.
            list.Add(("VolumeLabel", new Rectangle(x, y, colW, LabelH)));
            list.Add(("SpeedLabel", new Rectangle(col2, y, colW, LabelH)));
            y += LabelH + 2;
            list.Add(("VolumeField", new Rectangle(x, y, colW, RowH)));
            list.Add(("SpeedField", new Rectangle(col2, y, colW, RowH)));
            y += RowH + 14;

            // Position, full width — the longest line on the panel.
            list.Add(("ProgressLabel", new Rectangle(x, y, w, LabelH)));
            y += LabelH + 2;
            list.Add(("ProgressField", new Rectangle(x, y, w, RowH)));
            y += RowH + 18;

            // The eight commands, four and four: the app on the left, the book on
            // the right — the split the classic look already had across its outer
            // columns, and the one the new look keeps.
            for (int i = 0; i < 4; i++)
            {
                list.Add(("Left" + i, new Rectangle(x, y + i * Pitch, colW, BtnH)));
                list.Add(("Right" + i, new Rectangle(col2, y + i * Pitch, colW, BtnH)));
            }
            return list;
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

        /// <summary>Moves one control, and only if it is really there. A layout
        /// that throws on a control somebody removed would take the whole player
        /// down with it — and this one runs before anything is on screen to say
        /// so.</summary>
        private static void Place(Control c, int x, int y, int w, int h)
        {
            if (c == null) return;
            c.SetBounds(x, y, w, h);
        }
    }
}
