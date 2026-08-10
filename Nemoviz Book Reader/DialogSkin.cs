using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Which corner of the PLAYER a working dialog settles against.
    /// See <see cref="DialogSkin.AnchorToOwner"/>.</summary>
    internal enum DialogAnchor { BottomRight, BottomLeft }

    internal sealed class PropParts
    {
        public TextBox Info;
        public CheckBox Master, Bypass;
        public Button ResetAll, OK, Cancel;
        public GroupBox[] Stages;
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

        // THE PLAYBACK CELL IS GONE (2026-08-09, Gordan). It held volume and
        // speed, both of which are set from the player, remembered per book
        // regardless, and now READ in the info column beside this — so the
        // controls were a second way to do something already done, occupying the
        // one band of height the tone bands need.
        //
        // Its 76 units plus the 8 below it go straight into the three stage rows,
        // 28 each: 138 -> 166. The strip moves up to where it stood.
        public const int StripY = 12, StripH = 32;
        public static readonly int[] StageRowY = { 52, 228, 404 };
        public const int StageH = 166;
        public const int ButtonsY = 578, ButtonW = 112, ButtonH = 36;

        /// <summary>Where everything on a Properties AUDIO page goes, worked out
        /// from the space available rather than written down twice.
        ///
        /// <para>The hand-tuned constants above turn out to derive from one
        /// another exactly — the info column is a fixed 296 because it was
        /// measured against real book text, and the two control columns then split
        /// what is left; the stage rows fill the height between the strip and the
        /// buttons. <c>For(960, 628, 570)</c> reproduces every constant above to
        /// the unit, which is what makes it safe to compute the numbers for a
        /// NARROWER space instead of guessing a second set.</para>
        ///
        /// <para>That narrower space is a hybrid book's tab page: the strip and
        /// the page border cost about 32 units of width and 100 of height, so the
        /// six stage cells come out at 117 rather than 138. They were built at 112
        /// and grew to 138 without their contents being re-tightened (§10b), so
        /// 117 is still room to spare — the page is where the un-tightened slack
        /// gets spent, not where something has to be cut.</para></summary>
        public struct PropGeom
        {
            public Rectangle InfoPanel, InfoGlass;
            public int ColA, ColB, ColW, StripY, StripH, StageH;
            public int[] StageRowY;

            /// <param name="w">Width of the space being laid out in.</param>
            /// <param name="contentH">Where the content ends — the info column runs
            /// the whole way down to it.</param>
            /// <param name="rowsBottom">Where the last stage row must end. On the
            /// form that is above the buttons; on a page there are no buttons, so
            /// it is simply the foot of the page.</param>
            public static PropGeom For(int w, int contentH, int rowsBottom)
            {
                var g = new PropGeom();
                const int m = 12, infoW = 296, gap = 8, rowGap = 10;
                g.InfoPanel = new Rectangle(m, m, infoW, contentH - m);
                g.InfoGlass = new Rectangle(m + 17, m + 17, infoW - 34, contentH - m - 34);

                g.ColA = m + infoW + m;
                int colsW = (w - m) - g.ColA;
                g.ColW = (colsW - m) / 2;
                g.ColB = g.ColA + g.ColW + m;

                // No playback row any more — see PlaybackCell above. The strip
                // starts at the margin and the height it used to take is spread
                // across the three stage rows by the arithmetic below.
                g.StripY = m;
                g.StripH = 32;

                int firstRow = g.StripY + g.StripH + gap;
                g.StageH = Math.Max(96, ((rowsBottom - firstRow) - rowGap * 2) / 3);
                g.StageRowY = new[] { firstRow,
                                      firstRow + g.StageH + rowGap,
                                      firstRow + 2 * (g.StageH + rowGap) };
                return g;
            }
        }

        /// <summary>The owner-drawn tab strip, shared by Settings and by a hybrid
        /// book's Properties so the two cannot drift apart. The
        /// <see cref="TabControl"/> underneath stays a real one: a drawn strip
        /// would take away the tab role, the arrow navigation, and the "page 2 of
        /// 2" a screen reader announces.</summary>
        public const int TabW = 168, TabH = 30;

        /// <summary>Lays the master switch along the strip and leaves room at its
        /// end for the help key that goes beside it: the key sits 12 clear of
        /// whatever comes next, and the switch takes everything before it. Where
        /// the key ITSELF goes is <see cref="HelpKeyBounds"/>, so the two cannot
        /// disagree.</summary>
        public static void MasterWithHelpKey(Control master, int x, int y, int h, int nextLeft)
        {
            if (master == null) return;
            master.SetBounds(x, y, Math.Max(120, HelpKeyBounds(nextLeft, y).X - 6 - x), h);
        }

        /// <summary>The 22-unit help key, standing 12 clear of what follows it.</summary>
        public static Rectangle HelpKeyBounds(int nextLeft, int stripY)
        {
            return new Rectangle(nextLeft - 12 - 22, stripY + 5, 22, 22);
        }

        public static void StyleTabStrip(TabControl tabs)
        {
            if (tabs == null) return;
            EnsureFonts();
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(TabW, TabH);
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem -= PaintTab;
            tabs.DrawItem += PaintTab;
            tabs.Font = FBody;
        }

        private static void PaintTab(object sender, DrawItemEventArgs e)
        {
            var tabs = sender as TabControl;
            if (tabs == null || e.Index < 0 || e.Index >= tabs.TabPages.Count) return;

            Graphics g = e.Graphics;
            bool on = e.Index == tabs.SelectedIndex;
            Rectangle r = e.Bounds;

            using (var br = new SolidBrush(on ? Sticker : NewPlayerSkin.Glass))
                g.FillRectangle(br, r);
            using (var pen = new Pen(StickerEdge))
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);

            // The selected tab is lit, the rest silkscreened — the same two levels
            // the display glass uses, so the page you are on reads at a glance
            // without colour being the only thing carrying it.
            NewPlayerSkin.DrawString(g, tabs.TabPages[e.Index].Text,
                new RectangleF(r.X, r.Y, r.Width, r.Height),
                FBody, on ? NewPlayerSkin.Lit : NewPlayerSkin.Silk,
                StringAlignment.Center, StringAlignment.Center);
        }

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
            return Shell(f, W, height);
        }

        /// <summary>The same face at any width — for the smaller working dialogs
        /// (Go To, Sleep Timer, Manage Bookmarks, the archive password prompt),
        /// which have no business being 960 wide just because Properties is.</summary>
        public static DialogCanvas Shell(Form f, int width, int height)
        {
            EnsureFonts();
            f.FormBorderStyle = FormBorderStyle.None;
            f.ClientSize = new Size(width, height);
            f.BackColor = NewPlayerSkin.PanelMid;
            using (var casing = NewPlayerSkin.Round(new RectangleF(0, 0, width, height), NewPlayerSkin.CaseRadius))
                f.Region = new Region(casing);

            var canvas = new DialogCanvas(f);
            f.Controls.Add(canvas);
            canvas.SendToBack();
            return canvas;
        }

        /// <summary>A plain read-only, tabbable, word-wrapped TextBox with no
        /// position or colour yet — the shape a screen reader can walk line by
        /// line (a Label is never visited by Tab, the lesson the hint boxes and
        /// the info glass both already learned). Finish it with <see
        /// cref="AsGlass"/> at whatever rectangle the layout needs.</summary>
        public static TextBox NewMessageBox(string text)
        {
            var t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.TabStop = true;
            t.WordWrap = true;
            t.Text = text;
            t.AccessibleName = text;
            return t;
        }

        /// <summary>Where a working dialog settles: not centered, but anchored to
        /// a corner of the PLAYER itself (Gordan, 2026-07-29) — list dialogs (Go
        /// To, Manage Bookmarks) to the bottom right, short ones (Sleep Timer, the
        /// archive password prompt) to the bottom left, so each family always
        /// opens in its own zone and is learned once. The bottom edge is the one
        /// thing both families share, which is what keeps this a single
        /// convention rather than two unrelated ones.
        /// <para>Clamped to the working area of whichever screen the owner is on,
        /// so a smaller display never has the dialog land partly off-screen — the
        /// 13" laptop case in §10b is the one actually measured to be tight.</para>
        /// </summary>
        public static void AnchorToOwner(Form dlg, DialogAnchor anchor, int marginX = 24, int marginY = 24)
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Load += (s, e) =>
            {
                Rectangle refRect = dlg.Owner != null ? dlg.Owner.Bounds : Screen.FromControl(dlg).WorkingArea;
                int x = anchor == DialogAnchor.BottomRight
                    ? refRect.Right - dlg.Width - marginX
                    : refRect.Left + marginX;
                int y = refRect.Bottom - dlg.Height - marginY;

                Rectangle wa = Screen.FromRectangle(refRect).WorkingArea;
                x = Math.Max(wa.Left, Math.Min(x, wa.Right - dlg.Width));
                y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - dlg.Height));
                dlg.Location = new Point(x, y);
            };
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
            // The real metal, not a flat fill — see DialogCanvas.Backdrop.
            DialogCanvas.Backdrop(g, b, new Rectangle(0, 0, b.Width, b.Height));
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
    /// <para><b>A hybrid book (two tabs) drops to the classic dialog</b> — and
    /// that is unreachable today, not merely undone. Two tabs need
    /// <c>IsTextBook</c> AND <c>Chapters.Count &gt; 0</c>, but a book is only
    /// called a text book when its folder has NO audio, and chapters are built
    /// from audio. The condition contradicts itself, and §8c has text+audio DAISY
    /// importing as plain audio with its text unused, so no such book exists.
    /// Measured across a real 15-book library: every one is audio-only or
    /// text-only, none both.</para>
    ///
    /// <para><b>Whoever makes hybrids possible has to do this in the same
    /// breath</b>, or Properties will silently look like a different application
    /// the first time one is opened. It is not a small change: both paths below
    /// MOVE their controls off the tab page onto the form and hide the strip, so
    /// a hybrid needs versions that lay out INSIDE a page instead.</para>
    /// </summary>
    internal static class PropertiesSkin
    {
        public static void Apply(PropertiesForm f)
        {
            PropParts p = f.SkinParts;
            if (p == null || p.Tabs == null || p.Tabs.TabPages.Count == 0) return;
            if (p.Tabs.TabPages.Count > 1) { ApplyHybrid(f, p); return; }

            // Which page is the single one? A text-only book has no playback group
            // and its stage cells were built but never put on a page — running the
            // audio layout there walked straight into a null. The page decides.
            TabPage only = p.Tabs.TabPages[0];
            if (p.TextInfo != null && only.Controls.Contains(p.TextInfo))
            {
                ApplyTextPage(f, p, only);
                return;
            }
            if (p.Stages == null) return;

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

            for (int i = 0; i < p.Stages.Length; i++)
                DialogSkin.AsSticker(p.Stages[i], new Rectangle(
                    i % 2 == 0 ? DialogSkin.ColA : DialogSkin.ColB,
                    DialogSkin.StageRowY[i / 2], DialogSkin.ColW, DialogSkin.StageH));

            foreach (GroupBox g in p.Stages) Reflow(g, false);

            // The strip on the metal between the playback sticker and the stages.
            // The switch gives up the end of its run so its ? can stand beside it
            // — it is the only control on the strip with anything to explain, and
            // the six stages under it are what it explains. The width is worked
            // out backwards from where Bypass starts, not written down, because
            // the same code lays out a hybrid's narrower page.
            OnMetal(p.Master);
            DialogSkin.MasterWithHelpKey(p.Master, DialogSkin.ColA, DialogSkin.StripY,
                                         DialogSkin.StripH, 624);
            DialogSkin.AsSwitch(p.Bypass, new Rectangle(624, DialogSkin.StripY, 200, DialogSkin.StripH));
            DialogSkin.AsKey(p.ResetAll, new Rectangle(836, DialogSkin.StripY, 112, DialogSkin.StripH));

            DialogSkin.AsKey(p.Cancel, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            // Tab order: the master switch first now that playback has gone,
            // then what it gates, then the read-out, then the buttons.
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
            HintSystem.Attach(p.Master, "Hint.SoundProcessing", p.Master.Parent,
                              DialogSkin.HelpKeyBounds(624, DialogSkin.StripY),
                              Localization.T("Prop.SoundProcessing"));
            string[] stageHints = { "Hint.RemoveRumble", "Hint.NoiseRemoval", "Hint.SoftenSibilance",
                                    "Hint.EvenOutSpeech", "Hint.Tone", "Hint.AutomaticLoudness" };
            for (int i = 0; i < p.Stages.Length && i < stageHints.Length; i++)
                HintSystem.Attach(p.Stages[i], stageHints[i]);

            // Focus starts on the master switch — it used to start on the
            // playback group, which no longer exists, and the switch is what a
            // reader opened this page to reach.
            f.Shown += (s, e) => { try { f.ActiveControl = p.Master; } catch { } };

            f.ResumeLayout();
            canvas.Rebuild();
        }

        /// <summary>A HYBRID book — narrated audio plus the same words as text —
        /// gets both pages, and unlike the two single-page paths it lays them out
        /// <b>inside</b> the tab pages instead of moving the controls onto the
        /// form. The strip has something to show here, so it stays.
        ///
        /// <para>The reading page is not decoration on a book that plays itself
        /// (Gordan, 2026-07-30): the voice, pitch and volume still decide how a
        /// word looked up on demand is spoken, and braille and on-screen output
        /// are switched on there. So both pages carry their full contents.</para>
        ///
        /// <para>Each page gets its own <see cref="DialogCanvas"/> rather than
        /// showing the form's: a TabPage paints its own background over whatever
        /// is behind it, so the metal has to be drawn <i>on the page</i>. The
        /// canvas still takes the form as its owner, so dragging the window by the
        /// metal keeps working inside a page.</para></summary>
        private static void ApplyHybrid(PropertiesForm f, PropParts p)
        {
            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas shell = DialogSkin.Shell(f, DialogSkin.H);

            TabControl tabs = p.Tabs;
            tabs.SetBounds(DialogSkin.Rim, DialogSkin.Rim,
                           DialogSkin.W - 2 * DialogSkin.Rim,
                           DialogSkin.ButtonsY - 2 * DialogSkin.Rim);
            DialogSkin.StyleTabStrip(tabs);
            tabs.TabIndex = 0;

            // From the TabControl, never off a TabPage: inside SuspendLayout the
            // pages have not been resized yet and still answer with what they were
            // built at — the mistake that laid every Settings group out half a page
            // wide with its values clipped off the edge.
            int pw = tabs.Width - 8, ph = tabs.Height - DialogSkin.TabH - 8;
            var geom = DialogSkin.PropGeom.For(pw, ph, ph - 8);

            HintSystem.Clear();
            foreach (TabPage page in tabs.TabPages)
            {
                page.UseVisualStyleBackColor = false;
                page.BackColor = NewPlayerSkin.PanelMid;
                page.AutoScroll = false;   // the room is made below; nothing scrolls
                var canvas = new DialogCanvas(f);
                page.Controls.Add(canvas);
                canvas.SendToBack();

                // Which page is which is decided by what is ON it, not by index —
                // the same rule the single-page path uses, and it survives someone
                // adding a page or changing the order later.
                if (p.TextInfo != null && page.Controls.Contains(p.TextInfo))
                    LayOutReadingPage(p, page, canvas, geom, ph);
                else
                    LayOutAudioPage(p, page, canvas, geom, ph - 8);
                canvas.Rebuild();
            }

            DialogSkin.AsKey(p.Cancel, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            p.OK.TabIndex = 21;
            p.Cancel.TabIndex = 22;
            p.OK.BringToFront();
            p.Cancel.BringToFront();

            // Focus starts on the tab strip, not inside a page: on a book that has
            // two, which one you are on is the first thing to know.
            f.Shown += (s, e) => { f.ActiveControl = tabs; };

            f.ResumeLayout();
            shell.Rebuild();
        }

        /// <summary>The audio page laid out where it stands, on the tab page.
        /// Same shapes as the full-form version at the geometry the narrower space
        /// allows.</summary>
        private static void LayOutAudioPage(PropParts p, TabPage page, DialogCanvas canvas,
                                            DialogSkin.PropGeom geom, int pageBottom)
        {
            if (p.Stages == null || p.Info == null) return;

            canvas.Wells.Add(geom.InfoPanel);
            DialogSkin.AsGlass(p.Info, geom.InfoGlass);


            // ROWS ARE AS TALL AS WHAT STANDS IN THEM — not all alike, which is
            // what the full-form layout can afford and a tab page cannot. Five of
            // the six cells hold a title and one combo; Tone holds three spin
            // rows. Giving all six the same height cost the page 20 units it did
            // not have and clipped Treble off the bottom edge.
            //
            // Each cell is asked what it needs by measuring its own children,
            // rather than trusting the 112 they were all built at — that number
            // is the same for every cell and so cannot tell them apart.
            int rows = (p.Stages.Length + 1) / 2;
            var rowH = new int[rows];
            for (int i = 0; i < p.Stages.Length; i++)
            {
                int bottom = 0;
                foreach (Control c in p.Stages[i].Controls)
                {
                    // NOT c.Visible: this runs inside SuspendLayout on a page that
                    // is not the selected one, and Visible reports the whole
                    // chain — every child answers false, every row measures empty,
                    // and the six cells collapse to a stack of title bars.
                    Button b = c as Button;
                    if (b == null || !HintSystem.IsHelpKey(b))
                        bottom = Math.Max(bottom, c.Bottom);
                }
                rowH[i / 2] = Math.Max(rowH[i / 2], bottom + 14);
            }

            int gap = 10, used = 0;
            foreach (int h in rowH) used += h;
            int avail = pageBottom - geom.StageRowY[0];
            if (used + gap * (rows - 1) > avail) gap = 6;
            // Still over: take it off the tallest row rather than off all of them,
            // since the short ones have nothing left to give.
            int over = (used + gap * (rows - 1)) - avail;
            while (over > 0)
            {
                int t = 0;
                for (int r = 1; r < rows; r++) if (rowH[r] > rowH[t]) t = r;
                int take = Math.Min(over, Math.Max(1, rowH[t] / 20));
                rowH[t] -= take;
                over -= take;
            }

            // Slack goes into the GAPS, not into the boxes — growing a box does not
            // move anything inside it, so it only buys a band of dead glass under
            // its last row. The same rule the reading page already follows.
            int spare = avail - (used + gap * (rows - 1));
            if (spare > 0 && rows > 1) gap += Math.Min(24, spare / (rows - 1));

            int y = geom.StageRowY[0];
            for (int r = 0; r < rows; r++)
            {
                for (int i = r * 2; i < p.Stages.Length && i < r * 2 + 2; i++)
                    DialogSkin.AsSticker(p.Stages[i], new Rectangle(
                        i % 2 == 0 ? geom.ColA : geom.ColB, y, geom.ColW, rowH[r]));
                y += rowH[r] + gap;
            }

            foreach (GroupBox g in p.Stages) Reflow(g, false);

            OnMetal(p.Master);
            int right = geom.ColB + geom.ColW;
            DialogSkin.MasterWithHelpKey(p.Master, geom.ColA, geom.StripY, geom.StripH, right - 312);
            DialogSkin.AsSwitch(p.Bypass, new Rectangle(right - 312, geom.StripY, 200, geom.StripH));
            DialogSkin.AsKey(p.ResetAll, new Rectangle(right - 112, geom.StripY, 112, geom.StripH));

            p.Master.TabIndex = 1;
            p.ResetAll.TabIndex = 2;
            p.Bypass.TabIndex = 3;
            for (int i = 0; i < p.Stages.Length; i++) p.Stages[i].TabIndex = 4 + i;
            p.Info.TabIndex = 20;

            EventHandler gate = (s, e) =>
            {
                p.ResetAll.TabStop = p.Master.Checked;
                p.Bypass.TabStop = p.Master.Checked;
            };
            p.Master.CheckedChanged += gate;
            gate(null, EventArgs.Empty);

            // The same set as the single-page audio path, and for the same
            // reasons — a hybrid's audio page is that page.
            HintSystem.Attach(p.Master, "Hint.SoundProcessing", p.Master.Parent,
                              DialogSkin.HelpKeyBounds(right - 312, geom.StripY),
                              Localization.T("Prop.SoundProcessing"));
            string[] stageHints = { "Hint.RemoveRumble", "Hint.NoiseRemoval", "Hint.SoftenSibilance",
                                    "Hint.EvenOutSpeech", "Hint.Tone", "Hint.AutomaticLoudness" };
            for (int i = 0; i < p.Stages.Length && i < stageHints.Length; i++)
                HintSystem.Attach(p.Stages[i], stageHints[i]);
        }

        /// <summary>The reading page laid out where it stands. Same stacking rule
        /// as the full-form version — snug boxes, slack into the gaps — measured
        /// against the page's height rather than the form's.</summary>
        private static void LayOutReadingPage(PropParts p, TabPage page, DialogCanvas canvas,
                                              DialogSkin.PropGeom geom, int ph)
        {
            var groups = new List<GroupBox>();
            foreach (Control c in page.Controls)
            {
                GroupBox g = c as GroupBox;
                if (g != null) groups.Add(g);
            }

            canvas.Wells.Add(geom.InfoPanel);
            DialogSkin.AsGlass(p.TextInfo, geom.InfoGlass);

            int width = geom.ColB + geom.ColW - geom.ColA;

            // "Use visual output (show the text on screen while reading)" came out
            // as "…while readi". It was NOT overflowing the group — the check box
            // was built at a fixed width sized by hand for a narrower dialog, and
            // simply cuts its own caption off. So it is WIDENED to its row, the
            // same fix WidenLabels makes in the working dialogs, and only where
            // nothing else shares the row.
            foreach (GroupBox g in groups)
            {
                foreach (Control c in g.Controls)
                {
                    CheckBox cb = c as CheckBox;
                    if (cb == null || cb.AutoSize) continue;
                    bool alone = true;
                    foreach (Control other in g.Controls)
                        if (other != cb && !(other is Button)
                            && other.Top < cb.Bottom && cb.Top < other.Bottom) { alone = false; break; }
                    if (alone) cb.Width = width - cb.Left - 14;
                }
            }

            // Height comes from what each group HOLDS, not from the height it was
            // built at — the built number is padding the page cannot afford, and
            // measuring recovers enough of it that nothing has to be cut.
            var need = new int[groups.Count];
            int content = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                int bottom = 0;
                foreach (Control c in groups[i].Controls)
                {
                    Button b = c as Button;
                    if (b == null || !HintSystem.IsHelpKey(b)) bottom = Math.Max(bottom, c.Bottom);
                }
                need[i] = Math.Min(groups[i].Height, bottom + 14);
                content += need[i];
            }
            int n = Math.Max(1, groups.Count);

            // Even measured, three groups can be a little more than the page
            // holds. Take it off the TALLEST rather than off all of them — the
            // short ones have nothing left to give — so the last box keeps its
            // bottom edge instead of running off the foot of the page.
            int over = (12 + content + 6 * (n - 1)) - (ph - 8);
            while (over > 0)
            {
                int t = 0;
                for (int i = 1; i < n; i++) if (need[i] > need[t]) t = i;
                int take = Math.Min(over, Math.Max(1, need[t] / 20));
                need[t] -= take;
                content -= take;
                over -= take;
            }

            int slack = (ph - 12) - 24 - content;
            int pad = Math.Max(0, Math.Min(10, slack / (n * 2)));
            slack -= pad * n;
            int gap = n > 1 ? Math.Max(6, slack / (n - 1)) : 12;
            int y = 12;
            for (int i = 0; i < groups.Count; i++)
            {
                int h = need[i] + pad;
                DialogSkin.AsSticker(groups[i], new Rectangle(geom.ColA, y, width, h));
                foreach (Control c in groups[i].Controls) DialogSkin.OnGlass(c);
                y += h + gap;
            }

            int column = 0;
            foreach (GroupBox g in groups) column = Math.Max(column, LabelColumn(g));

            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].TabIndex = i;
                // BY IDENTITY WHERE THE GROUP SAYS SO, not by position (2026-08-04).
                // "Hint.Text" + i was safe only while every group was always
                // present, and the braille-source group is not: it appears for a
                // book that came from a braille file and for no other. Without
                // this the visual group would inherit the braille group's help the
                // moment it moved up a slot — the wrong text under the right
                // button, which is worse than no text.
                string key = groups[i].Tag as string;
                HintSystem.Attach(groups[i],
                    !string.IsNullOrEmpty(key) && key.StartsWith("Hint.") ? key : "Hint.Text" + i);
            }

            // AFTER the keys exist — see the reading page for why.
            foreach (GroupBox g in groups) PlaceValues(g, column);

            p.TextInfo.TabIndex = 20;
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

            // Stack them full width, each keeping the height it was built with so
            // nothing inside has to be moved, and give the slack to the GAPS.
            // Sharing it out into the boxes instead — which is what this did at
            // first — grows the box without moving the controls, so all it buys
            // is a band of dead glass under the last row of every group: about
            // 37 units under Pitch, and the same again under Background colour.
            // A box snug around its own contents, with air between the boxes, is
            // what makes the three read as three subjects.
            // Pad and gap are what is LEFT OVER, not fixed amounts. A group can
            // grow at runtime — the reading page's speech box does, when it has to
            // carry the "no voice for this language" line — and with a fixed 10
            // and 12 the stack simply ran past the bottom of the dialog and sat on
            // the OK button. Breathing room is the first thing to give up when
            // there is no room to breathe.
            // A check box built at a hand-picked width cuts its own caption off —
            // "…show the text on screen while readi". Widen the ones that own
            // their row, the same fix the hybrid page and the working dialogs
            // make. Found on the hybrid page and true here all along.
            const int textPageW = 628;
            foreach (GroupBox g in groups)
            {
                foreach (Control c in g.Controls)
                {
                    CheckBox cb = c as CheckBox;
                    if (cb == null || cb.AutoSize) continue;
                    bool alone = true;
                    foreach (Control other in g.Controls)
                        if (other != cb && !(other is Button)
                            && other.Top < cb.Bottom && cb.Top < other.Bottom) { alone = false; break; }
                    if (alone) cb.Width = textPageW - cb.Left - 14;
                }
            }

            int content = 0;
            foreach (GroupBox g in groups) content += g.Height;
            int n = Math.Max(1, groups.Count);
            int slack = DialogSkin.ButtonsY - 24 - content;
            int pad = Math.Max(0, Math.Min(10, slack / (n * 2)));
            slack -= pad * n;
            int gap = n > 1 ? Math.Max(6, slack / (n - 1)) : 12;
            int y = 12;
            foreach (GroupBox g in groups)
            {
                int h = g.Height + pad;
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

            DialogSkin.AsKey(p.Cancel, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.OK, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            HintSystem.Clear();
            for (int i = 0; i < groups.Count; i++)
                HintSystem.Attach(groups[i], "Hint.Text" + i);

            // AFTER the keys exist, never before. PlaceValues now measures the
            // room left by the group's help key, and it cannot measure a button
            // that has not been added yet — which is exactly why the first
            // attempt at this changed nothing at all.
            foreach (GroupBox g in groups) PlaceValues(g, column);

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
        internal static int LabelColumn(GroupBox g)
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
        // Internal, and shared with SettingsSkin: the two dialogs lay their groups
        // out the same way, and a second copy of this would drift from the first.
        internal static void PlaceValues(GroupBox g, int column)
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

            int margin = g.Width - 14;

            // KEEP CLEAR OF THE HELP KEY. It sits in the group's own top-right
            // corner (HintSystem.Attach puts it at Width-30, y 4, 22 x 22), and a
            // value stretched to `margin` runs straight underneath it: on the
            // Speech page the first combo's top-right corner and the key's
            // bottom-left corner were overlapping outright. Reported twice —
            // once before the ? was made to fit its button, which changed the
            // glyph and not the collision.
            //
            // Every row in a group with a key gives up the same width, not just
            // the row that collides: a single short combo in a column of long
            // ones looks like a mistake, where a column that stops 34 short of
            // the edge looks like the column.
            foreach (Control c in g.Controls)
                if (c is Button && HintSystem.IsHelpKey((Button)c)) { margin = c.Left - 8; break; }

            foreach (Control c in others)
            {
                bool labelled = false;
                foreach (Label l in labels)
                    if (c.Top < l.Bottom && l.Top < c.Bottom) { labelled = true; break; }
                if (!labelled) continue;

                // A combo takes the rest of the row. The cells were laid out for
                // a narrower dialog, so at 628 wide every value stopped a third
                // short of the right edge and each box looked half empty. A spin
                // box keeps its own width — a three-digit number does not need
                // 340 units, and stretching it would only move its arrows away
                // from the digits they belong to.
                ComboBox cb = c as ComboBox;
                if (cb != null && margin - column >= 120)
                {
                    cb.Left = column;
                    cb.Width = margin - column;
                    continue;
                }
                if (c.Left == column) continue;
                if (column + c.Width > margin) continue;   // no room; leave it
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

        /// <summary>The canvas a control on this dialog should take its backdrop
        /// from. Set once the canvas exists; null under the classic look.</summary>
        internal static DialogCanvas Active;

        /// <summary>Hands a control the piece of the real metal it stands on.
        ///
        /// <para>The same fault the panel keys had, and the same cure: a button
        /// that fills its own rectangle with one flat colour leaves a square
        /// patch at each corner outside its rounded bed, where the dialog's metal
        /// is a gradient. Gordan saw it as "OK and Cancel should have rounded
        /// beds" — the bed IS round, but the flat corners around it read as a
        /// square one. Blitting the cached layer is exact, because it IS the
        /// metal.</para></summary>
        internal static void Backdrop(Graphics g, Control c, Rectangle bounds)
        {
            DialogCanvas dc = Active;
            if (dc == null || c == null || dc.cached == null
                || dc.cached.Width < 8 || dc.cached.Height < 8)
            {
                using (var br = new SolidBrush(NewPlayerSkin.PanelMid)) g.FillRectangle(br, bounds);
                return;
            }
            Point at = c.Location;
            Rectangle src = new Rectangle(at.X + bounds.X, at.Y + bounds.Y, bounds.Width, bounds.Height);
            if (src.Right > dc.cached.Width || src.Bottom > dc.cached.Height
                || src.X < 0 || src.Y < 0)
            {
                using (var br = new SolidBrush(NewPlayerSkin.PanelMid)) g.FillRectangle(br, bounds);
                return;
            }
            g.DrawImage(dc.cached, bounds, src, GraphicsUnit.Pixel);
        }

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
                // The buttons draw before or after this depending on z-order, so
                // whoever asks for a backdrop must find a layer that is already
                // built. Repaint them once it is.
                Active = this;
                foreach (Control c in Parent == null ? new Control.ControlCollection(this) : Parent.Controls)
                    if (c is Button) c.Invalidate();
            }
            Active = this;
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
