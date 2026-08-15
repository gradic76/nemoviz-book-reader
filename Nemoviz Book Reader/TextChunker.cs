using System;
using System.Collections.Generic;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>One piece of a book on its way to be translated.</summary>
    internal sealed class TextChunk
    {
        /// <summary>Where this piece starts in the cleaned book text. The chunking
        /// must be reproducible from the text alone, so a job can be resumed
        /// without keeping a list of offsets anywhere.</summary>
        public int Start;
        public int Length;
        public string Text;

        /// <summary>The end of the PREVIOUS piece, sent as context and never asked
        /// for back. See <see cref="TextChunker"/> for why.</summary>
        public string Lead;

        public int Index;
        public int ParagraphCount;
    }

    /// <summary>
    /// Cuts a book into pieces a translation service will accept.
    ///
    /// <para><b>Pieces are as LARGE as the service allows, and that is a finding
    /// rather than a preference.</b> Every boundary is a place where the model
    /// makes afresh a decision the source never stated — measured on 2026-08-14,
    /// the same sentence came back with the speaker's gender assigned one way with
    /// a name in the context and the other way without it. The literature agrees
    /// from the other side: translating a whole document in one pass is the most
    /// consistent thing you can do, and every cut costs some of that. So the
    /// number here is a service limit, not a taste.</para>
    ///
    /// <para><b>Cuts fall only between paragraphs.</b> Never inside one, and never
    /// inside a sentence — a model handed half a sentence will finish it, and the
    /// next piece will start by translating the same half again.</para>
    ///
    /// <para><b>Each piece carries the tail of the one before it as
    /// <see cref="TextChunk.Lead"/>, marked as context and not to be translated.</b>
    /// This is "contextual chunking" and it is what a document-level translator
    /// does: it gives the model the thread it would otherwise have lost — who was
    /// speaking, which way the formality had settled, what a recurring term was
    /// called last time. It costs input tokens, which are the cheap ones, and both
    /// services cache a repeated prefix.</para>
    ///
    /// <para><b>Reading aloud will want the opposite and that is fine</b> (Gordan,
    /// 2026-08-14): a cloud voice loses level across a long generation, so speech
    /// wants SHORT pieces. Translation and reading are two passes over the same
    /// text at different times, so they simply chunk differently. There is no one
    /// number to reconcile.</para>
    /// </summary>
    internal static class TextChunker
    {
        /// <summary>Characters per piece. Comfortably inside what both services
        /// take, and large enough that an average book is a hundred-odd pieces
        /// rather than a thousand.</summary>
        public const int DefaultMaxChars = 6000;

        /// <summary>How much of the previous piece rides along as context.
        /// Roughly a long paragraph: enough to carry a speaker and a register,
        /// short enough not to dominate the request.</summary>
        public const int LeadChars = 700;

        /// <summary>How much of a NEW chapter may trail at the end of a piece
        /// before it is pushed into the next one instead. An eighth of a piece is
        /// a handful of sentences — Gordan asked for about three.
        ///
        /// <para>Only a SMALL tail is moved, and that is the whole economy of it:
        /// the piece is nearly full already, so ending it at the chapter costs
        /// almost nothing, while cutting at a boundary halfway through would
        /// throw away half a piece and buy an extra seam — and this class exists
        /// because every seam costs consistency.</para></summary>
        public const int ChapterTailShare = 8;

        public static List<TextChunk> Split(string text, int maxChars = DefaultMaxChars)
        {
            return Split(text, maxChars, null);
        }

        /// <summary>Cuts the book, and where <paramref name="chapterStarts"/> is
        /// given, avoids leaving the first few sentences of a chapter stranded at
        /// the end of the piece before it.
        ///
        /// <para><b>Gordan's, 2026-08-15, and he named it: widow and orphan
        /// control.</b> A piece that ends three sentences into the next chapter
        /// hands the model two chapters at once, and hands the piece AFTER it a
        /// <see cref="TextChunk.Lead"/> taken from the wrong one — the context
        /// meant to carry a speaker and a register across the seam instead
        /// carries the end of a scene that has finished. The cut is moved back to
        /// the chapter's own beginning, so a chapter starts a piece rather than
        /// finishing someone else's.</para>
        ///
        /// <para>Offsets are into the same cleaned text the chunking is done on
        /// (<see cref="BookData.TextHeadings"/> is in exactly those coordinates).
        /// A boundary that does not fall on a paragraph edge is ignored rather
        /// than forced — cutting inside a paragraph is the one cut this class
        /// refuses to make.</para></summary>
        public static List<TextChunk> Split(string text, int maxChars, IList<int> chapterStarts)
        {
            var chunks = new List<TextChunk>();
            if (string.IsNullOrEmpty(text)) return chunks;
            if (maxChars < 500) maxChars = 500;

            // Paragraphs, with their positions kept: the offsets are what let a
            // resumed job line pieces up with what is already cached.
            var paras = Paragraphs(text);
            if (paras.Count == 0) return chunks;

            // Chapter starts that really are paragraph starts, as paragraph
            // indices — resolved once rather than searched per piece.
            var breakAt = new HashSet<int>();
            if (chapterStarts != null)
            {
                var startOf = new Dictionary<int, int>();
                for (int p = 0; p < paras.Count; p++) startOf[paras[p].Start] = p;
                foreach (int off in chapterStarts)
                {
                    int p;
                    if (startOf.TryGetValue(off, out p) && p > 0) breakAt.Add(p);
                }
            }
            int tailLimit = Math.Max(1, maxChars / ChapterTailShare);

            int i = 0;
            while (i < paras.Count)
            {
                int first = i;
                int start = paras[i].Start;
                int end = paras[i].End;
                int count = 1;
                i++;
                while (i < paras.Count && (paras[i].End - start) <= maxChars)
                {
                    end = paras[i].End;
                    count++;
                    i++;
                }

                // The last chapter to begin inside this piece. Moved back to only
                // if what follows it here is short AND something is left behind —
                // a piece that would become empty is no improvement.
                if (breakAt.Count > 0 && i < paras.Count)
                {
                    for (int p = i - 1; p > first; p--)
                    {
                        if (!breakAt.Contains(p)) continue;
                        if (end - paras[p].Start > tailLimit) break;
                        end = paras[p - 1].End;
                        count = p - first;
                        i = p;
                        break;
                    }
                }

                var c = new TextChunk
                {
                    Start = start,
                    Length = end - start,
                    Text = text.Substring(start, end - start),
                    Index = chunks.Count,
                    ParagraphCount = count
                };
                // A single paragraph longer than the limit is taken whole rather
                // than cut. It is rare, the services allow a good deal more than
                // this limit, and splitting a paragraph is the one cut that
                // reliably produces a duplicated half-sentence.
                chunks.Add(c);
            }

            chunks = MergeSlivers(chunks);

            for (int k = 1; k < chunks.Count; k++)
            {
                string prev = chunks[k - 1].Text;
                chunks[k].Lead = prev.Length <= LeadChars
                    ? prev
                    : prev.Substring(prev.Length - LeadChars);
            }
            return chunks;
        }

        /// <summary>Joins a piece with almost nothing in it to the one after it.
        ///
        /// <para><b>Found in use, 2026-08-15.</b> A reader translated a braille book
        /// and was told three passages had been left in the original. Nothing was
        /// missing from the book: the three were <b>five and six characters long</b>
        /// — a blank line and the start of a word — and every one of them sat beside
        /// a chapter number standing on a line of its own (" X.", " XI.", " XV.").
        /// Sent away, a five-character request cannot pass a length ratio whatever
        /// comes back, so the checks condemned it and the count reported a failure
        /// that was not one.</para>
        ///
        /// <para><b>Merging forward, never dropping</b> — the invariant that the
        /// pieces rebuild the source character for character is what makes the
        /// resume cache safe, and it survives joining two neighbours. The measure is
        /// LETTERS rather than length, because what makes a piece worth sending is
        /// words: a line of roman numerals and full stops has nothing to translate
        /// however long it is.</para></summary>
        private static List<TextChunk> MergeSlivers(List<TextChunk> chunks)
        {
            const int MinLetters = 15;
            var merged = new List<TextChunk>(chunks.Count);
            TextChunk held = null;

            foreach (TextChunk c in chunks)
            {
                if (held != null)
                {
                    c.Start = held.Start;
                    c.Text = held.Text + c.Text;
                    c.Length = c.Text.Length;
                    c.ParagraphCount += held.ParagraphCount;
                    held = null;
                }
                if (Letters(c.Text) < MinLetters) { held = c; continue; }
                merged.Add(c);
            }

            // A sliver at the very end has nothing after it to join, so it goes back
            // onto the piece before — and if it is the only piece, it stands alone
            // rather than being lost.
            if (held != null)
            {
                if (merged.Count > 0)
                {
                    TextChunk last = merged[merged.Count - 1];
                    last.Text += held.Text;
                    last.Length = last.Text.Length;
                    last.ParagraphCount += held.ParagraphCount;
                }
                else merged.Add(held);
            }

            for (int k = 0; k < merged.Count; k++) merged[k].Index = k;
            return merged;
        }

        private static int Letters(string s)
        {
            int n = 0;
            foreach (char c in s) if (char.IsLetter(c)) n++;
            return n;
        }

        private struct Para { public int Start; public int End; }

        /// <summary>Paragraph spans, blank lines included at the end of each so the
        /// pieces reassemble into the original text exactly.</summary>
        private static List<Para> Paragraphs(string text)
        {
            var list = new List<Para>();
            int i = 0, n = text.Length;
            while (i < n)
            {
                int start = i;
                // to the end of this paragraph's text
                while (i < n && !IsBlankLineAt(text, i)) i++;
                // and past the blank lines that follow it
                while (i < n && IsBlankLineAt(text, i)) i = NextLine(text, i);
                if (i <= start) i = Math.Min(n, start + 1);
                list.Add(new Para { Start = start, End = i });
            }
            return list;
        }

        private static bool IsBlankLineAt(string s, int i)
        {
            if (i >= s.Length) return false;
            // at a line start?
            if (i > 0 && s[i - 1] != '\n') return false;
            int j = i;
            while (j < s.Length && (s[j] == ' ' || s[j] == '\t' || s[j] == '\r')) j++;
            return j < s.Length ? s[j] == '\n' : j > i;
        }

        private static int NextLine(string s, int i)
        {
            while (i < s.Length && s[i] != '\n') i++;
            return i < s.Length ? i + 1 : s.Length;
        }
    }
}
