using System;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Repairs to a page of recognized text that belong to the PAGE and not to
    /// the engine — things a printed page does that a reader does not want read
    /// aloud.
    ///
    /// <para>Kept apart from <see cref="WindowsOcr"/> deliberately: that class is
    /// about getting words off an image and is judged on whether the words are
    /// right. This is about what a book does with words, and every rule in it is
    /// a judgement that can be wrong on some book somewhere — so they are small,
    /// separately named, and each says what it is guarding against.</para>
    /// </summary>
    public static class OcrTidy
    {
        /// <summary>Rejoins a word that the printed page broke across a line.
        ///
        /// <para><b>Measured on a real scanned book</b> (Gordan's own, 252 pages):
        /// <c>kono- barom</c>, <c>napisa- no</c>, <c>za- boraviti</c>,
        /// <c>bi- ograne</c>. Two of seven lines on an average page end in a
        /// hyphen, so this is not a rarity — and spoken aloud it comes out as two
        /// words that are not words.</para>
        ///
        /// <para><b>The space is what makes it safe.</b> A hyphen the AUTHOR wrote
        /// — <c>hrvatsko-srpski</c>, <c>Rimac-Kovač</c> — has no space after it,
        /// because it was never a line break. Only "letter, hyphen, space,
        /// lower-case letter" is joined, so a real compound is left alone. The
        /// second letter must be lower case for the same reason: <c>Zagreb- Split</c>
        /// is a range or a pairing, not half a word.</para>
        ///
        /// <para>A dash is not a hyphen and is not touched: an em dash with spaces
        /// round it is punctuation the author meant.</para></summary>
        public static string JoinBrokenWords(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c == '-' || c == '­')          // hyphen, or a soft hyphen
                    && i > 0 && i + 2 < text.Length
                    && char.IsLetter(text[i - 1])
                    && char.IsLower(text[i - 1])
                    && (text[i + 1] == ' ' || text[i + 1] == '\n' || text[i + 1] == '\r'))
                {
                    // Look past the break for the rest of the word.
                    int j = i + 1;
                    while (j < text.Length && (text[j] == ' ' || text[j] == '\n' || text[j] == '\r')) j++;
                    if (j < text.Length && char.IsLetter(text[j]) && char.IsLower(text[j]))
                    {
                        i = j - 1;      // swallow the hyphen and the gap
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Strips the furniture off the top and bottom of every page: the
        /// page number, and a running head or footer where the book has one.
        ///
        /// <para><b>Everything here is proved by REPETITION, never by position
        /// alone.</b> A line at the top of a page is not a header — it is the
        /// first line of the page, and on most pages that is the book. What makes
        /// it furniture is that the same thing is there on page after page. So a
        /// candidate is removed only if it recurs on <see cref="MinRepeatShare"/>
        /// of the pages that have any text, which is why a book WITHOUT running
        /// heads loses nothing: measured on Gordan's 252-page scan, where the top
        /// line is either the page number or the opening of a paragraph, and no
        /// two pages agree.</para>
        ///
        /// <para>A page number counts as its own case because it is different on
        /// every page by design. It is recognised by being a line of nothing but
        /// digits — measured, that is exactly how it appears: a line reading "7",
        /// alone at the top, above the body by a wide gap.</para>
        ///
        /// <para><b>What is NOT attempted, and why:</b> footnotes. The obvious
        /// signal is small type, and it does not survive contact with the data —
        /// on the book measured, the lines flagged as small were ordinary short
        /// ones ("temama.", "„Ne znam.") whose words happen to lack tall letters,
        /// while the real body ran 33–43 px for the same size of type. There were
        /// no footnotes in the sample to build against either. Guessing here would
        /// cut sentences out of books, so it waits for a book that has some.</para></summary>
        /// <param name="pageLines">One list of lines per page, in order. Modified.</param>
        /// <returns>(page numbers removed, header/footer lines removed)</returns>
        public static Tuple<int, int> StripFurniture(
            System.Collections.Generic.IList<System.Collections.Generic.List<string>> pageLines)
        {
            if (pageLines == null || pageLines.Count < MinPagesToJudge) return Tuple.Create(0, 0);

            // NUMBERS, THEN HEADS, THEN NUMBERS AGAIN — and the second pass is
            // not belt and braces. Caught in testing: a page can carry BOTH, in
            // either order. Strip the number first and the head is exposed;
            // strip the head first and the number that was under it is now the
            // first line and would survive a pass that has already run. Two
            // number passes round the head pass catches both arrangements, and
            // costs nothing when there is only one layer.
            int numbers = StripNumbers(pageLines);

            int furniture = 0;
            furniture += StripRepeating(pageLines, true);
            furniture += StripRepeating(pageLines, false);

            if (furniture > 0) numbers += StripNumbers(pageLines);
            return Tuple.Create(numbers, furniture);
        }

        private static int StripNumbers(
            System.Collections.Generic.IList<System.Collections.Generic.List<string>> pageLines)
        {
            int numbers = 0;
            foreach (var lines in pageLines)
            {
                // Top and bottom only: a bare number in the middle of a page is
                // part of the book — a date, a track listing, a score.
                if (lines.Count > 0 && IsAllDigits(lines[0])) { lines.RemoveAt(0); numbers++; }
                if (lines.Count > 0 && IsAllDigits(lines[lines.Count - 1]))
                { lines.RemoveAt(lines.Count - 1); numbers++; }
            }
            return numbers;
        }

        /// <summary>A book has to be at least this long before its pages are
        /// judged against each other. Below it, a repetition is a coincidence.</summary>
        public const int MinPagesToJudge = 8;

        /// <summary>How many of the text-bearing pages must carry the same first
        /// (or last) line before it is called furniture rather than prose.</summary>
        public const double MinRepeatShare = 0.35;

        private static int StripRepeating(
            System.Collections.Generic.IList<System.Collections.Generic.List<string>> pageLines, bool fromTop)
        {
            var seen = new System.Collections.Generic.Dictionary<string, int>();
            int withText = 0;
            foreach (var lines in pageLines)
            {
                if (lines.Count == 0) continue;
                withText++;
                string key = Normalize(fromTop ? lines[0] : lines[lines.Count - 1]);
                if (key.Length == 0) continue;
                int n; seen.TryGetValue(key, out n); seen[key] = n + 1;
            }
            if (withText < MinPagesToJudge) return 0;

            string best = null; int bestCount = 0;
            foreach (var kv in seen)
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
            if (best == null || bestCount < withText * MinRepeatShare) return 0;

            int removed = 0;
            foreach (var lines in pageLines)
            {
                if (lines.Count == 0) continue;
                int at = fromTop ? 0 : lines.Count - 1;
                if (Normalize(lines[at]) != best) continue;
                lines.RemoveAt(at);
                removed++;
            }
            return removed;
        }

        /// <summary>Compares running heads the way a reader would hear them:
        /// case and spacing do not matter, and the DIGITS are dropped because a
        /// running head very often carries the page number with it — "34  SVE
        /// SAMO NE ROMANTIKA" is the same head as "35  SVE SAMO NE ROMANTIKA".</summary>
        private static string Normalize(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            var sb = new StringBuilder(line.Length);
            foreach (char c in line)
                if (char.IsLetter(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        private static bool IsAllDigits(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            bool digit = false;
            foreach (char c in line.Trim())
            {
                if (char.IsDigit(c)) { digit = true; continue; }
                if (c == ' ' || c == '.' || c == '|' || c == 'l' || c == 'I') continue;  // OCR noise round a lone numeral
                return false;
            }
            return digit;
        }
    }
}
