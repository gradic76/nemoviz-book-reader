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
            AttachLooseHints(f as SettingsForm, pw);

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

            // One column while it fits; TWO when it does not. Text Books is the
            // page that needs it — Speech, Braille and Visual come to more than a
            // 640-tall dialog has, and the old answer was a scroll bar. Dropping
            // the info column bought the width to solve it properly instead
            // (Gordan: "ako je stalo u Properties uz infobox, mora stati i ovdje").
            // Nothing is ever cut off and nothing scrolls.
            var columns = new List<List<GroupBox>>();
            if (content + 12 * (n - 1) > avail && n > 1)
            {
                // Balanced by height, in the order the groups were added — which
                // is the order they are read in, and that order must survive.
                var left = new List<GroupBox>();
                var right = new List<GroupBox>();
                int half = content / 2, run = 0;
                foreach (GroupBox g in groups)
                {
                    if (run > 0 && run + g.Height / 2 > half) right.Add(g);
                    else { left.Add(g); run += g.Height; }
                }
                if (right.Count == 0) { right.Add(left[left.Count - 1]); left.RemoveAt(left.Count - 1); }
                columns.Add(left);
                columns.Add(right);
            }
            else columns.Add(groups);

            int colW = columns.Count > 1 ? (width - Margin) / 2 : width;
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

        /// <summary>Hangs a <c>?</c> on the pages whose controls stand loose —
        /// General and Misc, which have no groups to carry one. Their
        /// explanations were written and then never appeared: every hint text in
        /// en.lang existed, and six of the ten had nothing to open them (Gordan
        /// found it while writing the Help, 2026-08-03).
        ///
        /// <para>The keys line up in a column at the right-hand edge rather than
        /// beside each control, because the controls are of every width — a
        /// checkbox, a text box with a Browse button, a combo — and keys chasing
        /// their right edges would read as scatter. Each one is still ABOUT its
        /// own row: it sits at that row's height and its name says which setting
        /// it explains.</para></summary>
        private static void AttachLooseHints(SettingsForm f, int pw)
        {
            if (f == null) return;
            foreach (SettingsForm.LooseHint h in f.LooseHints)
            {
                Control a = h.Anchor;
                if (a == null || a.Parent == null) continue;
                int y = a.Top + Math.Max(0, (a.Height - 22) / 2);
                HintSystem.Attach(a, h.BodyKey, a.Parent,
                                  new Rectangle(pw - Margin - 22, y, 22, 22), h.Subject);
            }
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
            return new string[0];
        }

    }
}
