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
            return extension == ".brf" || extension == ".brl" || extension == ".bra";
        }

        public TextDoc Parse(string filePath)
        {
            return Parse(filePath, null);
        }

        /// <summary>Parses with an explicit table id (from the book's settings);
        /// null auto-detects.</summary>
        public TextDoc Parse(string filePath, string tableId)
        {
            var doc = new TextDoc();
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes.Length == 0) return doc;

                // Split into braille pages (form feed) of cell-lines.
                List<List<string>> pages = ToBraillePages(bytes);
                if (pages.Count == 0) return doc;

                BrailleTableInfo table = BrailleTables.ById(tableId) ?? Detect(pages);
                if (table == null) return doc;    // liblouis unavailable → no text

                var sb = new StringBuilder();
                var pageMarks = new List<(string Label, int Offset)>();
                int pageNo = 0;
                foreach (List<string> page in pages)
                {
                    pageNo++;
                    bool pageHasText = false;
                    foreach (string cells in page)
                    {
                        string line = LibLouis.BackTranslate(cells, table.File);
                        if (line == null) continue;
                        line = line.TrimEnd();
                        if (IsDecorative(line)) continue;
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
            return map;
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

        /// <summary>Drops rules and ornamental boxes ("=====", "PCCCC?") that carry no
        /// reading content: a run of one repeated non-alphanumeric character.</summary>
        private static bool IsDecorative(string line)
        {
            string t = line.Trim();
            if (t.Length < 5) return false;
            char first = t[0];
            if (char.IsLetterOrDigit(first)) return false;
            int same = 0;
            foreach (char c in t) if (c == first) same++;
            return same * 10 >= t.Length * 7;   // ≥70 % the same character
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
