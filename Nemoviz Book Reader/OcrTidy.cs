using System;
using System.Collections.Generic;
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
        /// <summary>Puts back the bar on <c>đ</c>, which the recognizer drops more
        /// than any other Croatian letter.
        ///
        /// <para><b>Measured on a whole book</b> (252 pages, 2026-08-14): of the
        /// words that should carry it, roughly a third lose it. <c>đ</c> is also
        /// the rarest of the five — 160 occurrences against 2929 for <c>š</c> —
        /// which is why it goes unnoticed until someone hits "roden".</para>
        ///
        /// <para><b>The rule is the document's own evidence, not a word list I
        /// wrote.</b> A word is only corrected when the corrected spelling ALREADY
        /// APPEARS IN THE SAME BOOK, more often than the damaged one. That is what
        /// makes it safe, and the safety is not theoretical — the same book
        /// contains <c>tvrdi</c> 46 times against <c>tvrđi</c> once, and
        /// <c>medu</c> 30 times against <c>među</c> twice. A list of stems written
        /// out of my own head would have corrupted all 76 of those; the majority
        /// rule leaves them alone because the evidence points the other way.</para>
        ///
        /// <para><b>What it costs: it only catches half.</b> Where the damaged
        /// spelling outnumbers the good one — <c>dode</c> 10 against <c>dođe</c>
        /// once — the rule declines, and rightly, because from inside the document
        /// that looks exactly like the <c>tvrdi</c> case. Those need
        /// <see cref="AlwaysWrong"/>, which is a short list of forms that are not
        /// Croatian words in any spelling.</para>
        ///
        /// <para><b>Two entries I nearly put in that list and must not.</b>
        /// <c>svidjeti se</c> is spelt with <c>dj</c> and <c>svadba</c> has no bar
        /// at all — measured, 13 of 17 <c>svid…</c> hits and 1 of 16 <c>svad…</c>
        /// hits were ordinary words. My first count called all of them damage,
        /// which is where the inflated "40 %" came from.</para></summary>
        /// <summary>Whether <see cref="FixCroatianDiacritics"/> applies to a
        /// language.
        ///
        /// <para><b>Croatian, Bosnian, Serbian and Montenegrin, and no others</b>
        /// (Gordan, 2026-08-14). Those four share the orthography AND the word
        /// forms, which is what matters here: the list of damaged spellings is
        /// made of <i>između</i>, <i>također</i>, <i>događaj</i>, <i>rođen</i>,
        /// and those are the same words in all four.</para>
        ///
        /// <para><b>Slovenian and Macedonian are excluded although Slovenian has
        /// the letter</b>, and that is his call rather than an oversight: a shared
        /// letter is not a shared vocabulary, and every rule here is about
        /// particular WORDS. A Slovenian book would be judged against a list that
        /// has nothing to do with it — the majority rule would mostly decline, but
        /// the always-wrong list would not, and it has no business firing on a
        /// language nobody checked it against.</para>
        ///
        /// <para>Serbian in CYRILLIC costs nothing to allow: the rules look for a
        /// Latin <c>d</c> beside a Latin <c>đ</c>, and Cyrillic text contains
        /// neither, so it is a no-op rather than a risk.</para></summary>
        public static bool SharesCroatianSpelling(string languageTag)
        {
            string t = (languageTag ?? "").ToLowerInvariant();
            return t.StartsWith("hr", StringComparison.Ordinal)    // Croatian
                || t.StartsWith("bs", StringComparison.Ordinal)    // Bosnian
                || t.StartsWith("sr", StringComparison.Ordinal)    // Serbian
                || t.StartsWith("cnr", StringComparison.Ordinal)   // Montenegrin (ISO 639-3)
                || t.StartsWith("me", StringComparison.Ordinal);   // …and the tag Windows would likelier use
        }

        public static string FixCroatianDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('d') < 0) return text;

            // What this document itself says is right.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string w in Words(text))
            {
                int n; seen.TryGetValue(w, out n); seen[w] = n + 1;
            }

            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (!IsWordChar(text[i])) { sb.Append(text[i++]); continue; }
                int start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                sb.Append(Fix(text.Substring(start, i - start), seen));
            }
            return sb.ToString();
        }

        /// <summary>Damaged spellings that are not Croatian words under any
        /// reading, so they can be corrected without asking the document.
        ///
        /// <para>Every one was taken from the measured book, not from memory, and
        /// each is a PREFIX so its inflections follow. Anything whose plain-d form
        /// is also a word stays out — that is the whole test.</para></summary>
        private static readonly (string Bad, string Good)[] AlwaysWrong =
        {
            ("izmed",    "izmeđ"),      // izmedu
            ("takoder",  "također"),
            ("dogad",    "događ"),      // dogada, dogadalo, dogadaja
            ("medut",    "međut"),      // medutim
            ("medus",    "međus"),      // medusobno
            ("meduvre",  "međuvre"),    // meduvremenu
            ("roden",    "rođen"),      // roden, rodeni, rodenja, rodendan
            ("izad",     "izađ"),       // izade, izadeš, izadu
            ("dode",     "dođe"),
            ("dodu",     "dođu"),
            ("prode",    "prođe"),
            ("gradevin", "građevin"),
            ("potvrduj", "potvrđuj"),
            ("zaradiv",  "zarađiv"),
            ("izvodač",  "izvođač"),
            ("palamud",  "palamuđ"),
        };

        /// <summary>Forms that LOOK like the ones above but are ordinary words.
        /// Checked first, so a prefix rule cannot reach them.</summary>
        private static readonly string[] NeverTouch =
        {
            "svadb",      // svadba — no bar in it at all
            "svidj",      // svidjeti, svidjela — spelt with dj by the language
            "svidi",      // svidio
            "tvrd",       // tvrdi: 46 in one book, against one tvrđi
            "medu",       // dative of med; only "medut…/medus…/meduvre…" are safe
            "tuda",       // an adverb, not tuđa
            "doduš",      // doduše — an adverb; the "dodu" rule below ate it, and
                          // the eyeball pass over every change is what caught it.
        };

        private static string Fix(string word, Dictionary<string, int> seen)
        {
            if (word.IndexOf('d') < 0) return word;
            string lower = word.ToLowerInvariant();

            foreach (string safe in NeverTouch)
                if (lower.StartsWith(safe, StringComparison.Ordinal)) return word;

            foreach (var rule in AlwaysWrong)
                if (lower.StartsWith(rule.Bad, StringComparison.Ordinal))
                    return Splice(word, rule.Bad.Length, rule.Good);

            // Otherwise ask the document: is the barred spelling here, and more
            // common than this one?
            int at = lower.IndexOf('d');
            while (at >= 0)
            {
                string candidate = word.Substring(0, at) + "đ" + word.Substring(at + 1);
                int mine, theirs;
                seen.TryGetValue(word, out mine);
                if (seen.TryGetValue(candidate, out theirs) && theirs > mine) return candidate;
                at = lower.IndexOf('d', at + 1);
            }
            return word;
        }

        /// <summary>Replaces the first <paramref name="n"/> characters, keeping
        /// the case of the first letter — a sentence can start with one.</summary>
        private static string Splice(string word, int n, string good)
        {
            string tail = word.Substring(n);
            if (word.Length > 0 && char.IsUpper(word[0]) && good.Length > 0)
                good = char.ToUpperInvariant(good[0]) + good.Substring(1);
            return good + tail;
        }

        private static bool IsWordChar(char c) { return char.IsLetter(c) || c == '-'; }

        private static IEnumerable<string> Words(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                if (!IsWordChar(text[i])) { i++; continue; }
                int start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                yield return text.Substring(start, i - start);
            }
        }

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
