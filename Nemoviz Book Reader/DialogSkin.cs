using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    internal sealed class PropParts
    {
        public TextBox Info;
        public CheckBox Master, Bypass;
        public Button ResetAll, OK, Cancel;
        public GroupBox[] Stages;
        public GroupBox Playback;
        public TextBox TextInfo;
        public TabControl Tabs;
    }

    /// <summary>
    /// The look the three sub-windows share with the player: 960 wide, a 12-unit
    /// silver rim, and the same recessed dark glass. They cover the player exactly
    /// — same width, flush left and right — so opening one reads as the device
    /// changing face rather than a window landing on top of it.
    ///
    /// <para>The rule from the panel holds here and matters more, because these
    /// windows are made of list, combo and check boxes rather than drawn shapes:
    /// <b>every control stays a real control and is only repainted.</b> A drawn
    /// GroupBox would take away the group name a screen reader announces on the
    /// way in; a drawn ComboBox would take away type-ahead. So the "stickers" are
    /// real GroupBoxes we paint over, and nothing here touches a role, a name or
    /// the tab order.</para>
    /// </summary>
    internal static class DialogSkin
    {
        public const int W = 960, H = 640;
        public const int Rim = 12;

        // Left third is the info glass, the rest is controls.
        public static readonly Rectangle InfoPanel = new Rectangle(12, 12, 296, 616);
        public static readonly Rectangle InfoGlass = new Rectangle(29, 29, 262, 582);
        public const int ColA = 320, ColB = 640, ColW = 308;

        public static readonly Rectangle PlaybackCell = new Rectangle(320, 12, 628, 76);
        public const int StripY = 96, StripH = 32;
        public static readonly int[] StageRowY = { 136, 284, 432 };
        public const int StageH = 138;
        public const int ButtonsY = 578, ButtonW = 112, ButtonH = 36;

        // The sticker: a translucent panel laid on the glass, a shade lighter than
        // it, with its name printed along the top edge.
        public static readonly Color Sticker = Color.FromArgb(0x19, 0x20, 0x1C);
        public static readonly Color StickerEdge = Color.FromArgb(0x3E, 0x4A, 0x44);

        public static Font FTitle, FBody, FSilk;

        public static void EnsureFonts()
        {
            if (FBody != null) return;
            FTitle = new Font("Segoe UI", 12f, FontStyle.Bold);
            FBody = new Font("Segoe UI", 12f);
            FSilk = new Font("Segoe UI", 11f);
        }

        /// <summary>Turns a dialog into a face of the device: borderless, rounded,
        /// 960 wide so it sits flush over the player, and draggable by its metal
        /// since there is no title bar to grab.</summary>
        public static DialogCanvas Shell(Form f, int height)
        {
            EnsureFonts();
            f.FormBorderStyle = FormBorderStyle.None;
            f.ClientSize = new Size(W, height);
            f.BackColor = NewPlayerSkin.PanelMid;
            using (var casing = NewPlayerSkin.Round(new RectangleF(0, 0, W, height), NewPlayerSkin.CaseRadius))
                f.Region = new Region(casing);

            var canvas = new DialogCanvas(f);
            f.Controls.Add(canvas);
            canvas.SendToBack();
            return canvas;
        }

        /// <summary>A read-only field turned into display glass. The control is
        /// untouched below the paint: still a TextBox, still tabbable, still
        /// scrollable when the text outgrows the column.</summary>
        public static void AsGlass(TextBox t, Rectangle where)
        {
            if (t == null) return;
            t.SetBounds(where.X, where.Y, where.Width, where.Height);
            t.BorderStyle = BorderStyle.None;
            t.BackColor = NewPlayerSkin.Glass;
            t.ForeColor = NewPlayerSkin.Lit;
            t.Font = FBody;
            // It is first in the tab order, so it opens focused and a focused
            // multiline TextBox selects everything — a solid blue block where the
            // display should be. The caret goes to the start instead; the field is
            // read-only, so there is nothing a user loses by that.
            t.GotFocus -= Deselect;
            t.GotFocus += Deselect;
            t.Select(0, 0);
        }

        private static void Deselect(object sender, EventArgs e)
        {
            var t = sender as TextBox;
            if (t != null && t.SelectionLength > 0) t.Select(0, 0);
        }

        /// <summary>A real GroupBox wearing a sticker. Paint only — the box keeps
        /// its name and keeps announcing it.</summary>
        public static void AsSticker(GroupBox g, Rectangle where)
        {
            if (g == null) return;
            g.SetBounds(where.X, where.Y, where.Width, where.Height);
            g.ForeColor = NewPlayerSkin.Lit;
            g.Font = FBody;
            g.BackColor = Color.Transparent;
            g.Paint -= PaintSticker;
            g.Paint += PaintSticker;
        }

        private static void PaintSticker(object sender, PaintEventArgs e)
        {
            var g = sender as GroupBox;
            if (g == null) return;
            Graphics gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new RectangleF(0.5f, 0.5f, g.Width - 1, g.Height - 1);
            using (var p = NewPlayerSkin.Round(r, 8))
            using (var br = new LinearGradientBrush(new RectangleF(0, -1, g.Width, g.Height + 2),
                       Color.FromArgb(0x22, 0x2B, 0x26), Sticker, LinearGradientMode.Vertical))
                gr.FillPath(br, p);
            using (var p = NewPlayerSkin.Round(r, 8))
            using (var pen = new Pen(StickerEdge, 1.2f))
                gr.DrawPath(pen, p);

            NewPlayerSkin.LitString(gr, g.Text, new RectangleF(12, 2, g.Width - 20, 22),
                FTitle, NewPlayerSkin.Lit);
        }

        /// <summary>Checks and radios on the glass: the frame draws them with the
        /// system's own colours, which vanish on near-black. Only the colours
        /// change; the control is otherwise left completely alone.</summary>
        public static void OnGlass(Control c)
        {
            if (c == null) return;
            c.Font = FBody;
            c.ForeColor = NewPlayerSkin.Lit;

            // Only the controls that paint their own background accept a
            // transparent one. ComboBox and NumericUpDown throw outright — they
            // are windowed controls that must have a colour of their own, so they
            // get the glass instead of a hole in it.
            if (c is CheckBox || c is RadioButton || c is Label || c is GroupBox)
            {
                c.BackColor = Color.Transparent;
                return;
            }
            c.BackColor = Color.FromArgb(0x12, 0x18, 0x15);
            ComboBox cb = c as ComboBox;
            if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            NumericUpDown n = c as NumericUpDown;
            if (n != null) n.BorderStyle = BorderStyle.FixedSingle;
        }

        /// <summary>A silver key on the rim, same face as the player's.</summary>
        public static void AsKey(Button b, Rectangle where)
        {
            if (b == null) return;
            b.SetBounds(where.X, where.Y, where.Width, where.Height);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.Transparent;
            b.FlatAppearance.MouseDownBackColor = Color.Transparent;
            b.BackColor = NewPlayerSkin.PanelMid;
            b.ForeColor = NewPlayerSkin.Jet;
            b.Font = FBody;
            b.UseVisualStyleBackColor = false;
            b.Paint -= PaintKeyFace;
            b.Paint += PaintKeyFace;
        }

        private static void PaintKeyFace(object sender, PaintEventArgs e)
        {
            var b = sender as Button;
            if (b == null) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(NewPlayerSkin.PanelMid))
                g.FillRectangle(br, 0, 0, b.Width, b.Height);
            NewPlayerSkin.PaintOrigin = b.Location;
            var face = new Rectangle(NewPlayerSkin.Groove, NewPlayerSkin.Groove,
                b.Width - 2 * NewPlayerSkin.Groove, b.Height - 2 * NewPlayerSkin.Groove);
            NewPlayerSkin.Recess(g, face, 5, NewPlayerSkin.RingFor(b));
            NewPlayerSkin.SilverFace(g, face, 5);
            NewPlayerSkin.PaintOrigin = PointF.Empty;
            NewPlayerSkin.DrawString(g, b.Tag as string ?? b.Text,
                new RectangleF(0, 0, b.Width, b.Height), FBody, NewPlayerSkin.Jet,
                StringAlignment.Center, StringAlignment.Center);
        }

        /// <summary>Bypass, drawn as a rocker rather than a tick. It is not the
        /// same kind of thing as the enable checks — those save a setting into the
        /// book, this one only changes what you are hearing right now — so it
        /// should not look like them. It stays a CheckBox underneath, which is what
        /// keeps its role, its state and the space bar working.</summary>
        public static void AsSwitch(CheckBox c, Rectangle where)
        {
            if (c == null) return;
            c.SetBounds(where.X, where.Y, where.Width, where.Height);
            c.Appearance = Appearance.Button;
            c.FlatStyle = FlatStyle.Flat;
            c.FlatAppearance.BorderSize = 0;
            c.FlatAppearance.CheckedBackColor = Color.Transparent;
            c.FlatAppearance.MouseOverBackColor = Color.Transparent;
            c.FlatAppearance.MouseDownBackColor = Color.Transparent;
            c.BackColor = NewPlayerSkin.PanelMid;
            c.Tag = c.Text;
            c.Text = "";
            c.Paint -= PaintSwitch;
            c.Paint += PaintSwitch;
        }

        private static void PaintSwitch(object sender, PaintEventArgs e)
        {
            var c = sender as CheckBox;
            if (c == null) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(NewPlayerSkin.PanelMid))
                g.FillRectangle(br, 0, 0, c.Width, c.Height);

            string label = c.Tag as string ?? "";
            var track = new RectangleF(0, (c.Height - 22) / 2f, 52, 22);
            NewPlayerSkin.Recess(g, track, 11, c.Focused ? (Color?)NewPlayerSkin.FocusGlow : null);
            using (var p = NewPlayerSkin.Round(track, 11))
            using (var br = new SolidBrush(c.Checked
                       ? NewPlayerSkin.ElectricDeep : Color.FromArgb(0x1E, 0x1E, 0x1C)))
                g.FillPath(br, p);

            var knob = new RectangleF(c.Checked ? track.Right - 21 : track.Left + 1,
                                      track.Top + 1, 20, 20);
            NewPlayerSkin.PaintOrigin = c.Location;
            NewPlayerSkin.SilverFace(g, knob, 10);
            NewPlayerSkin.PaintOrigin = PointF.Empty;

            NewPlayerSkin.DrawString(g, label, new RectangleF(62, 0, c.Width - 62, c.Height),
                FBody, NewPlayerSkin.Jet, StringAlignment.Near, StringAlignment.Center);
        }
    }

    /// <summary>Properties, laid out the way it was agreed: the info glass down
    /// the whole left third, playback across the top of the other two, the master
    /// switch with Bypass and Reset on the metal beneath it, then the six stages
    /// as stickers three and three, and the buttons on the metal at the foot.
    ///
    /// <para>A hybrid book still has two tabs and the agreed layout has nowhere to
    /// put a tab strip, so those keep the classic dialog until that is decided.</para>
    /// </summary>
    internal static class PropertiesSkin
    {
        public static void Apply(PropertiesForm f)
        {
            PropParts p = f.SkinParts;
            if (p == null || p.Tabs == null || p.Tabs.TabPages.Count != 1) return;

            // Which page is the single one? A text-only book has no playback group
            // and its stage cells were built but never put on a page — running the
            // audio layout there walked straight into a null. The page decides.
            TabPage only = p.Tabs.TabPages[0];
            if (p.TextInfo != null && only.Controls.Contains(p.TextInfo))
            {
                ApplyTextPage(f, p, only);
                return;
            }
            if (p.Stages == null || p.Playback == null) return;

            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, DialogSkin.H);

            // Off the tab page and onto the form; the strip has nothing left to show.
            var move = new List<Control>();
            foreach (Control c in p.Tabs.TabPages[0].Controls) move.Add(c);
            foreach (Control c in move)
            {
                p.Tabs.TabPages[0].Controls.Remove(c);
                f.Controls.Add(c);
                c.BringToFront();
            }
            p.Tabs.Visible = false;

            canvas.Wells.Add(DialogSkin.InfoPanel);
            DialogSkin.AsGlass(p.Info, DialogSkin.InfoGlass);

            DialogSkin.AsSticker(p.Playback, DialogSkin.PlaybackCell);
            for (int i = 0; i < p.Stages.Length; i++)
                DialogSkin.AsSticker(p.Stages[i], new Rectangle(
                    i % 2 == 0 ? DialogSkin.ColA : DialogSkin.ColB,
                    DialogSkin.StageRowY[i / 2], DialogSkin.ColW, DialogSkin.StageH));

            Reflow(p.Playback, true);
            foreach (GroupBox g in p.Stages) Reflow(g, false);

            // The strip on the metal between the playback sticker and the stages.
            OnMetal(p.Master);
            p.Master.SetBounds(DialogSkin.ColA, DialogSkin.StripY, 292, DialogSkin.StripH);
            DialogSkin.AsSwitch(p.Bypass, new Rectangle(624, DialogSkin.StripY, 200, DialogSkin.StripH));
            DialogSkin.AsKey(p.ResetAll, new Rectangle(836, DialogSkin.StripY, 112, DialogSkin.StripH));

            DialogSkin.AsKey(p.Cancel, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            // Tab order as agreed: playback first, then the master switch, then
            // what it gates, then the read-out, then the buttons.
            p.Playback.TabIndex = 0;
            p.Master.TabIndex = 1;
            p.ResetAll.TabIndex = 2;
            p.Bypass.TabIndex = 3;
            for (int i = 0; i < p.Stages.Length; i++) p.Stages[i].TabIndex = 4 + i;
            p.Info.TabIndex = 20;
            p.OK.TabIndex = 21;
            p.Cancel.TabIndex = 22;

            // With processing off, Tab goes straight from the master switch to the
            // read-out. The six cells already drop out because they are disabled;
            // Reset and Bypass have to be told, since neither is disabled and both
            // would otherwise sit in the way of a route the user has switched off.
            EventHandler gate = (s, e) =>
            {
                p.ResetAll.TabStop = p.Master.Checked;
                p.Bypass.TabStop = p.Master.Checked;
            };
            p.Master.CheckedChanged += gate;
            gate(null, EventArgs.Empty);

            // One ? per group, and F1 as the second way to the same text.
            HintSystem.Clear();
            HintSystem.Attach(p.Playback, "Hint.Playback");
            string[] stageHints = { "Hint.RemoveRumble", "Hint.NoiseRemoval", "Hint.SoftenSibilance",
                                    "Hint.EvenOutSpeech", "Hint.Tone", "Hint.AutomaticLoudness" };
            for (int i = 0; i < p.Stages.Length && i < stageHints.Length; i++)
                HintSystem.Attach(p.Stages[i], stageHints[i]);

            f.Shown += (s, e) => { f.ActiveControl = p.Playback; f.SelectNextControl(p.Playback, true, true, true, false); };

            f.ResumeLayout();
            canvas.Rebuild();
        }

        /// <summary>The reading page: the same shell, the same glass down the left
        /// third, and the reading groups as stickers down the other two. There is
        /// no master switch here — nothing on this page gates anything else — so
        /// the strip the audio page uses for it simply is not there, and the
        /// groups start at the top.
        ///
        /// <para>The groups are taken as they come rather than named one by one,
        /// because unlike the six audio cells they are self-contained: each is a
        /// whole subject (speech, braille, on-screen text) and their order on the
        /// page is already the order they should be read in.</para></summary>
        private static void ApplyTextPage(PropertiesForm f, PropParts p, TabPage page)
        {
            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, DialogSkin.H);

            var groups = new List<GroupBox>();
            var move = new List<Control>();
            // Iterating Controls gives the order they were ADDED, which is already
            // Speech, Braille, Visual — reversing it, as this did at first, turned
            // the tab order upside down.
            foreach (Control c in page.Controls) move.Add(c);
            foreach (Control c in move)
            {
                page.Controls.Remove(c);
                f.Controls.Add(c);
                c.BringToFront();
                GroupBox g = c as GroupBox;
                if (g != null) groups.Add(g);
            }
            p.Tabs.Visible = false;

            canvas.Wells.Add(DialogSkin.InfoPanel);
            DialogSkin.AsGlass(p.TextInfo, DialogSkin.InfoGlass);

            // Stack them full width, each keeping the height it was built with, so
            // nothing inside has to be moved. What is left over goes to the gaps.
            // Three rows filling the column, each keeping its own proportions: the
            // slack is shared out in proportion to how tall each group was built,
            // so Speech — which has six settings to Braille's two — stays the tall
            // one instead of every row being forced to the same height.
            const int gap = 12;
            int used = 0;
            foreach (GroupBox g in groups) used += g.Height;
            int spare = DialogSkin.ButtonsY - 24 - used - gap * Math.Max(0, groups.Count - 1);
            int y = 12;
            for (int i = 0; i < groups.Count; i++)
            {
                GroupBox g = groups[i];
                int share = used > 0 ? (int)((long)spare * g.Height / used) : 0;
                int h = i == groups.Count - 1 ? DialogSkin.ButtonsY - 12 - y : g.Height + share;
                DialogSkin.AsSticker(g, new Rectangle(DialogSkin.ColA, y, 628, h));
                foreach (Control c in g.Controls) DialogSkin.OnGlass(c);
                y += h + gap;
            }

            // ONE value column for the whole page, not one per group. Measured
            // per group it came out in three different places — Speech pushed
            // right by "Reading speed (words per minute):", Braille barely past
            // "Braille table:" — and the three stacked boxes read as a ragged
            // edge down the page. The widest label on the page sets it for all
            // of them, so every value on the reading page starts at the same x.
            int column = 0;
            foreach (GroupBox g in groups) column = Math.Max(column, LabelColumn(g));
            foreach (GroupBox g in groups) PlaceValues(g, column);

            DialogSkin.AsKey(p.Cancel, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            HintSystem.Clear();
            for (int i = 0; i < groups.Count; i++)
                HintSystem.Attach(groups[i], "Hint.Text" + i);

            for (int i = 0; i < groups.Count; i++) groups[i].TabIndex = i;
            p.TextInfo.TabIndex = 20;
            p.OK.TabIndex = 21;
            p.Cancel.TabIndex = 22;

            f.Shown += (s, e) =>
            {
                if (groups.Count > 0)
                    f.SelectNextControl(groups[0], true, true, true, false);
            };

            f.ResumeLayout();
            canvas.Rebuild();
        }

        /// <summary>Where this group's values would have to start to clear its
        /// own captions. A label longer than the column it was laid out for ends
        /// up underneath the control it names — "Reading speed (words per
        /// minute):" swallowed its own spin box, arrows and all, and the value
        /// could not be seen. AutoSize is what makes the measurement real: the
        /// labels were built at a fixed width and would otherwise all report the
        /// same one.</summary>
        private static int LabelColumn(GroupBox g)
        {
            int column = 0;
            foreach (Control c in g.Controls)
            {
                Label l = c as Label;
                if (l == null) continue;
                l.AutoSize = true;
                column = Math.Max(column, l.Right + 10);
            }
            return column;
        }

        /// <summary>Moves every labelled value in the group to <paramref
        /// name="column"/>. Pushing each trapped control aside on its own did
        /// clear the overlap, but left the values in a ragged line — Reading
        /// speed shunted right while Volume and Pitch stayed where they were —
        /// so it is one column or nothing. A control with no label beside it
        /// (the check box that gates the group, the ? in the corner) is left
        /// exactly where it was.</summary>
        private static void PlaceValues(GroupBox g, int column)
        {
            if (column <= 0) return;

            var labels = new List<Label>();
            var others = new List<Control>();
            foreach (Control c in g.Controls)
            {
                Label l = c as Label;
                if (l != null) labels.Add(l);
                else if (!(c is Button)) others.Add(c);   // the ? key sits in the corner
            }

            foreach (Control c in others)
            {
                bool labelled = false;
                foreach (Label l in labels)
                    if (c.Top < l.Bottom && l.Top < c.Bottom) { labelled = true; break; }
                if (!labelled || c.Left == column) continue;
                if (column + c.Width > g.Width - 14) continue;   // no room; leave it
                c.Left = column;
            }
        }

        private static void OnMetal(Control c)
        {
            if (c == null) return;
            c.BackColor = Color.Transparent;
            c.ForeColor = NewPlayerSkin.Jet;
            c.Font = DialogSkin.FBody;
        }

        /// <summary>Recolour the innards and nothing else. A generic reflow was
        /// tried first and it pulled every label away from the control it labels —
        /// the cells were laid out by hand, pair by pair, and a loop that only
        /// knows "control" cannot put them back. The cells are now bigger than
        /// they were, so the old positions simply sit inside with room to spare;
        /// tightening them up is a hand job for later, not a loop.</summary>
        private static void Reflow(GroupBox g, bool wide)
        {
            if (g == null) return;
            foreach (Control c in g.Controls) DialogSkin.OnGlass(c);
        }
    }

    /// <summary>The metal a dialog is stamped from: panel, casing rim, the well the
    /// info glass sits in, and the contact shadow under everything sunk into it.
    /// Cached, like the player's — nothing here changes while the dialog lives.</summary>
    internal sealed class DialogCanvas : Control
    {
        private readonly Form owner;
        private Bitmap cached;
        public readonly List<Rectangle> Wells = new List<Rectangle>();

        public DialogCanvas(Form owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "";
        }

        public void Rebuild()
        {
            if (cached != null) { cached.Dispose(); cached = null; }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && cached != null) { cached.Dispose(); cached = null; }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (cached == null || cached.Width != Width || cached.Height != Height)
            {
                if (cached != null) cached.Dispose();
                if (Width < 8 || Height < 8) return;
                cached = new Bitmap(Width, Height);
                using (Graphics g = Graphics.FromImage(cached))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    PaintMetal(g);
                }
            }
            e.Graphics.DrawImageUnscaled(cached, 0, 0);
        }

        private void PaintMetal(Graphics g)
        {
            using (var br = new LinearGradientBrush(new Rectangle(0, -1, Width, Height + 2),
                       NewPlayerSkin.PanelLight, NewPlayerSkin.PanelDark, LinearGradientMode.Vertical))
            {
                var blend = new ColorBlend(4);
                blend.Colors = new[] { NewPlayerSkin.PanelLight, NewPlayerSkin.PanelMid,
                                       NewPlayerSkin.PanelMid, NewPlayerSkin.PanelDark };
                blend.Positions = new[] { 0f, 0.35f, 0.7f, 1f };
                br.InterpolationColors = blend;
                g.FillRectangle(br, 0, 0, Width, Height);
            }
            var rnd = new Random(7);
            for (int y = 0; y < Height; y += 2)
                using (var pen = new Pen(Color.FromArgb(rnd.Next(4, 10), Color.White)))
                    g.DrawLine(pen, 0, y, Width, y);

            // the casing rim
            using (var p = NewPlayerSkin.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1),
                       NewPlayerSkin.CaseRadius))
            using (var pen = new Pen(Color.FromArgb(215, 0x3C, 0x3C, 0x3A), 1.4f))
                g.DrawPath(pen, p);
            using (var p = NewPlayerSkin.Round(new RectangleF(2, 2, Width - 4, Height - 4),
                       NewPlayerSkin.CaseRadius - 2))
            using (var br = new LinearGradientBrush(new RectangleF(0, 0, Width, Height),
                       Color.FromArgb(235, 0xFF, 0xFF, 0xFC), Color.FromArgb(190, 0x76, 0x76, 0x72), 62f))
            using (var pen = new Pen(br, 1.8f))
                g.DrawPath(pen, p);

            foreach (Rectangle well in Wells)
            {
                NewPlayerSkin.ContactShadow(g, well, 6, 9);
                NewPlayerSkin.Recess(g, well, 6, null);
                using (var p = NewPlayerSkin.Round(well, 6))
                using (var br = new SolidBrush(NewPlayerSkin.Glass))
                    g.FillPath(br, p);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) NativeDrag.Begin(owner);
            else base.OnMouseDown(e);
        }
    }
}
