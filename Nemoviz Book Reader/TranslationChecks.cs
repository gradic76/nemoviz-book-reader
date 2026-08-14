using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nemoviz_Book_Reader
{
    internal enum CheckSeverity
    {
        /// <summary>Worth telling the reader about at the end.</summary>
        Note,
        /// <summary>The piece should be sent again; if it fails twice, say so.</summary>
        Suspect
    }

    internal sealed class TranslationIssue
    {
        public CheckSeverity Severity;
        public string Kind;          // short, stable, for the report
        public string Detail;
        public int ChunkIndex = -1;

        public override string ToString()
        {
            return (ChunkIndex >= 0 ? "chunk " + ChunkIndex + ": " : "") + Kind + " — " + Detail;
        }
    }

    /// <summary>
    /// What NBR checks for itself after a piece comes back, before anyone hears it.
    ///
    /// <para><b>Why this layer exists at all, and it is the sharpest thing the
    /// literature said:</b> a language model's mistakes are FLUENT. Somebody
    /// reading a page stumbles over a wrong sentence and looks again; somebody
    /// LISTENING has nothing to stumble over — a confidently wrong sentence sounds
    /// exactly like a right one. So for this audience every check that can be
    /// automated is worth more than it would be in an ordinary translation tool,
    /// and the ones below are the cheap ones: all local, all arithmetic, none of
    /// them asking a second model to judge the first.</para>
    ///
    /// <para>Two of them were not theory. Told plainly to keep the paragraph
    /// structure "exactly, line for line", one model merged twelve paragraphs and
    /// another split five, on the very first real attempt; and the quotation marks
    /// changed style between pieces of the same short text. Both are here.</para>
    /// </summary>
    internal static class TranslationChecks
    {
        // Croatian came back at 0.93-0.95 of the English source, measured on the
        // first real passage through three engines. The gate is set far wider than
        // that spread, because it is looking for a piece that was TRUNCATED or that
        // arrived with an essay attached, not for a stylistic difference.
        private const double MinRatio = 0.55;
        private const double MaxRatio = 1.60;

        /// <summary>Checks one translated piece against its source.</summary>
        public static List<TranslationIssue> Chunk(TextChunk chunk, string translated, string targetLang)
        {
            var found = new List<TranslationIssue>();
            if (chunk == null) return found;

            if (string.IsNullOrWhiteSpace(translated))
            {
                Add(found, CheckSeverity.Suspect, "empty", "the piece came back with nothing in it", chunk.Index);
                return found;
            }

            // 1. Paragraphs. The commonest heavy fault in the field's own tally is
            //    omission, and a lost paragraph is the loudest kind.
            int want = CountParagraphs(chunk.Text);
            int got = CountParagraphs(translated);
            if (got != want)
            {
                // A MERGED PARAGRAPH IS NOT A REASON TO THROW THE PASSAGE AWAY.
                // Measured on the first real run: both engines came back one
                // paragraph short on every piece, so treating that as fatal left
                // whole chapters in English — which is far worse for the reader
                // than a joined paragraph. And the translated book gets its own
                // offsets at import, so nothing downstream breaks.
                //
                // Only a real loss is serious: several paragraphs gone, or a
                // quarter of them, which is a passage that was skipped rather than
                // a blank line that went missing.
                int lost = want - got;
                bool serious = lost >= 3 || (want >= 8 && lost * 4 >= want);
                Add(found, serious ? CheckSeverity.Suspect : CheckSeverity.Note, "paragraphs",
                    string.Format(CultureInfo.InvariantCulture, "{0} in, {1} back", want, got), chunk.Index);
            }

            // 2. Length. Catches a piece cut short, and one that arrived with
            //    commentary the model was asked not to add.
            double ratio = translated.Length / (double)Math.Max(1, chunk.Text.Length);
            if (ratio < MinRatio || ratio > MaxRatio)
                Add(found, CheckSeverity.Suspect, "length",
                    string.Format(CultureInfo.InvariantCulture, "{0:P0} of the source", ratio), chunk.Index);

            // 3. Not translated at all. A known failure of these models: the source
            //    is echoed back, fluently and completely.
            if (Similarity(chunk.Text, translated) > 0.85)
                Add(found, CheckSeverity.Suspect, "untranslated",
                    "the text came back essentially unchanged", chunk.Index);

            // 4. The wrong language, which is the other known one — and it costs us
            //    no new code, since the detector has been measured over ~85 real
            //    books and lands on the right voice every time it is sure.
            if (!string.IsNullOrEmpty(targetLang) && translated.Length >= 200)
            {
                LanguageDetector.Result r = LanguageDetector.Detect(translated);
                if (r.Known && !LanguageDetector.SameLanguage(r.Code, targetLang))
                    Add(found, CheckSeverity.Suspect, "language",
                        "came back as " + LanguageDetector.DisplayName(r.Code), chunk.Index);
            }

            // 5. A model that has fallen into a loop repeats one sentence over and
            //    over. Rare, and unmistakable when it happens.
            string looped = RepeatedSentence(translated);
            if (looped != null)
                Add(found, CheckSeverity.Suspect, "repetition",
                    "\"" + Trim(looped, 60) + "\" repeats", chunk.Index);

            return found;
        }

        /// <summary>Checks that hold only across the WHOLE book — the ones a single
        /// piece cannot see, and exactly the ones chunking puts at risk.</summary>
        public static List<TranslationIssue> Book(string source, string translated)
        {
            var found = new List<TranslationIssue>();
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated)) return found;

            // 6. Names, the field's "lexical consistency" measure in its cheapest
            //    form. Croatian declines foreign names — Blake, Blakea, Blakeu — so
            //    a whole-word match would report every book as broken. The stem is
            //    what survives declension.
            foreach (var name in FrequentNames(source))
            {
                string stem = name.Key.Length <= 5 ? name.Key : name.Key.Substring(0, 5);
                int inTarget = CountOccurrences(translated, stem);

                // FIRES ON A NAME THAT VANISHED, NOT ON ONE THAT MERELY GOT
                // QUIETER — and the difference was measured on a real novel.
                // A title IS supposed to be translated: Widow went 342 -> 68 and
                // Mrs 224 -> 57 because they became udovica and gđa, which is
                // correct work, not a fault. A changed NAME goes to nothing:
                // Blake rendered as Blejk leaves no "Blake" anywhere. Reporting
                // the middle ground fills the report with correct translations
                // and buries the one line that matters.
                if (inTarget == 0)
                    Add(found, CheckSeverity.Note, "name",
                        "\"" + name.Key + "\" appears " + name.Value + " times in the original and not at all in the translation");
                else if (inTarget * 8 < name.Value)
                    Add(found, CheckSeverity.Note, "name",
                        "\"" + name.Key + "\": " + name.Value + " in the original, only " + inTarget + " in the translation");
            }

            // 7. Quotation marks. Measured changing style between pieces of one
            //    short text; over a book that is a different quote in every other
            //    chapter, which looks like a broken file rather than a choice.
            var styles = QuoteStyles(translated);
            if (styles.Count > 1)
            {
                var sb = new StringBuilder();
                foreach (var s in styles) { if (sb.Length > 0) sb.Append(", "); sb.Append(s.Key).Append(" x").Append(s.Value); }
                Add(found, CheckSeverity.Note, "quotes", "more than one style used: " + sb);
            }

            return found;
        }

        // ---- the small machinery ----------------------------------------------

        private static void Add(List<TranslationIssue> list, CheckSeverity sev, string kind, string detail, int chunk = -1)
        {
            list.Add(new TranslationIssue { Severity = sev, Kind = kind, Detail = detail, ChunkIndex = chunk });
        }

        public static int CountParagraphs(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            bool inPara = false;
            foreach (string line in s.Split('\n'))
            {
                bool blank = line.Trim().Length == 0;
                if (!blank && !inPara) { n++; inPara = true; }
                else if (blank) inPara = false;
            }
            return n;
        }

        /// <summary>How much of the shorter text appears verbatim in the longer, by
        /// word. Deliberately crude: it only has to tell "this is the same text"
        /// from "this is a translation of it", and those are far apart.</summary>
        private static double Similarity(string a, string b)
        {
            var wa = Words(a);
            if (wa.Count == 0) return 0;
            var wb = new HashSet<string>(Words(b), StringComparer.OrdinalIgnoreCase);
            int hit = 0;
            foreach (string w in wa) if (wb.Contains(w)) hit++;
            return hit / (double)wa.Count;
        }

        private static List<string> Words(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetter(c)) sb.Append(c);
                else if (sb.Length > 0) { if (sb.Length > 3) list.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 3) list.Add(sb.ToString());
            return list;
        }

        private static string RepeatedSentence(string s)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string raw in s.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim();
                if (t.Length < 25) continue;
                int n;
                seen.TryGetValue(t, out n);
                seen[t] = n + 1;
                if (n + 1 >= 3) return t;
            }
            return null;
        }

        /// <summary>Capitalised words that turn up often and are not merely
        /// sentence openers — good enough to find the people and places a book
        /// keeps naming.</summary>
        private static List<KeyValuePair<string, int>> FrequentNames(string s)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var starts = new Dictionary<string, int>(StringComparer.Ordinal);
            bool atStart = true;
            var sb = new StringBuilder();
            for (int i = 0; i <= s.Length; i++)
            {
                char c = i < s.Length ? s[i] : ' ';
                if (char.IsLetter(c)) { sb.Append(c); continue; }
                if (sb.Length > 2 && char.IsUpper(sb[0]))
                {
                    string w = sb.ToString();
                    int n; counts.TryGetValue(w, out n); counts[w] = n + 1;
                    if (atStart) { starts.TryGetValue(w, out n); starts[w] = n + 1; }
                }
                if (sb.Length > 0) { atStart = false; sb.Clear(); }
                // The colon and semicolon start a capital as readily as a full
                // stop does, and leaving them out is part of how "She" collected
                // nineteen "mid-sentence" appearances it had not earned.
                if (c == '.' || c == '!' || c == '?' || c == '\n' || c == ':' || c == ';') atStart = true;
            }

            var list = new List<KeyValuePair<string, int>>();
            foreach (var kv in counts)
            {
                if (kv.Value < 5) continue;
                int st; starts.TryGetValue(kv.Key, out st);
                int mid = kv.Value - st;

                // A NAME IS USED IN THE MIDDLE OF SENTENCES, AND OFTEN ENOUGH TO
                // MEAN IT. Calibrated against a real 750 000-character novel by
                // trying four rules and reading what each kept:
                //   "a name is mid-sentence at least once or twice"  -> kept She,
                //      The, You, And, When, What. "She" alone is mid-sentence 19
                //      times in a book, which is enough to slip through any rule
                //      that only counts a couple.
                //   mid >= 5 AND at least a fifth of its uses -> kept exactly the
                //      people and places: Beth, Kate, Justin, Worthy, Penrose,
                //      Sophie, Tamar... and not one function word.
                //
                // Two earlier attempts were both wrong, in opposite directions:
                // requiring a name to be mid-sentence MORE often than not threw
                // away every character (a novel's commonest sentence opens with a
                // name), and allowing one or two let the pronouns back in.
                if (mid < 5 || mid * 5 < kv.Value) continue;
                list.Add(kv);
            }
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            if (list.Count > 12) list.RemoveRange(12, list.Count - 12);
            return list;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static Dictionary<string, int> QuoteStyles(string s)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            Bump(d, "\"", CountChar(s, '"'));
            Bump(d, "„“", CountChar(s, '„'));   // „ “
            Bump(d, "«»", CountChar(s, '«'));   // « »
            Bump(d, "“”", CountChar(s, '”'));   // “ ”
            // A style used once or twice is a quotation inside a quotation, not a
            // second convention; only a real second style is worth reporting.
            var big = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in d) if (kv.Value >= 6) big[kv.Key] = kv.Value;
            return big;
        }

        private static void Bump(Dictionary<string, int> d, string k, int n) { if (n > 0) d[k] = n; }

        private static int CountChar(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }

        private static string Trim(string s, int n)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}
