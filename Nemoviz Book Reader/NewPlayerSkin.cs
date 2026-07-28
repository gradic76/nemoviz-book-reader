using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The controls BuildUI made, handed to the skin so it can rearrange
    /// and repaint them. It never makes its own copies of anything: the same
    /// Button that carried the accessible name and the click handler under the
    /// classic look carries them under the new one.</summary>
    internal sealed class PlayerParts
    {
        public Panel Top, Bottom;
        public TextBox Info, VolumeField, SpeedField, ProgressField;
        public ComboBox Seek;
        public Label SeekLabel, VolumeLabel, SpeedLabel, ProgressLabel;
        public Button[] Left, Right;
        public Button Back, PlayPause, Forward;
    }

    /// <summary>
    /// The hi-fi front panel: a borderless 960 × 480 window, 424 × 424 of display
    /// glass on the left and a silver control panel on the right, everything
    /// sitting in a 4-unit groove. See CLAUDE.md 8k for the measurements and for
    /// why each of these decisions is what it is.
    ///
    /// <para><b>What the skin does NOT do</b> is as important as what it does. It
    /// creates no new commands, changes no accessible name, and adds nothing to
    /// the tab order that was not already there. The read-only fields that a
    /// screen reader reads (volume, speed, position, the info box) stay exactly
    /// as they were — they are simply moved off the visible area, the same trick
    /// the announce labels have always used, so the drawn panel can own the
    /// space while the accessible truth stays untouched.</para>
    /// </summary>
    internal static class NewPlayerSkin
    {
        public const int W = 960, H = 480;

        // ── the panel ────────────────────────────────────────────────
        public static readonly Color PanelLight = Color.FromArgb(0xD8, 0xD8, 0xD4);
        public static readonly Color PanelMid = Color.FromArgb(0xC0, 0xC0, 0xBC);
        public static readonly Color PanelDark = Color.FromArgb(0xA6, 0xA6, 0xA2);
        public static readonly Color Jet = Color.FromArgb(0x0A, 0x0A, 0x0A);

        // A key is lit along its crown, falls away to a dark belly, and picks up a
        // little bounced light at the very bottom — the half cylinder Gordan asked
        // for. Four stops, not two.
        public static readonly Color KeyTop = Color.FromArgb(0xF4, 0xF4, 0xF0);
        public static readonly Color KeyBelly = Color.FromArgb(0x8E, 0x8E, 0x8A);
        public static readonly Color KeyFoot = Color.FromArgb(0xB4, 0xB4, 0xB0);

        // ── the groove, and the focus that lives in it ───────────────
        public const int Groove = 5;
        public const int CaseRadius = 12;
        public static readonly Color GrooveShadow = Color.FromArgb(0x00, 0x00, 0x00);
        public static readonly Color GrooveLit = Color.FromArgb(0x3A, 0x3A, 0x38);
        public static readonly Color FocusGlow = Color.FromArgb(0xFF, 0xC1, 0x4A);

        /// <summary>Electric blue: the seconds marker on the ring, and the
        /// backlight that flashes round a key when it fires. Against the amber
        /// focus it measures only 1.3:1 in luminance, which sounds wrong until you
        /// notice that is the wrong test — these two are told apart by HUE, and
        /// amber against blue is the single safest pair there is for colour
        /// blindness. Amber against the phosphor green of the glass would not have
        /// been. On the near-black channel the blue itself runs 8.2:1.</summary>
        public static readonly Color Electric = Color.FromArgb(0x4F, 0xB8, 0xFF);
        public static readonly Color ElectricDeep = Color.FromArgb(0x16, 0x68, 0xD8);

        // ── the glass ────────────────────────────────────────────────
        public static readonly Color Glass = Color.FromArgb(0x0E, 0x12, 0x10);
        public static readonly Color Silk = Color.FromArgb(0x8A, 0x92, 0x8C);
        public static readonly Color Lit = Color.FromArgb(0xD8, 0xF0, 0xE0);
        public static readonly Color Amber = Color.FromArgb(0xF2, 0xC4, 0x6A);
        public static readonly Color Tile = Color.FromArgb(0x1A, 0x1E, 0x1C);
        public static readonly Color TileTop = Color.FromArgb(0x24, 0x28, 0x26);
        public static readonly Color DigitInk = Color.FromArgb(0xED, 0xEF, 0xEA);

        // ── geometry, all of it from CLAUDE.md 8k ────────────────────
        public static readonly Rectangle Bezel = new Rectangle(16, 16, 448, 448);
        public static readonly Rectangle GlassRect = new Rectangle(28, 28, 424, 424);
        public const int ColLeftX = 492, ColRightX = 840, CellW = 108;
        public const int BtnH = 36, CellPitch = 114;
        // The power key and its lamp take the top of the middle column, so the
        // ring gives up six units of radius and everything below it moves down.
        // Nothing else on the panel had room to spare.
        public static readonly Rectangle PowerFace = new Rectangle(715, 9, 30, 30);
        public static readonly Rectangle Led = new Rectangle(694, 18, 12, 12);
        public const int RingCx = 720, RingCy = 212;
        public const int RScaleOut = 98, RScaleIn = 88, RBandOut = 82, RBandIn = 54, RPlay = 50;
        public static readonly Rectangle SpeedSlot = new Rectangle(620, 62, 200, 12);
        public static readonly Rectangle SpeedLegend = new Rectangle(608, 86, 224, 22);
        public static readonly Rectangle ComboRect = new Rectangle(620, 322, 200, 32);
        public static readonly Rectangle BarRect = new Rectangle(620, 378, 200, 20);

        public static Font FLegend, FSilk, FValue, FFlap, FGlyph, FCombo;

        /// <summary>The canvas owns the flash state, because the backlight blooms
        /// OUTSIDE a key's bounds and a control cannot paint past its own edge.
        /// The key asks it what colour its well should be right now.</summary>
        internal static SkinCanvas Canvas;

        /// <summary>What lights the inside of a key's well: the firing flash while
        /// it lasts, otherwise focus, otherwise nothing. Firing wins — a key you
        /// just pressed is the more urgent of the two facts.</summary>
        internal static Color? RingFor(Control c)
        {
            float f = Canvas != null ? Canvas.FlashPhase(c) : -1f;
            if (f >= 0f)
                return Color.FromArgb((int)(255 * (1f - f * f)), Electric);
            if (c.Focused) return FocusGlow;
            return Canvas != null ? Canvas.SteadyRing(c) : null;
        }

        public static void Build(Form1 form)
        {
            PlayerParts p = form.SkinParts;

            FLegend = new Font("Segoe UI", 12f);
            FSilk = new Font("Segoe UI", 11f);
            FValue = new Font("Segoe UI", 14f);
            FFlap = new Font("Segoe UI Semibold", 32f);
            FGlyph = new Font("Segoe UI Symbol", 20f, FontStyle.Bold);
            FCombo = new Font("Segoe UI", 14f);

            form.SuspendLayout();

            form.FormBorderStyle = FormBorderStyle.None;
            form.ClientSize = new Size(W, H);
            form.BackColor = PanelMid;

            // A device has a casing; a borderless window otherwise ends in a hard
            // cut straight into the desktop and reads as a rectangle of pixels
            // rather than an object. Rounding the region does the corners, and the
            // canvas paints the rim.
            using (var casing = Round(new RectangleF(0, 0, W, H), CaseRadius))
                form.Region = new Region(casing);

            // Everything moves out of the two panels onto the form itself; the
            // panels then have nothing left to show.
            var move = new List<Control>();
            move.AddRange(p.Left);
            move.AddRange(p.Right);
            move.Add(p.Back); move.Add(p.PlayPause); move.Add(p.Forward);
            move.Add(p.Seek); move.Add(p.Info);
            move.Add(p.VolumeField); move.Add(p.SpeedField); move.Add(p.ProgressField);
            foreach (Control c in move)
            {
                if (c == null) continue;
                if (c.Parent != null) c.Parent.Controls.Remove(c);
                form.Controls.Add(c);
            }
            if (p.Top != null) p.Top.Visible = false;
            if (p.Bottom != null) p.Bottom.Visible = false;

            // The read-only fields keep doing their job — carrying the value in
            // AccessibleName and being reachable by Tab — from just below the
            // client area, where the drawn panel can have the space instead.
            Park(p.VolumeField, 0, H + 4);
            // Speed and position stay on the Tab route; the volume READOUT does not,
            // because the ring's two arrows already carry volume and speak on every
            // step. The cost is that volume can no longer be QUERIED without being
            // changed — worth knowing, since that is what the field was for.
            p.SpeedField.TabIndex = 6;
            p.ProgressField.TabIndex = 7;
            p.VolumeField.TabStop = false;
            Park(p.SpeedField, 0, H + 32);
            Park(p.ProgressField, 0, H + 60);
            Park(p.Info, 0, H + 88);
            // The info box leaves the tab order by agreement: it is reached with
            // the I key and by the reader's own review cursor, and keeping it out
            // means the arrows never have two owners.
            if (p.Info != null) p.Info.TabStop = false;

            // The canvas paints panel, glass, sliders and legends. It sits at the
            // very back; every real control stays on top of it and keeps its own
            // hit testing.
            var canvas = new SkinCanvas(form, p);
            Canvas = canvas;
            form.Controls.Add(canvas);
            canvas.SendToBack();

            LayOutButtons(form, p, canvas);
            LayOutRing(form, p, canvas);
            LayOutCombo(p);
            LayOutPower(form, canvas);

            // Focus starts on Play/Pause every time the window opens — it is the
            // one key everybody wants first. Coming back from another application
            // is a different case: there the window should be where you left it,
            // so the last focused control is remembered and restored.
            Control last = null;
            form.Shown += (s, e) => { form.ActiveControl = p.PlayPause; last = p.PlayPause; };
            form.Deactivate += (s, e) => { if (form.ActiveControl != null) last = form.ActiveControl; };
            form.Activated += (s, e) =>
            {
                if (last != null && last.CanSelect && !last.IsDisposed) form.ActiveControl = last;
            };

            form.ResumeLayout();
        }

        private static void Park(Control c, int x, int y)
        {
            if (c == null) return;
            c.Location = new Point(x, y);
        }

        // ── the eight side keys ──────────────────────────────────────

        // The legend printed on the panel is not always the button's full name.
        // "Manage Bookmarks" measures 148 units against a 100-unit cell and
        // "Set Bookmark" 107, so both get the short form — the same call Gordan
        // made in Croatian with Označi / Oznake. The full wording never leaves
        // AccessibleName, so a screen reader still says the whole thing.
        // Which key stands where, decided by Gordan. BuildUI's own grouping (app
        // keys left, book keys right) is not this order, so the columns are named
        // here rather than taken from the arrays it happens to hand over.
        //   A: Library, Settings, Properties, Help
        //   D: Go To..., Bookmark, Bookmarks, Timer
        private static void LayOutButtons(Form1 form, PlayerParts p, SkinCanvas canvas)
        {
            Button[] colA = { p.Left[0], p.Left[1], p.Right[0], p.Left[3] };
            string[] keyA = { "Btn.Library.Legend", "Btn.Settings.Legend",
                              "Btn.Properties.Legend", "Btn.Help.Legend" };
            Button[] colD = { p.Right[1], p.Right[2], p.Right[3], p.Left[2] };
            string[] keyD = { "Btn.GoTo.Legend", "Btn.SetBookmark.Legend",
                              "Btn.ManageBookmarks.Legend", "Btn.Timer.Legend" };

            for (int i = 0; i < 4; i++)
            {
                PlaceKey(colA[i], keyA[i], ColLeftX, 12 + i * CellPitch, canvas);
                PlaceKey(colD[i], keyD[i], ColRightX, 12 + i * CellPitch, canvas);
                // Tab reaches the transport first and the keys afterwards, in the
                // order they are read down column A and then down column D.
                colA[i].TabIndex = 20 + i;
                colD[i].TabIndex = 24 + i;
            }
            canvas.TimerKey = p.Left[2];
            {
            }
        }

        /// <summary>The control's bounds hold the face AND its groove, so a key
        /// draws its own recess and its own focus without the canvas having to
        /// know anything about it.</summary>
        private static void PlaceKey(Button b, string legendKey, int x, int y, SkinCanvas canvas)
        {
            if (b == null) return;
            string legend = Localization.T(legendKey);
            if (legend == legendKey) legend = b.Text;   // no short form defined
            canvas.Legends[b] = new LegendSpot(legend, new Rectangle(x, y + BtnH + 2 * Groove + 8, CellW, 24));
            b.Text = "";
            b.SetBounds(x, y, CellW, BtnH + 2 * Groove);
            Flatten(b);
            b.Paint += (s, e) => PaintKey(e.Graphics, b, new Rectangle(0, 0, b.Width, b.Height), 5);
            b.Click += (s, e) => canvas.Flash(b);
        }

        private static void Flatten(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.Transparent;
            b.FlatAppearance.MouseDownBackColor = Color.Transparent;
            b.BackColor = PanelMid;
            b.UseVisualStyleBackColor = false;
            b.SetStyle_DoubleBuffer();
        }

        /// <summary>Recess, then silver face, then the focus glow in the groove —
        /// which is the whole reason the groove exists.</summary>
        internal static void PaintKey(Graphics g, Control c, Rectangle bounds, int radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // The canvas has already laid the panel and this key's contact shadow
            // down behind us; we only repaint our own patch of it.
            using (var br = new SolidBrush(PanelMid)) g.FillRectangle(br, bounds);

            PaintOrigin = c.Location;
            var face = Rectangle.Inflate(bounds, -Groove, -Groove);
            Recess(g, face, radius, RingFor(c));
            SilverFace(g, face, radius);
            PaintOrigin = PointF.Empty;
        }

        // ── the ring ─────────────────────────────────────────────────

        private static void LayOutRing(Form1 form, PlayerParts p, SkinCanvas canvas)
        {
            // Up and down are volume, left and right are the seek step. The two
            // volume keys are the only controls the skin creates, and they are
            // built exactly like the ones BuildUI makes.
            Button up = MakeRingKey(form, "Btn.VolumeUp.Accessible", delegate { form.SkinVolume(+5); });
            Button right = p.Forward;
            Button down = MakeRingKey(form, "Btn.VolumeDown.Accessible", delegate { form.SkinVolume(-5); });
            Button left = p.Back;

            PlaceSector(up, 0, "▲", canvas);
            PlaceSector(right, 1, "▶", canvas);
            PlaceSector(down, 2, "▼", canvas);
            PlaceSector(left, 3, "◀", canvas);

            // Gordan's order: play, forward, back, up, down, then the seek step,
            // speed and position, then the eight keys.
            right.TabIndex = 1; left.TabIndex = 2; up.TabIndex = 3; down.TabIndex = 4;

            // Play / Pause in the middle. The glyph is drawn, not set in a font —
            // no typeface has a pause mark you can rely on.
            Button play = p.PlayPause;
            play.Text = "";
            play.SetBounds(RingCx - RBandIn, RingCy - RBandIn, RBandIn * 2, RBandIn * 2);
            Flatten(play);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(0, 0, RBandIn * 2, RBandIn * 2);
                play.Region = new Region(path);
            }
            play.Paint += (s, e) => PaintPlay(e.Graphics, play, form);
            play.Click += (s, e) => canvas.Flash(play);
            play.TabIndex = 0;
            play.BringToFront();
        }

        private static Button MakeRingKey(Form1 form, string nameKey, EventHandler click)
        {
            var b = new Button();
            b.Text = "";
            b.AccessibleName = Localization.T(nameKey);
            b.TabStop = true;
            b.Click += click;
            form.Controls.Add(b);
            return b;
        }

        /// <summary>One arrow key: a slice of the band. The Region is the slice,
        /// so the four keys can share one bounding box and the mouse still lands
        /// on exactly the one that was clicked.</summary>
        private static void PlaceSector(Button b, int quadrant, string glyph, SkinCanvas canvas)
        {
            if (b == null) return;
            b.Text = "";
            b.SetBounds(RingCx - RBandOut, RingCy - RBandOut, RBandOut * 2, RBandOut * 2);
            Flatten(b);
            float start = -90 + quadrant * 90 - 38;
            using (var path = SectorPath(RBandOut, RBandOut, RBandOut, RBandIn, start, 76))
                b.Region = new Region(path);
            b.Paint += (s, e) => PaintSector(e.Graphics, b, quadrant, glyph);
            b.Click += (s, e) => canvas.Flash(b);
            b.BringToFront();
        }

        private static void PaintSector(Graphics g, Button b, int quadrant, string glyph)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = RBandOut, cy = RBandOut;
            float start = -90 + quadrant * 90 - 38;

            using (var outer = SectorPath(cx, cy, RBandOut, RBandIn, start, 76))
            using (var br = new LinearGradientBrush(
                       new RectangleF(0, 0, RBandOut * 2, RBandOut * 2),
                       RingFor(b) ?? GrooveShadow, GrooveLit, 55f))
                g.FillPath(br, outer);

            // Each sector is lit as if it really were that part of a ring: the
            // gradient angle comes from where the sector sits under the lamp, not
            // from a shared vertical one. That is the cheap half of "radial
            // shading" and it is most of the effect.
            double mid = (-90 + quadrant * 90) * Math.PI / 180;
            PaintOrigin = new PointF(
                RingCx - RBandOut + (float)(Math.Cos(mid) * (RBandOut + RBandIn) / 2),
                RingCy - RBandOut + (float)(Math.Sin(mid) * (RBandOut + RBandIn) / 2));
            using (var inner = SectorPath(cx, cy, RBandOut - Groove, RBandIn + Groove, start + 2.6f, 70.8f))
            using (var br = SilverBrush(new RectangleF(0, -1, RBandOut * 2, RBandOut * 2 + 2)))
                g.FillPath(br, inner);
            PaintOrigin = PointF.Empty;

            double a = mid;
            float gx = cx + (float)(Math.Cos(a) * (RBandOut + RBandIn) / 2);
            float gy = cy + (float)(Math.Sin(a) * (RBandOut + RBandIn) / 2);
            DrawString(g, glyph, new RectangleF(gx - 20, gy - 18, 40, 36), FGlyph, Jet,
                       StringAlignment.Center, StringAlignment.Center);
        }

        private static void PaintPlay(Graphics g, Button b, Form1 form)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, RBandIn * 2, RBandIn * 2);
            var face = Rectangle.Inflate(bounds, -Groove, -Groove);
            PaintOrigin = b.Location;
            Recess(g, bounds, RBandIn, RingFor(b));
            SilverFace(g, face, RPlay);
            PaintOrigin = PointF.Empty;

            float cx = RBandIn, cy = RBandIn;
            using (var br = new SolidBrush(Jet))
            {
                if (form.SkinIsPlaying)
                {
                    // Playing: the press would pause, so the key shows pause.
                    g.FillRectangle(br, cx - 15, cy - 20, 10, 40);
                    g.FillRectangle(br, cx + 5, cy - 20, 10, 40);
                }
                else
                {
                    using (var tri = new GraphicsPath())
                    {
                        tri.AddPolygon(new[]
                        {
                            new PointF(cx - 13, cy - 21),
                            new PointF(cx + 20, cy),
                            new PointF(cx - 13, cy + 21)
                        });
                        g.FillPath(br, tri);
                    }
                }
            }
        }

        // ── the power key ───────────────────────────────────────────

        /// <summary>With no title bar there is no X, so the panel carries its own
        /// power key. It goes <b>last in the tab order</b> on purpose: closing is
        /// the one thing nobody should reach by accident on the way somewhere
        /// else. Alt+F4 still works as it always did.</summary>
        private static void LayOutPower(Form1 form, SkinCanvas canvas)
        {
            var b = new Button();
            b.Text = "";
            b.AccessibleName = Localization.T("Btn.Power.Accessible");
            // Out of the tab order by decision: anyone who can see it can click
            // it, anyone who cannot has Alt+F4, and a keyboard user could never
            // reach a title bar X either — so nothing is lost and the one
            // irreversible key on the panel cannot be hit on the way past.
            b.TabStop = false;

            b.SetBounds(PowerFace.X - Groove, PowerFace.Y - Groove,
                        PowerFace.Width + 2 * Groove, PowerFace.Height + 2 * Groove);
            form.Controls.Add(b);
            Flatten(b);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(0, 0, b.Width, b.Height);
                b.Region = new Region(path);
            }
            b.Paint += (s, e) => PaintPower(e.Graphics, b);
            b.Click += (s, e) => { canvas.Flash(b); form.Close(); };
            b.BringToFront();
        }

        private static void PaintPower(Graphics g, Button b)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, b.Width, b.Height);
            var face = Rectangle.Inflate(bounds, -Groove, -Groove);
            PaintOrigin = b.Location;
            Recess(g, face, face.Width / 2f, RingFor(b));
            SilverFace(g, face, face.Width / 2f);
            PaintOrigin = PointF.Empty;

            // The standby mark, drawn rather than set in a font: a broken ring
            // with a bar standing in the gap. No typeface can be relied on for it.
            float cx = b.Width / 2f, cy = b.Height / 2f, r = face.Width * 0.30f;
            using (var pen = new Pen(Jet, 2.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -62, 304);
                g.DrawLine(pen, cx, cy - r * 1.25f, cx, cy - r * 0.05f);
            }
        }

        // ── the seek-step combo, drawn as a little screen ────────────

        private static void LayOutCombo(PlayerParts p)
        {
            ComboBox cb = p.Seek;
            if (cb == null) return;
            cb.SetBounds(ComboRect.X, ComboRect.Y, ComboRect.Width, ComboRect.Height);
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.FlatStyle = FlatStyle.Flat;
            cb.BackColor = Glass;
            cb.ForeColor = Lit;
            cb.Font = FCombo;
            cb.ItemHeight = 26;
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.TabIndex = 5;
            cb.DrawItem += (s, e) =>
            {
                bool on = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(on ? Color.FromArgb(0x1E, 0x2A, 0x24) : Glass))
                    e.Graphics.FillRectangle(br, e.Bounds);
                if (e.Index < 0) return;
                string text = cb.Items[e.Index].ToString();
                var r = new RectangleF(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
                if (on) LitString(e.Graphics, text, r, FCombo, Lit);
                else DrawString(e.Graphics, text, r, FCombo, Color.FromArgb(0xA8, 0xC4, 0xB4),
                                StringAlignment.Near, StringAlignment.Center);
            };
            if (p.SeekLabel != null) p.SeekLabel.Visible = false;
            if (p.VolumeLabel != null) p.VolumeLabel.Visible = false;
            if (p.SpeedLabel != null) p.SpeedLabel.Visible = false;
            if (p.ProgressLabel != null) p.ProgressLabel.Visible = false;
        }

        // ── shared drawing ───────────────────────────────────────────

        internal static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            if (rad <= 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>The recess. What makes a groove read as a hole rather than an
        /// outline is not its colour but its two edges: the panel's cut edge is in
        /// shadow at the top-left, and its far lip catches the light at the
        /// bottom-right. Drawn as one flat ring it looks like a border — which is
        /// exactly what the first build looked like.</summary>
        /// <param name="lit">False on the display glass: a bright lip is the panel
        /// metal catching light, and on near-black glass it just draws a chrome
        /// outline round every flap tile.</param>
        /// <param name="ring">What to light the inside of the well with, if
        /// anything: amber while the key has focus, electric blue while it is
        /// firing. A key is not a switch and does not sink — it flashes.</param>
        internal static void Recess(Graphics g, RectangleF face, float rad, Color? ring, bool lit = true)
        {
            RectangleF outer = RectangleF.Inflate(face, Groove, Groove);

            // the lip: a light line just outside the well, low and to the right
            if (lit)
                using (var p = Round(RectangleF.Inflate(outer, 1f, 1f), rad + Groove + 1))
                using (var pen = new Pen(Color.FromArgb(210, 0xF2, 0xF2, 0xEE), 1.6f))
                {
                    g.TranslateTransform(0.9f, 1.2f);
                    g.DrawPath(pen, p);
                    g.TranslateTransform(-0.9f, -1.2f);
                }

            // the well itself
            using (var p = Round(outer, rad + Groove))
            using (var br = new LinearGradientBrush(outer,
                       Color.FromArgb(0x00, 0x00, 0x00), Color.FromArgb(0x4E, 0x4E, 0x4B), 55f))
                g.FillPath(br, p);

            // the cut edge, in shadow across the top and the left
            using (var p = Round(outer, rad + Groove))
            using (var pen = new Pen(Color.FromArgb(235, 0, 0, 0), 1.8f))
            {
                g.TranslateTransform(-0.7f, -0.9f);
                g.DrawPath(pen, p);
                g.TranslateTransform(0.7f, 0.9f);
            }

            // Focus and the firing flash both ride INSIDE the well so the key
            // still reads as the same key, only lit — replacing the whole recess
            // made it look like a different control altogether.
            if (ring.HasValue)
                using (var p = Round(RectangleF.Inflate(face, Groove * 0.5f, Groove * 0.5f), rad + Groove * 0.5f))
                using (var pen = new Pen(ring.Value, Groove * 0.9f))
                    g.DrawPath(pen, p);
        }

        /// <summary>A key is a half cylinder lying on its side: bright along the
        /// top where the light strikes it, falling away to a dark underside, with
        /// a little bounced light right at the bottom edge. A plain two-stop
        /// gradient gives a flat card instead, which is what the first build had.</summary>
        /// <summary>Where the light is, in design units — above the panel and off
        /// to the left. One lamp for the whole window: a key near it is lit from a
        /// different angle than one far from it, and that difference is most of
        /// what separates a rendered panel from a drawn one. Every key having the
        /// identical gradient was the giveaway in the first build.</summary>
        public static readonly PointF Light = new PointF(240f, -40f);

        /// <summary>Where the thing being painted sits in the window. A control
        /// paints in its own coordinates, but the lamp is nailed to the panel, so
        /// the angle has to be worked out in window space. Set by whoever is
        /// painting a control, left at zero for anything the canvas draws.</summary>
        [ThreadStatic] internal static PointF PaintOrigin;

        internal static float LightAngle(RectangleF r)
        {
            float dx = PaintOrigin.X + r.X + r.Width / 2 - Light.X;
            float dy = PaintOrigin.Y + r.Y + r.Height / 2 - Light.Y;
            return (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        }

        internal static LinearGradientBrush SilverBrush(RectangleF r)
        {
            var br = new LinearGradientBrush(r, KeyTop, KeyFoot, LightAngle(r));
            var blend = new ColorBlend(5);
            blend.Colors = new[] { KeyTop, PanelLight, PanelMid, KeyBelly, KeyFoot };
            blend.Positions = new[] { 0f, 0.16f, 0.46f, 0.86f, 1f };
            br.InterpolationColors = blend;
            return br;
        }

        internal static void SilverFace(Graphics g, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            {
                using (var br = SilverBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2)))
                    g.FillPath(br, p);

                Region saved = g.Clip;
                g.SetClip(p, CombineMode.Intersect);

                // A half cylinder falls away at its ends too — without this the
                // key is rounded top to bottom and cut off flat left and right.
                float end = Math.Min(r.Width * 0.28f, 22f);
                if (end > 2)
                {
                    using (var br = new LinearGradientBrush(
                               new RectangleF(r.Left - 1, r.Top, end + 1, r.Height),
                               Color.FromArgb(120, 0x5E, 0x5E, 0x5A), Color.FromArgb(0, 0x5E, 0x5E, 0x5A),
                               LinearGradientMode.Horizontal))
                        g.FillRectangle(br, r.Left, r.Top, end, r.Height);
                    using (var br = new LinearGradientBrush(
                               new RectangleF(r.Right - end, r.Top, end + 1, r.Height),
                               Color.FromArgb(0, 0x5E, 0x5E, 0x5A), Color.FromArgb(120, 0x5E, 0x5E, 0x5A),
                               LinearGradientMode.Horizontal))
                        g.FillRectangle(br, r.Right - end, r.Top, end, r.Height);
                }

                // The specular line along the crown, and the dark line under the
                // belly. The crown slides sideways depending on where the key
                // stands relative to the lamp.
                float slide = (PaintOrigin.X + r.X + r.Width / 2 - Light.X) / (float)W * r.Width * 0.18f;
                float inset = rad * 0.7f;
                using (var pen = new Pen(Color.FromArgb(235, 0xFF, 0xFF, 0xFC), 1.4f))
                    g.DrawLine(pen, r.Left + inset - slide, r.Top + 1.2f, r.Right - inset - slide, r.Top + 1.2f);
                using (var pen = new Pen(Color.FromArgb(150, 0x6E, 0x6E, 0x6A), 1.4f))
                    g.DrawLine(pen, r.Left + inset, r.Bottom - 1.4f, r.Right - inset, r.Bottom - 1.4f);
                g.Clip = saved;
            }
        }

        /// <summary>The soft dark that spills out of a recess onto the panel
        /// around it. GDI+ has no blur, so it is faked with a handful of strokes
        /// at falling alpha — which at this size is indistinguishable and costs
        /// nothing once the panel is cached. This single effect does more for
        /// "the part is really sunk into the metal" than any amount of gradient.</summary>
        internal static void ContactShadow(Graphics g, RectangleF outer, float rad, int reach = 7)
        {
            for (int i = reach; i >= 1; i--)
            {
                int alpha = (int)(26.0 * (1.0 - (i - 1) / (double)reach));
                if (alpha <= 0) continue;
                using (var p = Round(RectangleF.Inflate(outer, i, i), rad + i))
                using (var pen = new Pen(Color.FromArgb(alpha, 0x2A, 0x2A, 0x28), 2f))
                {
                    g.TranslateTransform(0.5f, 0.9f);
                    g.DrawPath(pen, p);
                    g.TranslateTransform(-0.5f, -0.9f);
                }
            }
        }

        internal static GraphicsPath SectorPath(float cx, float cy, float rOut, float rIn, float start, float sweep)
        {
            var p = new GraphicsPath();
            p.AddArc(cx - rOut, cy - rOut, rOut * 2, rOut * 2, start, sweep);
            p.AddArc(cx - rIn, cy - rIn, rIn * 2, rIn * 2, start + sweep, -sweep);
            p.CloseFigure();
            return p;
        }

        internal static void DrawString(Graphics g, string s, RectangleF r, Font f, Color c,
                                        StringAlignment h, StringAlignment v)
        {
            using (var sf = new StringFormat(StringFormatFlags.NoWrap)
            { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter })
            using (var br = new SolidBrush(c))
                g.DrawString(s, f, br, r, sf);
        }

        /// <summary>Lit text: a coloured halo behind it, the glyph itself always
        /// crisp — blurring the glyph would take exactly what low vision needs.</summary>
        internal static void LitString(Graphics g, string s, RectangleF r, Font f, Color c,
                                       StringAlignment h = StringAlignment.Near)
        {
            using (var sf = new StringFormat(StringFormatFlags.NoWrap)
            { Alignment = h, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            {
                using (var halo = new SolidBrush(Color.FromArgb(18, c)))
                    for (int i = 3; i >= 1; i -= 2)
                        for (int dx = -i; dx <= i; dx += i)
                            for (int dy = -i; dy <= i; dy += i)
                                g.DrawString(s, f, halo, new RectangleF(r.X + dx, r.Y + dy, r.Width, r.Height), sf);
                using (var br = new SolidBrush(c))
                    g.DrawString(s, f, br, r, sf);
            }
        }
    }

    internal struct LegendSpot
    {
        public readonly string Text;
        public readonly Rectangle Where;
        public LegendSpot(string text, Rectangle where) { Text = text; Where = where; }
    }

    internal static class ControlDoubleBuffer
    {
        /// <summary>Buttons that paint themselves flicker without this.</summary>
        public static void SetStyle_DoubleBuffer(this Control c)
        {
            typeof(Control).GetMethod("SetStyle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(c, new object[]
                {
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true
                });
        }
    }
}
