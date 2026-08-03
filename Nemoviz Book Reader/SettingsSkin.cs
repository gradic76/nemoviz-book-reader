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

        /// <summary>One page: glass underneath, its groups as stickers down the
        /// full width, everything else recoloured where it stands. A page's loose
        /// controls are laid out by hand, pair by pair, and a loop that only knows
        /// "control" cannot put a label back beside the thing it labels — the
        /// lesson the audio cells taught.</summary>
        private static void LayOutPage(TabPage page, int pw, int ph)
        {
            page.UseVisualStyleBackColor = false;
            page.BackColor = NewPlayerSkin.Glass;
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
            int content = 0;
            foreach (GroupBox g in groups) content += g.Height;
            int n = groups.Count;

            // A page can ask for a GRID: three across, wrapping — which is what
            // General wants, five short groups of a kind. Left to right and then
            // down is also the order they were added, so what a reader hears is
            // what a reader sees (Gordan, 2026-08-03).
            if ((page.Tag as string) == "grid3")
            {
                LayOutGrid(groups, 3, width, avail);
                // NOT a return — the help keys are attached at the foot of this
                // method, and jumping out took every ? off the page. Found by the
                // audit, which is the whole reason it counts them.
                AttachGroupHints(page, groups);
                return;
            }

            // Otherwise: as FEW columns as the height allows, up to three. One
            // while everything fits; two for Speech and Braille, whose three
            // groups come to more than a 640-tall dialog has and whose old answer
            // was a scroll bar. Nothing is cut off and nothing scrolls, and the
            // ORDER the groups were added survives.
            int wanted = 1;
            while (wanted < 3 && wanted < n && !FitsInColumns(groups, wanted, avail)) wanted++;

            var columns = SplitIntoColumns(groups, wanted, content);

            int colW = columns.Count > 1
                ? (width - Margin * (columns.Count - 1)) / columns.Count : width;
            for (int ci = 0; ci < columns.Count; ci++)
            {
                List<GroupBox> col = columns[ci];
                int used = 0;
                foreach (GroupBox g in col) used += g.Height;
                int cn = col.Count;
                int slack = avail - used;
                int pad = Math.Max(0, Math.Min(10, slack / (cn * 2)));
                slack -= pad * cn;
                int gap = cn > 1 ? Math.Max(6, slack / (cn - 1)) : 12;

                int x = Margin + ci * (colW + Margin);
                int y = Margin;
                foreach (GroupBox g in col)
                {
                    int h = g.Height + pad;
                    DialogSkin.AsSticker(g, new Rectangle(x, y, colW, h));
                    foreach (Control c in g.Controls) DialogSkin.OnGlass(c);
                    y += h + gap;
                }

                // The value column is worked out PER COLUMN. Sharing one across
                // both would let the widest label on the page push the other
                // column's values into its own right-hand edge.
                int column = 0;
                foreach (GroupBox g in col) column = Math.Max(column, PropertiesSkin.LabelColumn(g));
                foreach (GroupBox g in col) { PropertiesSkin.PlaceValues(g, column); PullButtonsIn(g); }
            }

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

        /// <summary>A row of buttons laid out for a 500-wide dialog does not fit a
        /// 444-wide column, and "Speech dictionary…" was simply cut off by the
        /// group's edge. They move together, by the worst overflow among them:
        /// nudging only the offender would push it straight into the button on its
        /// left. The <c>?</c> in the corner is left alone — it is positioned from
        /// the right edge and belongs there.</summary>
        private static void PullButtonsIn(GroupBox g)
        {
            int limit = g.Width - 14, over = 0;
            var buttons = new List<Button>();
            foreach (Control c in g.Controls)
            {
                Button b = c as Button;
                if (b == null || HintSystem.IsHelpKey(b)) continue;
                buttons.Add(b);
                over = Math.Max(over, b.Right - limit);
            }
            if (over <= 0) return;
            foreach (Button b in buttons) b.Left = Math.Max(14, b.Left - over);
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

            Button pairButton = null;
            TextBox pairBox = null;
            foreach (Control c in g.Controls)
            {
                Button b = c as Button;
                if (b != null && !HintSystem.IsHelpKey(b)) pairButton = b;
                TextBox t = c as TextBox;
                if (t != null) pairBox = t;
            }

            if (pairButton != null && pairBox != null)
            {
                pairButton.Left = Math.Max(pairBox.Left + 60, inner - pairButton.Width);
                pairBox.Width = Math.Max(60, pairButton.Left - 8 - pairBox.Left);
                return;
            }

            foreach (Control c in g.Controls)
            {
                Button b = c as Button;
                if (b != null && HintSystem.IsHelpKey(b)) continue;   // pinned to its corner
                if (c.Right <= inner) continue;
                if (c is Button) c.Left = Math.Max(14, inner - c.Width);
                else c.Width = Math.Max(40, inner - c.Left);
            }
        }

        /// <summary>Groups laid left to right, <paramref name="across"/> to a
        /// row, wrapping. Every group in a row is given the height of the tallest
        /// in it, so the rows line up and a short group does not leave a step in
        /// the edge beside a tall one.</summary>
        private static void LayOutGrid(List<GroupBox> groups, int across, int width, int avail)
        {
            int colW = (width - Margin * (across - 1)) / across;
            int y = Margin;
            for (int i = 0; i < groups.Count; i += across)
            {
                int rowH = 0;
                for (int k = i; k < groups.Count && k < i + across; k++)
                    rowH = Math.Max(rowH, groups[k].Height);

                for (int k = i; k < groups.Count && k < i + across; k++)
                {
                    int x = Margin + (k - i) * (colW + Margin);
                    DialogSkin.AsSticker(groups[k], new Rectangle(x, y, colW, rowH));
                    foreach (Control c in groups[k].Controls) DialogSkin.OnGlass(c);
                    groups[k].TabIndex = k;
                }

                int column = 0;
                for (int k = i; k < groups.Count && k < i + across; k++)
                    column = Math.Max(column, PropertiesSkin.LabelColumn(groups[k]));
                for (int k = i; k < groups.Count && k < i + across; k++)
                { PropertiesSkin.PlaceValues(groups[k], column); FitContents(groups[k]); }

                y += rowH + Margin;
            }
        }

        /// <summary>Would these groups fit if they were dealt into this many
        /// columns, keeping their order? Height only — the width is whatever is
        /// left after the split.</summary>
        private static bool FitsInColumns(List<GroupBox> groups, int count, int avail)
        {
            int total = 0;
            foreach (GroupBox g in groups) total += g.Height;
            foreach (List<GroupBox> col in SplitIntoColumns(groups, count, total))
            {
                int used = 0;
                foreach (GroupBox g in col) used += g.Height;
                if (used + 12 * (col.Count - 1) > avail) return false;
            }
            return true;
        }

        /// <summary>Deals the groups into columns <b>in the order they were
        /// added</b>, filling each until it has had its share of the total
        /// height. Reading order is the one thing that must not be rearranged for
        /// the sake of a tidier picture.</summary>
        private static List<List<GroupBox>> SplitIntoColumns(List<GroupBox> groups, int count, int content)
        {
            var columns = new List<List<GroupBox>>();
            if (count <= 1) { columns.Add(new List<GroupBox>(groups)); return columns; }

            int share = Math.Max(1, content / count);
            var col = new List<GroupBox>();
            int run = 0;
            foreach (GroupBox g in groups)
            {
                if (col.Count > 0 && columns.Count < count - 1 && run + g.Height / 2 > share)
                {
                    columns.Add(col);
                    col = new List<GroupBox>();
                    run = 0;
                }
                col.Add(g);
                run += g.Height;
            }
            columns.Add(col);
            // A column left empty by rounding takes the last group of the one
            // before it, rather than being drawn as a blank stripe.
            while (columns.Count < count && columns[columns.Count - 1].Count > 1)
            {
                var last = columns[columns.Count - 1];
                var moved = new List<GroupBox> { last[last.Count - 1] };
                last.RemoveAt(last.Count - 1);
                columns.Add(moved);
            }
            return columns;
        }

        /// <summary>The help text behind each group's <c>?</c>, in the order the
        /// groups were added to the page. These are the keys the hint boxes used:
        /// the words were written for exactly these groups, so they move to the
        /// pop-up rather than being written again.</summary>
        private static string[] HintKeys(TabPage page)
        {
            string name = page.Text ?? "";
            if (name == Localization.T("Settings.Tab.TextBooks"))
                return new[] { "Settings.TextBooks.Speech.Hint",
                               "Settings.TextBooks.Braille.Hint",
                               "Settings.TextBooks.Visual.Hint" };
            // General, in the order Gordan set: Language, Library location,
            // Media keys, Metadata, Look. Five groups now, where five loose
            // controls used to need machinery of their own to carry a key.
            if (name == Localization.T("Settings.Tab.General"))
                return new[] { "Settings.General.Language.Hint",
                               "Settings.General.LibraryLocation.Hint",
                               "Settings.General.UseMultimediaKeys.Hint",
                               "Settings.General.UseMetadata.Hint",
                               "Settings.Misc.Look.Hint" };
            return new string[0];
        }

    }
}
