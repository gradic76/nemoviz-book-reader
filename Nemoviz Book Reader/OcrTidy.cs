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

        /// <summary>Removes the printed page number from the start of a page.
        ///
        /// <para><b>Only when it is proved to BE one.</b> The scanned book
        /// measured here puts it there — every page begins "7 PROLOG…",
        /// "9 Ali, dobro…", "11 Dobro, super…" — and read aloud that is a bare
        /// number at the head of every page. But a page can also legitimately
        /// begin with a number, and guessing costs the reader a word of the book.
        ///
        /// <para>So it is not guessed. The numbers are collected across the whole
        /// book first, and a leading number is only removed where
        /// <c>number − pageIndex</c> is the SAME on most pages — which is what a
        /// page number is and what a sentence beginning with a year is not. The
        /// offset is whatever it is, because scans start numbering after the front
        /// matter.</para></summary>
        /// <param name="pageTexts">One entry per page, in order. Modified in place.</param>
        /// <returns>How many page numbers were removed.</returns>
        public static int StripPageNumbers(System.Collections.Generic.IList<string> pageTexts)
        {
            if (pageTexts == null || pageTexts.Count < 5) return 0;

            var offsets = new System.Collections.Generic.Dictionary<int, int>();
            var leading = new int[pageTexts.Count];
            for (int i = 0; i < pageTexts.Count; i++)
            {
                leading[i] = LeadingNumber(pageTexts[i]);
                if (leading[i] < 0) continue;
                int off = leading[i] - i;
                int n; offsets.TryGetValue(off, out n); offsets[off] = n + 1;
            }
            if (offsets.Count == 0) return 0;

            int best = 0, bestCount = 0;
            foreach (var kv in offsets)
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }

            // A run of pages agreeing on the same offset is a page number. A
            // handful is a coincidence, and a coincidence is not worth cutting
            // words out of a book for.
            int pagesWithText = 0;
            foreach (string t in pageTexts) if (!string.IsNullOrWhiteSpace(t)) pagesWithText++;
            if (bestCount < Math.Max(4, pagesWithText / 3)) return 0;

            int removed = 0;
            for (int i = 0; i < pageTexts.Count; i++)
            {
                if (leading[i] < 0 || leading[i] - i != best) continue;
                string t = pageTexts[i];
                int k = 0;
                while (k < t.Length && char.IsDigit(t[k])) k++;
                while (k < t.Length && (t[k] == ' ' || t[k] == '\r' || t[k] == '\n')) k++;
                pageTexts[i] = t.Substring(k);
                removed++;
            }
            return removed;
        }

        /// <summary>The number a page opens with, or −1. A number followed by a
        /// full stop is a date or an ordinal and never a page number.</summary>
        private static int LeadingNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            int len = i - start;
            if (len < 1 || len > 4) return -1;
            if (i < text.Length && text[i] == '.') return -1;
            if (i >= text.Length || !char.IsWhiteSpace(text[i])) return -1;
            int value;
            return int.TryParse(text.Substring(start, len), out value) ? value : -1;
        }
    }
}
