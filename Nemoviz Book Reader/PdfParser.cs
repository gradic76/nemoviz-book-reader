using System;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Outline;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Extracts text from a PDF via PdfPig (Apache-2.0). A text-layer PDF yields
    /// its text in reading order, a page marker per page, and — when the PDF has
    /// an outline — headings from its bookmarks (each mapped to the start of its
    /// target page). A scanned / image-only PDF has NO text layer, so this returns
    /// (near-)empty text; the caller then treats it as "no text" (a future OCR
    /// path). A PDF that requires a user password is flagged DrmProtected.
    /// </summary>
    public class PdfParser : ITextFormatParser
    {
        public bool Handles(string extension) { return extension == ".pdf"; }

        public TextDoc Parse(string filePath)
        {
            var doc = new TextDoc();

            PdfDocument pdf;
            try
            {
                pdf = PdfDocument.Open(filePath);
            }
            catch (Exception ex)
            {
                // A user-password-protected PDF can't be opened → report as
                // protected so the import shows the "protected book" message.
                string info = ex.GetType().Name + " " + (ex.Message ?? "");
                if (info.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    info.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0)
                    doc.DrmProtected = true;
                return doc;
            }

            using (pdf)
            {
                try
                {
                    doc.Title = pdf.Information?.Title ?? "";
                    doc.Author = pdf.Information?.Author ?? "";
                }
                catch { }

                // Text (reading order) + a page marker at each page's start.
                //
                // READ EVERY PAGE FIRST, STRIP THE FURNITURE, THEN ASSEMBLE
                // (2026-08-28). A PDF is the one format that hands us real page
                // boundaries, which is exactly what RunningHeads needs — and
                // until now they were thrown away by appending straight into one
                // StringBuilder. CLAUDE.md §10 had recorded that whether the PDF
                // path runs the running-head stripper was unchecked; it did not.
                //
                // Measured on ten books off a sharing forum (Gordan, 2026-08-28),
                // and there were two different kinds of furniture in them:
                //   · the PRINTED PAGE NUMBER as a line of its own — in all ten,
                //     once per page, 166 to 334 times a book;
                //   · the scanner's signature ("Scan i obrada: Knjige.Club
                //     Books", sometimes a person's name beneath it) — in five of
                //     the ten it stands at the foot of EVERY page, and in one of
                //     those it lands mid-sentence when a paragraph runs over the
                //     break, which is precisely the fault §10g describes for
                //     braille.
                // Both are furniture by RunningHeads' own definition: the same
                // line in the same place on most pages. Nothing here knows the
                // words, so a forum that changes its signature tomorrow needs no
                // change to this.
                //
                // The offsets are computed AFTER the strip, or every page mark
                // would point past its own page by the length of what was
                // removed — the drift §8e already paid for once.
                var pageLines = new List<List<string>>();
                foreach (Page page in pdf.GetPages())
                {
                    string text;
                    try { text = ContentOrderTextExtractor.GetText(page); }
                    catch { try { text = page.Text; } catch { text = ""; } }
                    pageLines.Add(new List<string>(
                        (text ?? "").Replace("\r\n", "\n").Split('\n')));
                }

                RunningHeads.Strip(pageLines);

                var sb = new StringBuilder();
                var pageStart = new List<int>();
                int pageNumber = 1;
                foreach (List<string> lines in pageLines)
                {
                    int start = sb.Length;
                    pageStart.Add(start);
                    doc.Pages.Add((pageNumber.ToString(), start));
                    pageNumber++;
                    AppendWithParagraphs(sb, lines);
                    sb.Append("\n\n");
                }
                doc.Text = sb.ToString();

                // Headings from the document outline (bookmarks), mapped to the
                // start of each target page. A missing/short outline falls back to
                // flat text via the global structure rule (TextExtractor).
                try
                {
                    Bookmarks bookmarks;
                    if (pdf.TryGetBookmarks(out bookmarks) && bookmarks != null)
                    {
                        foreach (BookmarkNode node in bookmarks.GetNodes())
                        {
                            DocumentBookmarkNode d = node as DocumentBookmarkNode;
                            if (d == null) continue;
                            int pi = d.PageNumber - 1;
                            if (pi < 0 || pi >= pageStart.Count) continue;
                            string title = (node.Title ?? "").Trim();
                            if (title.Length == 0) continue;
                            doc.Headings.Add((Math.Max(1, node.Level + 1), title, pageStart[pi]));
                        }
                        doc.Headings.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                    }
                }
                catch { }
            }

            return doc;
        }

        /// <summary>Writes a page's lines out with its PARAGRAPHS marked.
        ///
        /// <para><b>A PDF carries no paragraph at all</b>, and that was measured
        /// before this was written (2026-08-28): the extracted lines have no
        /// indent — every one starts at column zero — and no blank line, so the
        /// only break in the whole book is the one between pages. On ten real
        /// books that came out as paragraphs == pages, which meant the reader got
        /// no pauses, and the Paragraph seek step correctly refused to appear
        /// because a "paragraph" was a whole page.</para>
        ///
        /// <para><b>The signal is already there and was simply not written down:
        /// a line that ENDS A SENTENCE ends a paragraph</b>, because everything
        /// else was wrapped at the right margin by the typesetter. That is the
        /// same test `TextCleaner.Unwrap` uses from the other side, and the two
        /// meet: what is marked here as a paragraph break stays one, and what is
        /// left as a single newline inside a paragraph gets joined back into a
        /// line by the cleaner afterwards. Measured, that yields 2 666 to 4 328
        /// paragraphs a book — one every 114 to 209 characters, which for a novel
        /// full of dialogue is the right order of magnitude, and each of those
        /// breaks was ALREADY in the text as a single newline. Nothing is
        /// invented; it is promoted.</para>
        ///
        /// <para><b>Done here rather than in the cleaner, and that matters.</b>
        /// The page offsets are taken from this StringBuilder, so a rule that
        /// added characters afterwards would push every page mark past its own
        /// page — the drift §8e paid for once. Here the text is final before a
        /// single offset is read.</para>
        ///
        /// <para>Deliberately NOT the reference cleaner's `auto_paragraf`, which
        /// breaks after every sentence: run on wrapped text after unwrapping, that
        /// gives one paragraph per sentence, and a Paragraph step identical to a
        /// Sentence step.</para></summary>
        private static void AppendWithParagraphs(StringBuilder sb, List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].TrimEnd();
                sb.Append(line);
                if (i == lines.Count - 1) break;
                sb.Append(EndsSentence(line) ? "\n\n" : "\n");
            }
        }

        /// <summary>True when the line finished a sentence — allowing for the
        /// closing quote or bracket that comes after the stop, and for the
        /// guillemets Croatian and Serbian books are set in.</summary>
        private static bool EndsSentence(string line)
        {
            string s = line.TrimEnd();
            // Walk back over anything that can follow a full stop.
            //
            // BOTH guillemets, and that was a real bug for one build: Croatian
            // and Serbian books open a quotation with » and CLOSE it with «, so
            // a list carrying only » left every line of dialogue joined to the
            // next one. Caught by reading a passage rather than by the counts,
            // which looked plausible either way.
            int i = s.Length - 1;
            while (i >= 0 && "\"'”’“„»«)]}".IndexOf(s[i]) >= 0) i--;
            return i >= 0 && ".!?…".IndexOf(s[i]) >= 0;
        }
    }
}
