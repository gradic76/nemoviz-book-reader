using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

                var sb = new StringBuilder();
                var pageMarks = new List<(string Label, int Offset)>();
                int pageNo = 0;
                char rail = '\0';   // side-rail character of a box we're inside
                foreach (List<string> page in pages)
                {
                    pageNo++;
                    bool pageHasText = false;
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
                        line = StripRail(line, rail);
                        if (!pageHasText)
                        {
                            pageMarks.Add((pageNo.ToString(), sb.Length));
                            pageHasText = true;
                        }
                        sb.Append(line).Append('\n');
                    }
                }

                doc.Text = sb.ToString().TrimEnd('\n');
                // Page markers only mean something when the file actually paginates
                // (some producers ship one continuous stream with no form feeds).
                doc.Pages = pageMarks.Count > 1 ? pageMarks : new List<(string, int)>();
                doc.Producer = table.Display;   // surfaced so the user sees which table was used
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
                if (c >= 'A' && c <= 'Z') map[char.ToLowerInvariant(c)] = dots;
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
                int dots = CellOfByte[b];
                if (dots < 0) continue;                  // stray byte — skip
                line.Append((char)(0x2800 + dots));
            }
            endPage();
            return pages;
        }

        /// <summary>Removes cells liblouis had no text for — formatting indicators
        /// (emphasis, producer marks) that would otherwise reach TTS as raw braille
        /// characters.</summary>
        private static string StripUntranslated(string line)
        {
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
            return best;
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

        // Everyday words that dominate ordinary prose in each supported language.
        private static readonly HashSet<string> HrStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "i","je","se","na","da","u","za","od","su","to","ne","li","ali","pa","kad","bi","sam","kao","po","s","iz","ga","me","mu","ja","ti","ali","već" };
        private static readonly HashSet<string> EnStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the","and","of","to","a","in","is","that","it","was","for","with","he","she","as","on","at","but","his","her","had","not","you","have","this" };
        private static readonly HashSet<string> FrStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "le","la","les","de","des","du","et","un","une","est","que","qui","pour","dans","en","il","elle","ne","pas","se","sur","au","aux","ce","vous","je" };

        /// <summary>Share of words that are everyday words of the table's language.</summary>
        private static double StopwordRate(string text, string tableId)
        {
            HashSet<string> stop = tableId.StartsWith("hr-", StringComparison.Ordinal) ? HrStop
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
                if (".,;:!?-'\"()[]«»…".IndexOf(c) < 0) junk++;
            }
            if (letters == 0) return double.MinValue;

            double n = text.Length;
            double score = (letters - 6.0 * leftover - 3.0 * junk - 4.0 * midCaps) / n;

            // The decisive signal: how much of the output is made of this language's
            // own everyday words. Real prose runs a quarter to a third stopwords;
            // text decoded with the wrong table scores a small fraction of that.
            score += 3.0 * StopwordRate(text, tableId);

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
