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

        public static List<TextChunk> Split(string text, int maxChars = DefaultMaxChars)
        {
            var chunks = new List<TextChunk>();
            if (string.IsNullOrEmpty(text)) return chunks;
            if (maxChars < 500) maxChars = 500;

            // Paragraphs, with their positions kept: the offsets are what let a
            // resumed job line pieces up with what is already cached.
            var paras = Paragraphs(text);
            if (paras.Count == 0) return chunks;

            int i = 0;
            while (i < paras.Count)
            {
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
