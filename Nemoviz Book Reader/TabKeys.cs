using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The keyboard a tabbed dialog is expected to have: Ctrl+Tab and
    /// Ctrl+Shift+Tab to step, Ctrl+1…9 to go straight to one.
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

            if (keyData == (Keys.Control | Keys.Tab)) { Step(tabs, +1); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { Step(tabs, -1); return true; }

            if ((keyData & Keys.Control) == Keys.Control &&
                (keyData & Keys.Alt) == 0 && (keyData & Keys.Shift) == 0)
            {
                Keys k = keyData & Keys.KeyCode;
                int n = -1;
                if (k >= Keys.D1 && k <= Keys.D9) n = k - Keys.D1;
                else if (k >= Keys.NumPad1 && k <= Keys.NumPad9) n = k - Keys.NumPad1;
                // A number past the last page is not an error worth a beep, but
                // it must not be swallowed either — someone else may want it.
                if (n >= 0 && n < tabs.TabCount) { Select(tabs, n); return true; }
            }
            return false;
        }

        private static void Step(TabControl tabs, int by)
        {
            int n = tabs.TabCount;
            Select(tabs, ((tabs.SelectedIndex + by) % n + n) % n);   // wraps both ways
        }

        private static void Select(TabControl tabs, int index)
        {
            if (index == tabs.SelectedIndex) return;
            tabs.SelectedIndex = index;
            // Focus follows the page, not the strip. Landing on the strip would
            // make the next Tab walk the tabs instead of the page just opened,
            // and a screen reader announces the page either way.
            try
            {
                TabPage page = tabs.TabPages[index];
                if (!page.SelectNextControl(null, true, true, true, true)) page.Focus();
            }
            catch { }
        }
    }
}
