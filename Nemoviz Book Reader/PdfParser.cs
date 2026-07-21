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
                var sb = new StringBuilder();
                var pageStart = new List<int>();
                foreach (Page page in pdf.GetPages())
                {
                    int start = sb.Length;
                    pageStart.Add(start);
                    doc.Pages.Add((page.Number.ToString(), start));

                    string text;
                    try { text = ContentOrderTextExtractor.GetText(page); }
                    catch { try { text = page.Text; } catch { text = ""; } }
                    sb.Append(text ?? "").Append("\n\n");
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
    }
}
