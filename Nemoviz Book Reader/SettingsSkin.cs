using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The handful of controls the Settings skin is allowed to touch.
    /// Everything else it finds by walking the tab pages.</summary>
    internal sealed class SettingsParts
    {
        public TabControl Tabs;
        public CheckBox ShowHints;
        public List<TextBox> Hints;
        public Button OK, Cancel, Apply;
    }

    /// <summary>
    /// Settings wearing the same face as Properties: 960 × 640 borderless casing,
    /// silver rim, dark glass, real controls only repainted.
    ///
    /// <para><b>No info column.</b> Properties has one because it describes a
    /// book; Settings has no book, so the whole width goes to the tabs. That is
    /// also the room the Text Books page needed — it did not fit 640 in one
    /// column and had to be scrolled.</para>
    ///
    /// <para><b>The hint boxes are gone, and so is the switch that hid them</b>
    /// (Gordan, 2026-07-29). A hint under every control cost a third of each page
    /// to say things a reader only wants once; the <c>?</c> on each group, with
    /// F1 as the second route, says the same thing on demand and costs a corner.
    /// The reserved space goes with them, which is most of why the pages fit.</para>
    ///
    /// <para><b>The tab strip is owner-drawn but the TabControl is real.</b> The
    /// panel's rule holds here as everywhere: a drawn tab strip would lose the tab
    /// role, the arrow navigation and the "page 2 of 5" a reader announces. Only
    /// the paint changes.</para>
    /// </summary>
    internal static class SettingsSkin
    {
        private const int Margin = 12;

        public static void Apply(SettingsForm f)
        {
            SettingsParts p = f.SkinParts;
            if (p == null || p.Tabs == null) return;

            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, DialogSkin.H);

            // The switch and everything it switched. Removed rather than hidden:
            // a hidden control that still exists is one a later change can bring
            // back by accident, and these have no home in the new design.
            if (p.ShowHints != null)
            {
                p.ShowHints.Visible = false;
                p.ShowHints.TabStop = false;
                if (p.ShowHints.Parent != null) p.ShowHints.Parent.Controls.Remove(p.ShowHints);
            }
            if (p.Hints != null)
            {
                foreach (TextBox h in p.Hints)
                    if (h != null && h.Parent != null) h.Parent.Controls.Remove(h);
                p.Hints.Clear();
            }

            // The tabs take the whole client area above the buttons.
            TabControl tabs = p.Tabs;
            tabs.SetBounds(Margin, Margin, DialogSkin.W - 2 * Margin,
                           DialogSkin.ButtonsY - 2 * Margin);
            DialogSkin.StyleTabStrip(tabs);
            tabs.TabIndex = 0;

            // The page size is worked out from the TabControl, NOT read off the
            // page: inside SuspendLayout the pages have not been resized yet, so
            // ClientSize still answers with what they were built at — which laid
            // the groups out barely half a page wide and clipped every value off
            // the right-hand edge. 4 units of border each side, the strip on top.
            int pw = tabs.Width - 8, ph = tabs.Height - DialogSkin.TabH - 8;

            HintSystem.Clear();
            foreach (TabPage page in tabs.TabPages) LayOutPage(page, pw, ph);

            DialogSkin.AsKey(p.OK, new Rectangle(596, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.Cancel, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.Apply, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            p.OK.TabIndex = 20;
            p.Cancel.TabIndex = 21;
            p.Apply.TabIndex = 22;

            f.ResumeLayout();
            canvas.Rebuild();
        }

        /// <summary>One page: METAL underneath, its groups as stickers down the
        /// full width, everything else recoloured where it stands. A page's loose
        /// controls are laid out by hand, pair by pair, and a loop that only knows
        /// "control" cannot put a label back beside the thing it labels — the
        /// lesson the audio cells taught.
        ///
        /// <para><b>Metal, not glass, since 2026-08-10.</b> Gordan: the Settings
        /// groups have "too exposed label frames" and should look like the ones
        /// in Audio and Speech properties. Both pages already use the SAME
        /// sticker painter, and sampling the two captures showed the borders are
        /// near enough the same colour — #2A332E here against #474F48 there. What
        /// differed was what lay BEHIND them: this page was glass, near-black at
        /// #0E1210, so a sticker at #1A211D sat slightly LIGHTER than its
        /// surround and read as a raised card with an outline drawn round it. In
        /// Properties the same sticker sits on metal at #C6C6C2 and reads as a
        /// panel recessed into it, which is the look he wants — and the look the
        /// whole dialog family is built on, where dark means inset.</para>
        ///
        /// <para>So the fix is one line, and it is not the frame. Chasing the
        /// border colour would have made the frame invisible and left the card
        /// still floating the wrong way round.</para></summary>
        private static void LayOutPage(TabPage page, int pw, int ph)
        {
            page.UseVisualStyleBackColor = false;
            page.BackColor = NewPlayerSkin.PanelMid;
            page.AutoScroll = false;      // the room is there now; nothing scrolls

            var groups = new List<GroupBox>();
            foreach (Control c in page.Controls)
            {
                GroupBox g = c as GroupBox;
                if (g != null) groups.Add(g);
                else DialogSkin.OnGlass(c);
            }
            if (groups.Count == 0) return;

            int width = pw - 2 * Margin;
            int avail = ph - 2 * Margin;
            // EVERY page goes on the three-column frame — A, B and C — and each
            // arranges itself within them (Gordan, 2026-08-03). A group takes one
            // column unless it says otherwise on itself.
            //
            // What this replaced: General was three columns of 293 and Speech and
            // Braille two of 446, so nothing on one page lined up with anything on
            // the other. Two arrangements, each reasonable alone, that together
            // read as carelessness.
            LayOutGrid(groups, 3, width, avail);
            AttachGroupHints(page, groups);
        }

        /// <summary>One <c>?</c> per group, in the order the groups were added.
        /// Called from BOTH layouts — a page laid out as a grid needs its help
        /// keys exactly as much as one laid out in columns.</summary>
        private static void AttachGroupHints(TabPage page, List<GroupBox> groups)
        {
            string[] keys = HintKeys(page);
            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].TabIndex = i;
                // No key, no ?. A help key nobody wrote shows the key itself,
                // which is worse than no help at all — and a reader would be told
                // to press a button that says "Hint.Settings.General.0".
                if (i < keys.Length && !string.IsNullOrEmpty(keys[i]))
                    HintSystem.Attach(groups[i], keys[i]);
            }
        }

        /// <summary>Makes a group's contents fit the width the grid gave it.
        ///
        /// <para>They are built for a 500-wide group on a 560-wide dialog and the
        /// grid puts them in 293, so anything sized in absolute units hangs over
        /// the edge. The check boxes did — 470 wide in a 293 group, saved from
        /// looking wrong only by the group clipping them — and the Library
        /// location was worse: a 340-wide path box with Browse dragged back
        /// underneath it, so the button was there, drawn over, and to a reader
        /// with eyes simply MISSING. Gordan's screenshot is what showed it; the
        /// numbers then said exactly why.</para>
        ///
        /// <para>A text box with a button beside it is treated as the pair it is:
        /// the button takes the right-hand end, the box takes the rest. Anything
        /// else that overhangs is simply narrowed.</para></summary>
        private static void FitContents(GroupBox g)
        {
            int inner = g.Width - 14;

            // The top-right corner belongs to the help key, so anything on the top
            // row stops short of it — the braille and visual checkboxes were both
            // running underneath it. The corner is reserved unconditionally: the
            // keys are attached AFTER the layout runs, so looking for one here
            // finds nothing and the checkbox goes right on sitting under it.
            int keyLeft = inner - 28;

            Button pairButton = null;
            TextBox pairBox = null;
            var buttons = new List<Button>();
            foreach (Control c in g.Controls)
            {
                Button b = c as Button;
                if (b != null && !HintSystem.IsHelpKey(b)) { buttons.Add(b); pairButton = b; }
                TextBox t = c as TextBox;
                if (t != null) pairBox = t;
            }

            // A path box with Browse beside it is one thing, not two: the button
            // takes the right-hand end and the box takes the rest.
            if (buttons.Count == 1 && pairBox != null)
            {
                // Browse sits on the group's FIRST row, which is the row the help
                // key's corner reaches into — so it stops where the key starts,
                // not at the group's edge.
                int edge = pairButton.Top < 26 ? keyLeft : inner;
                pairButton.Left = Math.Max(pairBox.Left + 60, edge - pairButton.Width);
                pairBox.Width = Math.Max(60, pairButton.Left - 8 - pairBox.Left);
                return;
            }

            // Buttons that SHARE a row are packed left to right, not each pushed
            // to the right-hand edge — doing that to "Test voice" and
            // "Pronunciation dictionary…" put one exactly on top of the other.
            // They fit side by side at this width; they only had to be placed.
            buttons.Sort((a, b) => a.Left.CompareTo(b.Left));
            var rows = new Dictionary<int, List<Button>>();
            foreach (Button b in buttons)
            {
                int row = b.Top / 8;
                if (!rows.ContainsKey(row)) rows[row] = new List<Button>();
                rows[row].Add(b);
            }
            foreach (var row in rows.Values)
            {
                int x = 14;
                foreach (Button b in row)
                {
                    if (row.Count > 1 || b.Right > inner) b.Left = x;
                    x = b.Right + 8;
                }
                // Still over? Then the row genuinely does not fit and the last
                // one gives up the difference rather than leaving the group.
                Button last = row[row.Count - 1];
                if (last.Right > inner) last.Width = Math.Max(40, inner - last.Left);
            }

            foreach (Control c in g.Controls)
            {
                Button b2 = c as Button;
                if (b2 != null) continue;                    // handled above
                int limit = c.Top < 26 ? keyLeft : inner;
                if (c.Right <= limit) continue;
                c.Width = Math.Max(40, limit - c.Left);
            }
        }

        /// <summary>The three-column frame the rest of NBR is laid out on —
        /// <b>A, B and C</b>, groups dropped into them "kako je bilo
        /// najpraktičnije" (Gordan's phrase, and the convention Properties has
        /// always used). Groups flow left to right and wrap; a group may SPAN two
        /// or three columns by carrying the number in its <c>Tag</c>.
        ///
        /// <para>One frame for every page is the point. Before this, General was
        /// three columns of 293 and Speech and Braille two of 446, so nothing on
        /// one page lined up with anything on the other — two arrangements that
        /// were each reasonable alone and looked like carelessness together.</para>
        ///
        /// <para>Every group in a row takes the height of the tallest in it, so
        /// the rows line up and a short group does not leave a step in the edge
        /// beside a tall one.</para></summary>
        private static void LayOutGrid(List<GroupBox> groups, int across, int width, int avail)
        {
            int colW = (width - Margin * (across - 1)) / across;
            int y = Margin, i = 0;

            while (i < groups.Count)
            {
                // Fill one row: take groups until the columns are used up.
                var row = new List<GroupBox>();
                var spans = new List<int>();
                int used = 0;
                while (i < groups.Count)
                {
                    int span = Math.Max(1, Math.Min(across, SpanOf(groups[i])));
                    if (used + span > across) break;
                    row.Add(groups[i]); spans.Add(span); used += span; i++;
                }
                if (row.Count == 0) { row.Add(groups[i]); spans.Add(across); i++; }

                int rowH = 0;
                foreach (GroupBox g in row) rowH = Math.Max(rowH, g.Height);

                int x = Margin;
                for (int k = 0; k < row.Count; k++)
                {
                    int w = colW * spans[k] + Margin * (spans[k] - 1);
                    DialogSkin.AsSticker(row[k], new Rectangle(x, y, w, rowH));
                    foreach (Control c in row[k].Controls) DialogSkin.OnGlass(c);
                    x += w + Margin;
                }

                // The value column is worked out per row, so a long caption in one
                // group cannot push another row's values around — but only among
                // groups of the SAME width. A wide group's captions used to set
                // the column for a narrow one beside it, which left the braille
                // list 65 pixels wide next to Speech's 174-pixel captions.
                var column = new Dictionary<int, int>();
                foreach (GroupBox g in row)
                {
                    int c = PropertiesSkin.LabelColumn(g);
                    if (!column.ContainsKey(g.Width) || column[g.Width] < c) column[g.Width] = c;
                }
                foreach (GroupBox g in row) { PropertiesSkin.PlaceValues(g, column[g.Width]); FitContents(g); }

                y += rowH + Margin;
            }

            for (int k = 0; k < groups.Count; k++) groups[k].TabIndex = k;
        }

        /// <summary>How many of the three columns a group asks for. Written on the
        /// group as <c>Tag = "span2"</c> by whoever built the page; one column
        /// when it says nothing.</summary>
        private static int SpanOf(GroupBox g)
        {
            string tag = g.Tag as string;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("span")) return 1;
            int n;
            return int.TryParse(tag.Substring(4), out n) ? n : 1;
        }
        /// <summary>The help text behind each group's <c>?</c>, in the order the
        /// groups were added to the page. These are the keys the hint boxes used:
        /// the words were written for exactly these groups, so they move to the
        /// pop-up rather than being written again.</summary>
        private static string[] HintKeys(TabPage page)
        {
            string name = page.Text ?? "";
            if (name == Localization.T("Settings.Tab.TextBooks"))
                // Braille lost its group on this page (2026-08-04): the reading
                // window is the braille output, so there was nothing left to set.
                return new[] { "Settings.TextBooks.Speech.Hint",
                               "Settings.TextBooks.Visual.Hint" };
            // General, in the order Gordan set: Language, Library location,
            // Media keys, Metadata, Look. Five groups now, where five loose
            // controls used to need machinery of their own to carry a key.
            if (name == Localization.T("Settings.Tab.General"))
                // No key on Language (Gordan, 2026-08-03), for the reason the
                // sound card has none: a list of languages under a label that
                // says "Language" explains itself, and a help text that only
                // restates the caption costs a reader the same time to hear as
                // one that tells them something. The empty string is how this
                // table says "no ?" — see the loop that reads it.
                return new[] { "",
                               "Settings.General.LibraryLocation.Hint",
                               "Settings.General.UseMultimediaKeys.Hint",
                               "Settings.General.UseMetadata.Hint",
                               "Settings.Misc.Look.Hint" };
            // Device: one group, and the ? carries the keep-alive text that used
            // to be an inline box the skin removed.
            if (name == Localization.T("Settings.Tab.Device"))
                return new[] { "Settings.Device.KeepAlive.Hint" };
            // OCR and Translate: reading pictures, then translating. The first group
            // still carries its explanation as an inline box, so it gets no ? — two
            // routes to the same text on one group is one route too many. The
            // translation group has no inline box and needs the key.
            if (name == Localization.T("Settings.Tab.Ocr"))
                return new[] { "", "Settings.Translate.Hint" };
            return new string[0];
        }

    }
}
