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
            // THIS RULE HAS BEEN WRONG TWICE, IN OPPOSITE DIRECTIONS, and both were
            // found by measurement rather than by reading it.
            //
            // First it said "keep their original spelling", which a model obeys
            // literally and so will not DECLINE a name — in Croatian it then pads
            // around the problem: "u programu Tobi" where a translator writes "u
            // Tobiju". Keeping a name and inflecting it are different things.
            //
            // Then it said "do not transliterate", which is a LATIN-SCRIPT
            // ASSUMPTION and wrong for a good third of the 138 languages on offer.
            // Measured 2026-08-15 on one passage carrying four names: into Russian,
            // Greek and Japanese it left all four standing in Latin, and into
            // SERBIAN CYRILLIC it produced a translation with not one Cyrillic
            // character in it — the rule did not merely spare the names, it dragged
            // the whole passage out of the script that was asked for. Gordan's
            // question is what exposed it: we cannot write guidance for languages
            // we do not know, and this line was doing exactly that.
            //
            // What it wants to say is the convention plus the consistency, and to
            // let each language answer for its own script. Re-measured: Croatian
            // still gives "u Tobiju" and still keeps the names in Latin, Russian
            // now transliterates and comes back in Cyrillic.
            sb.AppendLine("- Render proper names the way the target language normally does, transliterating them into its own script where that is its convention, and inflect them as its grammar requires (cases, endings). Be consistent throughout the book.");
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
            /// <summary>The stops, in the order they are tried. Built by
            /// <see cref="TranslationEngines.Chain"/> from the one engine the reader
            /// chose — they pick a translation they would rather read, not a retry
            /// policy.</summary>
            public List<TranslationEngine> Chain = new List<TranslationEngine>();
            public string SourceLang = "en";
            public string TargetLang = "hr";
            public string ReaderNotes;
            public string CachePath;                // null = do not cache
            public int MaxChars = TextChunker.DefaultMaxChars;
            public int MaxOutputTokens = 8000;
            /// <summary>Whether the book has headings of its own, which decides
            /// whether its printed table of contents is kept — see
            /// <see cref="BookMatter.Find"/>.</summary>
            public bool HasHeadings;

            /// <summary>Where each chapter begins, as offsets into the book text
            /// handed to <see cref="Run"/>. Used only to keep a chapter from
            /// starting three sentences before the end of a piece — see
            /// <see cref="TextChunker.Split(string,int,IList{int})"/>. Null or
            /// empty simply means the cutting knows nothing about chapters.</summary>
            public IList<int> ChapterStarts;
            /// <summary>Where to write a line per piece. Null writes none.
            ///
            /// <para><b>What a summary cannot tell you afterwards</b>: which stop
            /// took each piece, how many asks it cost and how long it waited. Over a
            /// book that refuses a sixth of its passages those three are the whole
            /// story, and the counters at the end flatten them into one number.</para></summary>
            public string LogPath;
            /// <summary>Called after each piece: (done, total, message). Return
            /// false to stop — the pieces already done stay in the cache.</summary>
            public Func<int, int, string, bool> Progress;

            public TranslationEngine First { get { return Chain.Count > 0 ? Chain[0] : null; } }
        }

        /// <summary>One piece as it came out, and by whom.</summary>
        private sealed class Piece
        {
            public string Text;
            public bool LastResort;
            /// <summary>Which stop took it, how many asks it cost, and how long —
            /// the three things a summary cannot tell you afterwards.</summary>
            public string Engine;
            public int Asks;
            public long Ms;
        }

        public static TranslationReport Run(string bookText, Options opt)
        {
            var report = new TranslationReport();
            var started = DateTime.UtcNow;
            if (string.IsNullOrEmpty(bookText)) { report.Error = "no text"; return report; }
            if (opt == null || opt.First == null) { report.Error = "no engine"; return report; }

            // WHAT IS NOT THE BOOK DOES NOT GO TO THE TRANSLATOR. The cover, the
            // imprint and the printed contents list are kept as they were written —
            // and keeping the original is the right answer for them in the same way
            // it is for a title: a machine-invented Croatian name for a book exists
            // nowhere but in this file, and an official edition may later choose
            // something else entirely.
            BookMatter.Split parts = BookMatter.Divide(bookText, opt.HasHeadings);
            if (parts.Note != null)
                report.Issues.Add(new TranslationIssue
                {
                    Severity = CheckSeverity.Note,
                    Kind = "front and back matter",
                    Detail = parts.Note
                });

            // Chapter starts are offsets into the WHOLE book; the cutting happens
            // on the body, which begins further in. Shifting them here rather
            // than making the chunker aware of matter keeps the two jobs apart —
            // and an offset that lands outside the body (a heading inside the
            // front matter) is dropped rather than clamped, since clamping would
            // invent a chapter boundary at the first paragraph.
            List<int> bodyChapters = null;
            if (opt.ChapterStarts != null && opt.ChapterStarts.Count > 0)
            {
                bodyChapters = new List<int>();
                foreach (int off in opt.ChapterStarts)
                {
                    int at = off - parts.BodyStart;
                    if (at > 0 && at < parts.Body.Length) bodyChapters.Add(at);
                }
            }

            List<TextChunk> chunks = TextChunker.Split(parts.Body, opt.MaxChars, bodyChapters);
            report.Chunks = chunks.Count;

            StartLog(opt, bookText.Length, parts, chunks.Count);
            var chainState = new ChainState(opt);

            var cache = TranslationCache.Open(opt.CachePath);
            string system = BuildSystemPrompt(opt.SourceLang, opt.TargetLang, opt.ReaderNotes);

            var pieces = new List<Piece>(chunks.Count);
            foreach (TextChunk c in chunks)
            {
                string done = cache.Get(c.Start, c.Text);
                if (done != null)
                {
                    report.FromCache++;
                    pieces.Add(new Piece { Text = done });
                    if (!Report(opt, c.Index + 1, chunks.Count, "cached")) { report.Cancelled = true; break; }
                    continue;
                }

                string user = BuildUserMessage(c);
                Piece piece = TranslateOne(c, user, system, opt, report, chainState);
                if (piece.Text != null) cache.Put(c.Start, c.Text, piece.Text);
                else { piece.Text = c.Text; }        // left as it was written

                pieces.Add(piece);
                Log(opt, string.Format(CultureInfo.InvariantCulture,
                    "{0,4}/{1}  {2,6} chars  {3,-14} {4} ask{5}  {6,7:N1} s  {7}",
                    c.Index + 1, chunks.Count, c.Text.Length,
                    piece.Engine ?? "-", piece.Asks, piece.Asks == 1 ? " " : "s",
                    piece.Ms / 1000.0,
                    piece.Engine == null ? "LEFT IN THE ORIGINAL"
                                         : (piece.LastResort ? "last resort" : "ok")));

                if (!Report(opt, c.Index + 1, chunks.Count,
                            piece.LastResort ? "last resort" : "translated"))
                { report.Cancelled = true; break; }
            }

            cache.Flush();
            report.Text = Assemble(parts, pieces, opt);
            FinishLog(opt, report, pieces, DateTime.UtcNow - started);
            if (!report.Cancelled)
            {
                report.Issues.AddRange(TranslationChecks.Book(bookText, report.Text));
                report.Issues.AddRange(GenderIssues(pieces));
            }
            report.Ok = !report.Cancelled && report.Text.Length > 0;
            report.Elapsed = DateTime.UtcNow - started;
            return report;
        }

        /// <summary>One piece down the chain. Returns a piece whose Text is null
        /// when every stop refused it.
        ///
        /// <para><b>Each stop is asked more than once before the chain moves on</b>,
        /// because a refusal is not a verdict about the text — it is a throw of the
        /// dice. Measured on seven passages a novel had been refused over: sent four
        /// times each, FOUR OF THE SEVEN went through at least once, and plainly at
        /// random — one passed twice then failed, another passed three times and
        /// failed the fourth. What does not clear within four asks never clears,
        /// being systematic rather than moody, which is why three is the number and
        /// five would only add hours to the one book that needs them.</para>
        ///
        /// <para>It matters for quality and not only for coverage: a piece that goes
        /// through on a second ask keeps the engine the reader chose, where moving
        /// down the chain trades the prose away.</para></summary>
        /// <summary>What the chain has learned about itself as the job runs: which
        /// engine the reader actually chose, and which have stopped answering.
        ///
        /// <para><b>An engine that has refused three pieces running is not having
        /// a bad minute, it is out</b> — a day's quota, a dead key, a service
        /// down. Reported from use, 2026-08-15: Gordan's second novel logged
        /// <c>deepseek, 4 asks</c> on every single piece, meaning three Gemini
        /// attempts thrown away each time because his Gemini allowance had gone
        /// on the book before. Three quarters of every request wasted, a hundred
        /// and twelve times, and the run went from seventy minutes to an
        /// estimated four hundred.</para>
        ///
        /// <para>Standing an engine down is for THIS JOB only. Nothing is stored,
        /// so tomorrow's quota is tried afresh — the state is a fact about the
        /// last few minutes, not a verdict.</para></summary>
        private sealed class ChainState
        {
            public const int StandDownAfter = 3;

            private readonly Dictionary<string, int> misses =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> down =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly string primaryId;

            public ChainState(Options opt)
            {
                primaryId = opt != null && opt.Chain != null && opt.Chain.Count > 0
                    ? opt.Chain[0].Id : "";
            }

            /// <summary><b>The engine the reader asked for</b>, which is not the
            /// same as "first in the chain" once anything can be stood down — and
            /// that distinction is load-bearing. The in-book last-resort notice
            /// exists to say "you did not choose this"; keyed on position it
            /// would fall silent exactly when everything above Azure had dropped
            /// out and Azure was carrying the book on its own, which is the one
            /// case the notice is for.</summary>
            public bool IsPrimary(TranslationEngine e)
            {
                return e != null && string.Equals(e.Id, primaryId, StringComparison.OrdinalIgnoreCase);
            }

            public bool IsDown(TranslationEngine e)
            {
                return e != null && down.Contains(e.Id);
            }

            public void Worked(TranslationEngine e)
            {
                if (e != null) misses.Remove(e.Id);
            }

            /// <summary>Records a piece this engine would not take. Returns true
            /// the moment it is stood down, so the caller can say so once.</summary>
            public bool Missed(TranslationEngine e, int enginesStillUp)
            {
                if (e == null || down.Contains(e.Id)) return false;
                // Never the last one standing. A chain with nothing left in it
                // does not fail faster, it just fails.
                if (enginesStillUp <= 1) return false;

                int n;
                misses.TryGetValue(e.Id, out n);
                misses[e.Id] = ++n;
                if (n < StandDownAfter) return false;
                down.Add(e.Id);
                return true;
            }

            public int StillUp(List<TranslationEngine> chain)
            {
                int n = 0;
                foreach (var e in chain) if (!down.Contains(e.Id)) n++;
                return n;
            }
        }

        private static Piece TranslateOne(TextChunk c, string user, string system,
                                          Options opt, TranslationReport report, ChainState state)
        {
            TranslationResult last = null;
            List<TranslationIssue> lastIssues = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int asks = 0;
            var refused = new List<TranslationEngine>();

            for (int stop = 0; stop < opt.Chain.Count; stop++)
            {
                TranslationEngine engine = opt.Chain[stop];
                if (state.IsDown(engine)) continue;
                int attempts = Math.Max(1, engine.Attempts);

                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    asks++;
                    TranslationResult r = Translator.Send(engine, null, system, user,
                                                          opt.MaxOutputTokens, opt.SourceLang, opt.TargetLang);
                    List<TranslationIssue> issues = r.Ok
                        ? TranslationChecks.Chunk(c, r.Text, opt.TargetLang)
                        : new List<TranslationIssue>();
                    last = r; lastIssues = issues;

                    if (!r.Ok || HasSuspect(issues))
                    {
                        // Only the LAST attempt counts as this engine refusing the
                        // piece — the earlier ones are the retries it is allowed.
                        if (attempt == attempts - 1 && !refused.Contains(engine)) refused.Add(engine);
                        continue;
                    }

                    if (stop == 0 && attempt > 0) report.RetriedOk++;
                    if (stop > 0)
                    {
                        report.ViaFallback++;
                        // Why a later stop was needed is worth recording: over a
                        // real novel the second engine took 29 pieces of 159, and
                        // without knowing whether that was refusals or failed
                        // checks there is no telling a filtering problem from a
                        // quality one.
                        report.Issues.Add(new TranslationIssue
                        {
                            Severity = CheckSeverity.Note,
                            Kind = engine.LastResort ? "last resort" : "later engine",
                            Detail = engine.DisplayName + " — " +
                                     (last.Ok ? Describe(lastIssues) : (last.Error + " " + last.Detail)),
                            ChunkIndex = c.Index
                        });
                    }
                    foreach (var i in issues) report.Issues.Add(i);
                    // A LAST RESORT IS A FALLBACK, NOT A CHOICE — so the notice
                    // goes into the book only when the reader did NOT ask for this
                    // engine. Choosing Azure for a whole book is a legitimate thing
                    // to want (it is free, and it is instant), and it never fails,
                    // so without this test every piece would be marked and the
                    // consecutive-merge would wrap the entire book in a warning
                    // about a decision the reader made themselves.
                    clock.Stop();
                    // This one answered; anything above it that would not, on this
                    // piece, has its count moved on.
                    state.Worked(engine);
                    foreach (var miss in refused)
                        if (state.Missed(miss, state.StillUp(opt.Chain)))
                            Log(opt, string.Format(CultureInfo.InvariantCulture,
                                "  stood down   {0} — {1} pieces in a row it would not take",
                                miss.DisplayName, ChainState.StandDownAfter));
                    return new Piece
                    {
                        Text = r.Text,
                        LastResort = engine.LastResort && !state.IsPrimary(engine),
                        Engine = engine.Id,
                        Asks = asks,
                        Ms = clock.ElapsedMilliseconds
                    };
                }
            }

            // Nothing would take it. Counted and reported, never silently dropped —
            // the reader has to be able to find out that these paragraphs are in the
            // language the book came in.
            clock.Stop();
            // Nobody took it, so every engine that was asked refused it.
            foreach (var miss in refused)
                if (state.Missed(miss, state.StillUp(opt.Chain)))
                    Log(opt, string.Format(CultureInfo.InvariantCulture,
                        "  stood down   {0} — {1} pieces in a row it would not take",
                        miss.DisplayName, ChainState.StandDownAfter));
            report.LeftInOriginal++;
            report.Issues.Add(new TranslationIssue
            {
                Severity = CheckSeverity.Suspect,
                Kind = "left in the original",
                Detail = last != null && last.Ok ? Describe(lastIssues)
                       : last != null ? (last.Error + " " + last.Detail) : "no engine answered",
                ChunkIndex = c.Index
            });
            return new Piece { Text = null, Asks = asks, Ms = clock.ElapsedMilliseconds };
        }

        /// <summary>
        /// Puts the book back together: what was kept, what was translated, and a
        /// notice around anything the last resort had to rescue.
        ///
        /// <para><b>A last-resort passage announces itself IN THE BOOK</b> (Gordan,
        /// 2026-08-15), and that is not a courtesy. Azure sets the narrator's sex
        /// and the level of address wrongly for a whole passage; the first of those
        /// a check can catch, and the second no check ever will, because a real book
        /// carries both registers between different pairs of characters. So the
        /// notice is the only mechanism that reports the fault at all.</para>
        ///
        /// <para><b>Two notices, not one</b> — without the closing one the reader
        /// never learns where the weaker translation stops and reads the rest of the
        /// book suspicious of it. <b>And consecutive pieces share one pair</b>: five
        /// in a row are one stretch of thirty thousand characters, not five, and
        /// unmerged a heavily-refused book would carry forty of these.</para>
        /// </summary>
        private static string Assemble(BookMatter.Split parts, List<Piece> pieces, Options opt)
        {
            string open = null, close = null;
            foreach (Piece p in pieces) if (p.LastResort) { NoticePair(opt, out open, out close); break; }

            var sb = new StringBuilder();
            if (parts.Front.Length > 0) sb.Append(Tidy(parts.Front));

            bool inside = false;
            foreach (Piece p in pieces)
            {
                if (p.LastResort && !inside) { sb.Append(Tidy(open)); inside = true; }
                else if (!p.LastResort && inside) { sb.Append(Tidy(close)); inside = false; }
                sb.Append(Tidy(p.Text));
            }
            if (inside) sb.Append(Tidy(close));

            if (parts.Back.Length > 0) sb.Append(Tidy(parts.Back));
            return sb.ToString();
        }

        /// <summary><b>The notice is written in the BOOK's language, not in ours.</b>
        /// `en.lang` is the only language file there is, and an English sentence
        /// inside a French book would be read aloud by the French voice under French
        /// rules. So the engine that is already translating this book translates the
        /// notice too — one small request at the start, no new language table, and no
        /// half-supported languages.
        ///
        /// <para>If that request fails the English stands rather than nothing: a
        /// notice in the wrong language still tells the reader something, where
        /// silence tells them nothing at all.</para></summary>
        private static void NoticePair(Options opt, out string open, out string close)
        {
            open = Localization.T("Translate.LastResort.Begin");
            close = Localization.T("Translate.LastResort.End");
            if (string.IsNullOrEmpty(opt.TargetLang) ||
                opt.TargetLang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                TranslationResult r = Translator.Send(opt.First, null,
                    "You are translating two short notices that a reading program shows inside a book. " +
                    "Translate from English into " + (LanguageDetector.DisplayName(opt.TargetLang) ?? opt.TargetLang) +
                    ". Keep them as two lines, in the same order. Output only the two lines.",
                    open + "\n" + close, 400, "en", opt.TargetLang);
                if (!r.Ok || string.IsNullOrWhiteSpace(r.Text)) return;
                string[] lines = r.Text.Replace("\r\n", "\n").Split('\n');
                var kept = new List<string>();
                foreach (string l in lines) if (l.Trim().Length > 0) kept.Add(l.Trim());
                if (kept.Count >= 2) { open = kept[0]; close = kept[1]; }
            }
            catch { /* the English stands */ }
        }

        /// <summary>The narrator does not change sex halfway through a book.
        ///
        /// <para>Croatian marks the speaker's gender in every first-person past
        /// form, so a piece that disagrees with the book around it is measurable —
        /// and it is the ONE check that separated the engines when paragraph counts,
        /// length ratios and figures agreed across all of them.</para></summary>
        private static List<TranslationIssue> GenderIssues(List<Piece> pieces)
        {
            var found = new List<TranslationIssue>();
            int bookM = 0, bookF = 0;
            var perPiece = new List<int[]>();
            foreach (Piece p in pieces)
            {
                int m, f;
                TranslationChecks.GenderCounts(p.Text, out m, out f);
                perPiece.Add(new[] { m, f });
                bookM += m; bookF += f;
            }
            int total = bookM + bookF;
            // Below a handful of forms in the whole book there is nothing to be
            // consistent with — a third-person narrative has no first person at all.
            if (total < 12) return found;
            bool bookIsFeminine = bookF > bookM;
            // A book that is genuinely half and half has more than one narrator, and
            // then a piece disagreeing with the average means nothing.
            int major = Math.Max(bookM, bookF);
            if (major * 4 < total * 3) return found;

            for (int i = 0; i < perPiece.Count; i++)
            {
                int m = perPiece[i][0], f = perPiece[i][1];
                if (m + f < 3) continue;
                int against = bookIsFeminine ? m : f;
                int with = bookIsFeminine ? f : m;
                if (against > with)
                    found.Add(new TranslationIssue
                    {
                        Severity = CheckSeverity.Note,
                        Kind = "narrator's gender",
                        Detail = "this piece reads as " + (bookIsFeminine ? "masculine" : "feminine") +
                                 " where the book reads as " + (bookIsFeminine ? "feminine" : "masculine"),
                        ChunkIndex = i
                    });
            }
            return found;
        }

        /// <summary>A line in the run's log. <b>Never allowed to fail the job</b> —
        /// a translation that stops because a diagnostic could not be written would
        /// be the instrument breaking the thing it measures.</summary>
        private static void Log(Options opt, string line)
        {
            if (opt == null || string.IsNullOrEmpty(opt.LogPath)) return;
            try { File.AppendAllText(opt.LogPath, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch { }
        }

        private static void StartLog(Options opt, int bookChars, BookMatter.Split parts, int chunks)
        {
            if (opt == null || string.IsNullOrEmpty(opt.LogPath)) return;
            try { File.Delete(opt.LogPath); } catch { }
            var sb = new StringBuilder();
            sb.AppendLine("Nemoviz Book Reader translation log");
            sb.AppendLine("started      " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "book         {0:N0} characters; front {1:N0}, body {2:N0}, back {3:N0}",
                bookChars, parts.Front.Length, parts.Body.Length, parts.Back.Length));
            if (parts.Note != null) sb.AppendLine("matter       " + parts.Note);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "from {0} into {1}, {2} pieces", opt.SourceLang, opt.TargetLang, chunks));
            var chain = new StringBuilder();
            foreach (TranslationEngine e in opt.Chain)
            {
                if (chain.Length > 0) chain.Append(" -> ");
                chain.Append(e.Id).Append(" x").Append(e.Attempts);
            }
            sb.AppendLine("chain        " + chain);
            sb.AppendLine();
            Log(opt, sb.ToString().TrimEnd());
        }

        private static void FinishLog(Options opt, TranslationReport report,
                                      List<Piece> pieces, TimeSpan elapsed)
        {
            if (opt == null || string.IsNullOrEmpty(opt.LogPath)) return;
            var byEngine = new Dictionary<string, int>(StringComparer.Ordinal);
            long slowest = 0; int slowestAt = 0; long asked = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                string e = pieces[i].Engine ?? "(none)";
                int n; byEngine.TryGetValue(e, out n); byEngine[e] = n + 1;
                asked += pieces[i].Asks;
                if (pieces[i].Ms > slowest) { slowest = pieces[i].Ms; slowestAt = i + 1; }
            }
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("finished     " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "elapsed      {0:hh\\:mm\\:ss}  ({1:N1} s for {2} pieces, {3:N1} s each)",
                elapsed, elapsed.TotalSeconds, Math.Max(1, pieces.Count),
                elapsed.TotalSeconds / Math.Max(1, pieces.Count)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "requests     {0} for {1} pieces", asked, pieces.Count));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "slowest      piece {0}, {1:N1} s", slowestAt, slowest / 1000.0));
            foreach (var kv in byEngine)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-14} {1} piece(s)", kv.Key, kv.Value));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "cached {0}, retried ok {1}, later engine {2}, left in the original {3}{4}",
                report.FromCache, report.RetriedOk, report.ViaFallback, report.LeftInOriginal,
                report.Cancelled ? ", STOPPED" : ""));
            Log(opt, sb.ToString().TrimEnd());
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
