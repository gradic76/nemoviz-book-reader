using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>How much of the screen the reading window takes, and how the text
    /// moves. These are the three rows already in Properties.</summary>
    public enum VisualMode
    {
        /// <summary>Two rows at the player's own size and position, so it reads as
        /// the player having become a display. The player really is behind it.</summary>
        TwoRows = 0,
        /// <summary>Fills the working area; the text is replaced in place.</summary>
        FullInstant,
        /// <summary>Fills the working area; the text scrolls.</summary>
        FullScrolling
    }

    /// <summary>
    /// The on-screen reading view — a SEPARATE window, never a resized player.
    ///
    /// <para>The player is a fixed borderless casing with a <c>Region</c> clip:
    /// growing it does not enlarge the text, it breaks the drawing. A second
    /// window also gives two Alt+Tab entries, so a reader has "player" and
    /// "reading" as two places rather than one window that changes identity
    /// underneath them (§8l).</para>
    ///
    /// <para><b>It BORROWS the player's reading surface rather than making its
    /// own</b>, and hands it back on close. That is the whole point: braille
    /// works because the text sits in one real focusable control that the screen
    /// reader tracks (measured on NVDA and JAWS), so a second control showing the
    /// same words would split visual and braille into two features that must be
    /// kept in step. One control, two placements — parked off the player's client
    /// area when this window is shut, hosted here when it is open.</para>
    ///
    /// <para><b>The column is 60 characters, centred, whatever the window
    /// size.</b> Long lines are hard to track back to the start of the next one;
    /// the guidance converges on 45–75. So <c>+</c> and <c>−</c> change the font
    /// AND re-measure the column, rather than changing how much text sits on a
    /// line. The wide empty margins are not waste — they are the help.</para>
    /// </summary>
    public sealed class ReadingWindow : Form
    {
        private const int Rim = 12;          // silver margin around the glass
        private const int BarH = 52;         // the control strip along the foot

        private readonly RichTextBox surface;
        private readonly Control returnTo;   // where the surface came from
        private readonly VisualMode mode;
        private readonly Func<char[]> bookChars;
        private readonly Action<Keys> forwardKey;

        private Button btnBack, btnPlay, btnForward, btnSmaller, btnBigger;
        private ComboBox cmbFont;
        /// <summary>Holds off applying a font until the reader stops moving
        /// through the list — see the picker's handler for why that matters.</summary>
        private Timer fontApply;
        /// <summary>The font THIS window made, so it can be released without
        /// touching one it merely inherited.</summary>
        private Font ownFont;
        private Panel metal, glass;
        // What the window opens on: whatever it was left on last time, or the
        // measured default (60 characters across a 960-wide window) the first
        // time. This is the reader's eyesight, not the book's, so it is one
        // setting for the app and not one per book (Gordan, 2026-08-03).
        private float fontSize = 26f;
        private string fontFamily = "Segoe UI";
        // The book's colours, as indices; -1 means it has none and the skin's own
        // glass stands.
        private readonly int textColour = -1, backColour = -1;

        /// <param name="surface">The player's reading surface. It is re-parented
        /// in and given back when this window closes.</param>
        /// <param name="bookChars">The distinct characters of the book being read,
        /// for filtering the font list — see <see cref="FontsFor"/>.</param>
        /// <param name="forwardKey">Transport keys go back to the player, which
        /// owns playback. Nothing about reading is decided here.</param>
        /// <param name="textColour">Index into <see cref="ReadingColours"/> — the
        /// book's own choice, or -1 to keep the skin's dark glass.</param>
        /// <param name="backColour">Likewise, for what it is printed on.</param>
        public ReadingWindow(Form owner, RichTextBox surface, VisualMode mode,
                             Func<char[]> bookChars, Action<Keys> forwardKey,
                             int textColour = -1, int backColour = -1)
        {
            this.surface = surface;
            this.returnTo = surface != null ? surface.Parent : null;
            this.mode = mode;
            this.bookChars = bookChars;
            this.forwardKey = forwardKey;
            this.textColour = textColour;
            this.backColour = backColour;

            AppSettings kept = AppSettings.Current;
            if (kept != null)
            {
                if (!string.IsNullOrEmpty(kept.ReadingFont)) fontFamily = kept.ReadingFont;
                if (kept.ReadingFontSize > 0) fontSize = kept.ReadingFontSize;
            }

            Owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;                 // it is a place, so it is in Alt+Tab
            KeyPreview = true;
            Text = Localization.T("Reading.Title");
            AccessibleName = Text;

            BuildLayout(owner);
        }

        // ── Layout ────────────────────────────────────────────────────────────
        private void BuildLayout(Form owner)
        {
            SuspendLayout();

            Rectangle area = PlaceAndSize(owner);
            BackColor = NewPlayerSkin.PanelMid;

            // The casing, quieter than the player's. That one is an instrument
            // read from across the room and is deliberately rich; a reading
            // surface wants the eye on the TEXT, so: thin rim, no ornament in the
            // field of view, and nothing that moves.
            using (var casing = NewPlayerSkin.Round(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
                                                    NewPlayerSkin.CaseRadius))
                Region = new Region(casing);

            metal = new Panel();
            metal.Dock = DockStyle.Bottom;
            metal.Height = BarH;
            metal.BackColor = NewPlayerSkin.PanelMid;
            metal.TabStop = false;
            Controls.Add(metal);

            // The glass fills everything above the strip, and the text sits ON it.
            // Without this, subtitle mode showed two lit rows floating in a field
            // of bare metal — the opposite of the intended illusion, which is that
            // the player itself has become a display with subtitles along the
            // bottom, the way they sit on a picture.
            glass = new Panel();
            glass.BackColor = SystemInformation.HighContrast ? SystemColors.Window : NewPlayerSkin.Glass;
            glass.TabStop = false;
            glass.SetBounds(Rim, Rim, ClientSize.Width - 2 * Rim, ClientSize.Height - BarH - 2 * Rim);
            Controls.Add(glass);

            BuildControls();

            if (surface != null)
            {
                if (surface.Parent != null) surface.Parent.Controls.Remove(surface);
                glass.Controls.Add(surface);
                surface.BringToFront();
                StyleSurface();
            }

            ResumeLayout();

            // Said BEFORE the window is shown, which is the whole point.
            //
            // Every previous attempt set focus AFTER the window appeared —
            // from Shown, then from the player a message cycle later, then on a
            // retry timer — and each was a race against the play path, which
            // puts focus back on the player's own controls at its own pace.
            // Gordan kept having to fetch the window with F9.
            //
            // ActiveControl is not a request to move focus; it is the window
            // telling WinForms where focus BELONGS. The system then hands it
            // over as part of activating the window, with nothing to race.
            if (surface != null) ActiveControl = surface;

            // Still done on Shown as well, for the caret rather than the focus:
            // focusing a multiline TextBox SELECTS ALL of it, and a selection is
            // news to a screen reader — it reads the marked text out and braille
            // shows a solid block. The reading position is a caret, never a
            // range (§8l), so it is put back the instant focus lands.
            Shown += (s, e) =>
            {
                // Aggressive on purpose, and only once, on the way up. A window
                // the reader cannot find is worse than no window, and F9 having
                // to fetch it has cost a whole evening.
                try { TopMost = true; BringToFront(); Activate(); TopMost = false; } catch { }
                try { SetForegroundWindow(Handle); } catch { }
                FocusSurface();
            };
            Activated += (s, e) => FocusSurface();
            FormClosed += (s, e) => GiveSurfaceBack();
        }

        /// <summary>Where the window goes, and how big.
        ///
        /// <para>Subtitle mode is fixed at the player's own size and position —
        /// deliberately NOT responsive, because stretching it breaks the illusion
        /// that the player itself became a display. The two full modes take the
        /// <b>working area</b>, not a fixed number: a hard-coded width is a bet on
        /// 16:9 and overflows anything narrower, and the working area is the only
        /// thing that knows about the taskbar.</para></summary>
        private Rectangle PlaceAndSize(Form owner)
        {
            StartPosition = FormStartPosition.Manual;
            if (mode == VisualMode.TwoRows && owner != null)
            {
                Size = owner.Size;
                Location = owner.Location;
            }
            else
            {
                Rectangle wa = Screen.FromControl(owner ?? (Control)this).WorkingArea;
                Location = wa.Location;
                Size = wa.Size;
            }
            return ClientRectangle;
        }

        private void StyleSurface()
        {
            bool hc = SystemInformation.HighContrast;

            surface.BorderStyle = BorderStyle.None;
            surface.Multiline = true;
            surface.WordWrap = true;
            surface.ReadOnly = true;
            surface.HideSelection = false;
            surface.ScrollBars = mode == VisualMode.FullScrolling
                ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None;
            // High contrast outranks everything (§8k): there the user has told the
            // system what they need, and our colours — chosen or not — yield.
            // Below that, the BOOK's pair outranks the skin, because a reader who
            // went into Properties and chose yellow on black meant it.
            if (hc)
            {
                surface.BackColor = SystemColors.Window;
                surface.ForeColor = SystemColors.WindowText;
            }
            else
            {
                surface.BackColor = backColour >= 0
                    ? ReadingColours.At(backColour) : NewPlayerSkin.Glass;
                surface.ForeColor = textColour >= 0
                    ? ReadingColours.At(textColour) : NewPlayerSkin.Lit;
            }
            ApplyFont();
        }

        /// <summary>Sets the font and then measures the column to hold about 60
        /// characters of it, centred. The two are not independent: at 960 units
        /// wide, 60 characters is roughly 26 pt, where 12 pt would fit 130.</summary>
        private void ApplyFont()
        {
            if (surface == null) return;
            // Remembered at the moment it takes effect, which is the moment the
            // reader would call it chosen. Writes only on a real change.
            if (AppSettings.Current != null)
                AppSettings.Current.SetReadingFont(fontFamily, (int)fontSize);
            Font f;
            // Through BundledFonts, not new Font(name, size): the shipped faces
            // are private to this process, and GDI+ resolving a name it does not
            // know hands back a SUBSTITUTE carrying the requested name — so
            // picking Andika would silently give you something else called
            // Andika, with no error to notice.
            try { f = BundledFonts.Make(fontFamily, fontSize); }
            catch { f = new Font(FontFamily.GenericSansSerif, fontSize); }
            // Only ever OUR OWN previous font goes back — never surface.Font.
            // That property returns the AMBIENT font when the control has not
            // been given one of its own, so on the first pass it hands back the
            // form's font, and disposing that destroys a GDI handle every other
            // control on the window is still drawing with. It broke the whole
            // output the moment it shipped. A Font is still worth releasing —
            // arrowing a long list was leaving one behind per keystroke — but
            // only one we made.
            Font mine = ownFont;
            ownFont = f;
            surface.Font = f;
            if (mine != null && !ReferenceEquals(mine, f)) try { mine.Dispose(); } catch { }

            // Measured on a real sample rather than assumed: character width
            // varies enormously between faces at the same point size, which is
            // half the reason a font list is offered at all.
            const string sample = "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefgh";
            int want = TextRenderer.MeasureText(sample, f).Width;

            // Positions are inside the glass panel, which is the surface's parent.
            int maxW = glass.ClientSize.Width - 16;
            int w = Math.Min(want, maxW);
            if (w < 120) w = Math.Min(120, maxW);

            int top = 8;
            int h = glass.ClientSize.Height - 16;
            if (mode == VisualMode.TwoRows)
            {
                // Two rows as one frame, CENTRED in the glass (Gordan,
                // 2026-08-10). They used to sit along the bottom edge, on the
                // reasoning that this is where subtitles live on a picture — but
                // this window is not a picture with subtitles under it, it is a
                // window with nothing in it except these two rows. Pinned to the
                // bottom they read as having fallen to the floor of an empty
                // frame, and there is nothing above them for them to be under.
                //
                // The height is left exactly as it was, and that is deliberate:
                // Form1.ScrollSurfaceForMode works out how many lines a frame
                // holds as ClientSize.Height / Font.Height, so making the box
                // roomier would silently turn the two-row mode into three.
                h = Math.Max(f.Height * 2 + 8, 40);
                top = Math.Max(8, (glass.ClientSize.Height - h) / 2);
            }
            surface.SetBounds((glass.ClientSize.Width - w) / 2, top, w, Math.Max(h, 40));
        }

        // ── Controls on the metal ─────────────────────────────────────────────
        /// <summary>Three transport keys, the two size keys and the font picker,
        /// along the BOTTOM edge only. The top edge competes with the eye's return
        /// sweep to the start of each new line.</summary>
        private void BuildControls()
        {
            btnBack = MakeKey("Reading.Back", () => forwardKey?.Invoke(Keys.Shift | Keys.Left));
            btnPlay = MakeKey("Reading.PlayPause", () => forwardKey?.Invoke(Keys.Space));
            btnForward = MakeKey("Reading.Forward", () => forwardKey?.Invoke(Keys.Shift | Keys.Right));
            btnSmaller = MakeKey("Reading.Smaller", () => ChangeSize(-2));
            btnBigger = MakeKey("Reading.Bigger", () => ChangeSize(+2));

            cmbFont = new ComboBox();
            cmbFont.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFont.AccessibleName = Localization.T("Reading.Font.Accessible");
            cmbFont.Width = 220;
            foreach (string name in FontsFor(bookChars == null ? null : bookChars()))
                cmbFont.Items.Add(name);
            int at = cmbFont.Items.IndexOf(fontFamily);
            if (at < 0 && cmbFont.Items.Count > 0) at = 0;
            if (at >= 0) cmbFont.SelectedIndex = at;
            cmbFont.SelectedIndexChanged += (s, e) =>
            {
                fontFamily = cmbFont.SelectedItem as string ?? fontFamily;
                // NOT applied here at all any more — not even after a pause.
                // Setting Font on the surface re-wraps everything in it, which is
                // seconds on a real book, and a timer only meant the machine
                // seized a third of a second after each key instead of during it
                // (Gordan). Browsing a list must cost nothing.
                //
                // The name is still spoken on the keystroke, because that is what
                // the reader is choosing BY. The face itself waits for the choice
                // to be made — Enter, or closing the list.
                // NVDA does not announce a closed combo changed with the arrows
                // (§11); JAWS does, so this is a no-op there and never doubles up.
                NvdaController.Speak(fontFamily);
            };
            // The choice is made when the list closes or Enter is pressed, and
            // only then is the book re-laid-out. Leaving the picker without
            // choosing applies whatever is showing, which is the same thing a
            // combo box means everywhere else.
            cmbFont.DropDownClosed += (s, e) => ApplyFont();
            cmbFont.Leave += (s, e) => ApplyFont();
            cmbFont.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true; e.SuppressKeyPress = true;
                ApplyFont();
            };
            metal.Controls.Add(cmbFont);

            metal.Resize += (s, e) => LayOutBar();
            LayOutBar();
        }

        private Button MakeKey(string key, Action onClick)
        {
            var b = new Button();
            b.Text = Localization.T(key);
            b.AccessibleName = Localization.T(key + ".Accessible");
            if (string.IsNullOrEmpty(b.AccessibleName)) b.AccessibleName = b.Text;
            b.Click += (s, e) => onClick();
            metal.Controls.Add(b);
            return b;
        }

        private void LayOutBar()
        {
            if (metal == null || btnBack == null) return;
            const int h = 34, gap = 8;
            int y = (BarH - h) / 2;
            int x = Rim;
            foreach (Button b in new[] { btnBack, btnPlay, btnForward, btnSmaller, btnBigger })
            {
                b.SetBounds(x, y, 92, h);
                x += 92 + gap;
            }
            if (cmbFont != null)
                cmbFont.SetBounds(metal.ClientSize.Width - Rim - cmbFont.Width,
                                  (BarH - cmbFont.Height) / 2, cmbFont.Width, cmbFont.Height);
        }

        private void ChangeSize(float delta)
        {
            fontSize = Math.Max(10f, Math.Min(96f, fontSize + delta));
            ApplyFont();
            AnnounceSize();
        }

        private void AnnounceSize()
        {
            string msg = Localization.T("Reading.Size.Announce", (int)fontSize);
            NvdaController.Speak(msg);
        }

        // ── Font list ─────────────────────────────────────────────────────────
        /// <summary>Every installed family that can actually render THIS book.
        ///
        /// <para>Measured against the book's own characters rather than a
        /// language→script table: a Croatian book can quote Greek, and a table
        /// would say all was well while the reader saw boxes. Same principle as
        /// §8e refusing to trust a declared <c>dc:language</c>.</para>
        ///
        /// <para>The character test alone removes Wingdings, Marlett and most
        /// symbol and decorative faces — they have no <c>č</c>. Non-scalable
        /// (bitmap) faces go too, since they fall apart on <c>+</c>.</para></summary>
        public static List<string> FontsFor(char[] chars)
        {
            var result = new List<string>();
            // The bundled faces come first in the sweep, not because they are
            // ranked above the installed ones — the list is sorted afterwards
            // anyway — but so that a family NBR ships and the user also happens
            // to have installed appears once, as ours. FontFamily.Families never
            // lists a privately loaded face, so there is no double entry to
            // remove; the Contains check guards the reverse case.
            var sweep = new List<FontFamily>(BundledFonts.Faces);
            sweep.AddRange(FontFamily.Families);
            foreach (FontFamily f in sweep)
            {
                try
                {
                    if (result.Contains(f.Name)) continue;
                    if (!f.IsStyleAvailable(FontStyle.Regular)) continue;
                    if (chars != null && chars.Length > 0 && !CanRender(f, chars)) continue;
                    result.Add(f.Name);
                }
                catch { }
            }
            result.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
        }

        /// <summary>Asks the FONT which code points it has, through GDI's
        /// <c>GetFontUnicodeRanges</c>.
        ///
        /// <para>WPF's <c>GlyphTypeface</c> answers the same question in one line
        /// and is what the offline measuring used, but referencing PresentationCore
        /// would drag the whole of WPF into a WinForms player's startup for a
        /// coverage check. This is thirty lines and costs nothing.</para></summary>
        private static bool CanRender(FontFamily family, char[] chars)
        {
            IntPtr hdc = IntPtr.Zero, hfont = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                using (var f = new Font(family, 12f))
                {
                    hdc = CreateCompatibleDC(IntPtr.Zero);
                    if (hdc == IntPtr.Zero) return true;
                    hfont = f.ToHfont();
                    old = SelectObject(hdc, hfont);

                    uint size = GetFontUnicodeRanges(hdc, IntPtr.Zero);
                    if (size == 0) return true;                 // cannot tell
                    IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetFontUnicodeRanges(hdc, buf) == 0) return true;
                        int count = System.Runtime.InteropServices.Marshal.ReadInt32(buf, 12); // cRanges
                        var have = new HashSet<char>();
                        for (int i = 0; i < count; i++)
                        {
                            int at = 16 + i * 4;                // WCRANGE { WCHAR low; USHORT n; }
                            int low = (ushort)System.Runtime.InteropServices.Marshal.ReadInt16(buf, at);
                            int n = (ushort)System.Runtime.InteropServices.Marshal.ReadInt16(buf, at + 2);
                            foreach (char c in chars)
                                if (c >= low && c < low + n) have.Add(c);
                        }
                        foreach (char c in chars) if (!have.Contains(c)) return false;
                        return true;
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
                }
            }
            catch { return true; }   // cannot tell — do not hide it on a guess
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    if (old != IntPtr.Zero) SelectObject(hdc, old);
                    if (hfont != IntPtr.Zero) DeleteObject(hfont);
                    DeleteDC(hdc);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern uint GetFontUnicodeRanges(IntPtr hdc, IntPtr lpgs);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        // ── Keys ──────────────────────────────────────────────────────────────
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Escape closes. Standard Windows convention — dialogs close on
            // Escape, main windows do not — and this is dialog-class (Gordan).
            if (keyData == Keys.Escape) { Close(); return true; }

            // The two keys this window owns.
            switch (keyData)
            {
                case Keys.Oemplus: case Keys.Add:
                    ChangeSize(+2); return true;
                case Keys.OemMinus: case Keys.Subtract:
                    ChangeSize(-2); return true;
            }

            // EVERYTHING ELSE the player owns goes back to the player — the whole
            // shortcut set, not just the transport. If this window is where a
            // braille reader lives (see §8l), then from inside it they must still
            // reach the Library, Go To, the bookmarks and the timer; forwarding
            // only Space and the arrows made it a room with no doors.
            //
            // It also happens to be what makes a braille display's own keys work.
            // A display sends commands to the SCREEN READER, not to Windows, and
            // both readers can map one of its keys to "emulate a system key" — at
            // which point an ordinary keystroke arrives here and nothing can tell
            // it from the keyboard. Nothing to build for that; it just has to not
            // be swallowed. Simple, modifier-free keys are the easy ones to map,
            // which is a second reason the F-key set earns its place.
            // …but not out from under a control that needs them itself. The font
            // picker is a closed combo: its whole operation is Up and Down, and
            // those were being taken for volume before they ever reached it, so
            // the list could be opened and not moved through (Gordan). Space is
            // in the same position — it opens a combo and presses a button — and
            // a control that wants a bare arrow or a bare space is exactly a
            // control the player must keep its hands off.
            Control focused = ActiveControl;
            bool ownsKeys = focused is ComboBox || focused is Button;
            if (ownsKeys)
            {
                Keys plain = keyData & ~Keys.Shift;
                if (plain == Keys.Up || plain == Keys.Down || plain == Keys.Space ||
                    keyData == Keys.Left || keyData == Keys.Right)
                    return base.ProcessCmdKey(ref msg, keyData);
            }

            switch (keyData)
            {
                case Keys.Space:
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Shift | Keys.Left: case Keys.Shift | Keys.Right:
                case Keys.Shift | Keys.Up: case Keys.Shift | Keys.Down:
                case Keys.Control | Keys.Left: case Keys.Control | Keys.Right:
                case Keys.F1: case Keys.F2: case Keys.F3: case Keys.F4:
                case Keys.F5: case Keys.F6: case Keys.F7: case Keys.F8:
                case Keys.Alt | Keys.Enter:
                // Ctrl+G, Ctrl+T and Ctrl+B are no longer forwarded: the player
                // no longer has them. Everything they did is on the F-keys, which
                // this window already forwards.
                case Keys.Control | Keys.O:
                // TEMPORARY test aid — see ReadingDiagnostics. Forwarded from
                // here too because the surface is where a tester will be standing
                // when they want to switch it. Remove with the file.
                case Keys.Control | Keys.Shift | Keys.H:
                    forwardKey?.Invoke(keyData);
                    return true;
            }
            // Ctrl+1..9 — the percentage jumps.
            if ((keyData & Keys.Control) == Keys.Control)
            {
                Keys k = keyData & Keys.KeyCode;
                if (k >= Keys.D1 && k <= Keys.D9) { forwardKey?.Invoke(keyData); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Puts the caret in the reading surface, which is where braille
        /// and the screen reader both look. Called on Shown, and again by the
        /// player once the play path has finished putting focus wherever it
        /// wanted it.
        ///
        /// <para>Focusing a multiline TextBox SELECTS ALL of it, and a selection
        /// is news to a screen reader: it reads the marked text out and braille
        /// shows a solid block. The reading position is a caret, never a range
        /// (§8l) — so it is put back where it was the instant focus lands, before
        /// anything can announce it.</para></summary>
        public void FocusSurface()
        {
            if (surface == null || surface.IsDisposed) return;
            int at = surface.SelectionStart;
            surface.Focus();
            surface.Select(at, 0);
            surface.ScrollToCaret();
        }

        /// <summary>Hands the surface back where it came from. Without this the
        /// player loses its reading surface for good, and with it braille.</summary>
        private void GiveSurfaceBack()
        {
            if (surface == null) return;
            if (surface.Parent != null) surface.Parent.Controls.Remove(surface);
            if (returnTo != null && !returnTo.IsDisposed) returnTo.Controls.Add(surface);
            // Hand it back on the player's own font, THEN release ours. The other
            // order would leave the surface drawing with a disposed handle, and
            // keeping ours alive would leak one per open and close.
            try { surface.Font = null; } catch { }
            if (ownFont != null) { try { ownFont.Dispose(); } catch { } ownFont = null; }
            if (fontApply != null) { try { fontApply.Stop(); fontApply.Dispose(); } catch { } fontApply = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (SystemInformation.HighContrast) return;   // the casing gets out of the way
            // A single quiet groove around the glass. No relief, no gradient in
            // the field of view — the player's panel does that because it is an
            // instrument; here the eye belongs on the text.
            Rectangle r = new Rectangle(Rim - 2, Rim - 2,
                                        ClientSize.Width - 2 * (Rim - 2),
                                        ClientSize.Height - BarH - 2 * (Rim - 2));
            using (var p = new Pen(NewPlayerSkin.GrooveShadow, 2))
                e.Graphics.DrawRectangle(p, r);
        }
    }

    /// <summary>The six reading colours, in the order the combo boxes list them,
    /// and the only place that knows which colour each name means. The names live
    /// in <c>en.lang</c> (<c>Settings.Colour.*</c>) and what a book stores is the
    /// INDEX, so a book set up on an English NBR is painted the same by a
    /// translated one.
    ///
    /// <para>Flat and saturated rather than a palette: the point is contrast that
    /// survives a bad screen in a bright room, and which pairs with which is the
    /// reader's to decide — including the combinations a designer would not
    /// choose.</para></summary>
    internal static class ReadingColours
    {
        private static readonly Color[] All =
        {
            Color.White, Color.Black, Color.Yellow,
            Color.FromArgb(0x00, 0x33, 0xCC),   // blue
            Color.FromArgb(0x00, 0x80, 0x00),   // green
            Color.FromArgb(0xCC, 0x00, 0x00),   // red
        };

        // What the dialog has always shown before anyone chose: yellow on black,
        // and blue behind the reading mark.
        public const int DefaultText = 2;
        public const int DefaultBack = 1;
        public const int DefaultHighlight = 3;

        public static int Count { get { return All.Length; } }
        public static int Clamp(int i) { return i < 0 || i >= All.Length ? 0 : i; }
        public static Color At(int i) { return All[Clamp(i)]; }
    }
}
