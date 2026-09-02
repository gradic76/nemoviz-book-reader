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
        public static string BuildSystemPrompt(string sourceLang, string targetLang, string readerNotes, TranslationBible bible = null)
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
            // Her Serbian document asks for this in as many words -- "Ne koristi
            // Markdown. Ne koristi zvezdice za naglašavanje." -- and nothing here
            // said it. The source is plain text and carries none, so anything the
            // model adds is its own habit; a stray ** reaches the reader as
            // "zvjezdica zvjezdica" or is silently eaten by the cleaner, and
            // neither is what the author wrote.
            sb.AppendLine("- Return plain text. No Markdown, no asterisks for emphasis, no headings the source does not have.");
            // The same comparison showed our version running 100 % of the source's
            // length where the human translation ran 92 %, and reading as a
            // word-for-word trace of the English: "je uređivanje teksta omogućeno
            // putem zasebnog dijaloškog okvira" against "se tekst uređuje u
            // zasebnom dijalogu". Wordiness is not a separate fault from
            // literalness; it is what literalness looks like in the target.
            sb.AppendLine("- Write natural, idiomatic prose in the target language. Do not mirror the source's sentence structure where the target would put it differently, and do not reach for a loanword when the target has its own word.");
            sb.AppendLine("- Stay consistent with the rest of the book: the same character names, the same terms, the same level of address between the same people, and the same gender for the same speaker.");
            // THE LAYERS, ORDERED MOST STABLE FIRST, and the order is not tidiness.
            // Prompt caching pays for a stable PREFIX, so what never changes has to
            // come before what changes per language, which comes before what changes
            // per book. Written the other way round, one new book would break the
            // cache for everything above it.
            //
            //   1. the rules above          the same for all 138 languages
            //   2. TranslationRules.For     the same for every book in this language
            //   3. bible.ToPrompt           this book: its narrator, its names
            //   4. the reader notes         this book, and they outrank everything
            string langRules = TranslationRules.For(targetLang);
            if (langRules.Length > 0) sb.AppendLine(langRules);
            if (bible != null)
            {
                string facts = bible.ToPrompt();
                if (facts.Length > 0) sb.Append(facts);
            }
            if (!string.IsNullOrWhiteSpace(readerNotes))
            {
                sb.AppendLine();
                sb.AppendLine("Notes from the reader about this book (these take precedence):");
                sb.AppendLine(readerNotes.Trim());
            }
            return sb.ToString();
        }

        /// <summary>What is actually sent: the context, clearly marked as not to be
        /// translated, then the passage.
        ///
        /// <para><b>AZURE GETS THE BARE PASSAGE AND NOTHING ELSE, and the first
        /// real Azure output this project ever saw is why.</b> Azure Translator is
        /// a machine translator, not a model you talk to: it does not read
        /// instructions, it translates whatever text it is handed. Handed the
        /// scaffolding below it translated THAT too, so two pieces of Second Strike
        /// reached the reader carrying "KONTEKST (kraj prethodnog odlomka, radi
        /// kontinuiteta — NEMOJTE ga prevoditi niti ponavljati):" and "PREVEDI:" as
        /// running text, with the whole previous passage repeated between them.
        /// Read aloud, which is the only way this reader meets it, that is a
        /// paragraph of nonsense followed by a page they have already heard.</para>
        ///
        /// <para>The system prompt was already dropped for Azure and the comment in
        /// <c>Translator.Send</c> says so plainly — but the USER message was built
        /// once per piece, before the chain knew which engine would take it, so the
        /// half nobody had thought about went out unchanged. It cost nothing for
        /// three years of chat models and everything the first time a translator
        /// took a piece.</para>
        ///
        /// <para>Nothing is lost by it: the lead exists to give a model context it
        /// can read, and Azure has no use for context in any form — it translates
        /// sentence by sentence and carries nothing between requests.</para>
        ///
        /// <para><b>The length ratio is what caught it</b>, and only because it was
        /// logged for every piece the night before rather than only for failures:
        /// Gemini ran 0.93-1.05 through the book and the two Azure pieces came back
        /// at <b>1.11 and 1.50</b> — inflated by exactly the context and the labels.
        /// Neither tripped the 1.60 gate.</para></summary>
        private static string BuildUserMessage(TextChunk c, TranslationEngine engine)
        {
            if (engine != null && engine.Kind == EngineKind.AzureTranslator) return c.Text;
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
            /// <summary>Where to write what was decided about this book — the
            /// narrator and the glossary. Null writes none.</summary>
            public string BiblePath;
            /// <summary>A glossary from an EARLIER book to start from, chosen by the
            /// reader. Empty for a standalone book.
            ///
            /// <para><b>Why the reader picks it and NBR does not.</b> Book two of a
            /// trilogy must render its names exactly as book one did, and nothing in
            /// the text says two books belong together — the reader knows and the
            /// program cannot. The same line the narrator gender sits on: NBR
            /// supplies the tool, the reader supplies what exists only outside the
            /// text. What is inherited is intersected with the names this book
            /// really uses, so a decision carries over and an irrelevant one does
            /// not ride along in every request.</para></summary>
            public string InheritBiblePath;
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
            /// <summary>Why nothing took it — set only when the piece was left
            /// in the original, and it says WHICH kind of failure it was.</summary>
            public string Why;
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

            // THE BOOK FACTS, BEFORE A WORD OF IT IS SENT. An inherited glossary
            // first -- the reader saying "this is book two of that trilogy" -- then
            // one call for whatever it does not already answer. Inheriting is
            // narrowed to the names this book really uses, so book one's cast does
            // not ride along in every request.
            // THE BOOK'S OWN GLOSSARY COMES FIRST, and until 2026-08-29 it was
            // never read at all. Load() took only the INHERITED path — the one
            // picked in the dialog, whose list excludes the book being translated
            // — so a re-run of the same book started from nothing, asked the model
            // again, and SAVED OVER what was there.
            //
            // Which made a documented feature impossible: this file says of itself
            // that it is "meant to be opened and corrected by the reader after they
            // have read the book". A correction could not take effect and was
            // destroyed by the next run. That is exactly the repair Helena Sedanka
            // needed — three novels with a first-person narrator read as a man.
            //
            // The cascade is the one the rest of the app already uses for voices:
            // what this book says, then what it inherits, then what the model can
            // work out. Found because Gordan said he would re-translate "uz
            // postojeći glosar" and it would not have been.
            TranslationBible bible = TranslationBible.Load(opt.BiblePath);
            bible.FillGapsFrom(TranslationBible.Load(opt.InheritBiblePath));
            bible.KeepOnlyPresentIn(parts.Body);
            bible.FillGapsFrom(DetectBible(parts.Body, opt, report));
            bible.Save(opt.BiblePath);
            if (bible.NarratorGender.Length > 0)
                Log(opt, "narrator      " + bible.NarratorGender);
            if (bible.Names.Count > 0)
                Log(opt, "glossary      " + bible.Names.Count + " names and terms");

            // WHICH RULEBOOK, AND HOW BIG. The rules used to be compiled in, and
            // the argument for that was that a file can go missing and a
            // translator that silently loses its rules produces work that looks
            // finished. They are files now (Gordan, 2026-09-01), so the silence
            // is what had to go: every book's log says what was read and how
            // much of it, and a language with none says so in as many words.
            {
                TranslationRules.Reload();
                string rulesText = TranslationRules.For(opt.TargetLang);
                Log(opt, "rules         " + (rulesText.Length > 0
                    ? rulesText.Length + " characters from " + TranslationRules.PathFor(opt.TargetLang)
                    : "none for " + opt.TargetLang + " -- looked in "
                      + TranslationRules.PathFor(opt.TargetLang)));
            }
            var cache = TranslationCache.Open(opt.CachePath);
            string system = BuildSystemPrompt(opt.SourceLang, opt.TargetLang, opt.ReaderNotes, bible);

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

                Piece piece = TranslateOne(c, system, opt, report, chainState);
                if (piece.Text != null) cache.Put(c.Start, c.Text, piece.Text);
                else { piece.Text = c.Text; }        // left as it was written

                pieces.Add(piece);
                // THE LENGTH RATIO IS LOGGED EVEN WHEN IT PASSES, and that is the
                // point of it. TranslationChecks computes it for every piece and
                // then drops it unless it trips the 0.55-1.60 gate, so the only
                // ratios anyone ever saw were the failures.
                //
                // Gordan, 2026-08-28, on being offered a synthetic test for
                // paraphrasing: *"time ne bismo nista dobili... jedino sto cemo
                // saznati tim testom jest da moze proci a to znamo i sad"*. He is
                // right -- feeding the gate an input built to pass it measures the
                // gate's own definition. What DOES carry information is the
                // distribution over a real book: Croatian runs 0.93-0.95 of the
                // English source, so an engine quietly retelling rather than
                // translating would sit systematically low and IN BAND, say 0.75
                // across every piece, which no single-piece threshold can see and a
                // column of numbers shows at a glance.
                //
                // One number in a line that was already being written. It answers
                // nothing by itself; it makes the question answerable from any run
                // that has already happened.
                string ratio = piece.Engine == null || piece.Text == null || c.Text.Length == 0
                    ? "     "
                    : string.Format(CultureInfo.InvariantCulture, "{0,5:0.00}",
                                    piece.Text.Length / (double)c.Text.Length);
                Log(opt, string.Format(CultureInfo.InvariantCulture,
                    "{0,4}/{1}  {2,6} chars {3}x  {4,-14} {5} ask{6}  {7,7:N1} s  {8}",
                    c.Index + 1, chunks.Count, c.Text.Length, ratio,
                    piece.Engine ?? "-", piece.Asks, piece.Asks == 1 ? " " : "s",
                    piece.Ms / 1000.0,
                    piece.Engine == null ? "LEFT IN THE ORIGINAL — " + (piece.Why ?? "no reason recorded")
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
                report.Issues.AddRange(GenderIssues(pieces, bible));
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


        /// <summary>Establishes the book facts before a word of it is translated:
        /// who the narrator is, and how the names it keeps using are to be rendered.
        ///
        /// <para><b>The finding half costs nothing and reads the WHOLE book.</b>
        /// <see cref="TranslationChecks.FrequentNames"/> is a deterministic scan for
        /// capitalised words that are not merely sentence openers, and it already
        /// existed for the vanished-name check. Sampling would have been the obvious
        /// shortcut and it is the wrong one: a character introduced in chapter
        /// thirty is exactly the one that ends up with five different forms across a
        /// book, because nothing earlier fixed a choice.</para>
        ///
        /// <para><b>The model is asked only to DECIDE</b>, over a shortlist, with
        /// the opening of the book for context — which is where a narrator gives
        /// themselves away and where the cast is introduced. One request. Measured
        /// against the jobs we have actually run (8 min, 3 h, 5 h) it is under one
        /// per cent of the wall clock on anything but the shortest book.</para>
        ///
        /// <para><b>A failure here is not a failure of the job.</b> No answer, a
        /// refusal, no key — the book is translated exactly as it would have been
        /// before this existed, and the report says the facts were not established.
        /// The alternative, refusing to translate because a preparatory call did not
        /// come back, would be worse than the fault it prevents.</para></summary>
        /// <summary>Below this many first-person words per 100 000 characters of
        /// narration, a book is not written in the first person and any narrator
        /// gender offered for it is refused.
        ///
        /// <para><b>Measured through this very method</b> (not through a script
        /// beside it — an earlier draft of this comment quoted numbers from a probe
        /// that counted differently, which is a comment that lies). English books
        /// in the library, dialogue stripped:</para>
        ///
        /// <para>first person — The Speed of Souls 1639, The Tenant 1622,
        /// Harvest Home 791, Lady 697, The Origin of Species 107.
        /// Third person — a novel written as an in-universe history 68,
        /// Robin Cook's Pandemic 1.</para>
        ///
        /// <para><b>Set low on purpose, because the two ways to be wrong are not
        /// equal.</b> Refusing a real narrator gender costs the fault the glossary
        /// was built to cure — a woman narrating in a man's voice, bad but
        /// survivable. Accepting one the book does not have costs the whole novel
        /// rewritten into the first person, which is what Pandemic did. So the
        /// floor sits well above the dangerous case (100 against 1, a hundredfold)
        /// and only just above the tightest honest one.</para>
        ///
        /// <para><b>Known and accepted:</b> The Origin of Species clears this by
        /// seven per cent. It is first-person and its author is male, so the line
        /// it keeps is true; but a first-person book that reads more like a report
        /// than a story sits near this line and a future sample could fall the
        /// wrong side of it. Move the floor DOWN if that happens, never up.</para>
        ///
        /// <para>The language gate earns its place in the same table: the Croatian
        /// translation of Harvest Home scores 70 against the English original's
        /// 791, and it is the same book. Forced through the English rule it would
        /// be refused every time.</para></summary>
        private const int FirstPersonFloor = 100;

        private static TranslationBible DetectBible(string body, Options opt, TranslationReport report)
        {
            var bible = new TranslationBible();
            if (opt == null || opt.First == null || string.IsNullOrEmpty(body)) return bible;

            List<string> candidates = TranslationChecks.FrequentNameList(body, 60);
            // The scan above takes capitalised words only, so a term the book writes
            // in lower case is invisible to it -- and that is where the OTHER half of
            // the blind test's glossary gap was: "human" and "she-human", the central
            // term of a novel narrated by a cat, never once offered to the model.
            List<string> terms = TranslationChecks.FrequentTermList(body, 20);

            // Measured ONCE, and before anything is asked: it is a property of
            // the book, not of whichever engine happens to answer.
            int firstPerson = TranslationChecks.FirstPersonDensity(body, opt.SourceLang);
            // The opening, and enough of it to meet the narrator and the first few
            // people they talk to. Front matter is already gone by the time this is
            // called, so this really is the first page of the story.
            string opening = body.Length <= 6000 ? body : body.Substring(0, 6000);

            var sb = new StringBuilder();
            sb.AppendLine("You are preparing to translate a novel from "
                          + (LanguageDetector.DisplayName(opt.SourceLang) ?? opt.SourceLang)
                          + " into " + (LanguageDetector.DisplayName(opt.TargetLang) ?? opt.TargetLang) + ".");
            sb.AppendLine("Answer in PLAIN LINES, nothing else. No JSON, no prose, no explanation.");
            sb.AppendLine();
            sb.AppendLine("Line 1, only if the book is narrated in the FIRST PERSON:");
            sb.AppendLine("NARRATOR: feminine");
            sb.AppendLine("or");
            sb.AppendLine("NARRATOR: masculine");
            sb.AppendLine("Give this line ONLY if you are sure from the text. If the book is in the third person, or you cannot tell, leave the line out entirely rather than guessing.");
            sb.AppendLine();
            sb.AppendLine("Then one line for each name below that appears in the story, in this shape:");
            sb.AppendLine("NAME: <as written in the source> = <how it is to be written in the target language, and how it inflects if the target language inflects names>");
            sb.AppendLine("Keep a name in its original form where that is the convention of the target language, and transliterate it where THAT is the convention. Say which case forms to use if the target language declines.");
            // JUDGE BY HOW THE BOOK USES THE WORD, NOT BY WHETHER THE WORD IS
            // ORDINARY. This line used to end "an ordinary word that happens to be
            // capitalised is not wanted", and it did exactly what it said: Gordan's
            // blind test came back with "Outside" dropped, which in that book is a
            // PLACE -- an ordinary word the story has made its own, which is what a
            // good half of the proper nouns in fiction are. The intent was to keep
            // "The" and "When" out on the days the scan lets them through, and that
            // intent is kept; only the test moves off the word and onto its use.
            sb.AppendLine("Leave out a candidate only if the book does not use it as a name or as a term of its own - a word the scan picked up in passing. A perfectly ordinary word IS wanted when the story uses it as one: a place, a nickname, a thing this book has named.");
            sb.AppendLine();
            sb.AppendLine("Candidate names, most frequent first:");
            sb.AppendLine(string.Join(", ", candidates.ToArray()));
            if (terms.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recurring compounds this book writes for itself. Give a NAME: line for any that is a term of the story, and ignore the rest:");
                sb.AppendLine(string.Join(", ", terms.ToArray()));
            }
            sb.AppendLine();
            sb.AppendLine("The opening of the book:");
            sb.AppendLine(opening);

            // THE PREPARATION WALKS THE CHAIN, exactly as the translation does —
            // found on the first real run (2026-08-20). It asked opt.First alone,
            // and on a book Gemini would not touch the preparation was refused with
            // everything else: the glossary came out empty while DeepSeek went on
            // to translate the whole passage perfectly well. A step that has no
            // fallback in a design built around fallback is a step that fails on
            // exactly the books the fallback exists for.
            TranslationResult r = null;
            foreach (TranslationEngine stop in opt.Chain)
            {
                // Azure cannot be asked a question -- it translates and nothing
                // else -- so it has no answer to give here.
                if (stop == null || stop.LastResort) continue;
                try { r = Translator.Send(stop, null, "", sb.ToString(), Math.Max(4000, opt.MaxOutputTokens / 2),
                                          opt.SourceLang, opt.TargetLang); }
                catch (Exception ex) { r = new TranslationResult { Error = ex.Message }; }
                if (r == null || !r.Ok || string.IsNullOrEmpty(r.Text)) continue;

                // A PARTIAL ANSWER IS NOT A FINISHED ONE, and this is the THIRD
                // time the preparation has failed in the same family (2026-08-21).
                // First it asked only one engine; then it accepted text it could
                // parse nothing out of; now: Gemini answered the narrator question
                // and gave no names at all, the loop saw a non-empty result and
                // stopped, and that sample was translated with no glossary while
                // the other had seven names with full declensions. Same shape every
                // time -- one sample prepared, the other not, and a comparison that
                // measures our own bug.
                //
                // So the answers are MERGED across the chain rather than taken from
                // whoever speaks first, and the walk stops only when both halves are
                // in hand or the chain runs out. Merging costs at most one extra
                // request per book and cannot lose an answer already given:
                // FillGapsFrom only fills what is still missing.
                var got = new TranslationBible();
                foreach (string line in r.Text.Replace("\r\n", "\n").Split('\n')) got.ReadLine(line);

                // A NARRATOR THE BOOK DOES NOT HAVE IS WORSE THAN NO NARRATOR AT
                // ALL. The prompt asks for this line only for a first-person book
                // and says in as many words to leave it out otherwise; Gemini gave
                // "masculine" for Robin Cook's Pandemic, which is third person, and
                // the model then rewrote the novel into a voice it was not written
                // in -- see TranslationChecks.FirstPersonDensity for the passage.
                // So the answer is checked against the text rather than trusted.
                // The veto only ever REMOVES the line: it cannot invent one, and
                // where the language cannot be measured it does nothing.
                if (firstPerson >= 0 && firstPerson < FirstPersonFloor && got.NarratorGender.Length > 0)
                {
                    Log(opt, "narrator      \"" + got.NarratorGender + "\" refused -- only "
                             + firstPerson + " first-person words per 100,000 of narration, so this "
                             + "book is not written in the first person");
                    got.NarratorGender = "";
                }

                bible.FillGapsFrom(got);
            }

            // Whatever was gathered, even if one half is missing -- a narrator with
            // no names still fixes the fault that started all of this.
            if (!bible.IsEmpty) return bible;

            if (r == null || !r.Ok || string.IsNullOrEmpty(r.Text))
            {
                Add(report, CheckSeverity.Note, "book facts",
                    "the narrator and the names could not be established, so the book was translated without them"
                    + (r != null && r.Error != null ? " (" + r.Error + ")" : ""));
                return bible;
            }

            // THE LAST-DITCH READ MUST TAKE THE VETO WITH IT, and until
            // 2026-08-29 it did not — it parsed the raw answer straight into
            // `bible`, putting back the very line the veto had just removed.
            //
            // Seen in Gordan's own log, twice, on Second Strike:
            //
            //     narrator "masculine" refused -- only 6 first-person words per
            //              100,000 of narration, so this book is not written in
            //              the first person
            //     narrator masculine
            //
            // Refused on one line and adopted on the next. It happens whenever the
            // chain's answer carries a narrator and NO names: the veto empties the
            // narrator, `bible` is therefore empty, and this block re-reads the
            // same text with nothing checking it. Which is exactly the fault the
            // veto exists for — the comment above it records Robin Cook's
            // Pandemic, a third-person novel rewritten into a voice it was not
            // written in — and the consequence is that all 112 requests of a
            // third-person thriller were told "The narrator speaks in the first
            // person and is masculine. Every first-person past-tense form must
            // agree with that, in every passage."
            var late = new TranslationBible();
            foreach (string line in r.Text.Replace("\r\n", "\n").Split('\n')) late.ReadLine(line);
            if (firstPerson >= 0 && firstPerson < FirstPersonFloor && late.NarratorGender.Length > 0)
            {
                Log(opt, "narrator      \"" + late.NarratorGender + "\" refused -- only "
                         + firstPerson + " first-person words per 100,000 of narration, so this "
                         + "book is not written in the first person");
                late.NarratorGender = "";
            }
            bible.FillGapsFrom(late);
            return bible;
        }

        private static void Add(TranslationReport report, CheckSeverity sev, string kind, string detail)
        {
            if (report == null) return;
            report.Issues.Add(new TranslationIssue { Severity = sev, Kind = kind, Detail = detail });
        }
        private static Piece TranslateOne(TextChunk c, string system,
                                          Options opt, TranslationReport report, ChainState state)
        {
            TranslationResult last = null;
            List<TranslationIssue> lastIssues = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int asks = 0;
            var refused = new List<TranslationEngine>();
            var whyRefused = new Dictionary<TranslationEngine, string>();

            for (int stop = 0; stop < opt.Chain.Count; stop++)
            {
                TranslationEngine engine = opt.Chain[stop];
                string user = BuildUserMessage(c, engine);
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
                        // WHY IT WOULD NOT TAKE IT, kept for the report. Without this
                        // the note explaining the fallback described the SUCCESSFUL
                        // attempt instead, so it read "DeepSeek — " with nothing
                        // after the dash and the reason was lost. Measured
                        // 2026-08-21: Gemini was failing every piece of two books
                        // with "429, your prepayment credits are depleted", and the
                        // report said only that a later engine had been used --
                        // which reads as a content refusal and sent this session
                        // hunting the wrong fault twice.
                        if (!whyRefused.ContainsKey(engine))
                        {
                            // BOTH KINDS OF FAILURE, and until 2026-08-29 only one
                            // of them was recorded. A service that REFUSES sets
                            // Error and Detail and was written down; a service that
                            // ANSWERS and has its answer thrown out by our own
                            // checks set nothing, so the log said
                            //
                            //     handed on piece 112 -- Azure Translator took it.
                            //     Gemini (Google):
                            //
                            // with nothing after the colon. WhyEarlierStopsFailed
                            // does try to fall back to Describe(lastIssues), and
                            // that cannot work from there: by the time it runs,
                            // `lastIssues` has been overwritten by the SUCCEEDING
                            // engine's own findings, which are empty precisely
                            // because that engine passed. The reason has to be
                            // taken here, at the moment the answer is rejected.
                            //
                            // Gordan found it from one line of a real log and was
                            // right that it was worth chasing: this is the same
                            // shape as the fault fixed on 2026-08-21, reached down
                            // the other branch, and it has been hiding every
                            // check-rejection in every book since the checks were
                            // written.
                            string said = !r.Ok
                                ? (r.Error + " " + r.Detail).Trim()
                                : "answered, but our own check rejected it — " + Describe(issues);
                            if (said.Length > 0) whyRefused[engine] = said;
                        }
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
                        // AND IT GOES IN THE LOG, not only in the report.
                        //
                        // Gordan, 2026-08-21, of a piece that had just fallen
                        // through: "do we know why?" We did not. The reason was
                        // computed right here and put in the report, which lives
                        // as long as the dialog does; the LOG, which is what
                        // survives the run and what anybody reads afterwards,
                        // recorded only the tally "later engine 1". That is the
                        // same gap he found this morning for a piece left in the
                        // original, one layer up -- and the same answer.
                        string handedWhy = engine.DisplayName + " took it. "
                                   + WhyEarlierStopsFailed(opt, stop, whyRefused, last, lastIssues);
                        Log(opt, "  handed on   piece " + (c.Index + 1) + " -- " + handedWhy);
                        report.Issues.Add(new TranslationIssue
                        {
                            Severity = CheckSeverity.Note,
                            Kind = engine.LastResort ? "last resort" : "later engine",
                            Detail = handedWhy,
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
            // WHICH OF THE TWO IT WAS, and they are not the same thing at all
            // (Gordan, 2026-08-21: "u logovima ne pise nista o razlogu"). An engine
            // REFUSING a passage and OUR OWN CHECK rejecting a good translation both
            // end here, and the log said only LEFT IN THE ORIGINAL for either. That
            // matters beyond tidiness: he is about to measure which engine refuses
            // more over a whole novel, and a count that mixes the two measures us as
            // well as them. The very piece that prompted this was not refused by
            // anybody -- the repetition check threw away a faithful translation six
            // times.
            string why = last == null ? "no engine answered"
                       : last.Ok ? "our check: " + Describe(lastIssues)
                       : "the service: " + (last.Error + " " + last.Detail).Trim();
            report.Issues.Add(new TranslationIssue
            {
                Severity = CheckSeverity.Suspect,
                Kind = "left in the original",
                Detail = why,
                ChunkIndex = c.Index
            });
            return new Piece { Text = null, Asks = asks, Ms = clock.ElapsedMilliseconds, Why = why };
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
        private static List<TranslationIssue> GenderIssues(List<Piece> pieces, TranslationBible expected)
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

            // THE FACT COMES FROM THE SOURCE WHERE WE HAVE IT, NOT FROM THE
            // TRANSLATION (2026-08-20). Taking the book's own majority makes this a
            // CONSISTENCY test, and a uniformly wrong book is perfectly consistent:
            // that is exactly how three Richard Swan novels went out with a female
            // first-person narrator rendered as a man, and every check passing.
            // Given the detected narrator, the same counting becomes a CORRECTNESS
            // test and a book that is wrong throughout is the loudest thing in the
            // report rather than the quietest.
            bool haveFact = expected != null && expected.NarratorGender.Length > 0;
            bool bookIsFeminine = haveFact
                ? expected.NarratorGender == "feminine"
                : bookF > bookM;

            if (haveFact)
            {
                int against = bookIsFeminine ? bookM : bookF;
                int with = bookIsFeminine ? bookF : bookM;
                // Not "some pieces disagree" but "the book disagrees with itself
                // about who is telling it", which is a different and worse sentence
                // to read in a report.
                if (against > with)
                    found.Add(new TranslationIssue
                    {
                        Severity = CheckSeverity.Suspect,
                        Kind = "narrator",
                        Detail = "the narrator is " + expected.NarratorGender
                                 + ", but the translation uses the other gender in " + against
                                 + " of " + total + " first-person past-tense forms across the whole book"
                    });
            }
            else
            {
                // A book that is genuinely half and half has more than one narrator,
                // and then a piece disagreeing with the average means nothing.
                int major = Math.Max(bookM, bookF);
                if (major * 4 < total * 3) return found;
            }

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


        /// <summary>Why the stops before this one did not take the piece.
        ///
        /// <para><b>The whole point of the note</b>, and it was missing. It used to
        /// describe the attempt that SUCCEEDED, which by construction has nothing
        /// wrong with it — so the line read "DeepSeek — " and stopped. A reader of
        /// the report could see that a later engine had been used and never learn
        /// why, and "a later engine was needed" reads as a content refusal when it
        /// may be anything at all.</para>
        ///
        /// <para>It cost this session two wrong conclusions in a row: Gemini was
        /// failing every piece of two different books because its account had run
        /// out of prepaid credit, and the report's silence let that pass as the
        /// model declining to translate a published book.</para></summary>
        private static string WhyEarlierStopsFailed(Options opt, int reached,
                                                    Dictionary<TranslationEngine, string> why,
                                                    TranslationResult last,
                                                    List<TranslationIssue> lastIssues)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < reached && i < opt.Chain.Count; i++)
            {
                TranslationEngine e = opt.Chain[i];
                if (e == null) continue;
                string reason;
                if (!why.TryGetValue(e, out reason) || string.IsNullOrEmpty(reason))
                    // It answered every time and the answer failed a check, which is
                    // a different thing from refusing and has to read differently.
                    reason = last != null && last.Ok ? Describe(lastIssues) : "no reason given";
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(e.DisplayName).Append(": ").Append(reason);
            }
            return sb.Length == 0 ? "" : sb.ToString();
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
        /// <para><b>AND FLAT IS THE RIGHT TARGET, for a reason that is not
        /// typographic</b> (Gordan, 2026-09-02). Splitting the rules per language
        /// made the model start writing each language's own marks -- Croatian
        /// low-9 quotes, French guillemets -- and the instinct was to keep them and
        /// normalise to those instead. He turned it down on the one ground that
        /// outranks print convention here: <b>this book is read ALOUD</b>, and a
        /// speech engine meets an unusual character however it likes -- reads it,
        /// ignores it, or announces something like "char2334" when its encoding or
        /// its inventory does not cover it. The straight ASCII pair is the one every
        /// engine handles. Same class of problem as the Private Use Area characters
        /// TextCleaner blanks, and the same answer.</para>
        ///
        /// <para>The single family goes with it, to a straight apostrophe rather
        /// than a quote, since U+2019 is far more often an apostrophe than a
        /// quotation mark. Measured on two real translated books: 103 + 27 + 1 of
        /// them in one, 15 in the other, against zero in the English source -- so
        /// the model introduces them, which is exactly what this method is for.</para>
        ///
        /// <para>This is the general lesson and it is worth taking further: <b>a
        /// rule that can be enforced afterwards should be, not requested.</b> An
        /// instruction is a request the model may honour; a substitution is a fact.
        /// Only the mechanical rules qualify — nothing here could decide a gender
        /// or a level of address, which is exactly why those still have to be
        /// asked for.</para></summary>
        private static string Tidy(string s)
        {
            s = s.Replace('„', '"')      // „
                 .Replace('“', '"')      // “
                 .Replace('”', '"')      // ”
                 .Replace('«', '"')      // «
                 .Replace('»', '"')      // »
                 .Replace('‚', '\'')     // ‚
                 .Replace('‘', '\'')     // ‘
                 .Replace('’', '\'')     // ’  also the curly apostrophe
                 .Replace('‹', '\'')     // ‹
                 .Replace('›', '\'');    // ›
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
        /// <para><b>THE KEY IS THE TEXT, NOT THE PROMPT — and that is a trap worth
        /// knowing before it costs an afternoon.</b> A cached piece comes back
        /// unchanged however much the SYSTEM prompt has moved: new language rules,
        /// a corrected glossary, a fixed narrator, all of it. Measured 2026-08-29:
        /// after four fixes to the prompt the book was re-translated and came back
        /// in <b>2.8 seconds with 0 requests</b>, every piece from cache, carrying
        /// exactly the text the old prompt had produced. Only DetectBible runs
        /// regardless, so a glossary fix shows and a wording fix does not.
        ///
        /// <para>That is the right default — the cache exists so a stop, a crash or
        /// a corrected line costs seconds instead of a book — but a change to the
        /// RULES means the cache has to go, and nothing says so. Delete
        /// <c>translation.cache</c> beside the book to force a real run.</para>
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
