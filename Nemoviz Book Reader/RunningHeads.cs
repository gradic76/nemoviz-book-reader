using System;
using System.Collections.Generic;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Removes the line a producer prints at the top or the bottom of
    /// every page — the running title, the page number, the volume note.
    ///
    /// <para><b>Why it must be general.</b> Gordan, 2026-08-04: a rule per book
    /// is no rule. The signal is not the words, which differ in every book and
    /// every language; it is the REPETITION. A line that appears in the same
    /// place on most pages of a book is furniture, not text, whatever it
    /// says.</para>
    ///
    /// <para><b>What it sounds like without this.</b> A paragraph that runs over
    /// a page break comes back with the furniture spliced into it — "You can hear
    /// conversations from the top <i>1 we all live here</i> floor as the words
    /// float upwards" — 111 times in one measured book. It reads as words thrown
    /// in at random, and no amount of tidying elsewhere can find them, because on
    /// their own they are perfectly ordinary words.</para>
    ///
    /// <para><b>Two candidates per end, not one.</b> Books printed on both sides
    /// often alternate: the author on the left-hand page, the title on the right.
    /// Taking only the commonest line would leave every other page's furniture in
    /// place, which is worse than leaving all of it — the fault would look
    /// intermittent and nobody would find it.</para>
    ///
    /// <para><b>Numbers are normalised away before counting.</b> "1 we all live
    /// here" and "2 we all live here" are the same furniture; comparing them
    /// literally would find nothing to repeat. Every run of digits becomes a
    /// single marker, so the page number varies without hiding the pattern.</para>
    /// </summary>
    internal static class RunningHeads
    {
        /// <summary>A line has to recur on this share of the pages before it is
        /// treated as furniture. High on purpose: a line of a book's own prose
        /// repeating on three pages in five does not happen, and the cost of
        /// being wrong here is a deleted sentence.</summary>
        private const double Share = 0.60;

        /// <summary>Below this many pages there is no pattern to see, only
        /// coincidence. A three-page document whose pages happen to start alike
        /// would lose its first lines.</summary>
        private const int MinPages = 5;

        /// <summary>Strips running heads and feet from a book already split into
        /// pages of lines. Edits the lists in place; a page that is left empty is
        /// left empty, because a blank page is still a page and the page marks
        /// count on it.</summary>
        public static void Strip(List<List<string>> pages)
        {
            if (pages == null || pages.Count < MinPages) return;
            // PREFIX FIRST, and the order is not a preference. The whole-line pass
            // removes the heads that stand alone, and once it has, the pages no
            // longer agree about how they begin — so the prefix pass, which needs
            // that agreement on 60% of them, finds nothing and the heads sharing a
            // line with the text survive. Measured: run second it changed no book
            // at all; run first it clears them.
            //
            // They do not do each other's work. A head whose page number comes
            // FIRST ("1 we all live here") has no common prefix to find, and one
            // written with braille letters for digits ("…pblea", "…pbleb") has
            // nothing a digit rule can normalise. Each pass catches what the other
            // structurally cannot.
            StripPrefix(pages, true);
            StripPrefix(pages, false);
            StripEnd(pages, true);
            StripEnd(pages, false);
        }

        private static void StripEnd(List<List<string>> pages, bool top)
        {
            // What stands at this end of each page, and where.
            var form = new string[pages.Count];
            var at = new int[pages.Count];
            for (int p = 0; p < pages.Count; p++)
            {
                at[p] = -1;
                List<string> page = pages[p];
                if (page == null) continue;
                for (int k = 0; k < page.Count; k++)
                {
                    int i = top ? k : page.Count - 1 - k;
                    if (string.IsNullOrWhiteSpace(page[i])) continue;
                    at[p] = i;
                    form[p] = Normalise(page[i]);
                    break;
                }
            }

            var count = new Dictionary<string, int>();
            int seen = 0;
            for (int p = 0; p < pages.Count; p++)
            {
                if (at[p] < 0 || form[p].Length == 0) continue;
                seen++;
                count[form[p]] = count.TryGetValue(form[p], out int n) ? n + 1 : 1;
            }
            if (seen < MinPages) return;

            // The two commonest, for books that alternate left and right pages.
            string first = null, second = null;
            int firstN = 0, secondN = 0;
            foreach (var kv in count)
            {
                if (kv.Value > firstN) { second = first; secondN = firstN; first = kv.Key; firstN = kv.Value; }
                else if (kv.Value > secondN) { second = kv.Key; secondN = kv.Value; }
            }
            if (first == null) return;

            var furniture = new HashSet<string>();
            if (firstN >= seen * Share) furniture.Add(first);
            else if (second != null && firstN + secondN >= seen * Share)
            { furniture.Add(first); furniture.Add(second); }
            else return;

            for (int p = 0; p < pages.Count; p++)
                if (at[p] >= 0 && furniture.Contains(form[p]))
                    pages[p].RemoveAt(at[p]);
        }

        /// <summary>The second shape furniture comes in: a head that SHARES its
        /// line with the text, and whose page number is not a number.
        ///
        /// <para>Measured on <i>Daily Gospel Devotional</i>, which survived the
        /// whole-line pass with 23 of its heads intact:</para>
        /// <code>
        /// Daily Gospel Devotional pblea
        /// Daily Gospel Devotional pbleb Jan. 15 Matthew 2:according-ah
        /// Daily Gospel Devotional pblec Feb. 4 John 3:bc-cf
        /// </code>
        /// <para>Two things defeat the first pass at once. The page number is
        /// written the braille way — the letters a to j standing for 1 to 0 — so
        /// normalising DIGITS finds nothing varying to normalise. And the head is
        /// followed on the same line by the day's reading, so removing the line
        /// would take a sentence with it.</para>
        ///
        /// <para>So this matches the common PREFIX instead of the whole line, and
        /// removes only that. It then eats the token after it, which is where the
        /// page number sits, and the space following. Everything else on the line
        /// stays.</para>
        ///
        /// <para>Guarded the same way and for the same reason: at least 12
        /// characters, ending on a word boundary, on 60% of the pages. A short
        /// prefix would match the ordinary way sentences begin.</para></summary>
        private static void StripPrefix(List<List<string>> pages, bool top)
        {
            var line = new string[pages.Count];
            var at = new int[pages.Count];
            var have = new List<string>();
            for (int p = 0; p < pages.Count; p++)
            {
                at[p] = -1;
                List<string> page = pages[p];
                if (page == null) continue;
                for (int k = 0; k < page.Count; k++)
                {
                    int i = top ? k : page.Count - 1 - k;
                    if (string.IsNullOrWhiteSpace(page[i])) continue;
                    at[p] = i; line[p] = page[i].TrimStart(); have.Add(line[p]);
                    break;
                }
            }
            if (have.Count < MinPages) return;

            // The longest prefix that at least the required share of pages agree
            // on. Grown one character at a time rather than guessed: the answer is
            // whatever the book itself repeats.
            int need = (int)Math.Ceiling(have.Count * Share);
            string best = "";
            for (int len = 12; len <= 60; len++)
            {
                var count = new Dictionary<string, int>();
                foreach (string s in have)
                {
                    if (s.Length < len) continue;
                    string key = s.Substring(0, len).ToLowerInvariant();
                    count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
                }
                string top1 = null; int top1N = 0;
                foreach (var kv in count) if (kv.Value > top1N) { top1 = kv.Key; top1N = kv.Value; }
                if (top1 == null || top1N < need) break;
                best = top1;
            }
            if (best.Length < 12) return;
            // Only on a word boundary — half a word is not a running head.
            int cut = best.Length;
            while (cut > 0 && !char.IsWhiteSpace(best[cut - 1])) cut--;
            if (cut < 12) return;

            for (int p = 0; p < pages.Count; p++)
            {
                if (at[p] < 0 || line[p].Length < cut) continue;
                if (!line[p].Substring(0, cut).ToLowerInvariant().StartsWith(best.Substring(0, cut))) continue;
                int i = cut;
                while (i < line[p].Length && !char.IsWhiteSpace(line[p][i])) i++;   // the page number
                while (i < line[p].Length && char.IsWhiteSpace(line[p][i])) i++;
                string rest = line[p].Substring(i);
                if (rest.Trim().Length == 0) pages[p].RemoveAt(at[p]);
                else pages[p][at[p]] = rest;
            }
        }

        /// <summary>The shape of a line, with the parts that vary from page to
        /// page taken out: digits become one marker and runs of space become one
        /// space. What is left is what the producer prints unchanged.</summary>
        private static string Normalise(string line)
        {
            var sb = new StringBuilder(line.Length);
            bool inDigits = false, lastSpace = true;
            foreach (char c in line)
            {
                if (char.IsDigit(c))
                {
                    if (!inDigits) { sb.Append('#'); inDigits = true; }
                    lastSpace = false;
                    continue;
                }
                inDigits = false;
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
                lastSpace = false;
            }
            return sb.ToString().Trim();
        }
    }
}
