using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nemoviz_Book_Reader
{
    internal enum MatterKind
    {
        /// <summary>The book. Translated.</summary>
        Prose,
        /// <summary>Not prose and standing at one end of the book — a cover, a
        /// title page, an imprint, a page-number index. Kept, not translated.</summary>
        Matter,
        /// <summary>A table of contents. Kept only when the book has no headings of
        /// its own; see <see cref="Find"/>.</summary>
        Contents
    }

    internal sealed class MatterBlock
    {
        public MatterKind Kind;
        public string Text;
        public int Start;
    }

    /// <summary>
    /// What at the front and back of a book is not the book.
    ///
    /// <para><b>The axis is KIND, not position</b> (Gordan, 2026-08-15). A preface
    /// and a copyright page both stand before chapter one; an afterword and an
    /// advertisement both stand after the epilogue. One of each pair is the author's
    /// own writing and must be translated, so "everything before the first chapter"
    /// is not a rule, it is a way of losing a preface.</para>
    ///
    /// <para><b>Where a format DECLARES the kind, that is the answer</b> — EPUB 3
    /// writes <c>epub:type</c> (<c>cover</c>, <c>copyright-page</c>, <c>preface</c>,
    /// <c>bodymatter</c>, <c>afterword</c>…), EPUB 2 has <c>guide</c>, DAISY its
    /// own. <b>That layer is not here and cannot be</b>: by the time a book reaches
    /// translation it is <c>content.txt</c>, one flat text, and the declaration was
    /// spent at import. Wiring it through is real work in the parsers and is written
    /// down as owed rather than pretended.</para>
    ///
    /// <para><b>So this is the second layer, the one that works on any format</b> —
    /// and it has to exist regardless, because declarations lie (a measured 17 % of
    /// <c>dc:language</c> values are wrong) and because plain text, RTF, Word and
    /// braille declare nothing at all.</para>
    ///
    /// <para><b>The rule above every other rule: WHEN IN DOUBT, TRANSLATE.</b>
    /// Translating a copyright page costs a few cents. Dropping a preface loses the
    /// author's words and the reader never finds out. The cost is asymmetric, so
    /// every test below is written to fail towards prose.</para>
    /// </summary>
    internal static class BookMatter
    {
        /// <summary>Cuts the book into blocks and says which of them are not the
        /// book. Only an UNBROKEN run of non-prose at each end is claimed: the first
        /// real paragraph from either side ends the run, so a preface or an
        /// afterword stops the walk rather than being swallowed by it.
        ///
        /// <para><paramref name="hasHeadings"/> decides what becomes of a table of
        /// contents. A printed contents list is PAPER navigation — its whole
        /// function is "turn to page 247" and there are no pages to turn to, while
        /// NBR's own Go To and heading step do the same job navigably. So it is
        /// dropped from the reading text. <b>The guard is that a book with no
        /// extracted headings has nothing else recording its structure</b>, and
        /// there the list stays.</para></summary>
        public static List<MatterBlock> Find(string text, bool hasHeadings)
        {
            var blocks = new List<MatterBlock>();
            if (string.IsNullOrEmpty(text)) return blocks;

            List<MatterBlock> paras = Paragraphs(text);
            if (paras.Count == 0) return blocks;

            // Walk in from each end while the paragraphs are not prose. The walk
            // stops at the first one that is, which is what protects a preface at
            // the front and an afterword at the back.
            int front = 0;
            while (front < paras.Count && !IsProse(paras[front].Text)) front++;

            int back = paras.Count - 1;
            while (back > front && !IsProse(paras[back].Text)) back--;

            // A book that is ENTIRELY non-prose by this test is a book the test does
            // not understand — a play, a book of verse, a dictionary. Translate all
            // of it rather than throw it away.
            if (front >= paras.Count || front > back) { MarkAll(paras, MatterKind.Prose); return paras; }

            for (int i = 0; i < paras.Count; i++)
            {
                bool isMatter = i < front || i > back;
                if (!isMatter) { paras[i].Kind = MatterKind.Prose; continue; }
                paras[i].Kind = hasHeadings && LooksLikeContents(paras[i].Text)
                    ? MatterKind.Contents
                    : MatterKind.Matter;
            }
            return paras;
        }

        private static void MarkAll(List<MatterBlock> list, MatterKind k)
        {
            foreach (var b in list) b.Kind = k;
        }

        /// <summary>Is this paragraph running text?
        ///
        /// <para>Every threshold here is set well clear of anything a real paragraph
        /// reaches, because the cost of a false "no" is a deleted piece of the book
        /// and the cost of a false "yes" is a few cents.</para></summary>
        public static bool IsProse(string s)
        {
            if (s == null) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;

            // Very short standalone lines carry no evidence either way — a chapter
            // title, a dedication of four words, "THE END". They are only ever
            // claimed as matter when they sit inside a run of other non-prose, and
            // the walk above is what decides that.
            if (t.Length < 40) return false;

            int letters = 0, digits = 0, other = 0;
            foreach (char c in t)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) digits++;
                else other++;
            }
            int solid = letters + digits + other;
            if (solid == 0) return false;

            // A RUN OF NUMBERS IS NOT PROSE, and this is the one that was measured.
            // The last chunk of a real novel carried 1488 digits in 2046 characters
            // — a printed contents list with its page numbers — and the length check
            // duly reported the model had truncated its answer, when there was
            // simply nothing there to translate. Real prose is nowhere near: a
            // page of dialogue with dates and ages in it stays under a twentieth.
            if (digits * 5 >= solid) return false;

            // Mostly punctuation or symbols: a rule of dashes, an ornament, a table.
            if (letters * 2 < solid) return false;

            // PROSE HAS SENTENCES. A contents list has one line per entry and
            // frequently not one full stop in the whole block; an imprint is a stack
            // of short declarations. Anything with a terminator every few hundred
            // characters counts, which is far looser than real writing (a novel runs
            // one per 60-120) and leaves long single-sentence paragraphs alone.
            int enders = 0;
            foreach (char c in t) if (c == '.' || c == '!' || c == '?' || c == '…') enders++;
            if (enders == 0 && t.Length > 120) return false;
            if (enders > 0 && t.Length / enders > 400) return false;

            return true;
        }

        /// <summary>A printed table of contents: many short lines, most of them
        /// ending in a number, or the word for "contents" over such a list.</summary>
        public static bool LooksLikeContents(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string[] lines = s.Replace("\r\n", "\n").Split('\n');
            int useful = 0, endsInNumber = 0, shortLines = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                useful++;
                if (line.Length <= 80) shortLines++;
                int i = line.Length - 1;
                while (i >= 0 && (line[i] == '.' || line[i] == ' ')) i--;
                if (i >= 0 && char.IsDigit(line[i])) endsInNumber++;
            }
            if (useful < 4) return false;
            // Either the lines are numbered like a contents list, or the whole block
            // is a stack of short entries with a great many digits in it.
            if (endsInNumber * 2 >= useful) return true;
            if (shortLines == useful)
            {
                int digits = 0;
                foreach (char c in s) if (char.IsDigit(c)) digits++;
                if (digits * 10 >= s.Length) return true;
            }
            return false;
        }

        /// <summary>Splits on blank lines, keeping each block's offset so nothing
        /// downstream has to find it again.</summary>
        private static List<MatterBlock> Paragraphs(string text)
        {
            var list = new List<MatterBlock>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && (text[i] == '\n' || text[i] == '\r')) i++;
                if (i >= text.Length) break;
                int start = i;
                int blank = -1;
                while (i < text.Length)
                {
                    if (text[i] == '\n')
                    {
                        int j = i + 1;
                        while (j < text.Length && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r')) j++;
                        if (j < text.Length && text[j] == '\n') { blank = i; break; }
                    }
                    i++;
                }
                int end = blank >= 0 ? blank : i;
                string body = text.Substring(start, end - start);
                if (body.Trim().Length > 0)
                    list.Add(new MatterBlock { Start = start, Text = body });
                i = end;
            }
            return list;
        }

        /// <summary>The three spans a book divides into, which is all a caller
        /// needs: what stands before the writing, the writing, and what stands
        /// after. There are only ever three because <see cref="Find"/> claims a
        /// single unbroken run at each end — non-prose in the MIDDLE of a book is a
        /// poem, a list, a letter, and is translated like everything else.</summary>
        public sealed class Split
        {
            public string Front = "";
            public string Body = "";
            public string Back = "";
            /// <summary>For the report. Null when nothing was set aside.</summary>
            public string Note;
        }

        public static Split Divide(string text, bool hasHeadings)
        {
            var s = new Split();
            List<MatterBlock> blocks = Find(text, hasHeadings);
            if (blocks.Count == 0) { s.Body = text ?? ""; return s; }

            int first = -1, last = -1;
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i].Kind == MatterKind.Prose) { if (first < 0) first = i; last = i; }
            if (first < 0) { s.Body = text ?? ""; return s; }

            var front = new StringBuilder();
            var back = new StringBuilder();
            for (int i = 0; i < first; i++)
                if (blocks[i].Kind != MatterKind.Contents) Append(front, blocks[i].Text);
            for (int i = last + 1; i < blocks.Count; i++)
                if (blocks[i].Kind != MatterKind.Contents) Append(back, blocks[i].Text);

            s.Front = front.ToString();
            s.Back = back.ToString();
            // The body runs from the first real paragraph to the last, verbatim, so
            // nothing inside it can be lost by this pass however odd it looks.
            int bodyStart = blocks[first].Start;
            int bodyEnd = blocks[last].Start + blocks[last].Text.Length;
            s.Body = text.Substring(bodyStart, bodyEnd - bodyStart);
            s.Note = Describe(blocks);
            return s;
        }

        private static void Append(StringBuilder sb, string para)
        {
            if (sb.Length > 0) sb.Append(Environment.NewLine).Append(Environment.NewLine);
            sb.Append(para.Trim());
        }

        /// <summary>What was left in the source language, named for the report —
        /// because silent removal reads as full coverage.</summary>
        public static string Describe(List<MatterBlock> blocks)
        {
            int matter = 0, contents = 0, chars = 0;
            foreach (var b in blocks)
            {
                if (b.Kind == MatterKind.Prose) continue;
                if (b.Kind == MatterKind.Contents) contents++; else matter++;
                chars += b.Text.Length;
            }
            if (matter == 0 && contents == 0) return null;
            var sb = new StringBuilder();
            if (matter > 0) sb.Append(matter.ToString(CultureInfo.InvariantCulture)).Append(" not translated");
            if (contents > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(contents.ToString(CultureInfo.InvariantCulture)).Append(" table of contents removed");
            }
            sb.Append(" (").Append(chars.ToString("N0", CultureInfo.InvariantCulture)).Append(" characters)");
            return sb.ToString();
        }
    }
}
