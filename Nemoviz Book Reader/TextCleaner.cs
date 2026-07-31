using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>Tidies unstructured text before it is read aloud, so TTS doesn't
    /// stumble on layout noise: huge gaps between lines (long silences), words
    /// split by an end-of-line hyphen ("co-\nmma"), stray tabs/bullets, and
    /// standalone dashes. Distilled from Gordan's Word "cleanup" macro but
    /// adapted for our needs — notably, runs of blank lines collapse to a
    /// *single* blank line (not zero), preserving paragraph boundaries that the
    /// reader navigates by. Conservative on symbols: quotes (« » „ "), &amp;,
    /// brackets and the like are left intact. Deterministic, so a book's saved
    /// character-offset position stays valid across reloads.</summary>
    public static class TextCleaner
    {
        // Clear layout/list noise + the invisible soft hyphen. Left alone:
        // guillemets/quotes, ampersand, angle/brackets, backslash, tilde.
        private static readonly Regex Noise = new Regex("[•·◦▪‣⁃¶­]", RegexOptions.Compiled);
        // Private Use Area: not text at all, but a glyph from some symbol font —
        // a Word/Wingdings list bullet (U+F0B7) above all — that survived the
        // conversion. It has no meaning to read out, and a speech engine either
        // stumbles on it or invents a name for it. Zero-width marks and a stray
        // mid-file BOM go the same way. Replaced with a space rather than deleted:
        // "•Nema" must not become "Nema" glued to the word before it.
        private static readonly Regex Invisible =
            new Regex(@"[\uE000-\uF8FF\u200B-\u200F\uFEFF]", RegexOptions.Compiled);
        // letter-hyphen-newline-letter → glue the word back together.
        private static readonly Regex Dehyphenate = new Regex(@"(\p{L})-\n(\p{L})", RegexOptions.Compiled);
        // A spaced dash (hyphen / en / em) used as punctuation → comma.
        // A hard line break in the middle of a sentence is just the source's
        // wrapping — braille wraps at ~40 columns, PDF at the page width, plain text
        // at 70-odd. Speech engines treat every newline as a prosodic boundary, so
        // those breaks make a voice stutter mid-sentence (very audible on Microsoft
        // voices, less so on eSpeak). A line that continues in lowercase is a
        // continuation, so the break becomes a space. It is a REPLACEMENT, not a
        // deletion: the text keeps its length, so every heading/page offset already
        // stored for a book stays exactly valid. Blank-line paragraph breaks are
        // untouched (the next line doesn't start with a lowercase letter there).
        private static readonly Regex WrappedLine =
            new Regex(@"(?<=\S)\n(?=\p{Ll})", RegexOptions.Compiled);

        private static readonly Regex SpacedDash = new Regex(@" [-–—] ", RegexOptions.Compiled);
        private static readonly Regex TrailingSpace = new Regex(@"[ \t]+\n", RegexOptions.Compiled);
        private static readonly Regex MultiSpace = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
        private static readonly Regex BlankRuns = new Regex(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>
        /// Cleans the text AND moves a set of character offsets with it, so the
        /// positions a parser recorded (heading and page marks) still point at the
        /// same words afterwards.
        ///
        /// <para>The offsets are the cut points: the text is split there, each piece
        /// is cleaned on its own, and the pieces are put back together — so each
        /// offset's new value is simply the length of everything cleaned before it.
        /// Exact, and with no marker characters smuggled into the text where they
        /// could change what the cleaning rules see.</para>
        ///
        /// <para>This is what keeps headings honest. Cleaning removes characters
        /// (bullets, a spaced dash, runs of spaces and blank lines), so text
        /// cleaned *after* the offsets were taken leaves every heading pointing
        /// slightly too far into the book, and further with each one.</para>
        /// </summary>
        public static string CleanWithOffsets(string text, IList<int> offsets)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (offsets == null || offsets.Count == 0) return Clean(text);

            // Cut points in order, inside the text, without duplicates.
            var cuts = new List<int>();
            foreach (int o in offsets)
                if (o > 0 && o < text.Length && !cuts.Contains(o)) cuts.Add(o);
            cuts.Sort();

            // Clean each piece on its own. They must NOT be trimmed: the blank line
            // in front of a heading belongs to the text.
            var pieces = new List<string>();
            int from = 0;
            foreach (int cut in cuts)
            {
                pieces.Add(Clean(text.Substring(from, cut - from), false));
                from = cut;
            }
            pieces.Add(Clean(text.Substring(from), false));

            // Put them back together, collapsing the whitespace where two pieces
            // meet exactly as cleaning the whole text would — otherwise a paragraph
            // break split by a cut survives as three newlines, and the text stops
            // matching what a plain clean produces.
            var sb = new StringBuilder(pieces[0]);
            var pieceStart = new int[pieces.Count];
            for (int i = 1; i < pieces.Count; i++)
            {
                string piece = pieces[i];
                int tail = TrailingWhitespace(sb);
                int head = LeadingWhitespace(piece);
                string joint = CollapseWhitespace(sb.ToString(sb.Length - tail, tail)
                                                  + piece.Substring(0, head));
                sb.Length -= tail;

                // A cut can land in the middle of the very thing a rule was going
                // to fix — a page mark in a braille book sits exactly at a line
                // break — and neither piece can see across the seam on its own. So
                // the two rules that span a line break are applied here by hand:
                // a word broken by a hyphen is glued back together, and a line that
                // continues in lower case has its break turned into a space.
                if (joint == "\n" && sb.Length > 0 && head < piece.Length)
                {
                    char before = sb[sb.Length - 1];
                    char after = piece[head];
                    if (before == '-' && sb.Length > 1 && char.IsLetter(sb[sb.Length - 2])
                        && char.IsLetter(after))
                    {
                        sb.Length -= 1;            // "poč-\nne" → "počne"
                        joint = "";
                    }
                    else if (!char.IsWhiteSpace(before) && char.IsLower(after))
                        joint = " ";               // "po\nkojima" → "po kojima"
                }
                // A dash used as punctuation ("Dado je - prvi put") becomes a
                // comma, and that pattern can straddle a seam as well.
                if (joint == " " && sb.Length >= 2 && sb[sb.Length - 2] == ' '
                    && (sb[sb.Length - 1] == '-' || sb[sb.Length - 1] == '–' || sb[sb.Length - 1] == '—'))
                {
                    sb.Length -= 2;
                    sb.Append(',');
                }

                sb.Append(joint);
                pieceStart[i] = sb.Length;         // the cut sits here
                sb.Append(piece, head, piece.Length - head);
            }

            // The ends are trimmed once, over the whole thing, the way Clean would
            // do it; everything then shifts back by whatever came off the front.
            string all = sb.ToString();
            int lead = all.Length - all.TrimStart().Length;
            all = all.Trim();

            var newAt = new Dictionary<int, int>();   // old cut → new cut
            for (int i = 0; i < cuts.Count; i++) newAt[cuts[i]] = pieceStart[i + 1] - lead;

            for (int i = 0; i < offsets.Count; i++)
            {
                int o = offsets[i];
                int moved;
                if (o <= 0) moved = 0;
                else if (o >= text.Length) moved = all.Length;
                else moved = newAt[o];
                offsets[i] = moved < 0 ? 0 : (moved > all.Length ? all.Length : moved);
            }
            return all;
        }

        private static int TrailingWhitespace(StringBuilder sb)
        {
            int n = 0;
            while (n < sb.Length && char.IsWhiteSpace(sb[sb.Length - 1 - n])) n++;
            return n;
        }

        private static int LeadingWhitespace(string s)
        {
            int n = 0;
            while (n < s.Length && char.IsWhiteSpace(s[n])) n++;
            return n;
        }

        /// <summary>What a run of whitespace between two pieces becomes: a
        /// paragraph break if it held a blank line, a single line break if it held
        /// one newline, otherwise a single space — the same shapes the cleaning
        /// rules leave behind.</summary>
        private static string CollapseWhitespace(string ws)
        {
            if (ws.Length == 0) return "";
            int newlines = 0;
            foreach (char c in ws) if (c == '\n') newlines++;
            if (newlines >= 2) return "\n\n";
            if (newlines == 1) return "\n";
            return " ";
        }

        /// <summary>Cleans an extracted document in place — its text and the
        /// heading and page offsets together — so what is written to
        /// <c>content.txt</c> is what the reader will read, with the marks already
        /// pointing at the right places in it. Called once, at import; the reader
        /// then does not clean again, and nothing can drift.</summary>
        public static void CleanDoc(TextDoc doc)
        {
            if (doc == null || string.IsNullOrEmpty(doc.Text)) return;

            var offsets = new List<int>();
            foreach (var h in doc.Headings) offsets.Add(h.Offset);
            foreach (var p in doc.Pages) offsets.Add(p.Offset);
            // The sync ids ride along for the same reason the other two do: they
            // were taken on the raw text, and a book aligned to audio drifts out
            // of step with its own narration if they are not moved.
            var syncKeys = doc.SyncIds == null ? null : new List<string>(doc.SyncIds.Keys);
            if (syncKeys != null)
                foreach (string k in syncKeys) offsets.Add(doc.SyncIds[k]);

            doc.Text = CleanWithOffsets(doc.Text, offsets);

            int at = 0;
            for (int i = 0; i < doc.Headings.Count; i++, at++)
                doc.Headings[i] = (doc.Headings[i].Level, doc.Headings[i].Title, offsets[at]);
            for (int i = 0; i < doc.Pages.Count; i++, at++)
                doc.Pages[i] = (doc.Pages[i].Label, offsets[at]);
            if (syncKeys != null)
                foreach (string k in syncKeys) doc.SyncIds[k] = offsets[at++];
        }

        public static string Clean(string text) { return Clean(text, true); }

        /// <summary><paramref name="trimEnds"/> is false for a piece of a larger
        /// text (see <see cref="CleanWithOffsets"/>): trimming there would eat the
        /// blank line in front of a heading and run it into the paragraph before
        /// it.</summary>
        private static string Clean(string text, bool trimEnds)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string t = text.Replace("\r\n", "\n").Replace("\r", "\n");
            t = t.Replace('\t', ' ');
            t = Noise.Replace(t, "");
            t = Invisible.Replace(t, " ");
            t = Dehyphenate.Replace(t, "$1$2");
            t = WrappedLine.Replace(t, " ");   // unwrap mid-sentence line breaks
            t = SpacedDash.Replace(t, ", ");
            t = TrailingSpace.Replace(t, "\n");
            t = MultiSpace.Replace(t, " ");
            t = BlankRuns.Replace(t, "\n\n"); // many blank lines → one
            return trimEnds ? t.Trim() : t;
        }
    }
}
