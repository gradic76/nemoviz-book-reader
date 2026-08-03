using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The keyboard a tabbed dialog is expected to have: Ctrl+Tab and
    /// Ctrl+Shift+Tab to step, Ctrl+1…9 to go straight to one, and the arrows
    /// along the strip once it has focus.
    ///
    /// <para><b>Two different jobs, so two behaviours.</b> The Ctrl keys are how
    /// a reader ARRIVES somewhere: they select the page and put focus in it.
    /// The arrows are how a reader LOOKS along the strip: they select the page
    /// and leave focus on the strip, because dropping into the page would end
    /// the walk at its first step — measured, the second arrow then moved
    /// between controls instead of tabs.</para>
    ///
    /// <para><b>Why it has to be written at all.</b> A WinForms TabControl only
    /// answers Ctrl+Tab while the TAB STRIP itself has focus. Once focus is
    /// inside the page — which is where it is the whole time anyone is actually
    /// using the dialog — the key does nothing, so the reader has to tab back
    /// out to the strip and arrow along it. Everywhere else in Windows those
    /// keys work from inside, and a dialog that looks like every other tabbed
    /// dialog should behave like one (Gordan, 2026-08-02).</para>
    ///
    /// <para><b>It wraps.</b> Stepping past the last page comes back to the
    /// first, and back from the first goes to the last. A reader moving by
    /// keyboard has no edge to see and no reason to expect one; stopping dead at
    /// the end just reads as the key having failed.</para>
    ///
    /// <para>Shared rather than written twice, so Properties and Settings cannot
    /// drift apart on something a reader will assume is the same everywhere.</para></summary>
    internal static class TabKeys
    {
        /// <summary>Handles the key if it is one of ours. Returns true when it
        /// did, for a ProcessCmdKey override to pass straight back.</summary>
        public static bool Handle(TabControl tabs, Keys keyData)
        {
            if (tabs == null || tabs.TabCount == 0) return false;

            if (keyData == (Keys.Control | Keys.Tab)) { Step(tabs, +1, true); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { Step(tabs, -1, true); return true; }

            // Arrows, but ONLY while the strip itself has the focus. Inside a
            // page the arrows belong to whatever is there — a combo box needs Up
            // and Down, and taking them would be worse than anything this fixes.
            //
            // Windows moves the selection with them already; what it will not do
            // is WRAP. So the same dialog answered Ctrl+Tab past the last page by
            // coming round to the first and answered Right by doing nothing at
            // all, which reads as the key being broken rather than as an edge.
            // Focus stays on the strip here, unlike the Ctrl keys: someone
            // arrowing along the tabs is still choosing, and dropping them into
            // the page would end the walk after one step.
            if (tabs.Focused && (keyData & Keys.Modifiers) == 0)
            {
                Keys arrow = keyData & Keys.KeyCode;
                if (arrow == Keys.Right || arrow == Keys.Down) { Step(tabs, +1, false); return true; }
                if (arrow == Keys.Left || arrow == Keys.Up) { Step(tabs, -1, false); return true; }
            }

            if ((keyData & Keys.Control) == Keys.Control &&
                (keyData & Keys.Alt) == 0 && (keyData & Keys.Shift) == 0)
            {
                Keys k = keyData & Keys.KeyCode;
                int n = -1;
                if (k >= Keys.D1 && k <= Keys.D9) n = k - Keys.D1;
                else if (k >= Keys.NumPad1 && k <= Keys.NumPad9) n = k - Keys.NumPad1;
                // A number past the last page is not an error worth a beep, but
                // it must not be swallowed either — someone else may want it.
                if (n >= 0 && n < tabs.TabCount) { Select(tabs, n, true); return true; }
            }
            return false;
        }

        private static void Step(TabControl tabs, int by, bool intoPage)
        {
            int n = tabs.TabCount;
            Select(tabs, ((tabs.SelectedIndex + by) % n + n) % n, intoPage);   // wraps both ways
        }

        /// <param name="intoPage">Whether focus should follow into the page. True
        /// for the Ctrl keys, which are how a reader ARRIVES at a page — landing
        /// on the strip would make the next Tab walk the tabs instead of the page
        /// just opened. False for the arrows, which are how a reader LOOKS along
        /// the strip; dropping them into the page would end the walk at its first
        /// step.</param>
        private static void Select(TabControl tabs, int index, bool intoPage)
        {
            if (index == tabs.SelectedIndex) return;
            tabs.SelectedIndex = index;
            if (!intoPage)
            {
                // Changing the selection hands focus to the new page by itself,
                // which ends an arrow walk after one step: the second arrow is
                // then a page key and moves between controls instead of tabs.
                // Measured — the first Right went to the next tab and the Left
                // after it moved along the page.
                tabs.Focus();
                return;
            }
            try
            {
                TabPage page = tabs.TabPages[index];
                if (!page.SelectNextControl(null, true, true, true, true)) page.Focus();
            }
            catch { }
        }
    }
}
