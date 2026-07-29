using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    internal sealed class LibraryParts
    {
        public MenuStrip Menu;
        public Panel SearchRow, BottomPanel;
        public TextBox Search;
        public ComboBox Filter;
        public SplitContainer Split;
        public ListView Books, Details;
        public Button Refresh, Load, Close;
    }

    /// <summary>
    /// The Library wearing the same face as Properties and Settings — and the
    /// last of the three sub-windows to get it.
    ///
    /// <para><b>The grid, as agreed (Gordan, 2026-07-29).</b> The menu bar runs
    /// the full width across the top. Below it the window is three columns with
    /// <b>A and B joined and C on its own</b>: the search box spans AB with the
    /// filter over C, and under them the shelf spans AB with the info box under
    /// C. So a column means the same thing all the way down — what you are
    /// looking through on the left, what you have picked on the right.</para>
    ///
    /// <para>Refresh, Load and Close go on the metal. The names are the short
    /// ones: the buttons said "OK" and "Cancel", which say nothing about a shelf
    /// — you <i>load</i> a book and you <i>close</i> the Library.</para>
    /// </summary>
    internal static class LibrarySkin
    {
        private const int Margin = 12;
        private const int MenuH = 26, RowH = 28, Gap = 8;
        // AB and C. The split mirrors the proportions Properties uses, the wide
        // part simply being on the other side: the shelf is what you read here.
        private const int AbX = 12, AbW = 616;
        private const int CX = 640, CW = 308;

        public static void Apply(LibraryForm f)
        {
            LibraryParts p = f.SkinParts;
            if (p == null || p.Split == null) return;

            DialogSkin.EnsureFonts();
            f.SuspendLayout();
            DialogCanvas canvas = DialogSkin.Shell(f, DialogSkin.H);

            // The menu bar keeps every bit of what it is — a real MenuStrip, real
            // items, real Alt access keys. Only the colours change.
            if (p.Menu != null)
            {
                p.Menu.Dock = DockStyle.None;
                p.Menu.SetBounds(Margin, Margin, DialogSkin.W - 2 * Margin, MenuH);
                // The professional renderer paints its own gradient over
                // BackColor and ignores it; the system renderer honours it, which
                // is all that is wanted here — the bar goes dark, the items stay
                // real menu items with their Alt keys.
                p.Menu.RenderMode = ToolStripRenderMode.System;
                p.Menu.BackColor = NewPlayerSkin.Glass;
                p.Menu.ForeColor = NewPlayerSkin.Lit;
                p.Menu.Font = DialogSkin.FBody;
                foreach (ToolStripItem it in p.Menu.Items)
                {
                    it.BackColor = NewPlayerSkin.Glass;
                    it.ForeColor = NewPlayerSkin.Lit;
                }
                p.Menu.BringToFront();
            }

            // The menu BAR stays a MenuStrip, and this is measured, not assumed.
            // A real Win32 menu bar (Form.Menu / MainMenu) was tried on this very
            // window: it does draw on a borderless form, but Windows draws it in
            // the window's own top strip — OUTSIDE the rounded casing, in system
            // colours, and it takes 15 units of client height with it, which
            // shoved the whole skin down. A menu bar lives in the non-client area
            // by definition, and this window has none. So the bar keeps the
            // control that can live inside the casing; only the shelf's popup
            // menu became a real Windows menu, which it can, being a popup.
            int y = Margin + MenuH + Gap;

            // Row 1: what you are looking through, and what you are looking at.
            if (p.SearchRow != null)
            {
                p.SearchRow.SetBounds(0, y, DialogSkin.W, RowH);
                p.SearchRow.BackColor = NewPlayerSkin.PanelMid;
            }
            if (p.Search != null)
            {
                p.Search.SetBounds(AbX, 2, AbW, 24);
                DialogSkin.OnGlass(p.Search);
                p.Search.BorderStyle = BorderStyle.FixedSingle;
            }
            if (p.Filter != null)
            {
                p.Filter.SetBounds(CX, 2, CW, 24);
                DialogSkin.OnGlass(p.Filter);
            }

            y += RowH + Gap;

            // The shelf and the info box, in the same two columns.
            p.Split.SetBounds(0, y, DialogSkin.W, DialogSkin.ButtonsY - Gap - y);
            p.Split.BackColor = NewPlayerSkin.PanelMid;
            p.Split.SplitterWidth = CX - (AbX + AbW);
            p.Split.Panel1MinSize = 200;
            p.Split.Panel2MinSize = 200;
            p.Split.SplitterDistance = AbX + AbW;
            p.Split.Panel1.Padding = new Padding(AbX, 0, 0, 0);
            p.Split.Panel2.Padding = new Padding(0, 0, DialogSkin.W - (CX + CW), 0);

            AsShelf(p.Books);
            AsShelf(p.Details);
            // Less the panel's own 10-unit padding on each side and the vertical
            // scroll bar, or the columns add up to more than the list and it grows
            // a horizontal bar under itself.
            FitColumns(p.Details, CW - 20 - SystemInformation.VerticalScrollBarWidth - 4);

            // The buttons sit on the metal, so the panel they were built in has
            // nothing left to be.
            if (p.BottomPanel != null)
            {
                p.BottomPanel.Visible = false;
                // BringToFront is not optional here. Controls.Add appends, and in
                // WinForms the END of the collection is the BACK of the z-order —
                // so a control added after the canvas exists lands behind it. The
                // buttons were there and painting, underneath the metal.
                Front(f, p.Refresh);
                Front(f, p.Load);
                Front(f, p.Close);
            }

            Rename(p.Load, "Library.Btn.Load");
            Rename(p.Close, "Library.Btn.Close");

            DialogSkin.AsKey(p.Refresh, new Rectangle(Margin, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.Load, new Rectangle(716, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));
            DialogSkin.AsKey(p.Close, new Rectangle(836, DialogSkin.ButtonsY,
                DialogSkin.ButtonW, DialogSkin.ButtonH));

            f.ResumeLayout();
            canvas.Rebuild();
        }

        /// <summary>Shares a two-column list's width between its columns. The
        /// info box came over with the widths it was built for and ran off its own
        /// right edge — "PDF — Portable Docum" with a horizontal scroll bar under
        /// it. The width is passed in rather than read off the control, which
        /// inside SuspendLayout still answers with the old one.</summary>
        private static void FitColumns(ListView v, int width)
        {
            if (v == null || v.Columns.Count != 2 || width <= 0) return;
            int label = (int)(width * 0.38);
            v.Columns[0].Width = label;
            v.Columns[1].Width = Math.Max(40, width - label);
        }

        private static void Front(Form f, Control c)
        {
            if (c == null) return;
            f.Controls.Add(c);
            c.BringToFront();
        }

        /// <summary>A list on the glass. It stays a real ListView in Details view
        /// — the shelf reads like a file list on purpose, one row per book, and
        /// nothing here changes what a screen reader is handed.</summary>
        private static void AsShelf(ListView v)
        {
            if (v == null) return;
            v.BorderStyle = BorderStyle.None;
            v.BackColor = NewPlayerSkin.Glass;
            v.ForeColor = NewPlayerSkin.Lit;
            v.Font = DialogSkin.FBody;
        }

        /// <summary>Gives a button the short name and keeps its spoken name in
        /// step. Done here rather than in the form so the classic path still says
        /// OK and Cancel.</summary>
        private static void Rename(Button b, string key)
        {
            if (b == null) return;
            b.Text = Localization.T(key);
            b.AccessibleName = Localization.T(key + ".Accessible");
        }
    }
}
