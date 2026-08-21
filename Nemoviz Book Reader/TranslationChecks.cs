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
            string looped = RepeatedSentence(chunk.Text, translated);
            if (looped != null)
                Add(found, CheckSeverity.Suspect, "repetition",
                    "\"" + Trim(looped, 60) + "\" repeats", chunk.Index);

            // 8. FIGURES. Measured on Azure, which turned "forty pounds" into
            //    "četrdeset kilograma" — it swapped the unit word and kept the
            //    number, doubling the man. That class of error is worse for this
            //    audience than any clumsiness: a calque sounds wrong and the ear
            //    catches it, while a converted-but-false figure reads perfectly
            //    naturally and is simply untrue.
            //
            //    A NUMBER THAT MERELY CHANGED IS NOT REPORTED, because a good
            //    translation converts: forty pounds SHOULD become about twenty
            //    kilograms, and flagging that would fill the report with correct
            //    work. What is reported is a figure that VANISHED with nothing
            //    numeric put in its place, and a translation that grew figures the
            //    source never had.
            string figures = FigureTrouble(chunk.Text, translated);
            if (figures != null)
                Add(found, CheckSeverity.Note, "figures", figures, chunk.Index);

            return found;
        }

        /// <summary>Masculine and feminine first-person past forms, which is how
        /// Croatian and its neighbours carry the speaker's sex in every sentence.
        ///
        /// <para><b>This is the one check that separated the engines.</b> On a
        /// French chapter narrated by a girl, the paragraph count, the length ratio
        /// and the figures agreed across all four services and told us nothing;
        /// counting these two endings put Azure at eight masculine forms to one
        /// feminine while the language models sat at nought. It also caught the one
        /// slip a good engine made.</para>
        ///
        /// <para>Crude by design and it does not need to be otherwise: it is not
        /// parsing the sentence, only counting an ending that means one thing.</para></summary>
        public static void GenderCounts(string s, out int masculine, out int feminine)
        {
            masculine = 0; feminine = 0;
            if (string.IsNullOrEmpty(s)) return;
            var words = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in s + " ")
            {
                if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
                else { if (sb.Length > 0) words.Add(sb.ToString()); sb.Clear(); }
            }
            for (int i = 0; i < words.Count; i++)
            {
                // "sam" on one side or the other is what makes it FIRST person and
                // therefore the narrator, rather than any character being described.
                bool first = (i > 0 && words[i - 1] == "sam") || (i + 1 < words.Count && words[i + 1] == "sam");
                if (!first) continue;
                string w = words[i];
                if (w.Length < 4) continue;
                if (w.EndsWith("ao", StringComparison.Ordinal) || w.EndsWith("io", StringComparison.Ordinal)) masculine++;
                else if (w.EndsWith("la", StringComparison.Ordinal)) feminine++;
            }
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

        /// <summary>A sentence the translation repeats MORE OFTEN THAN THE SOURCE
        /// DOES — which is a model stuck in a loop. A sentence the source itself
        /// repeats is not.
        ///
        /// <para><b>It used to count only the translation, and that cost a whole
        /// passage</b> (measured 2026-08-21 on Nick Pirog's The Speed of Souls).
        /// The book has a kitten repeating his own name three times over as a
        /// refrain — "I'm a baby cat named Cheese." three times in a row — and the
        /// threshold here is three. Both engines translated it faithfully, this
        /// check rejected the result six times between them, and the piece went
        /// out in English. Nothing was wrong with the translation and nothing was
        /// wrong with either engine.</para>
        ///
        /// <para><b>The fix is the one the checks on either side of it already
        /// use.</b> The figures check deliberately does not report a number that
        /// merely CHANGED, because a good translation converts; the name check
        /// fires on a name that VANISHED, not one that merely got quieter. Same
        /// principle: compare against the source, and report only what the source
        /// does not account for. The source is already in hand — nothing new is
        /// needed to ask it.</para>
        ///
        /// <para><b>The general lesson, and this is the second time it has been
        /// paid for:</b> a check that assumes prose never does an odd thing is
        /// wrong about literature. The first time it was a length ratio calling a
        /// column of page numbers a truncation.</para></summary>
        /// <summary>Did the model get stuck repeating a line the book does not repeat?
        ///
        /// <para><b>It is answered by comparing HOW OFTEN, never by comparing the
        /// sentences themselves.</b> The first attempt at this fix looked each
        /// translated sentence up in the source and asked whether the source had it
        /// too — which cannot work across a translation and was measured failing the
        /// same afternoon it was written: the source says "I'm a baby cat named
        /// Cheese" and the translation says "Ja sam mala macka po imenu Sir", so the
        /// lookup misses every time and every book with a refrain is condemned. The
        /// text does not survive translation. The COUNT does.</para>
        ///
        /// <para>So: the most-repeated sentence in the translation against the
        /// most-repeated sentence in the source. Three against one is the model
        /// looping. Three against three is the book, and this book really does say
        /// it three times — a deliberate refrain in The Speed of Souls, which both
        /// engines rendered faithfully and this check threw away six times over,
        /// sending the whole 5 925-character passage out in English in BOTH samples
        /// of Gordan's blind test. The fault we were measuring was our own.</para>
        ///
        /// <para><b>The one case it lets through, knowingly:</b> a source that
        /// repeats sentence A three times while the model loops on a different
        /// sentence B three times. Both maxima are three, so it passes. That is the
        /// right way to be wrong here — a missed loop costs one odd-reading passage,
        /// while a false alarm costs the reader the passage in a language they do not
        /// read, which is the thing that actually happened.</para></summary>
        private static string RepeatedSentence(string source, string translated)
        {
            string worst = null;
            int most = 0;
            foreach (var kv in Sentences(translated))
                if (kv.Value > most) { most = kv.Value; worst = kv.Key; }
            if (most < 3) return null;

            int allowed = 0;
            foreach (var kv in Sentences(source)) if (kv.Value > allowed) allowed = kv.Value;
            return most > allowed ? worst : null;
        }

        /// <summary>Every sentence of 25 characters or more, and how often it
        /// occurs. Split the same way on both sides so the two counts mean the
        /// same thing.</summary>
        private static Dictionary<string, int> Sentences(string s)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return seen;
            foreach (string raw in s.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim();
                if (t.Length < 25) continue;
                int n;
                seen.TryGetValue(t, out n);
                seen[t] = n + 1;
            }
            return seen;
        }

        /// <summary>The frequent capitalised words, most frequent first, as a plain
        /// list — the candidate names the book keeps using.
        ///
        /// <para>Exposed because two jobs want the same scan: the vanished-name
        /// check that has always used it, and the glossary built before a word is
        /// translated. It reads the WHOLE text on purpose. A character introduced
        /// late is precisely the one that ends up with several different forms,
        /// because nothing earlier fixed a choice.</para></summary>
        public static List<string> FrequentNameList(string s, int max)
        {
            var names = new List<string>();
            foreach (var kv in FrequentNames(s))
            {
                if (names.Count >= max) break;
                names.Add(kv.Key);
            }
            return names;
        }
        /// <summary>Capitalised words that turn up often and are not merely
        /// sentence openers — good enough to find the people and places a book
        /// keeps naming.</summary>


        /// <summary>How much first person there is in the NARRATION — dialogue
        /// taken out — per 100 000 characters. -1 when the question cannot be
        /// asked of this language.
        ///
        /// <para><b>Why it exists.</b> The preparation step is asked to give the
        /// narrator's gender ONLY for a first-person book, and to leave the line
        /// out otherwise. It does not always obey. Measured on Robin Cook's
        /// Pandemic (2026-08-21) it answered "masculine" for a book written in the
        /// third person -- 1 first-person word per 100 000 of narration against
        /// Harvest Home's 791, and <see cref="TranslationBible.ToPrompt"/> then told the
        /// model, in all 123 passages, that the narrator speaks in the first person
        /// and is male. The model obeyed: "Jack left Toxicology and took the
        /// elevator down" came back as "napustio SAM toksikologiju i spustio SAM
        /// se dizalom" — the hero's name gone and the whole novel rewritten into a
        /// voice it was not written in. That is worse than the fault the glossary
        /// was built to cure.</para>
        ///
        /// <para><b>Dialogue has to come out first, and that is the whole trick.</b>
        /// A third-person novel is full of people saying "I"; Pandemic counts 1360
        /// of them. Outside the quotation marks it has FOUR, in 711 441 characters.
        /// Harvest Home, genuinely first-person, has 2878.</para>
        ///
        /// <para><b>English only, and that is a real limit rather than a shortcut.</b>
        /// Croatian carries the first person in the verb ending, not in a pronoun —
        /// measured, the Croatian translation of Harvest Home scores 29 where the
        /// English original scores 363, and it is the same book. So for any other
        /// source language this answers -1 and nothing is vetoed.</para></summary>
        public static int FirstPersonDensity(string s, string sourceLang)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2000) return -1;
            if (sourceLang == null || !sourceLang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return -1;

            var narration = new StringBuilder(s.Length);
            bool inQuote = false;
            char opened = '\0';
            foreach (char c in s)
            {
                if (!inQuote && (c == '\u201C' || c == '"')) { inQuote = true; opened = c; continue; }
                if (inQuote)
                {
                    // A curly quote closes with its own partner; a straight one
                    // closes with another straight one.
                    if ((opened == '\u201C' && c == '\u201D') || (opened == '"' && c == '"')) inQuote = false;
                    continue;
                }
                narration.Append(c);
            }

            string n = narration.ToString();
            if (n.Length < 1000) return -1;          // nearly all of it was dialogue

            int hits = 0;
            for (int i = 0; i < n.Length; i++)
            {
                if (n[i] != 'I' && n[i] != 'm' && n[i] != 'M') continue;
                if (i > 0 && (char.IsLetter(n[i - 1]) || n[i - 1] == '\'')) continue;
                int j = i;
                while (j < n.Length && (char.IsLetter(n[j]) || n[j] == '\'')) j++;
                string w = n.Substring(i, j - i);
                if (w == "I" || w == "I'm" || w == "I'd" || w == "I've" || w == "I'll"
                    || w == "my" || w == "My" || w == "me" || w == "Me"
                    || w == "myself" || w == "Myself" || w == "mine" || w == "Mine") hits++;
                i = j - 1;
            }
            return (int)((long)hits * 100000 / n.Length);
        }

        /// <summary>Recurring HYPHENATED lower-case compounds — the terms a book
        /// invents for itself, which <see cref="FrequentNames"/> structurally cannot
        /// see because it takes only capitalised words.
        ///
        /// <para><b>Why the hyphen and nothing else.</b> Measured on the book that
        /// exposed the gap (Gordan's blind test, 2026-08-21): its central term is
        /// "she-human", and plain frequency can never find it. "she-human" occurs 32
        /// times and "human" 27, against "back" 311 and "head" 220 — the terms of
        /// the story sit far BELOW ordinary words, so any threshold that caught them
        /// would catch half the language first. A hyphenated compound is different:
        /// it is not an ordinary word of the language, it is one the author built.
        ///
        /// <para>Measured across ten real novels the list is 0 to 24 entries, median
        /// about five — cheap enough to hand over whole. Most of it is ordinary
        /// (twenty-three, father-in-law, walkie-talkie) and the term of the story is
        /// in there with it (she-human, time-line, no-space, tri-vi). That is the
        /// right shape for a HINT: the scan finds candidates, the model decides
        /// which belong to the story. It does not, and should not, try to judge.
        /// </para></summary>
        public static List<string> FrequentTermList(string s, int max)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(s))
            {
                var sb = new StringBuilder();
                bool hyphen = false;
                for (int i = 0; i <= s.Length; i++)
                {
                    char c = i < s.Length ? s[i] : ' ';
                    if (char.IsLetter(c)) { sb.Append(c); continue; }
                    // A hyphen between two letters continues the word; anywhere else
                    // it ends it, so a dash used as punctuation cannot weld two words
                    // into a compound that was never written.
                    if (c == '-' && sb.Length > 0 && i + 1 < s.Length && char.IsLetter(s[i + 1]))
                    { sb.Append('-'); hyphen = true; continue; }

                    if (hyphen && sb.Length > 4 && char.IsLower(sb[0]))
                    {
                        string w = sb.ToString();
                        int n; counts.TryGetValue(w, out n); counts[w] = n + 1;
                    }
                    sb.Clear();
                    hyphen = false;
                }
            }

            var list = new List<KeyValuePair<string, int>>();
            // Five, the same floor the name scan uses: a compound written once is
            // the author reaching for a phrase, not a term the book runs on.
            foreach (var kv in counts) if (kv.Value >= 5) list.Add(kv);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));

            var terms = new List<string>();
            foreach (var kv in list) { if (terms.Count >= max) break; terms.Add(kv.Key); }
            return terms;
        }

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

        /// <summary>Reports a figure that disappeared, or figures that appeared out
        /// of nowhere. Deliberately blind to a figure that merely CHANGED, since a
        /// good translation converts units and reporting that would drown the one
        /// line worth reading.</summary>
        private static string FigureTrouble(string source, string translated)
        {
            List<string> a = Figures(source), b = Figures(translated);
            if (a.Count == 0 && b.Count == 0) return null;

            // A whole passage's figures gone is the loud case: a list of dates or
            // measurements dropped rather than translated.
            if (a.Count >= 3 && b.Count * 3 <= a.Count)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} figures in the source, {1} in the translation", a.Count, b.Count);

            // Figures invented. Rarer, and it means the model has written something
            // the author did not.
            if (b.Count >= 3 && a.Count * 3 <= b.Count)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} figures in the source, {1} in the translation", a.Count, b.Count);

            return null;
        }

        private static List<string> Figures(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in (s ?? "") + " ")
            {
                if (char.IsDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
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
