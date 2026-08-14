using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>How a translation ended, and what the reader is told about it.</summary>
    internal sealed class TranslationReport
    {
        public bool Ok;
        public string Text;                 // the whole translated book
        public int Chunks;
        public int FromCache;
        public int ViaFallback;
        public int RetriedOk;
        public int LeftInOriginal;          // refused by every engine we had
        public bool Cancelled;
        public string Error;
        public readonly List<TranslationIssue> Issues = new List<TranslationIssue>();
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Translating a whole book: cutting it up, sending the pieces, checking what
    /// comes back, keeping what worked, and being able to pick up where it stopped.
    ///
    /// <para><b>Everything here exists because a book is not a paragraph.</b> At
    /// 450 000 characters — Gordan's own measure of an average book, 500 pages or
    /// 250 typographic cards — a translation is a hundred-odd requests and the
    /// better part of ten minutes. Over that distance three things stop being
    /// theoretical: a request will fail, an engine will refuse a passage, and the
    /// reader will want to stop and come back.</para>
    ///
    /// <para><b>Every piece is cached the moment it arrives</b>, keyed by where it
    /// starts and what it says. A break at piece 280 of 300 must not cost the 280,
    /// and it does not: a resumed job re-cuts the same text the same way, finds the
    /// pieces already there, and asks only for what is missing. That is also why
    /// the cut has to be a pure function of the text.</para>
    ///
    /// <para><b>A refusal is not a failure.</b> The engines filter different
    /// things — one balks at explicit content, the other at politics — so a passage
    /// one declines usually goes through the other. When none will take it, the
    /// passage is left in the original, counted, and reported. A book with three
    /// untranslated paragraphs is a book; a book that stopped at chapter nine is
    /// not.</para>
    /// </summary>
    internal static class TranslationJob
    {
        /// <summary>Told to the model before every piece. The standing half; the
        /// reader's own notes for this book are appended to it.
        ///
        /// <para>Each line is here because something measurable went wrong without
        /// it — see the notes in <c>docs/Prijevod - specifikacija.txt</c>. The
        /// consistency line matters most: what a cut takes away is the thread, and
        /// the model has no other way to know that this piece is the middle of
        /// something.</para></summary>
        public static string BuildSystemPrompt(string sourceLang, string targetLang, string readerNotes)
        {
            var sb = new StringBuilder();
            sb.Append("You are a literary translator. Translate from ")
              .Append(LanguageDetector.DisplayName(sourceLang) ?? sourceLang)
              .Append(" into ")
              .Append(LanguageDetector.DisplayName(targetLang) ?? targetLang)
              .AppendLine(".");
            sb.AppendLine("You are given one piece of a longer book.");
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- Output ONLY the translation of the passage marked TRANSLATE. No notes, no commentary, no preamble, no explanation of your choices.");
            sb.AppendLine("- Keep the paragraph structure exactly: the same number of paragraphs, in the same order, separated by a blank line.");
            sb.AppendLine("- Do not summarise, do not omit, do not add.");
            sb.AppendLine("- Leave passages written in a language other than the source exactly as they are.");
            // "KEEP THEIR ORIGINAL SPELLING" WAS DOING HARM, and it was found by
            // comparing our output against a human translation of the same manual.
            // A model obeying that literally will not DECLINE a foreign name — and
            // in Croatian, where declension is ordinary, it then pads around the
            // problem: "u programu Tobi" where a translator writes "u Tobiju".
            // Keeping a name and inflecting it are different things, and the rule
            // has to say so.
            sb.AppendLine("- Do not translate or transliterate proper names, but DO inflect them as the target language's grammar requires (cases, endings). Keeping a name is not the same as leaving it unchanged in every position.");
            sb.AppendLine("- Use straight double quotes for speech throughout.");
            // The same comparison showed our version running 100 % of the source's
            // length where the human translation ran 92 %, and reading as a
            // word-for-word trace of the English: "je uređivanje teksta omogućeno
            // putem zasebnog dijaloškog okvira" against "se tekst uređuje u
            // zasebnom dijalogu". Wordiness is not a separate fault from
            // literalness; it is what literalness looks like in the target.
            sb.AppendLine("- Write natural, idiomatic prose in the target language. Do not mirror the source's sentence structure where the target would put it differently, and do not reach for a loanword when the target has its own word.");
            sb.AppendLine("- Stay consistent with the rest of the book: the same character names, the same terms, the same level of address between the same people, and the same gender for the same speaker.");
            if (!string.IsNullOrWhiteSpace(readerNotes))
            {
                sb.AppendLine();
                sb.AppendLine("Notes from the reader about this book (these take precedence):");
                sb.AppendLine(readerNotes.Trim());
            }
            return sb.ToString();
        }

        /// <summary>What is actually sent: the context, clearly marked as not to be
        /// translated, then the passage.</summary>
        private static string BuildUserMessage(TextChunk c)
        {
            if (string.IsNullOrEmpty(c.Lead)) return "TRANSLATE:\n" + c.Text;
            return "CONTEXT (the end of the previous passage, for continuity — do NOT translate or repeat it):\n"
                   + c.Lead + "\n\nTRANSLATE:\n" + c.Text;
        }

        public sealed class Options
        {
            public TranslationEngine Primary;
            public TranslationEngine Fallback;      // may be null
            public string SourceLang = "en";
            public string TargetLang = "hr";
            public string ReaderNotes;
            public string CachePath;                // null = do not cache
            public int MaxChars = TextChunker.DefaultMaxChars;
            public int MaxOutputTokens = 8000;
            /// <summary>How many times the chosen engine is asked before the
            /// fallback is tried. Three, because a refusal is a throw of the dice
            /// and most passages that can get through do so within two or three
            /// asks — see the loop that uses this.</summary>
            public int PrimaryAttempts = 3;
            /// <summary>Called after each piece: (done, total, message). Return
            /// false to stop — the pieces already done stay in the cache.</summary>
            public Func<int, int, string, bool> Progress;
        }

        public static TranslationReport Run(string bookText, Options opt)
        {
            var report = new TranslationReport();
            var started = DateTime.UtcNow;
            if (string.IsNullOrEmpty(bookText)) { report.Error = "no text"; return report; }
            if (opt == null || opt.Primary == null) { report.Error = "no engine"; return report; }

            List<TextChunk> chunks = TextChunker.Split(bookText, opt.MaxChars);
            report.Chunks = chunks.Count;

            var cache = TranslationCache.Open(opt.CachePath);
            string system = BuildSystemPrompt(opt.SourceLang, opt.TargetLang, opt.ReaderNotes);

            var outText = new StringBuilder(bookText.Length);
            foreach (TextChunk c in chunks)
            {
                string done = cache.Get(c.Start, c.Text);
                if (done != null)
                {
                    report.FromCache++;
                    outText.Append(Tidy(done));
                    if (!Report(opt, c.Index + 1, chunks.Count, "cached")) { report.Cancelled = true; break; }
                    continue;
                }

                string user = BuildUserMessage(c);

                // ASK THE SAME ENGINE AGAIN BEFORE GIVING UP ON IT, because a
                // refusal is not a verdict about the text — it is a throw of the
                // dice. Measured 2026-08-15 on seven passages a novel had been
                // refused over: sent four times each, FOUR OF THE SEVEN went
                // through at least once, and the pattern was plainly random —
                // one passed on the first two attempts and was refused on the
                // third, another passed three times and failed the fourth.
                //
                // It matters for quality, not just for coverage: a piece that
                // goes through on a second ask keeps the engine the reader chose,
                // where dropping to the fallback trades the prose away. And a
                // retry costs one request against a free allowance, where the
                // fallback costs money.
                TranslationResult r = null;
                List<TranslationIssue> issues = null;
                bool bad = true;
                for (int attempt = 0; attempt < Math.Max(1, opt.PrimaryAttempts); attempt++)
                {
                    r = Translator.Send(opt.Primary, null, system, user, opt.MaxOutputTokens, opt.SourceLang, opt.TargetLang);
                    issues = r.Ok
                        ? TranslationChecks.Chunk(c, r.Text, opt.TargetLang)
                        : new List<TranslationIssue>();
                    bad = !r.Ok || HasSuspect(issues);
                    if (!bad) { if (attempt > 0) report.RetriedOk++; break; }
                }

                // The second engine is tried for a refusal AND for a piece that
                // came back wrong, because the remedy is the same either way and
                // the two engines fail at different things.
                if (bad && opt.Fallback != null)
                {
                    TranslationResult f = Translator.Send(opt.Fallback, null, system, user, opt.MaxOutputTokens, opt.SourceLang, opt.TargetLang);
                    if (f.Ok)
                    {
                        var fi = TranslationChecks.Chunk(c, f.Text, opt.TargetLang);
                        if (!HasSuspect(fi))
                        {
                            // Why the second engine was needed is worth recording:
                            // over a real novel it took 22 pieces of 131 — 17 % —
                            // and without knowing whether that was refusals or
                            // failed checks there is no way to tell a filtering
                            // problem from a quality one.
                            report.Issues.Add(new TranslationIssue
                            {
                                Severity = CheckSeverity.Note,
                                Kind = "second engine",
                                Detail = r.Ok ? Describe(issues) : (r.Error + " " + r.Detail),
                                ChunkIndex = c.Index
                            });
                            r = f; issues = fi; bad = false; report.ViaFallback++;
                        }
                    }
                }

                if (bad)
                {
                    // Left as it was written. Counted and reported, never silently
                    // dropped — the reader has to be able to find out that these
                    // three paragraphs are in the original language.
                    report.LeftInOriginal++;
                    report.Issues.Add(new TranslationIssue
                    {
                        Severity = CheckSeverity.Suspect,
                        Kind = "left in the original",
                        Detail = r.Ok ? Describe(issues) : (r.Error + " " + r.Detail),
                        ChunkIndex = c.Index
                    });
                    outText.Append(Tidy(c.Text));
                }
                else
                {
                    foreach (var i in issues) report.Issues.Add(i);
                    cache.Put(c.Start, c.Text, r.Text);
                    outText.Append(Tidy(r.Text));
                }

                if (!Report(opt, c.Index + 1, chunks.Count, bad ? "left in the original" : "translated"))
                { report.Cancelled = true; break; }
            }

            cache.Flush();
            report.Text = outText.ToString();
            if (!report.Cancelled)
                report.Issues.AddRange(TranslationChecks.Book(bookText, report.Text));
            report.Ok = !report.Cancelled && report.Text.Length > 0;
            report.Elapsed = DateTime.UtcNow - started;
            return report;
        }

        private static bool Report(Options opt, int done, int total, string what)
        {
            if (opt.Progress == null) return true;
            try { return opt.Progress(done, total, what); }
            catch { return true; }
        }

        private static bool HasSuspect(List<TranslationIssue> issues)
        {
            foreach (var i in issues) if (i.Severity == CheckSeverity.Suspect) return true;
            return false;
        }

        private static string Describe(List<TranslationIssue> issues)
        {
            var sb = new StringBuilder();
            foreach (var i in issues)
            {
                if (i.Severity != CheckSeverity.Suspect) continue;
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(i.Kind).Append(": ").Append(i.Detail);
            }
            return sb.ToString();
        }

        /// <summary>Every piece ends with exactly one blank line, so the pieces
        /// join into a book rather than into a wall of text or a gappy one. The
        /// model's own trailing whitespace varies from piece to piece and is not
        /// worth trusting.
        ///
        /// <para><b>And the speech marks are made one style, because asking did not
        /// work.</b> The instruction says plainly to use straight double quotes
        /// throughout; measured over a whole novel the answer came back with THREE
        /// conventions mixed — 1 832 straight, 2 171 of one curly pair and 1 124 of
        /// another — which over a book means the quotes change between chapters and
        /// reads as a broken file rather than a choice.</para>
        ///
        /// <para>This is the general lesson and it is worth taking further: <b>a
        /// rule that can be enforced afterwards should be, not requested.</b> An
        /// instruction is a request the model may honour; a substitution is a fact.
        /// Only the mechanical rules qualify — nothing here could decide a gender
        /// or a level of address, which is exactly why those still have to be
        /// asked for.</para></summary>
        private static string Tidy(string s)
        {
            s = s.Replace('„', '"')     // „
                 .Replace('“', '"')     // “
                 .Replace('”', '"')     // ”
                 .Replace('«', '"')     // «
                 .Replace('»', '"');    // »
            return s.TrimEnd('\r', '\n', ' ', '\t') + Environment.NewLine + Environment.NewLine;
        }
    }

    /// <summary>
    /// The pieces already translated, so a stopped job costs nothing to resume.
    ///
    /// <para>One file beside the book, one record per piece: where it starts, a
    /// hash of the source text, and the translation. The hash is what makes it
    /// safe — if the book's text changes, the pieces no longer match and are simply
    /// re-fetched rather than silently reused against different words.</para>
    ///
    /// <para>Plain text and not an INI: this is bulk, the same reason
    /// <c>sync.map</c> is its own file. Records are separated by a marker that
    /// cannot occur in book text.</para>
    /// </summary>
    internal sealed class TranslationCache
    {
        private const string Sep = "␞";        // SYMBOL FOR RECORD SEPARATOR
        private readonly string path;
        private readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool dirty;

        private TranslationCache(string path) { this.path = path; }

        public static TranslationCache Open(string path)
        {
            var c = new TranslationCache(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return c;
            try
            {
                string all = File.ReadAllText(path, Encoding.UTF8);
                foreach (string rec in all.Split(new[] { Sep + "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int nl = rec.IndexOf('\n');
                    if (nl <= 0) continue;
                    string key = rec.Substring(0, nl).Trim();
                    if (key.Length == 0) continue;
                    c.map[key] = rec.Substring(nl + 1);
                }
            }
            catch { }
            return c;
        }

        private static string Key(int start, string source)
        {
            unchecked
            {
                // FNV-1a over the source piece. Not a security hash — it only has
                // to notice that the text under this offset is not what it was.
                ulong h = 14695981039346656037;
                foreach (char ch in source) { h ^= ch; h *= 1099511628211; }
                return start.ToString(CultureInfo.InvariantCulture) + "-" +
                       h.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        public string Get(int start, string source)
        {
            string v;
            return map.TryGetValue(Key(start, source), out v) ? v : null;
        }

        public void Put(int start, string source, string translated)
        {
            map[Key(start, source)] = translated ?? "";
            dirty = true;
            // Written as it goes, not at the end: the whole point is surviving a
            // stop, and a stop is not usually polite enough to let us flush.
            Flush();
        }

        public void Flush()
        {
            if (!dirty || string.IsNullOrEmpty(path)) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in map)
                    sb.Append(kv.Key).Append('\n').Append(kv.Value).Append(Sep).Append('\n');
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                dirty = false;
            }
            catch { }
        }
    }
}
