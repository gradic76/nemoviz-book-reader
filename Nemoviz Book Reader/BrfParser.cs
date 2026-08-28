using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Electronic braille (.brf / .brl / .bra) — a file of braille CELLS, not text.
    /// Each byte is one cell in the standard "Braille ASCII" encoding; a form feed
    /// ends a braille page. The cells are turned into Unicode braille patterns and
    /// back-translated to readable text by <see cref="LibLouis"/>, so the book can be
    /// read aloud by TTS like any other text book.
    ///
    /// The catch: a .brf declares neither its language nor its grade (contracted or
    /// not) nor, for Croatian, which revision of the national standard it follows —
    /// and the same cell means different things across those (dots-126 is "lj" under
    /// the old Croatian standard but "(" under the 2020 one). So the table is chosen
    /// per book: auto-detected here, and meant to be overridable by the user.
    /// </summary>
    public class BrfParser : ITextFormatParser
    {
        /// <summary>Braille ASCII: index = the cell's dot bitmask (dot1=1 … dot6=32),
        /// value = the byte used for it in a .brf.</summary>
        private const string BrailleAscii =
            " A1B'K2L@CIF/MSP\"E3H9O6R^DJG>NTQ,*5<-U8V.%[$+X!&;:4\\0Z7(_?W]#Y)=";

        // byte → dot bitmask (0..63), or -1 when the byte isn't a braille cell.
        private static readonly int[] CellOfByte = BuildCellMap();

        public bool Handles(string extension)
        {
            // .i55 is braille ASCII under another producer's extension — measured
            // at 98.3% clean cells across the samples, the same shape as any .brf.
            return extension == ".brf" || extension == ".brl"
                || extension == ".bra" || extension == ".i55";
        }

        /// <summary>Is this really braille ASCII, or something else wearing the
        /// extension?
        ///
        /// <para><b>Why a file has to earn it.</b> <c>.brl</c> is used both for
        /// plain braille ASCII and for Braillo Text, an embosser stream with a
        /// header line and control codes. Fed to this parser the Braillo files
        /// measure <b>64% of bytes that are not cells at all</b> — 79 870 of one
        /// byte alone — and the parser's habit of skipping what it does not
        /// recognise turned the surviving third into fluent-looking nonsense. A
        /// reader has no way to tell that from a badly transcribed book.</para>
        ///
        /// <para>Genuine files are nowhere near the line: the samples measure
        /// 0.00% to 1.74% strays. Twenty per cent is a wide moat, and refusing is
        /// the honest answer — better no book than a book that says the wrong
        /// thing with confidence.</para></summary>
        private static bool LooksLikeBraille(byte[] bytes)
        {
            // Known formats are named, not guessed at. Measured across 86 sample
            // files, the proportion test alone CANNOT separate these from braille:
            // genuine .brf runs from 0.00% up to 13.96% strays, and Duxbury starts
            // at 2.95%. The ranges overlap, so a threshold that refused Duxbury
            // would refuse real books too. A signature does not have that problem.
            if (StartsWith(bytes, 0xFF, 'D', 'S', 'I')) return false;   // Duxbury .dxb
            if (StartsWith(bytes, 'B', 'r', 'a', 'i', 'l', 'l', 'o', ' ',
                                  'T', 'e', 'x', 't')) return false;    // Braillo Text

            int cells = 0, strays = 0;
            foreach (byte b in bytes)
            {
                if (b == 10 || b == 13 || b == 12 || b == 26) continue;
                cells++;
                if (CellOfByte[b] < 0) strays++;
            }
            if (cells < 64) return false;
            return strays * 100 / cells < 20;
        }

        private static bool StartsWith(byte[] bytes, params int[] signature)
        {
            if (bytes.Length < signature.Length) return false;
            for (int i = 0; i < signature.Length; i++)
                if (bytes[i] != (byte)signature[i]) return false;
            return true;
        }

        public TextDoc Parse(string filePath)
        {
            return Parse(filePath, null);
        }

        /// <summary>Parses with an explicit table id (from the book's settings);
        /// null auto-detects.</summary>
        public TextDoc Parse(string filePath, string tableId)
        {
            try { return ParseBytes(File.ReadAllBytes(filePath), tableId); }
            catch { return new TextDoc(); }
        }

        /// <summary>The braille pipeline, from bytes rather than from a path.
        ///
        /// <para>Exposed so a container format can hand over the braille it was
        /// carrying — Duxbury wraps ordinary braille ASCII in a binary envelope
        /// with markup, and once that is off, what is left is a .brf in every
        /// respect. Re-implementing the cell mapping, the page splitting, the
        /// table detection and the box-frame handling for each such wrapper is
        /// how they come to disagree with each other.</para></summary>
        internal TextDoc ParseBytes(byte[] bytes, string tableId)
        {
            var doc = new TextDoc();
            try
            {
                if (bytes == null || bytes.Length == 0) return doc;
                // Null, not an empty book: "I cannot read this" and "this book is
                // empty" are different answers and only one of them is true.
                if (!LooksLikeBraille(bytes)) return null;

                // Split into braille pages (form feed) of cell-lines.
                List<List<string>> pages = ToBraillePages(bytes);
                if (pages.Count == 0) return doc;

                BrailleTableInfo table = BrailleTables.ById(tableId) ?? Detect(pages);
                if (table == null) return doc;    // liblouis unavailable → no text

                // TRANSLATE FIRST, ASSEMBLE SECOND. The two used to be one pass,
                // which left nowhere to stand and look at the book as pages: a
                // running head can only be recognised by seeing that the same line
                // opens most of them (see RunningHeads), and by the time the old
                // loop had a line in hand it had already appended the ones before.
                var text = new List<List<string>>();
                char rail = '\0';   // side-rail character of a box we're inside
                foreach (List<string> page in pages)
                {
                    var outLines = new List<string>();
                    foreach (string cells in page)
                    {
                        string line = LibLouis.BackTranslate(cells, table.File);
                        if (line == null) continue;
                        line = StripUntranslated(line).TrimEnd();
                        if (IsDecorative(line))
                        {
                            // A box side-rail is a lone character; remember it so the
                            // framed line between the rails can shed its prefix.
                            string bare = line.Trim();
                            rail = bare.Length == 1 ? bare[0] : '\0';
                            continue;
                        }
                        // Blank lines are kept: they carry the paragraph breaks the
                        // reader needs for pacing.
                        outLines.Add(StripRail(line, rail));
                    }
                    text.Add(outLines);
                }

                // The producer's furniture — the running title and the page number
                // at the head or foot of every page. Left in, it is spliced into
                // whatever sentence spans the page break, and reads as words thrown
                // in at random.
                RunningHeads.Strip(text);

                var sb = new StringBuilder();
                var pageMarks = new List<(string Label, int Offset)>();
                for (int p = 0; p < text.Count; p++)
                {
                    bool pageHasText = false;
                    foreach (string line in text[p])
                    {
                        if (!pageHasText)
                        {
                            pageMarks.Add(((p + 1).ToString(), sb.Length));
                            pageHasText = true;
                        }
                        sb.Append(line).Append('\n');
                    }
                }

                doc.Text = sb.ToString().TrimEnd('\n');
                // Page markers only mean something when the file actually paginates
                // (some producers ship one continuous stream with no form feeds).
                doc.Pages = pageMarks.Count > 1 ? pageMarks : new List<(string, int)>();
                // NOT into Producer any more (2026-08-04). The table used to be
                // written there so the reader could see it at all, back when
                // nothing else showed it. Properties now has a row of its own —
                // "Input Braille Table" — so writing it here as well put the same
                // fact on the glass twice under two names, one of them wrong:
                // Producer means who made the recording or the edition, and
                // "English (British) — contracted" is not a producer.
                // Gordan spotted it in the info box.
                doc.BrailleTable = table.Id;
            }
            catch { return new TextDoc(); }
            return doc;
        }

        // ── Cells ────────────────────────────────────────────────────────────
        private static int[] BuildCellMap()
        {
            var map = new int[256];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            for (int dots = 0; dots < BrailleAscii.Length; dots++)
            {
                char c = BrailleAscii[dots];
                map[c] = dots;
                // .brf files are conventionally uppercase, but accept lowercase too.
                //
                // THE SHIFT IS 0x40..0x5E, NOT JUST A..Z (fixed 2026-08-04). Braille
                // ASCII proper is 0x20..0x5F; a file written in the lowercase
                // convention carries the whole upper half shifted by 0x20, which is
                // A..Z but ALSO @ [ \ ] ^ arriving as ` { | } ~. Only the letters
                // were being accepted, so five cells were dropped without a sound.
                //
                // It reads as a table problem, which is how it was filed (§10g: the
                // English sample "reads Have can a man be Born Again", blamed on
                // UEB vs EBAE). It is not. The file writes the title as ",h{ …":
                // dropping the { leaves a bare h, and a bare h is the word sign for
                // "have" in EVERY English grade-2 standard — so all three of them
                // agreed on the wrong word and the table looked guilty. Measured
                // across the samples, 13 files lose cells this way, up to 3.34% of
                // one Korean book and 1.37% of the English one; §10g's "stray bytes
                // not yet mapped" (0x60 in the French integrals, 0x7C in an
                // abridged) are the same bug seen from the other end, and its guess
                // that they were 8-dot cells was wrong.
                //
                // Additive and therefore safe, by the same argument AddLatin1Cells
                // makes below: these bytes map to nothing today, so nothing that
                // parses now can parse differently after it.
                if (c >= '@' && c <= '^') map[(char)(c + 0x20)] = dots;
            }
            AddLatin1Cells(map);
            return map;
        }

        /// <summary>Adds the accented letters that French braille files write as
        /// themselves, taken from the liblouis table NBR already ships.
        ///
        /// <para><b>Why they were being lost.</b> Braille ASCII is a 7-bit
        /// convention, and a byte outside it is skipped as "not a cell". French
        /// producers write é, è, à, ê, ç and the diaeresis as the Latin-1
        /// characters themselves, so those cells were silently dropped — measured
        /// at <b>5.0% of every cell</b> in the sample files, 19 068 of them in one
        /// book, and the reader would never know: the text simply came out with
        /// its accented letters missing.</para>
        ///
        /// <para><b>Read, not invented.</b> The dot patterns come out of
        /// <c>fr-bfu-comp6.utb</c>, the French table already in
        /// <c>louis\tables</c> — é is 123456, è is 2346, à is 12356, ê is 126,
        /// the diaeresis 46. Writing those from memory is exactly the kind of
        /// thing that looks right and is not.</para>
        ///
        /// <para><b>Safe for every other file.</b> These are bytes 0x80 and up,
        /// which braille ASCII never uses: the American, Vietnamese and
        /// upper-case samples measured zero of them. Nothing that parses today
        /// can parse differently tomorrow.</para></summary>
        private static void AddLatin1Cells(int[] map)
        {
            try
            {
                string path = LibLouis.TablePath("fr-bfu-comp6.utb");
                if (path == null || !File.Exists(path)) return;
                var rx = new System.Text.RegularExpressions.Regex(
                    @"^\s*(?:base\s+)?(?:lowercase|uppercase|letter|sign|punctuation|math)\s+\\x00([0-9A-Fa-f]{2})\s+([0-8]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (string line in File.ReadAllLines(path))
                {
                    var m = rx.Match(line);
                    if (!m.Success) continue;
                    int b = Convert.ToInt32(m.Groups[1].Value, 16);
                    if (b < 0x80 || b > 0xFF || map[b] >= 0) continue;   // never overwrite ASCII
                    int dots = 0;
                    bool ok = true;
                    foreach (char d in m.Groups[2].Value)
                    {
                        if (d < '1' || d > '6') { ok = false; break; }   // 7/8-dot: not our cells
                        dots |= 1 << (d - '1');
                    }
                    if (ok && dots > 0) map[b] = dots;
                }
            }
            catch { }   // a missing table is a file that keeps parsing as it did
        }

        /// <summary>Converts the file to pages of lines, each line a string of Unicode
        /// braille patterns. CR/LF end a line, form feed (0x0C) ends a page.</summary>
        private static List<List<string>> ToBraillePages(byte[] bytes)
        {
            int[] cellOf = UsesFrenchConvention(bytes) ? FrenchCellOfByte : CellOfByte;
            var pages = new List<List<string>>();
            var page = new List<string>();
            var line = new StringBuilder();

            Action endLine = () => { page.Add(line.ToString()); line.Clear(); };
            Action endPage = () =>
            {
                if (line.Length > 0) endLine();
                if (page.Count > 0) { pages.Add(page); page = new List<string>(); }
            };

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b == 0x0D) continue;                 // CR — the LF ends the line
                if (b == 0x0A) { endLine(); continue; }
                if (b == 0x0C) { endPage(); continue; }
                int dots = cellOf[b];
                if (dots < 0) continue;                  // stray byte — skip
                line.Append((char)(0x2800 + dots));
            }
            endPage();
            return pages;
        }

        /// <summary>The same cells written the French way.
        ///
        /// <para>A French producer writes the PUNCTUATION as the printed character
        /// it stands for -- a full stop as ".", a comma as "," -- where braille
        /// ASCII writes them as "4" and "1". Nineteen bytes differ, and reading a
        /// French file with the ordinary map turns every one of them into some
        /// other cell: measured, the six French-convention books in the corpus came
        /// out with ZERO full stops and ZERO commas, against 9 to 16 per thousand
        /// characters in their own North-American twins. A book with no sentence
        /// boundaries is read by TTS as one unbroken run, and the sentence
        /// navigation collapses with it.</para>
        ///
        /// <para><b>Derived, not written from memory.</b> The Valentin Hauy library
        /// ships each title in BOTH conventions, so the two files are the same book
        /// byte for byte apart from this. Aligning them line by line -- 11 628
        /// comparable lines -- yields the mapping directly, and all six pairs
        /// (three titles x two grades) produce exactly the same 34 pairs with no
        /// byte mapping two ways. Fourteen of the 34 are the accented letters
        /// AddLatin1Cells already handles, and every one of those AGREES with the
        /// alignment, which is an independent check on that work. The one byte it
        /// did not know, 0xA4, is the last of the brief's "stray byte not yet
        /// mapped".</para></summary>
        private static readonly int[] FrenchCellOfByte = BuildFrenchCellMap();

        private static int[] BuildFrenchCellMap()
        {
            var map = (int[])CellOfByte.Clone();

            // Left of the arrow: what a French file writes. Right: the braille
            // ASCII byte standing for the same cell. Taken from the alignment, and
            // expressed as a LOOKUP rather than as dot numbers, so this cannot
            // disagree with the map it is derived from.
            string[] pairs =
            {
                "!6", "\"7", "%+", "(8", ")0", "*9", ",1", ".4", "0#", "9[",
                ":3", ";2", ">;", "?5", "@>", "^@", "_\"", "`,", "|_",
            };
            foreach (string p in pairs) map[p[0]] = CellOfByte[p[1]];

            map[0xA4] = CellOfByte['^'];   // the byte AddLatin1Cells did not know
            return map;
        }

        /// <summary>The bytes a French producer uses for the accented cells. A file
        /// whose high bytes are ALL from this set is written in that convention;
        /// one whose high bytes are spread more widely is something else in a code
        /// page -- the Braillo samples are Cyrillic under 1251 and reach only 43 to
        /// 45 % of this set, against 100.00 % for every French file measured.</summary>
        private static readonly bool[] FrenchHighByte = BuildFrenchHighBytes();

        private static bool[] BuildFrenchHighBytes()
        {
            var f = new bool[256];
            foreach (int b in new[] { 0xA4, 0xA8, 0xE0, 0xE2, 0xE7, 0xE8, 0xE9, 0xEA,
                                      0xEB, 0xEE, 0xEF, 0xF4, 0xF9, 0xFB, 0xFC })
                f[b] = true;
            return f;
        }

        /// <summary>True when the file writes its cells the French way.
        ///
        /// <para>Measured over 93 braille files: the six French-convention books
        /// score <b>100.00 %</b>, the five Braillo files 43 to 45 %, and one English
        /// book has a single high byte and scores 0. The minimum count is there so
        /// that one stray byte cannot decide a whole book -- the smallest genuine
        /// file carries 2 037 of them.</para></summary>
        private static bool UsesFrenchConvention(byte[] bytes)
        {
            int high = 0, french = 0;
            foreach (byte b in bytes)
            {
                if (b < 0xA0) continue;
                high++;
                if (FrenchHighByte[b]) french++;
            }
            return high >= 50 && french >= high * 0.95;
        }

        /// <summary>Removes cells liblouis had no text for -- formatting indicators
        /// (emphasis, producer marks) that would otherwise reach TTS as raw braille
        /// characters, or as liblouis's own spelling of them.
        ///
        /// <para><b>Two shapes, one meaning.</b> A cell that survives untranslated
        /// arrives either as the braille character itself (U+2800..U+28FF) or, more
        /// often, in liblouis's escape notation -- a backslash, the dot numbers and
        /// a slash, so the UEB italic indicator comes through as \46/. Both mean
        /// "this cell has no text", and a reader hears the second one spoken out as
        /// "backslash four six slash".
        ///
        /// <para><b>Why it was not worth doing until now.</b> Measured over 88
        /// braille books BEFORE the round-trip refinement, the escape notation
        /// appeared 2 997 times -- because the wrong table was usually winning, and
        /// a wrong table does not fail honestly: it reads an indicator cell as a
        /// contraction and produces a word. With the right table chosen the count is
        /// 36 680, so honest failure is now visible where silent damage used to be,
        /// and this is what turns it back into text.
        ///
        /// <para><b>Safe by construction.</b> The notation is liblouis's own and
        /// cannot occur in a book: nothing writes a backslash, digits and a slash
        /// with no spaces in running prose. It is dropped rather than replaced,
        /// because what it marks is emphasis, which nothing downstream can
        /// render.</summary>
        private static string StripUntranslated(string line)
        {
            if (line.IndexOf((char)92) >= 0) line = Untranslated.Replace(line, "");
            var sb = new StringBuilder(line.Length);
            foreach (char c in line)
                if (c < 0x2800 || c > 0x28FF) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Sheds the left rail of a title box from the line it frames, so
        /// "l⇥SMOGOVCI" reads as "SMOGOVCI".</summary>
        private static string StripRail(string line, char rail)
        {
            if (rail == '\0') return line;
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || line[i] != rail) return line;
            int j = i + 1;
            if (j >= line.Length || !char.IsWhiteSpace(line[j])) return line;
            return line.Substring(0, i) + line.Substring(j);
        }

        /// <summary>Drops ornamental rules and title boxes, which carry no reading
        /// content but would be spoken. A box border back-translates to a run of one
        /// repeated character ("pccccccccccccđ", "v-----------"), whatever that
        /// character happens to be — so the test is repetition, not punctuation. A
        /// line left with a single stray character is a box side-rail.</summary>
        private static bool IsDecorative(string line)
        {
            string t = line.Trim();
            if (t.Length == 0) return false;
            if (t.Length == 1) return !char.IsDigit(t[0]);   // side-rail remnant

            if (t.Length < 5) return false;
            int best = 0;
            var counts = new Dictionary<char, int>();
            foreach (char c in t)
            {
                if (char.IsWhiteSpace(c)) continue;
                int n = counts.TryGetValue(c, out int prev) ? prev + 1 : 1;
                counts[c] = n;
                if (n > best) best = n;
            }
            int solid = 0;
            foreach (char c in t) if (!char.IsWhiteSpace(c)) solid++;
            if (solid < 5) return false;
            return best * 10 >= solid * 6;   // ≥60 % one and the same character
        }

        // ── Table auto-detection ─────────────────────────────────────────────
        // A .brf says nothing about its language/grade/standard, so try each table on
        // a sample and keep the most plausible reading. This is a heuristic: the user
        // is the authority and can override it per book.
        private static BrailleTableInfo Detect(List<List<string>> pages)
        {
            if (!LibLouis.Available) return null;

            string sample = Sample(pages);
            if (sample.Length == 0) return null;

            // Structural signal first: the cells the 2020 Croatian standard freed
            // (single-cell nj / dž) can only appear in an older Croatian file.
            bool oldCroatianCells = false;
            foreach (char c in sample)
            {
                int dots = c - 0x2800;
                if (dots == 0x2B /*1246 nj*/ || dots == 0x3B /*12456 dž*/) { oldCroatianCells = true; break; }
            }

            BrailleTableInfo best = null;
            double bestScore = double.MinValue;
            foreach (BrailleTableInfo t in BrailleTables.All)
            {
                string text = LibLouis.BackTranslate(sample, t.File);
                if (string.IsNullOrEmpty(text)) continue;
                double score = Plausibility(text, t.Id);
                if (t.Id.StartsWith("hr-", StringComparison.Ordinal))
                {
                    // Prefer the revision matching the structural signal.
                    bool isOld = t.Id == "hr-old";
                    score += (isOld == oldCroatianCells) ? 0.25 : -0.25;
                }
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return RefineStandard(sample, best);
        }

        /// <summary>How much better a same-language table must round-trip before it
        /// overrides the plausibility winner, and how well it must do in absolute
        /// terms. Both bars come from the corpus, not from taste.</summary>
        private const double MinAdvantage = 6.0;
        private const double MinAgreement = 70.0;

        /// <summary>Plausibility chooses the LANGUAGE; this chooses the STANDARD and
        /// the GRADE, which plausibility measurably cannot.
        ///
        /// <para><b>Why a second stage at all.</b> Reading a UEB book with an EBAE
        /// table scores HIGHER, and not by a little: on <c>1670702.brf</c> (The
        /// Yield, produced by the Australian Braille Writing Association, and
        /// Australia has been UEB since 2005) EBAE won by 0.032. The scorer is not
        /// merely blind here, it is biased -- EBAE expands UEB indicator cells into
        /// contractions, so it produces MORE letters and MORE common English words
        /// than the correct table, and the letter and stopword terms both reward it
        /// for the damage. Measured: with the junk term removed entirely the wrong
        /// table still wins, so no reweighting of the existing terms can fix it.
        ///
        /// <para><b>What the round trip asks.</b> Cells to text and back to cells
        /// with the same table, compared as MULTISETS OF WORDS. The wrong table has
        /// nowhere to hide: the book spells "by" out in full and EBAE writes it as
        /// one cell, so that word cannot come back. Read off the shipped tables
        /// rather than from memory -- "by the way" is <c>BY ! WAY</c> under UEB and
        /// <c>0! WAY</c> under EBAE; likewise "table" (TABLE / TA#) and "o'clock"
        /// (O'CLOCK / O'C).</para>
        ///
        /// <para><b>Words, not cells.</b> A cell-by-cell comparison was tried first
        /// and is not usable: it depends on exactly how the <c>\NNN/</c> escapes are
        /// stripped, and changing that detail flipped the answer. Word multisets
        /// have no alignment to lose, so a mis-contracted word fails on its own
        /// instead of dragging everything after it down with it.</para>
        ///
        /// <para><b>Same language only, and this is load-bearing.</b> An
        /// uncontracted table is close to an identity map -- cells to letters and
        /// straight back -- so it round-trips at 95% and better on any file
        /// whatever, in any language. Measured over 93 files, letting the round trip
        /// choose freely handed 44 books to Croatian, Thai and Korean ones included.
        /// It answers "does this table explain these cells"; it has no opinion about
        /// what language they are in, and must not be asked for one.</para>
        ///
        /// <para><b>The two bars.</b> Both measured. The Valentin Hauy library ships
        /// the same title contracted and uncontracted, and the uncontracted editions
        /// win by <b>+63 to +73 points</b> while the contracted ones are
        /// mis-preferred by at most +4.8 -- so an advantage of 6 separates them with
        /// room to spare. The absolute bar catches the rest: the Ukrainian and
        /// Korean files have no table here at all, and their best reaches only
        /// 61-65%, where every genuine correction lands at 70% or better and the
        /// English ones at 92-99%.</para>
        ///
        /// <para><b>What it changes, over 93 files:</b> every book recorded in the
        /// brief as carrying untranslated markers moves to UEB, and the French
        /// uncontracted editions move to grade 1 -- which is also the brief's "Hauy
        /// comes out Haouy". The contracted editions stay where they were.</para>
        /// </summary>
        private static BrailleTableInfo RefineStandard(string sample, BrailleTableInfo best)
        {
            if (best == null || string.IsNullOrEmpty(sample)) return best;

            List<string> src = CellWords(sample);
            if (src.Count < 40) return best;   // too little to judge; leave it alone

            double baseline = RoundTrip(sample, src, best);
            if (baseline <= 0) return best;

            BrailleTableInfo winner = best;
            double winning = baseline;
            foreach (BrailleTableInfo t in BrailleTables.All)
            {
                if (t == best || t.Language != best.Language) continue;
                double a = RoundTrip(sample, src, t);
                if (a > winning) { winning = a; winner = t; }
            }

            if (winner == best) return best;
            if (winning < MinAgreement || winning - baseline < MinAdvantage) return best;
            return winner;
        }

        /// <summary>Back-translate the sample with this table, translate the result
        /// straight back, and report how much of it returned.</summary>
        private static double RoundTrip(string sample, List<string> src, BrailleTableInfo t)
        {
            string text = LibLouis.BackTranslate(sample, t.File);
            if (string.IsNullOrEmpty(text)) return 0;

            // liblouis writes a cell it cannot back-translate as \<dots>/. Forward
            // translating that SPELLING would turn one cell into half a dozen, so
            // the escapes come out and the words around them are judged instead.
            text = Untranslated.Replace(text, "");

            string cells = LibLouis.Translate(text, t.File);
            if (string.IsNullOrEmpty(cells)) return 0;
            return WordAgreement(src, CellWords(cells));
        }

        private static readonly Regex Untranslated = new Regex(@"\\\d+/", RegexOptions.Compiled);

        /// <summary>The cells as whitespace-separated words, written in braille
        /// ASCII whichever way the table chose to display them -- the English and
        /// French tables emit braille ASCII, the Croatian ones Unicode cells, and
        /// they are the same dots either way.</summary>
        private static List<string> CellWords(string s)
        {
            var words = new List<string>();
            var w = new StringBuilder();
            foreach (char c in s)
            {
                int dots = -1;
                if (c >= 0x2800 && c <= 0x283F) dots = c - 0x2800;
                else if (c < 256 && CellOfByte[c] >= 0) dots = CellOfByte[c];

                if (dots > 0) { w.Append(BrailleAscii[dots]); continue; }
                // dots == 0 is the blank cell, i.e. a space; anything unmapped
                // (a stray byte, a line break) separates words just as well.
                if (w.Length > 0) { words.Add(w.ToString()); w.Length = 0; }
            }
            if (w.Length > 0) words.Add(w.ToString());
            return words;
        }

        /// <summary>Share of the source words that came back, compared as multisets
        /// so that nothing depends on alignment.</summary>
        private static double WordAgreement(List<string> src, List<string> got)
        {
            if (src.Count == 0 || got.Count == 0) return 0;
            var bag = new Dictionary<string, int>();
            foreach (string w in got)
            {
                int v;
                bag[w] = bag.TryGetValue(w, out v) ? v + 1 : 1;
            }
            int hit = 0;
            foreach (string w in src)
            {
                int v;
                if (bag.TryGetValue(w, out v) && v > 0) { bag[w] = v - 1; hit++; }
            }
            return 100.0 * hit / Math.Max(src.Count, got.Count);
        }

        private static string Sample(List<List<string>> pages)
        {
            var sb = new StringBuilder();
            foreach (List<string> page in pages)
            {
                foreach (string line in page)
                {
                    if (line.Length == 0) continue;
                    sb.Append(line).Append(' ');
                    if (sb.Length > 4000) return sb.ToString();
                }
            }
            return sb.ToString();
        }

        // Accented letters each language actually uses. Reading a file with the WRONG
        // table still yields letters, so the signal isn't "are my letters present" —
        // it's that a mis-decode sprays accents where the real language uses them
        // sparingly (a few per cent of letters at most).
        private const string CroatianMarks = "čćšžđČĆŠŽĐ";
        private const string FrenchMarks = "éèêàçùôîïûœÉÈÊÀÇÙ";

        // Punctuation a real book may contain, so it does not count against the
        // table that produced it. The dashes, the curly quotes and the asterisk
        // are here because a table that back-translates into proper typography
        // was being PENALISED for doing so at 3 points a character: measured
        // over 88 braille books, 22 344 legitimate characters were being
        // charged as junk.
        //
        // It changes NO book's detected table on that corpus -- verified by
        // running the real, sample-based Detect over all 88 before and after.
        // It is correctness, not the repair -- the wrong-table faults recorded in
        // the brief are fixed by RefineStandard below, not by reweighting a term.
        private const string Punctuation =
            ".,;:!?-'\"()[]«»…"   // as before
          + "—–"                 // em dash, en dash
          + "‘’“”‚„"             // curly quotes, single and double
          + "*";                 // a scene break, "* * *"

        // Everyday words that dominate ordinary prose in each supported language.
        private static readonly HashSet<string> HrStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "i","je","se","na","da","u","za","od","su","to","ne","li","ali","pa","kad","bi","sam","kao","po","s","iz","ga","me","mu","ja","ti","ali","već" };
        private static readonly HashSet<string> EnStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the","and","of","to","a","in","is","that","it","was","for","with","he","she","as","on","at","but","his","her","had","not","you","have","this" };
        private static readonly HashSet<string> FrStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "le","la","les","de","des","du","et","un","une","est","que","qui","pour","dans","en","il","elle","ne","pas","se","sur","au","aux","ce","vous","je" };

        private static readonly HashSet<string> PtStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "de","a","o","que","e","do","da","em","um","para","com","não","uma","os","no","se","na","por","mais","as","dos","como","mas","ao","ele","das","seu","sua","ou","quando" };

        /// <summary>Share of words that are everyday words of the table's language.</summary>
        private static double StopwordRate(string text, string tableId)
        {
            HashSet<string> stop = tableId.StartsWith("hr-", StringComparison.Ordinal) ? HrStop
                                 : tableId.StartsWith("pt-", StringComparison.Ordinal) ? PtStop
                                 : tableId.StartsWith("fr-", StringComparison.Ordinal) ? FrStop
                                 : EnStop;
            int words = 0, hits = 0;
            var word = new StringBuilder();
            Action flush = () =>
            {
                if (word.Length == 0) return;
                words++;
                if (stop.Contains(word.ToString())) hits++;
                word.Clear();
            };
            foreach (char c in text)
            {
                if (char.IsLetter(c)) word.Append(c);
                else flush();
            }
            flush();
            return words < 20 ? 0.0 : (double)hits / words;
        }

        private static double Plausibility(string text, string tableId)
        {
            if (text.Length == 0) return double.MinValue;
            string marksOf = tableId.StartsWith("hr-", StringComparison.Ordinal) ? CroatianMarks
                           : tableId.StartsWith("fr-", StringComparison.Ordinal) ? FrenchMarks
                           : "";

            int letters = 0, leftover = 0, junk = 0, marks = 0, midCaps = 0;
            bool inWord = false, prevLower = false;
            foreach (char c in text)
            {
                if (c >= 0x2800 && c <= 0x28FF) { leftover++; inWord = false; prevLower = false; continue; }
                if (char.IsLetter(c))
                {
                    letters++;
                    if (marksOf.IndexOf(c) >= 0) marks++;
                    // A capital inside a word ("eldDŽly") is a hallmark of a mis-decode.
                    if (inWord && prevLower && char.IsUpper(c)) midCaps++;
                    prevLower = char.IsLower(c);
                    inWord = true;
                    continue;
                }
                inWord = false; prevLower = false;
                if (char.IsWhiteSpace(c) || char.IsDigit(c)) continue;
                if (Punctuation.IndexOf(c) < 0) junk++;
            }
            if (letters == 0) return double.MinValue;

            double n = text.Length;
            double score = (letters - 6.0 * leftover - 3.0 * junk - 4.0 * midCaps) / n;

            // The decisive signal: how much of the output is made of this language's
            // own everyday words. Real prose runs a quarter to a third stopwords;
            // text decoded with the wrong table scores a small fraction of that.
            //
            // WEIGHT 6, NOT 3, since 2026-08-28, and the reason is the term above
            // it. letters/n rewards a table for turning cells into letters whether
            // or not the letters mean anything, and that bias was demonstrated
            // twice in one session: EBAE beat UEB on a UEB book because it expands
            // indicator cells into contractions, and it beat Portuguese on a
            // Portuguese one by producing 3 065 letters where the right table
            // produced 1 780. The stopword rate is the only term that knows what
            // language it is looking at, so the balance moved toward it.
            //
            // Six is the smallest value that fixes the known miss: 4 and 5 change
            // nothing at all, and 7 starts moving books whose language has no table
            // here around between equally wrong ones. Measured end to end over 88
            // braille books, weight 6 changes exactly three: 4147bd_001.BRF finds
            // Portuguese, and a Thai and a Korean book -- neither of which has a
            // table in the tried set -- move between two wrong answers. Croatian,
            // French and English are untouched. The value is chosen against that
            // corpus and should be re-measured if the corpus grows.
            score += 6.0 * StopwordRate(text, tableId);

            // Accent rate: a little is expected for languages that have them; a lot
            // means this table is painting another language's accents over the text.
            double rate = (double)marks / letters;
            if (marksOf.Length > 0)
            {
                if (rate > 0.045) score -= (rate - 0.045) * 12.0;   // implausibly accent-heavy
                else score += rate * 2.0;                            // plausible: mild support
            }
            else if (rate > 0) { /* no marks defined for this language */ }
            return score;
        }
    }
}
