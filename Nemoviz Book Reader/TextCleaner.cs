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
        /// <summary>A drawn rule — a line of ten or more of the SAME character.
        ///
        /// <para>A speech engine reads it out one character at a time, and it
        /// carries nothing: it is a picture of a line. Measured 2026-08-28 over
        /// 355 books: 292 in 25 PDFs, 192 in 196 docx, 99 in 77 braille books.</para>
        ///
        /// <para><b>Ten of the same, and not "three or more symbols", which is the
        /// rule that would also catch <c>* * *</c>.</b> That one is a scene break
        /// — it means something, and a reader who loses it loses the pause between
        /// two scenes. The threshold is taken from the same place the rest of this
        /// file's judgements are: what the corpus shows a rule to be, rather than
        /// what looks tidy. It is also the rule the reference cleaner used
        /// (`Korice.py`, Gordan's colleague), arrived at independently.</para></summary>
        private static readonly Regex DrawnRule =
            new Regex(@"(?m)^[ \t]*([=\-*_~#.])\1{9,}[ \t]*$", RegexOptions.Compiled);
        /// <summary>True when this line finished a sentence — allowing for the
        /// closing quotes and brackets that come after the stop.</summary>
        private static readonly Regex LineEndsSentence =
            new Regex(@"[.!?…][""'”’\)\]»]*$", RegexOptions.Compiled);

        /// <summary>Turns the source's own line wrapping into spaces, so a voice
        /// does not stop in the middle of a sentence.
        ///
        /// <para><b>A break is wrapping unless the line before it ENDED a
        /// sentence</b> (Gordan, 2026-08-04). The rule this replaces asked the
        /// opposite end — "does the next line start with a lower-case letter" —
        /// and in braille that caught **nothing at all**: measured, 0 joins out of
        /// 43 466 breaks across 19 books, because a braille line that continues a
        /// sentence usually starts with a space, a quote or a capital. It left
        /// 13 954 mid-sentence breaks in braille, 7 987 in plain text and 1 212 in
        /// flat Word files, and each one is a pause the reader hears mid-sentence.
        /// Looking BACK is also the safer question: joining a line whose
        /// predecessor did end a sentence would be pointless rather than harmful,
        /// since the full stop still separates them for speech.</para>
        ///
        /// <para><b>Short lines are left alone, and the corpus chose that guard.</b>
        /// Without it the rule also glues title pages — "The Yield by" + "Tara
        /// June Winch" + "print pages", or "HRVOJE HITREC" + "SMOGOVCI". Reading
        /// the joins rather than counting them showed the split cleanly: every
        /// repair had a full line in it, every piece of damage was a stack of
        /// short ones. "Short" is half of what the text itself wraps at, not a
        /// constant — braille wraps near 40 columns, plain text near 70.</para>
        ///
        /// <para><b>It runs ONCE, over the WHOLE text, before anything is cut up,
        /// and that placement is the whole design.</b> Two earlier attempts put it
        /// inside <see cref="Clean"/> and both broke the invariant this file
        /// exists to keep — cleaning in pieces stopped matching cleaning the whole,
        /// on 7 to 8 books out of 21. The reason is the same either way round: a
        /// piece's first and last lines are cut off mid-line, so their LENGTHS are
        /// wrong, and any test that measures a line gives a different answer at a
        /// piece edge than it does in the middle of a text. Deciding before the
        /// cutting removes the question rather than answering it.</para>
        ///
        /// <para>Length-preserving to the character — every break becomes exactly
        /// as many spaces as it had characters — so the offsets
        /// <see cref="CleanWithOffsets"/> carries stay valid, and a cut still
        /// lands where it did.</para>
        ///
        /// <para>A break inside a hyphenated word is skipped and left for
        /// <see cref="Dehyphenate"/>, which needs to see the "-\n" it matches
        /// on.</para></summary>
        private static string Unwrap(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int shortLine = ShortLineFor(text);
            var sb = new StringBuilder(text);
            int lineStart = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int breakStart = (i > 0 && text[i - 1] == '\r') ? i - 1 : i;
                string prev = text.Substring(lineStart, breakStart - lineStart).TrimEnd();
                lineStart = i + 1;

                if (prev.Length == 0) continue;                 // blank line: a paragraph
                if (LineEndsSentence.IsMatch(prev)) continue;   // it finished; leave it
                if (prev.Length < shortLine) continue;          // a heading, not a wrap
                // "poč-\nne" belongs to Dehyphenate, which matches on the newline.
                if (prev[prev.Length - 1] == '-') continue;
                // A blank line after the break is a paragraph mark of its own.
                int n = i + 1;
                while (n < text.Length && (text[n] == ' ' || text[n] == '\t')) n++;
                if (n >= text.Length || text[n] == '\n' || text[n] == '\r') continue;

                for (int k = breakStart; k <= i; k++) sb[k] = ' ';
            }
            return sb.ToString();
        }

        /// <summary>Half the width this text wraps at, which is what "short" means
        /// for it. From the 90th percentile of the non-blank line lengths rather
        /// than a constant, because braille wraps near 40 columns, plain text near
        /// 70, and a flat Word export at whatever it likes.</summary>
        private static int ShortLineFor(string text)
        {
            var lens = new List<int>();
            foreach (string l in text.Split('\n'))
            {
                string s = l.TrimEnd();
                if (s.Length > 0) lens.Add(s.Length);
            }
            if (lens.Count == 0) return 20;
            lens.Sort();
            int wrap = lens[(int)(lens.Count * 0.90)];
            return wrap / 2 < 10 ? 10 : wrap / 2;
        }

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

            // FIRST, and over the whole text: the one rule that needs to see more
            // than a piece. It is length-preserving, so every cut below still
            // lands exactly where it did. See Unwrap for why it cannot live inside
            // the per-piece cleaning.
            text = Unwrap(text);

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
                    // The unwrapping rule is NOT applied here any more: it has
                    // already run over the whole text above, so a break that
                    // survives to this seam is one it deliberately kept.
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

        public static string Clean(string text)
        {
            // Same order as CleanWithOffsets: unwrap the whole thing first, then
            // clean. If these two ever disagree about when it happens, cleaning a
            // book in pieces stops matching cleaning it whole, which is the one
            // thing this file may not do.
            return Clean(Unwrap(text ?? ""), true);
        }

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
            // Before Dehyphenate, or a rule drawn with hyphens loses its first
            // character to it and stops being ten of the same.
            t = DrawnRule.Replace(t, "");
            t = Dehyphenate.Replace(t, "$1$2");
            t = SpacedDash.Replace(t, ", ");
            t = TrailingSpace.Replace(t, "\n");
            t = MultiSpace.Replace(t, " ");
            t = BlankRuns.Replace(t, "\n\n"); // many blank lines → one
            return trimEnds ? t.Trim() : t;
        }
    }
}
