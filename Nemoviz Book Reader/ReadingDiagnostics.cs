using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>TEMPORARY TEST AID — not a feature, and meant to be deleted.
    ///
    /// <para><b>What it is for.</b> Braille output and the reading window are one
    /// mechanism: a screen reader shows whatever holds FOCUS. Without a braille
    /// display there is no way to see that mechanism working — which is exactly
    /// the position we are in while testing. This makes the same mechanism
    /// AUDIBLE instead: with it on, the reading surface SELECTS the sentence
    /// being read rather than only moving the caret to it, and a screen reader
    /// treats a selection as news and speaks it. Put a different voice in the
    /// screen reader from the one in the player and it becomes obvious which
    /// channel is saying what — the player's own speech, or the reading surface
    /// going through the reader exactly as it would go to a display.</para>
    ///
    /// <para><b>Why this is not simply how the surface works.</b> Because it was,
    /// and it was wrong. §8l records the measurement: selecting carried braille
    /// perfectly, but the reader announced "selected" and "not selected", read
    /// the marked text out over NBR's own voice and repeated pieces. The caret
    /// alone gives a display the same line with none of the commentary. So this
    /// deliberately reintroduces a fault, because for a hearing test the
    /// commentary IS the signal.</para>
    ///
    /// <para><b>To remove it</b> — delete this file and the three call sites that
    /// name <c>ReadingDiagnostics</c>: the one in <c>Form1.UpdateReadingSurface</c>,
    /// the Ctrl+Shift+H case in <c>Form1.ProcessCmdKey</c>, and the forwarding
    /// entry in <c>ReadingWindow.ProcessCmdKey</c>. Nothing else refers to it and
    /// nothing persists it, so a removed build behaves as though it never
    /// existed.</para>
    ///
    /// <para><b>Its strings are English literals, on purpose.</b> §10 makes
    /// en.lang the single source of user-visible text, and this is the one
    /// deliberate exception: putting them there would mean removing this aid also
    /// meant editing en.lang and hr.lang, which is precisely the entanglement a
    /// temporary thing should not have. Two lines, spoken to a tester.</para></summary>
    internal static class ReadingDiagnostics
    {
        /// <summary>Off unless a tester turns it on, and never persisted — every
        /// run starts clean, so it cannot be left on by accident and quietly
        /// change what someone else is hearing.</summary>
        public static bool Highlight;

        public static string Toggle()
        {
            Highlight = !Highlight;
            return Highlight
                ? "Reading highlight on. The screen reader will speak each sentence."
                : "Reading highlight off. Caret only.";
        }

        /// <summary>Puts the reading position on the surface: a selection while
        /// the aid is on, the ordinary caret while it is off.
        ///
        /// <para>Returns nothing and decides nothing else — the caller's own
        /// behaviour is unchanged in the off case, which is what keeps this
        /// removable.</para></summary>
        public static void Place(TextBox surface, int start, string sentence)
        {
            if (surface == null) return;
            int length = 0;
            if (Highlight && !string.IsNullOrEmpty(sentence))
            {
                length = sentence.Length;
                if (start + length > surface.TextLength) length = surface.TextLength - start;
                if (length < 0) length = 0;
            }
            surface.Select(start, length);
            surface.ScrollToCaret();
        }
    }
}
