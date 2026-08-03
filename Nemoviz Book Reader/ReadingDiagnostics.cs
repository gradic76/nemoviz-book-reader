using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>TEMPORARY TEST AID — not a feature, and meant to be deleted.
    ///
    /// <para><b>What it is for.</b> Braille output and the reading window are one
    /// mechanism: a screen reader shows whatever holds FOCUS. Without a braille
    /// display there is no way to see that mechanism working — which is exactly
    /// the position we are in while testing. This makes it AUDIBLE instead: with
    /// the aid on, each sentence is spoken by the SCREEN READER as it becomes
    /// current. Put a different voice in the reader from the one in the player
    /// and it is immediately obvious which channel is saying what.</para>
    ///
    /// <para><b>Selecting the sentence is not enough — measured.</b> The first
    /// version of this aid only moved the selection, on the strength of §8l's
    /// note that a selection is "news" a reader announces. It is not, here: the
    /// surface's text is written ONCE now and only the selection travels, so
    /// there is no change of the kind that spoke last time. Nothing was read.
    /// So the sentence is now announced explicitly, through the two channels §11
    /// already uses — a UIA notification, which JAWS hears, and the NVDA client,
    /// which NVDA hears; each is a no-op under the other reader, so neither
    /// double-speaks. The selection is kept anyway, since it is what a display
    /// would be showing.</para>
    ///
    /// <para><b>Be clear about what this proves.</b> It is NBR pushing text to
    /// the reader, not the reader collecting it by following focus. It answers
    /// WHAT would reach a display and WHEN, which is the question being asked
    /// while there is no display — but it is not the focus path itself, and a
    /// pass here is not a substitute for trying real hardware.</para>
    ///
    /// <para><b>Why this is not simply how the surface works.</b> Because it was,
    /// and it was wrong. §8l records the measurement: selecting carried braille
    /// perfectly, but the reader announced "selected" and "not selected", read
    /// the marked text out over NBR's own voice and repeated pieces. The caret
    /// alone gives a display the same line with none of the commentary. So this
    /// deliberately reintroduces a fault, because for a hearing test the
    /// commentary IS the signal.</para>
    ///
    /// <para><b>To remove it</b> — delete this file and every line that names
    /// <c>ReadingDiagnostics</c>: two in <c>Form1.UpdateReadingSurface</c> (the
    /// placement and the announcement), the Ctrl+Shift+H case in
    /// <c>Form1.ProcessCmdKey</c>, the forwarding entry in
    /// <c>ReadingWindow.ProcessCmdKey</c>, and the <c>Compile</c> line in the
    /// csproj. Nothing else refers to it and nothing persists it, so a removed
    /// build behaves as though it never existed.</para>
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
            if (Highlight) Trace("TOGGLE -> ON");   // the OFF press is the last thing not logged
            return Highlight
                ? "Reading highlight on. The screen reader will speak each sentence."
                : "Reading highlight off. Caret only.";
        }

        /// <summary>An ALWAYS-ON trace, so the next failure is read rather than
        /// guessed at.
        ///
        /// <para>Three attempts at making this audible have failed, and each was
        /// a fix to a different link in the chain — selection, then the channel,
        /// then which window the notification came from. Guessing which link is
        /// broken is what has been wrong: the chain has to say so itself. This
        /// records the key press, whether the surface update is reached at all,
        /// and what each speech channel returned.</para>
        ///
        /// <para>It was briefly always-on, to answer whether the switch was ever
        /// coming on. It has answered that, and being always-on cost what disk
        /// I/O on the UI thread always costs here: a file opened, appended and
        /// closed once per sentence, and speech that chops in the gaps. That is
        /// the second time the same mistake has been made in this file's short
        /// life, the first being SurfaceLog. So it follows the switch now — while
        /// the aid is off, nothing writes anything.</para>
        ///
        /// <para>Goes to <c>%TEMP%\NBR-diagnostics.log</c>; deleted with the rest
        /// of this file.</para></summary>
        // ── In-memory recorder ────────────────────────────────────────────────
        /// <summary>Records timings without touching the disk while the book is
        /// being read.
        ///
        /// <para>Writing a line per event to a file is what has been chopping the
        /// speech all afternoon, twice, so the one thing a recorder in the audio
        /// path must not do is exactly that. Lines go to a capped list in memory
        /// and are written out only when the tester asks, by which time nothing
        /// is being timed.</para></summary>
        private static readonly System.Collections.Generic.List<string> notes =
            new System.Collections.Generic.List<string>();

        public static void Note(string line)
        {
            lock (notes)
            {
                if (notes.Count > 4000) return;      // a minute of reading is far less
                notes.Add(System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
            }
        }

        /// <summary>Writes what has been recorded and clears it. Returns where it
        /// went, or null if there was nothing.</summary>
        public static string Dump()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NBR-timing.log");
            try
            {
                string[] lines;
                lock (notes)
                {
                    if (notes.Count == 0) return null;
                    lines = notes.ToArray();
                    notes.Clear();
                }
                System.IO.File.WriteAllLines(path, lines);
                return path;
            }
            catch { return null; }
        }

        public static void Trace(string line)
        {
            if (!Highlight) return;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NBR-diagnostics.log"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + "\r\n");
            }
            catch { }
        }

        /// <summary>Puts the reading position on the surface: a selection while
        /// the aid is on, the ordinary caret while it is off.
        ///
        /// <para>Returns nothing and decides nothing else — the caller's own
        /// behaviour is unchanged in the off case, which is what keeps this
        /// removable.</para></summary>
        public static void Place(RichTextBox surface, int start, string sentence)
        {
            if (surface == null) return;
            // The caret, always — the same thing the shipping build does, and now
            // the same in both states. Selecting the sentence was in here on the
            // theory that a reader announces a selection; it does not (that is
            // what the explicit announcement is for), and all it added was a
            // second thing for the reader to talk about over the first. A braille
            // display follows the caret anyway, so nothing is lost.
            surface.Select(start, 0);
            surface.ScrollToCaret();
        }
    }
}
