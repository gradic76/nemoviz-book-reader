using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Everything on the new player that is not a control: the silver panel, the
    /// grooves, the key legends, the display glass with its silkscreened labels
    /// and split-flap numbers, the two sliders, and the seconds ring around Play.
    ///
    /// <para>The glass is rendered <b>from the info box's own text</b> rather than
    /// from the player's internals, so there is exactly one place that decides
    /// what a book shows. Whatever a screen reader would read out is what gets
    /// drawn — they cannot drift apart.</para>
    /// </summary>
    internal sealed class SkinCanvas : Control
    {
        private readonly Form1 form;
        private readonly PlayerParts parts;
        private readonly Timer tick;

        /// <summary>The legend printed under each key, and where.</summary>
        public readonly Dictionary<Button, LegendSpot> Legends = new Dictionary<Button, LegendSpot>();

        // The seconds marker. It steps once a second while something is playing
        // and stands still when it is not, which is what makes it double as a
        // state indicator right on the Play key.
        private int second;
        private DateTime lastSecond = DateTime.Now;

        // The transient volume readout: volume has no permanent slot on the
        // glass, so it appears for a moment on change, like an amplifier.
        private string volumeFlash;
        private DateTime volumeFlashUntil;

        private static readonly Regex TimeLike = new Regex(@"^-?\d{1,3}:\d{2}(:\d{2})?$",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        public SkinCanvas(Form1 form, PlayerParts parts)
        {
            this.form = form;
            this.parts = parts;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Fill;
            TabStop = false;
            // Nothing here is a control, so nothing here should be in the tree a
            // screen reader walks.
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "";

            if (parts.Info != null) parts.Info.TextChanged += (s, e) => Invalidate();
            if (parts.ProgressField != null) parts.ProgressField.TextChanged += (s, e) => Invalidate();
            if (parts.SpeedField != null) parts.SpeedField.TextChanged += (s, e) => Invalidate();
            if (parts.VolumeField != null)
                parts.VolumeField.TextChanged += (s, e) =>
                {
                    volumeFlash = parts.VolumeField.AccessibleName;
                    volumeFlashUntil = DateTime.Now.AddSeconds(2);
                    Invalidate();
                };

            // Twenty ticks a second, but a full repaint only once a second. The
            // in-between ticks exist solely for the lamp's breath, and they
            // repaint a 36-unit square — not the panel.
            tick = new Timer();
            tick.Interval = 50;
            tick.Tick += (s, e) =>
            {
                if ((DateTime.Now - lastSecond).TotalMilliseconds >= 1000)
                {
                    lastSecond = DateTime.Now;
                    if (form.SkinIsPlaying) second = (second + 1) % 60;
                    Invalidate();
                }
                else if (form.SkinSleepActive)
                {
                    Invalidate(Rectangle.Inflate(NewPlayerSkin.Led, 12, 12));
                    if (TimerKey != null)
                    {
                        Invalidate(Rectangle.Inflate(TimerKey.Bounds, 16, 16));
                        TimerKey.Invalidate();
                    }
                }
            };
            tick.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tick != null) { tick.Stop(); tick.Dispose(); }
                if (flipTimer != null) { flipTimer.Stop(); flipTimer.Dispose(); }
                if (flashTimer != null) { flashTimer.Stop(); flashTimer.Dispose(); }
                DropStatic();
            }
            base.Dispose(disposing);
        }

        // The panel, its contact shadows, the legends, the bezel and the ring's
        // scale never change. They are drawn once into this and blitted after
        // that — without it, every tick of the seconds marker would repaint a
        // few hundred gradient strokes.
        private Bitmap staticLayer;

        /// <summary>Hands a control the piece of the REAL panel it is standing on.
        ///
        /// <para><b>Why this exists</b> (Gordan, 2026-08-10: "corners of the
        /// buttons are curved and the corners of the button beds are
        /// rectangular"). A key's well is round, but the control it is drawn in
        /// is a rectangle, and <c>PaintKey</c> began by filling that whole
        /// rectangle with flat <c>PanelMid</c>. Outside the round well that left
        /// a square patch of one flat colour where the panel has a vertical
        /// gradient, the brushed-metal lines and the inner edge of the key's own
        /// contact shadow — so the eye reads a square bed under a round key.
        ///
        /// Blitting the cached layer is exact by construction: what the corner
        /// shows is what the panel shows, because it IS the panel.</para></summary>
        internal void DrawBackdrop(Graphics g, Control c, Rectangle bounds)
        {
            EnsureStatic();
            if (staticLayer == null || c == null)
            {
                using (var br = new SolidBrush(NewPlayerSkin.PanelMid)) g.FillRectangle(br, bounds);
                return;
            }
            Point at = c.Location;
            g.DrawImage(staticLayer,
                        bounds,
                        new Rectangle(at.X + bounds.X, at.Y + bounds.Y, bounds.Width, bounds.Height),
                        GraphicsUnit.Pixel);
        }

        private void DropStatic()
        {
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
        }

        private void EnsureStatic()
        {
            if (staticLayer != null && staticLayer.Width == Width && staticLayer.Height == Height) return;
            DropStatic();
            if (Width < 8 || Height < 8) return;

            staticLayer = new Bitmap(Width, Height);
            using (Graphics g = Graphics.FromImage(staticLayer))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                PaintPanel(g);
                PaintContactShadows(g);
                PaintLedSocket(g);

                PaintGlassWell(g);
                PaintSlots(g);
                PaintRingScale(g);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            EnsureStatic();
            if (staticLayer != null) g.DrawImageUnscaled(staticLayer, 0, 0);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            PaintLegends(g);
            PaintFlashes(g);
            PaintGlassContentAndSheen(g);
            PaintSpeedKnob(g);
            PaintBar(g);
            PaintRingMarker(g);
            PaintLed(g);
        }

        // ── the firing backlight ─────────────────────────────────────
        // A key here is not a switch and has no on and off, so it does not sink
        // when pressed. Instead the light behind it comes on for a moment: the
        // well goes electric blue and the glow blooms out onto the silver around
        // it. Fires on the mouse and on Enter/Space, which is Button.Click.
        private readonly Dictionary<Control, DateTime> firing = new Dictionary<Control, DateTime>();
        private Timer flashTimer;
        private const int FlashMs = 260;

        public void Flash(Control c)
        {
            if (c == null) return;
            firing[c] = DateTime.Now;
            if (flashTimer == null)
            {
                flashTimer = new Timer();
                flashTimer.Interval = 16;
                flashTimer.Tick += (s, e) =>
                {
                    bool any = false;
                    foreach (var pair in new List<KeyValuePair<Control, DateTime>>(firing))
                    {
                        if ((DateTime.Now - pair.Value).TotalMilliseconds >= FlashMs)
                            firing.Remove(pair.Key);
                        else any = true;
                        Invalidate(Rectangle.Inflate(pair.Key.Bounds, 22, 22));
                        pair.Key.Invalidate();
                    }
                    if (!any) flashTimer.Stop();
                };
            }
            flashTimer.Start();
            Invalidate(Rectangle.Inflate(c.Bounds, 22, 22));
            c.Invalidate();
        }

        /// <summary>The sleep-timer key, so its backlight can stay on while the
        /// countdown runs — the one key on the panel that has a lasting state.</summary>
        public Button TimerKey;

        /// <summary>How brightly the countdown backlight is burning right now,
        /// 0 when there is no countdown. It breathes on the SAME clock as the
        /// power lamp, so the two pulse together rather than drifting against
        /// each other, which would read as two unrelated things blinking.</summary>
        public float CountdownGlow
        {
            get
            {
                if (!form.SkinSleepActive) return 0f;
                double t = (DateTime.Now - ledEpoch).TotalMilliseconds % BreatheMs / BreatheMs;
                return 0.22f + 0.78f * (float)((1 - Math.Cos(t * 2 * Math.PI)) / 2);
            }
        }

        /// <summary>What a key's well should glow with when it is neither firing
        /// nor focused. Only the timer key has such a state.</summary>
        public Color? SteadyRing(Control c)
        {
            if (c == null || c != TimerKey) return null;
            float lit = CountdownGlow;
            return lit <= 0f ? (Color?)null
                             : Color.FromArgb((int)(210 * lit), NewPlayerSkin.Electric);
        }

        /// <summary>0 at the instant it fired, 1 as it dies, -1 when not firing.</summary>
        public float FlashPhase(Control c)
        {
            DateTime start;
            if (c == null || !firing.TryGetValue(c, out start)) return -1f;
            double ms = (DateTime.Now - start).TotalMilliseconds;
            return ms >= FlashMs ? -1f : (float)(ms / FlashMs);
        }

        private void PaintFlashes(Graphics g)
        {
            // The countdown's own backlight: the same bloom as a firing key, but
            // it stays and breathes instead of dying away.
            float steady = CountdownGlow;
            if (steady > 0f && TimerKey != null)
                for (int i = 1; i <= 12; i++)
                {
                    int alpha = (int)(46 * steady * (1f - i / 13f));
                    if (alpha <= 1) continue;
                    using (var p = NewPlayerSkin.Round(RectangleF.Inflate(TimerKey.Bounds, i, i), 10 + i))
                    using (var pen = new Pen(Color.FromArgb(alpha, NewPlayerSkin.ElectricDeep), 2f))
                        g.DrawPath(pen, p);
                }

            foreach (var pair in firing)
            {
                float t = FlashPhase(pair.Key);
                if (t < 0) continue;
                RectangleF b = pair.Key.Bounds;
                float radius = pair.Key.Width == pair.Key.Height ? pair.Key.Width / 2f : 10f;
                // The bloom spreads as it fades, which is what light does.
                for (int i = 1; i <= 14; i++)
                {
                    int alpha = (int)(70 * (1f - t) * (1f - i / 15f));
                    if (alpha <= 1) continue;
                    using (var p = NewPlayerSkin.Round(RectangleF.Inflate(b, i + t * 8, i + t * 8), radius + i))
                    using (var pen = new Pen(Color.FromArgb(alpha, NewPlayerSkin.ElectricDeep), 2f))
                        g.DrawPath(pen, p);
                }
            }
        }

        /// <summary>The soft dark spilling out of every recess onto the panel.
        /// Drawn here rather than by each key, because it falls OUTSIDE the key's
        /// own bounds and a control cannot paint past its edge.</summary>
        private void PaintContactShadows(Graphics g)
        {
            foreach (Button b in Legends.Keys)
                NewPlayerSkin.ContactShadow(g, b.Bounds, 9);

            NewPlayerSkin.ContactShadow(g,
                new RectangleF(NewPlayerSkin.RingCx - NewPlayerSkin.RScaleOut,
                               NewPlayerSkin.RingCy - NewPlayerSkin.RScaleOut,
                               NewPlayerSkin.RScaleOut * 2, NewPlayerSkin.RScaleOut * 2),
                NewPlayerSkin.RScaleOut, 9);
            NewPlayerSkin.ContactShadow(g, NewPlayerSkin.Bezel, 6, 9);
            NewPlayerSkin.ContactShadow(g, NewPlayerSkin.ComboRect, 4, 5);
            NewPlayerSkin.ContactShadow(g, NewPlayerSkin.SpeedSlot, 6, 5);
            NewPlayerSkin.ContactShadow(g, NewPlayerSkin.BarRect, 5, 5);
        }

        // ── the silver ───────────────────────────────────────────────

        private void PaintPanel(Graphics g)
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
            // brushed metal, barely there
            var rnd = new Random(7);
            for (int y = 0; y < Height; y += 2)
                using (var pen = new Pen(Color.FromArgb(rnd.Next(4, 10), Color.White)))
                    g.DrawLine(pen, 0, y, Width, y);

            PaintCasing(g);
        }

        /// <summary>The rim of the casing: a bright edge along the top and left
        /// where the light strikes the moulding, a dark one along the bottom and
        /// right, and a thin dark line right at the cut so the window has an
        /// outline against whatever is behind it.</summary>
        private void PaintCasing(Graphics g)
        {
            var edge = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var p = NewPlayerSkin.Round(edge, NewPlayerSkin.CaseRadius))
            using (var pen = new Pen(Color.FromArgb(215, 0x3C, 0x3C, 0x3A), 1.4f))
                g.DrawPath(pen, p);

            var inner = new RectangleF(2f, 2f, Width - 4, Height - 4);
            using (var p = NewPlayerSkin.Round(inner, NewPlayerSkin.CaseRadius - 2))
            using (var br = new LinearGradientBrush(new RectangleF(0, 0, Width, Height),
                       Color.FromArgb(235, 0xFF, 0xFF, 0xFC), Color.FromArgb(190, 0x76, 0x76, 0x72), 62f))
            using (var pen = new Pen(br, 1.8f))
                g.DrawPath(pen, p);
        }

        private void PaintLegends(Graphics g)
        {
            foreach (KeyValuePair<Button, LegendSpot> kv in Legends)
                NewPlayerSkin.DrawString(g, LiveLegend(g, kv.Key, kv.Value), kv.Value.Where,
                    NewPlayerSkin.FLegend, NewPlayerSkin.Jet,
                    StringAlignment.Center, StringAlignment.Center);

            // Under its slider now, not above it — the power key took the top,
            // and under is what the eight keys and the progress bar already do.
            NewPlayerSkin.DrawString(g, Localization.T("Player.Speed.Legend"),
                NewPlayerSkin.SpeedLegend, NewPlayerSkin.FLegend, NewPlayerSkin.Jet,
                StringAlignment.Center, StringAlignment.Center);

            NewPlayerSkin.DrawString(g, Localization.T("Player.Progress.Legend"),
                new Rectangle(608, 406, 224, 22), NewPlayerSkin.FLegend, NewPlayerSkin.Jet,
                StringAlignment.Center, StringAlignment.Center);
        }

        /// <summary>What is actually printed under a key. Normally the fixed
        /// legend, but a key whose Text the player changes at runtime — the sleep
        /// timer, which counts down on its own face — shows that instead. Without
        /// this the countdown was invisible under the new look: the legends were
        /// captured once at build time and never looked at the button again.
        /// If the live text will not fit the cell, only its last word is kept,
        /// which turns "Sleep Timer 14:59" into "14:59" rather than an ellipsis.</summary>
        private static string LiveLegend(Graphics g, Button b, LegendSpot spot)
        {
            string live = b.Text == null ? "" : b.Text.Trim();
            if (live.Length == 0) return spot.Text;
            if (g.MeasureString(live, NewPlayerSkin.FLegend).Width <= spot.Where.Width) return live;
            int cut = live.LastIndexOf(' ');
            return cut > 0 ? live.Substring(cut + 1) : live;
        }

        /// <summary>The socket the lamp sits in — cut into the metal once and
        /// never touched again, so it stays on the cached layer.</summary>
        private void PaintLedSocket(Graphics g)
        {
            RectangleF led = NewPlayerSkin.Led;
            NewPlayerSkin.ContactShadow(g, led, led.Width / 2f, 4);
            using (var p = NewPlayerSkin.Round(RectangleF.Inflate(led, 2.5f, 2.5f), led.Width / 2f + 2.5f))
            using (var br = new LinearGradientBrush(RectangleF.Inflate(led, 3f, 3f),
                       Color.FromArgb(0x00, 0x00, 0x00), Color.FromArgb(0x4E, 0x4E, 0x4B), 55f))
                g.FillPath(br, p);
        }

        /// <summary>The lamp itself. **Steady** while the app is simply running,
        /// and **breathing** — a slow fade up and down, never a hard blink — while
        /// a sleep timer counts down. One lamp, two states, no second colour: an
        /// active timer is otherwise invisible to anyone not using a screen
        /// reader. The cycle is deliberately slow; a hard blink at this size is
        /// the kind of motion that nags at the edge of vision while you read.</summary>
        private void PaintLed(Graphics g)
        {
            RectangleF led = NewPlayerSkin.Led;
            float lit = 1f;
            if (form.SkinSleepActive)
            {
                double t = (DateTime.Now - ledEpoch).TotalMilliseconds % BreatheMs / BreatheMs;
                lit = 0.22f + 0.78f * (float)((1 - Math.Cos(t * 2 * Math.PI)) / 2);
            }

            for (int i = 9; i >= 3; i -= 3)
                using (var br = new SolidBrush(Color.FromArgb((int)(40 * lit), NewPlayerSkin.Electric)))
                    g.FillEllipse(br, led.X + led.Width / 2 - i, led.Y + led.Height / 2 - i, i * 2, i * 2);

            Color top = Blend(Color.FromArgb(0x18, 0x26, 0x32), Color.FromArgb(0xBC, 0xE4, 0xFF), lit);
            Color bottom = Blend(Color.FromArgb(0x0C, 0x16, 0x24), NewPlayerSkin.ElectricDeep, lit);
            using (var br = new LinearGradientBrush(led, top, bottom, 60f))
                g.FillEllipse(br, led);
            using (var br = new SolidBrush(Color.FromArgb((int)(210 * lit), 0xFF, 0xFF, 0xFF)))
                g.FillEllipse(br, led.X + 3f, led.Y + 2.6f, 3.2f, 3.2f);
        }

        private const double BreatheMs = 2800;
        private readonly DateTime ledEpoch = DateTime.Now;

        private static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // ── the glass ────────────────────────────────────────────────

        private void PaintGlassWell(Graphics g)
        {
            NewPlayerSkin.Recess(g, NewPlayerSkin.Bezel, 6, null);
            using (var p = NewPlayerSkin.Round(NewPlayerSkin.Bezel, 6))
            using (var br = new SolidBrush(Color.FromArgb(0x05, 0x07, 0x06)))
                g.FillPath(br, p);

            Rectangle glass = NewPlayerSkin.GlassRect;
            using (var br = new LinearGradientBrush(
                       new Rectangle(glass.X, glass.Y - 1, glass.Width, glass.Height + 2),
                       Color.FromArgb(0x14, 0x18, 0x16), NewPlayerSkin.Glass, LinearGradientMode.Vertical))
                g.FillRectangle(br, glass);
        }

        private void PaintGlassContentAndSheen(Graphics g)
        {
            Rectangle glass = NewPlayerSkin.GlassRect;
            Region saved = g.Clip;
            g.SetClip(glass);
            try
            {
                PaintGlassContent(g, glass);

                if (volumeFlash != null && DateTime.Now < volumeFlashUntil)
                {
                    var strip = new Rectangle(glass.X, glass.Bottom - 64, glass.Width, 56);
                    using (var br = new SolidBrush(Color.FromArgb(232, 0x08, 0x0C, 0x0A)))
                        g.FillRectangle(br, strip);
                    NewPlayerSkin.LitString(g, volumeFlash, strip, NewPlayerSkin.FFlap,
                        NewPlayerSkin.Amber, StringAlignment.Center);
                }

                // Glass, not a black rectangle: a faint sheen falling across the
                // top-left corner, and the bezel's shadow darkening the edges.
                using (var br = new LinearGradientBrush(
                           new RectangleF(glass.X, glass.Y, glass.Width, glass.Height),
                           Color.FromArgb(20, 0xC8, 0xE8, 0xD8), Color.FromArgb(0, 0xC8, 0xE8, 0xD8), 38f))
                    g.FillRectangle(br, glass);

                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(glass.X - glass.Width * 0.28f, glass.Y - glass.Height * 0.28f,
                                    glass.Width * 1.56f, glass.Height * 1.56f);
                    using (var vig = new PathGradientBrush(path))
                    {
                        vig.CenterColor = Color.FromArgb(0, 0, 0, 0);
                        vig.SurroundColors = new[] { Color.FromArgb(120, 0, 0, 0) };
                        g.FillRectangle(vig, glass);
                    }
                }
            }
            finally { g.Clip = saved; }
        }

        /// <summary>Draws the info box's lines: the part before the first ": " is
        /// the silkscreened label, the rest is the lit value, and anything that
        /// looks like a time becomes split-flap tiles with the seconds dropped —
        /// real flip clocks never had them.</summary>
        private void PaintGlassContent(Graphics g, Rectangle glass)
        {
            string text = parts.Info != null ? parts.Info.Text : "";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            float x = glass.X, w = glass.Width, y = glass.Y;
            bool first = true;
            // The LABEL travels with the time, and until 2026-08-10 it did not.
            // PaintTimes printed "Time elapsed" and "Time remaining" from the
            // language file whatever the line actually said — so a multi-part
            // audio book, which lists "Part 3 elapsed", "Part 3 remaining",
            // "Time elapsed", "Time remaining", drew TWO blocks both captioned
            // Time elapsed / Time remaining. That is Gordan's "we see two time
            // elapsed segments", and the same invention left a lone trailing
            // clock captioned as elapsed when the reader could hear the info box
            // say remaining.
            var pendingTimes = new List<(string Label, string Value)>();

            for (int i = 0; i < lines.Length && y < glass.Bottom - 20; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                string label = null, value = line;
                int cut = line.IndexOf(": ", StringComparison.Ordinal);
                // The colon comes WITH the label. Splitting on ": " and keeping
                // the part before it threw the colon away, so the glass read
                // "Author" where the info box says "Author:" — and once the time
                // captions started carrying their own (they are drawn from the
                // line now) the two halves of the display no longer matched each
                // other. A colon is also what makes a label read as a label
                // rather than as a word standing on its own.
                if (cut > 0) { label = line.Substring(0, cut + 1); value = line.Substring(cut + 2).Trim(); }

                if (first)
                {
                    // The title needs no label, and is allowed two lines.
                    foreach (string part in Wrap(g, value, NewPlayerSkin.FValue, w, 2))
                    {
                        NewPlayerSkin.LitString(g, part, new RectangleF(x, y, w, 26), NewPlayerSkin.FValue, NewPlayerSkin.Lit);
                        y += 26;
                    }
                    first = false;
                    continue;
                }

                if (TimeLike.IsMatch(value))
                {
                    pendingTimes.Add((label, value));
                    // Elapsed and remaining share one block, side by side.
                    if (pendingTimes.Count == 2 || i == lines.Length - 1)
                    {
                        y = PaintTimes(g, x, y, w, pendingTimes);
                        pendingTimes.Clear();
                    }
                    continue;
                }

                if (label != null)
                {
                    NewPlayerSkin.DrawString(g, label, new RectangleF(x, y, 120, 22),
                        NewPlayerSkin.FSilk, NewPlayerSkin.Silk, StringAlignment.Near, StringAlignment.Center);
                    NewPlayerSkin.LitString(g, value, new RectangleF(x + 126, y, w - 126, 22),
                        NewPlayerSkin.FValue, NewPlayerSkin.Lit);
                }
                else
                {
                    NewPlayerSkin.LitString(g, value, new RectangleF(x, y, w, 22),
                        NewPlayerSkin.FValue, NewPlayerSkin.Lit);
                }
                y += 26;
            }

            if (pendingTimes.Count > 0) PaintTimes(g, x, y, w, pendingTimes);
        }

        /// <summary>One block of up to two clocks, each under ITS OWN label —
        /// the one the info box gave, never one this code chose. The flipper
        /// state is keyed on the side rather than on the words, so a book whose
        /// left clock is "Part 3 elapsed" animates exactly like one whose left
        /// clock is "Time elapsed".</summary>
        private float PaintTimes(Graphics g, float x, float y, float w,
                                 List<(string Label, string Value)> times)
        {
            NewPlayerSkin.DrawString(g, Caption(times[0].Label, "Player.Info.ElapsedLabel"),
                new RectangleF(x, y, 160, 20), NewPlayerSkin.FSilk, NewPlayerSkin.Silk,
                StringAlignment.Near, StringAlignment.Center);
            if (times.Count > 1)
                NewPlayerSkin.DrawString(g, Caption(times[1].Label, "Player.Info.RemainingLabel"),
                    new RectangleF(x + 170, y, 160, 20), NewPlayerSkin.FSilk, NewPlayerSkin.Silk,
                    StringAlignment.Near, StringAlignment.Center);
            // 24, not 20: at 20 the tile below started exactly where the
            // caption's descenders were still going, so "Part 12 elapsed:" lost
            // the tail of its p and its comma-like colon foot. Seen on a render,
            // which is the only way this kind of fault is ever seen.
            y += 24;
            Flaps(g, x, y, HoursMinutes(times[0].Value), "elapsed");
            if (times.Count > 1) Flaps(g, x + 170, y, HoursMinutes(times[1].Value), "remaining");

            // The bar's fraction comes from these two numbers, not from the
            // player's own counter: that one is per FILE and stops being
            // refreshed while playback is paused, so after a seek the blade sat
            // at zero while the display said two minutes in. Same source as
            // everything else on the glass, so it cannot disagree with it.
            //
            // The LAST block painted wins, and that is deliberate: a multi-part
            // book lists its part times first and the whole book's second, and
            // the bar is the whole book's.
            elapsedSec = Seconds(times[0].Value);
            remainingSec = times.Count > 1 ? Seconds(times[1].Value) : -1;
            return y + 66;
        }

        /// <summary>The line's own caption, falling back to the language file
        /// only when the line arrived without one — which is what every caption
        /// here used to do unconditionally.</summary>
        private static string Caption(string fromLine, string fallbackKey)
        {
            if (!string.IsNullOrEmpty(fromLine))
                return fromLine.EndsWith(":", StringComparison.Ordinal) ? fromLine : fromLine + ":";
            return Localization.T(fallbackKey);
        }

        // Elapsed and remaining, in seconds, as last drawn on the glass.
        private double elapsedSec = -1, remainingSec = -1;

        private static double Seconds(string t)
        {
            if (string.IsNullOrEmpty(t)) return -1;
            string[] bits = t.TrimStart('-', '+').Split(':');
            double total = 0;
            foreach (string b in bits)
            {
                int v;
                if (!int.TryParse(b, out v)) return -1;
                total = total * 60 + v;
            }
            return total;
        }

        /// <summary>"0:12:34" → "0:12". Seconds live on the ring, not here.</summary>
        private static string HoursMinutes(string t)
        {
            int last = t.LastIndexOf(':');
            int firstColon = t.IndexOf(':');
            return (last > firstColon) ? t.Substring(0, last) : t;
        }

        private const float TileW = 34, TileH = 56;

        // ── the flip ─────────────────────────────────────────────────
        // One state per readout ("elapsed", "remaining"): what is on the cards
        // now, what they are turning to, when the turn started, and which way.
        private sealed class Flipper
        {
            public string Shown = null, Target = null;
            public DateTime Start;
            public bool Up;
        }
        private readonly Dictionary<string, Flipper> flips = new Dictionary<string, Flipper>();
        private Timer flipTimer;
        private const int FlipMs = 160;

        /// <summary>Decides whether the cards turn or simply change. Only a step
        /// of ONE minute animates: a heading jump moves the clock by forty and
        /// that is not forty flips, it is a new number. This also covers seeking,
        /// which is why holding an arrow never sets off a queue of turns.</summary>
        private void Sync(string slot, string s)
        {
            Flipper f;
            if (!flips.TryGetValue(slot, out f)) flips[slot] = f = new Flipper();

            if (f.Shown == null) { f.Shown = f.Target = s; return; }
            if (f.Target == s) return;

            int before = Minutes(f.Target), after = Minutes(s);
            bool oneStep = f.Target.Length == s.Length && before >= 0 && after >= 0
                           && Math.Abs(after - before) == 1;
            if (!oneStep) { f.Shown = f.Target = s; return; }

            // Animation is a target, not a queue: a turn already under way is
            // abandoned at its destination rather than stacked behind the new one.
            f.Shown = f.Target;
            f.Target = s;
            f.Up = after < before;
            f.Start = DateTime.Now;
            StartFlipTimer();
        }

        private static int Minutes(string t)
        {
            if (t == null) return -1;
            string body = t.TrimStart('-', '+');
            string[] bits = body.Split(':');
            int a, b;
            if (bits.Length != 2 || !int.TryParse(bits[0], out a) || !int.TryParse(bits[1], out b))
                return -1;
            return a * 60 + b;
        }

        private void StartFlipTimer()
        {
            if (flipTimer == null)
            {
                flipTimer = new Timer();
                flipTimer.Interval = 16;
                flipTimer.Tick += (s, e) =>
                {
                    bool anyRunning = false;
                    foreach (Flipper f in flips.Values)
                        if (f.Shown != f.Target)
                        {
                            if ((DateTime.Now - f.Start).TotalMilliseconds >= FlipMs) f.Shown = f.Target;
                            else anyRunning = true;
                        }
                    Invalidate(NewPlayerSkin.GlassRect);
                    if (!anyRunning) flipTimer.Stop();
                };
            }
            flipTimer.Start();
        }

        private void Flaps(Graphics g, float x, float y, string s, string slot)
        {
            Sync(slot, s);
            Flipper f = flips[slot];
            float phase = f.Shown == f.Target
                ? 1f
                : (float)Math.Min(1.0, (DateTime.Now - f.Start).TotalMilliseconds / FlipMs);

            for (int i = 0; i < f.Target.Length; i++)
            {
                char c = f.Target[i];
                if (c == ':' || c == '-' || c == '+')
                {
                    NewPlayerSkin.LitString(g, c.ToString(), new RectangleF(x, y, 14, TileH),
                        NewPlayerSkin.FValue, NewPlayerSkin.Lit, StringAlignment.Center);
                    x += 15;
                    continue;
                }
                var card = new RectangleF(x, y, TileW, TileH);
                char from = (f.Shown != null && i < f.Shown.Length) ? f.Shown[i] : c;
                if (phase >= 1f || from == c) Card(g, card, c);
                else Flip(g, card, from, c, phase, f.Up);
                x += TileW + 3;
            }
        }

        /// <summary>One card at rest: the tile in its recess, the two flaps with
        /// the top one a shade lighter, the fold across the exact middle, and the
        /// two pivot pins. The pins are what make it read as a flip clock rather
        /// than a box with a line through it.</summary>
        private static void Card(Graphics g, RectangleF card, char c)
        {
            NewPlayerSkin.Recess(g, card, 3, null, false);
            using (var p = NewPlayerSkin.Round(card, 3))
            using (var br = new LinearGradientBrush(
                       new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2),
                       NewPlayerSkin.TileTop, NewPlayerSkin.Tile, LinearGradientMode.Vertical))
                g.FillPath(br, p);

            NewPlayerSkin.DrawString(g, c.ToString(), card, NewPlayerSkin.FFlap,
                NewPlayerSkin.DigitInk, StringAlignment.Center, StringAlignment.Center);

            Fold(g, card);
        }

        private static void Fold(Graphics g, RectangleF card)
        {
            float mid = card.Top + card.Height / 2;
            using (var pen = new Pen(Color.FromArgb(0x02, 0x03, 0x03), 1.6f))
                g.DrawLine(pen, card.Left + 1, mid - 0.4f, card.Right - 1, mid - 0.4f);
            using (var pen = new Pen(Color.FromArgb(70, 0xFF, 0xFF, 0xFF), 1f))
                g.DrawLine(pen, card.Left + 1, mid + 1.2f, card.Right - 1, mid + 1.2f);

            using (var br = new SolidBrush(Color.FromArgb(0x6E, 0x74, 0x70)))
            {
                g.FillEllipse(br, card.Left - 2.4f, mid - 2.4f, 4.8f, 4.8f);
                g.FillEllipse(br, card.Right - 2.4f, mid - 2.4f, 4.8f, 4.8f);
            }
        }

        /// <summary>Half a card, used by the flip: the leaf that falls carries the
        /// old digit's top, the leaf that rises carries the new digit's bottom.</summary>
        private static void Leaf(Graphics g, RectangleF card, char c, bool topHalf, float squash)
        {
            float mid = card.Top + card.Height / 2;
            var half = topHalf
                ? new RectangleF(card.X, card.Y, card.Width, card.Height / 2)
                : new RectangleF(card.X, mid, card.Width, card.Height / 2);

            GraphicsState st = g.Save();
            g.TranslateTransform(0, mid);
            g.ScaleTransform(1f, Math.Max(0.02f, squash));
            g.TranslateTransform(0, -mid);
            g.SetClip(half, CombineMode.Intersect);

            using (var p = NewPlayerSkin.Round(card, 3))
            using (var br = new LinearGradientBrush(
                       new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2),
                       NewPlayerSkin.TileTop, NewPlayerSkin.Tile, LinearGradientMode.Vertical))
                g.FillPath(br, p);
            NewPlayerSkin.DrawString(g, c.ToString(), card, NewPlayerSkin.FFlap,
                NewPlayerSkin.DigitInk, StringAlignment.Center, StringAlignment.Center);
            // the leaf's own edge catches a little light as it turns
            using (var pen = new Pen(Color.FromArgb(150, 0xFF, 0xFF, 0xFF), 1.2f))
                g.DrawLine(pen, card.Left + 1, topHalf ? mid - 1 : mid + 1,
                                card.Right - 1, topHalf ? mid - 1 : mid + 1);
            g.Restore(st);
        }

        private static void Flip(Graphics g, RectangleF card, char from, char to, float t, bool upwards)
        {
            // The half that is already settled shows through behind the leaf.
            NewPlayerSkin.Recess(g, card, 3, null, false);
            using (var p = NewPlayerSkin.Round(card, 3))
            using (var br = new LinearGradientBrush(
                       new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2),
                       NewPlayerSkin.TileTop, NewPlayerSkin.Tile, LinearGradientMode.Vertical))
                g.FillPath(br, p);

            float mid = card.Top + card.Height / 2;
            Region saved = g.Clip;
            g.SetClip(new RectangleF(card.X, card.Y, card.Width, card.Height / 2), CombineMode.Intersect);
            NewPlayerSkin.DrawString(g, (upwards ? from : to).ToString(), card, NewPlayerSkin.FFlap,
                NewPlayerSkin.DigitInk, StringAlignment.Center, StringAlignment.Center);
            g.Clip = saved;
            saved = g.Clip;
            g.SetClip(new RectangleF(card.X, mid, card.Width, card.Height / 2), CombineMode.Intersect);
            NewPlayerSkin.DrawString(g, (upwards ? to : from).ToString(), card, NewPlayerSkin.FFlap,
                NewPlayerSkin.DigitInk, StringAlignment.Center, StringAlignment.Center);
            g.Clip = saved;

            // First half the outgoing leaf folds down, then the incoming one
            // opens out. Going backwards it runs the other way round, so the
            // direction of a seek is visible without a single word.
            if (t < 0.5f)
                Leaf(g, card, from, !upwards, 1f - t * 2f);
            else
                Leaf(g, card, to, upwards, (t - 0.5f) * 2f);

            Fold(g, card);
        }

        private static IEnumerable<string> Wrap(Graphics g, string s, Font f, float width, int maxLines)
        {
            var outLines = new List<string>();
            string[] words = s.Split(' ');
            string line = "";
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (g.MeasureString(candidate, f).Width <= width) { line = candidate; continue; }
                if (line.Length > 0) outLines.Add(line);
                line = word;
                if (outLines.Count == maxLines - 1) break;
            }
            if (line.Length > 0 && outLines.Count < maxLines) outLines.Add(line);
            if (outLines.Count == 0) outLines.Add(s);
            return outLines;
        }

        // ── the two sliders ──────────────────────────────────────────

        /// <summary>Both slider tracks — they never move, so they belong to the
        /// cached layer.</summary>
        private void PaintSlots(Graphics g)
        {
            foreach (var pair in new[]
            {
                new { R = NewPlayerSkin.SpeedSlot, Rad = 6f },
                new { R = NewPlayerSkin.BarRect, Rad = 5f }
            })
            {
                NewPlayerSkin.Recess(g, pair.R, pair.Rad, null);
                using (var p = NewPlayerSkin.Round(pair.R, pair.Rad))
                using (var br = new LinearGradientBrush(
                           new Rectangle(pair.R.X, pair.R.Y - 1, pair.R.Width, pair.R.Height + 2),
                           Color.FromArgb(0x16, 0x1A, 0x18), Color.FromArgb(0x4A, 0x4A, 0x47),
                           LinearGradientMode.Vertical))
                    g.FillPath(br, p);
            }
        }

        private float PlayedFraction()
        {
            if (elapsedSec >= 0 && remainingSec >= 0 && elapsedSec + remainingSec > 0)
                return (float)Math.Max(0, Math.Min(1, elapsedSec / (elapsedSec + remainingSec)));
            return Math.Max(0f, Math.Min(1f, form.SkinProgress / 1000f));
        }

        private void PaintSpeedKnob(Graphics g)
        {
            Rectangle slot = NewPlayerSkin.SpeedSlot;
            float f = SpeedFraction();
            var knob = new RectangleF(slot.X + (slot.Width - 22) * f, slot.Y - 8, 22, 28);
            NewPlayerSkin.Recess(g, knob, 4, null);
            NewPlayerSkin.SilverFace(g, knob, 4);
            Grip(g, knob, 3);
        }

        /// <summary>The milled grip on a handle you are meant to take hold of.
        ///
        /// <para><b>Shared, since 2026-08-10.</b> The speed knob had it written
        /// inline and the progress blade had nothing, so one read as a machined
        /// part and the other as a flat card — Gordan: "speed looks like it has
        /// some texture on it, progress slider looks flat." Two handles doing the
        /// same job should be made of the same thing.</para>
        ///
        /// <para>The count is passed rather than fixed because the two are not
        /// the same width — 22 for the knob against 12 for the blade — and the
        /// spacing comes from the width, so the DENSITY matches even though the
        /// number of lines does not. Hard-coding the knob's ±5 would have put
        /// the blade's outer lines through its own rounded edge.</para></summary>
        private static void Grip(Graphics g, RectangleF r, int lines)
        {
            float step = r.Width / (lines + 1);
            float top = r.Y + r.Height * 0.25f, foot = r.Bottom - r.Height * 0.25f;
            for (int i = 1; i <= lines; i++)
            {
                float x = r.X + step * i;
                // A cut and the light catching its far wall: one line alone reads
                // as a drawn stripe, the pair reads as milling.
                using (var pen = new Pen(Color.FromArgb(0x50, 0x50, 0x4E)))
                    g.DrawLine(pen, x, top, x, foot);
                using (var pen = new Pen(Color.FromArgb(90, 0xFF, 0xFF, 0xFC)))
                    g.DrawLine(pen, x + 1, top, x + 1, foot);
            }
        }

        /// <summary>Read straight off the speed field, so the drawn knob and the
        /// spoken value can never disagree.</summary>
        private float SpeedFraction()
        {
            // Hundredths of a multiplier, 50..300, whichever kind of book is
            // loaded — a text book multiplies its voice's natural pace where an
            // audio book multiplies the recording's. It was words a minute for
            // text until 2026-08-23; see currentTextSpeed in Form1.
            double f = (form.SkinSpeedRaw - 50) / 150.0;
            return (float)Math.Max(0, Math.Min(1, f));
        }

        private void PaintBar(Graphics g)
        {
            Rectangle bar = NewPlayerSkin.BarRect;

            // While the blade is being dragged it shows where it is being taken,
            // not where playback still is.
            float f = grabbed == Grab.Bar ? dragFraction : PlayedFraction();
            if (f > 0.004f)
            {
                var done = new RectangleF(bar.X + 2, bar.Y + 2, (bar.Width - 4) * f, bar.Height - 4);
                using (var p = NewPlayerSkin.Round(done, 3))
                using (var br = new LinearGradientBrush(new RectangleF(done.X, done.Y - 1, done.Width, done.Height + 2),
                           Color.FromArgb(0x7A, 0xC0, 0x96), Color.FromArgb(0x3E, 0x7A, 0x5C), LinearGradientMode.Vertical))
                    g.FillPath(br, p);
            }
            // A blade, not a knob: shape says "this one is position, not a setting".
            var blade = new RectangleF(bar.X + (bar.Width - 4) * f - 4, bar.Y - 7, 12, 34);
            NewPlayerSkin.Recess(g, blade, 2, null);
            NewPlayerSkin.SilverFace(g, blade, 2);
            // Two, not the knob's three: at 12 wide against 22, two lines put the
            // cuts at the same spacing rather than the same count.
            Grip(g, blade, 2);
        }

        // ── the seconds ring ─────────────────────────────────────────

        private void PaintRingScale(Graphics g)
        {
            float cx = NewPlayerSkin.RingCx, cy = NewPlayerSkin.RingCy;
            float rOut = NewPlayerSkin.RScaleOut, rIn = NewPlayerSkin.RScaleIn;

            using (var p = new GraphicsPath())
            {
                p.AddEllipse(cx - rOut, cy - rOut, rOut * 2, rOut * 2);
                p.AddEllipse(cx - rIn, cy - rIn, rIn * 2, rIn * 2);
                using (var br = new SolidBrush(Color.FromArgb(0x18, 0x18, 0x16)))
                    g.FillPath(br, p);
            }
            using (var pen = new Pen(Color.FromArgb(90, Color.Black), 1.6f))
                g.DrawEllipse(pen, cx - rOut, cy - rOut, rOut * 2, rOut * 2);
            using (var pen = new Pen(Color.FromArgb(70, Color.White), 1.6f))
                g.DrawEllipse(pen, cx - rIn, cy - rIn, rIn * 2, rIn * 2);

            // Twelve marks, 52 units apart: one mark is five seconds, which is
            // exactly one arrow seek step. They were 1.4 units of #6A706C in the
            // first build and measured barely 3:1 against the channel — thin
            // enough that a describer looking at a screenshot reported the ring
            // as having no marks at all. Wider and much lighter now.
            for (int i = 0; i < 12; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 6;
                bool quarter = i % 3 == 0;
                using (var pen = new Pen(quarter
                           ? Color.FromArgb(0xE6, 0xEA, 0xE6)
                           : Color.FromArgb(0xB0, 0xB8, 0xB2), quarter ? 3.4f : 2.2f))
                    g.DrawLine(pen,
                        cx + (float)(Math.Cos(a) * (rIn + 1)), cy + (float)(Math.Sin(a) * (rIn + 1)),
                        cx + (float)(Math.Cos(a) * (rOut - 1)), cy + (float)(Math.Sin(a) * (rOut - 1)));
            }

        }

        /// <summary>The marker: a lit dot, never a dash — every five seconds it
        /// lands exactly on a mark, and a dash would just look like a brighter
        /// mark. This is the only thing on the panel that moves by itself, which
        /// is why everything behind it is cached.</summary>
        private void PaintRingMarker(Graphics g)
        {
            float cx = NewPlayerSkin.RingCx, cy = NewPlayerSkin.RingCy;
            float rOut = NewPlayerSkin.RScaleOut, rIn = NewPlayerSkin.RScaleIn;
            double am = -Math.PI / 2 + second * Math.PI / 30;
            float mx = cx + (float)(Math.Cos(am) * (rIn + rOut) / 2);
            float my = cy + (float)(Math.Sin(am) * (rIn + rOut) / 2);
            // Electric blue, not amber: amber belongs to focus, and a marker the
            // same colour as the focus ring left the user unable to tell which
            // fact they were looking at.
            for (int i = 14; i >= 6; i -= 4)
                using (var br = new SolidBrush(Color.FromArgb(48, NewPlayerSkin.Electric)))
                    g.FillEllipse(br, mx - i, my - i, i * 2, i * 2);
            using (var br = new SolidBrush(NewPlayerSkin.Electric))
                g.FillEllipse(br, mx - 7, my - 7, 14, 14);
            using (var pen = new Pen(Color.FromArgb(0x0A, 0x3A, 0x70), 1f))
                g.DrawEllipse(pen, mx - 7, my - 7, 14, 14);
        }

        // ── the mouse ────────────────────────────────────────────────

        /// <summary>The wheel does the fine step of whatever is under the pointer:
        /// over the bar a seek step, over the speed slot a speed step, over the
        /// ring a volume step. That is how a mouse gets the precision the keyboard
        /// gets from its plain arrows — dragging cannot, since the bar resolves
        /// about three minutes per unit in a ten-hour book.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int dir = e.Delta > 0 ? +1 : -1;
            if (Near(NewPlayerSkin.BarRect, e.Location, 14)) form.SkinArrowSeek(dir);
            else if (Near(NewPlayerSkin.SpeedSlot, e.Location, 16)) form.SkinSpeed(dir * 5);
            else if (InRing(e.Location)) form.SkinVolume(dir * 5);
            base.OnMouseWheel(e);
        }

        // Which slider the mouse has hold of, and — for the progress blade — where
        // it currently is, so the drag can be shown without being committed.
        private enum Grab { None, Speed, Bar }
        private Grab grabbed = Grab.None;
        private float dragFraction;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

            if (Near(NewPlayerSkin.SpeedSlot, e.Location, 16))
            {
                grabbed = Grab.Speed;
                DragSpeedTo(e.X);
                return;
            }
            if (Near(NewPlayerSkin.BarRect, e.Location, 14))
            {
                grabbed = Grab.Bar;
                dragFraction = FractionAt(NewPlayerSkin.BarRect, e.X);
                Invalidate();
                return;
            }

            // No title bar, so the panel itself is the grab handle.
            if (!NewPlayerSkin.GlassRect.Contains(e.Location))
            {
                NativeDrag.Begin(form);
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (grabbed == Grab.Speed) DragSpeedTo(e.X);
            else if (grabbed == Grab.Bar)
            {
                dragFraction = FractionAt(NewPlayerSkin.BarRect, e.X);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (grabbed == Grab.Bar)
            {
                // Committed here and only here — one seek per gesture.
                form.SkinSeekFraction(dragFraction);
            }
            grabbed = Grab.None;
            Invalidate();
            base.OnMouseUp(e);
        }

        private static float FractionAt(Rectangle slot, int x)
        {
            return Math.Max(0f, Math.Min(1f, (x - slot.X) / (float)slot.Width));
        }

        /// <summary>Speed follows the mouse live, but only ever in the same steps
        /// of five the keyboard uses — otherwise the mouse would set 73 and the
        /// next arrow press would look like it skipped a step.</summary>
        private void DragSpeedTo(int x)
        {
            float f = FractionAt(NewPlayerSkin.SpeedSlot, x);
            int want = (int)Math.Round((50 + f * 150) / 5.0) * 5;
            int delta = want - form.SkinSpeedRaw;
            if (delta != 0) form.SkinSpeed(delta);
        }

        private static bool Near(Rectangle r, Point p, int slack)
        {
            return Rectangle.Inflate(r, slack, slack).Contains(p);
        }

        private static bool InRing(Point p)
        {
            double dx = p.X - NewPlayerSkin.RingCx, dy = p.Y - NewPlayerSkin.RingCy;
            return Math.Sqrt(dx * dx + dy * dy) <= NewPlayerSkin.RScaleOut;
        }
    }

    /// <summary>Dragging a borderless window by its face — the same message the
    /// system sends when a title bar is grabbed, so snapping and multi-monitor
    /// behaviour stay the system's job rather than ours.</summary>
    internal static class NativeDrag
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void Begin(Form form)
        {
            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
    }
}
