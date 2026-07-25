using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Extracts the readable text of a TEXT DAISY book (no audio) so it can be
    /// read by TTS like any other text book. Two content shapes, both reduced to
    /// text + headings through the shared HTML pipeline (DAISY content uses
    /// &lt;h1&gt;–&lt;h6&gt; / &lt;p&gt; just like HTML):
    ///   • DAISY 3 / Z39.86 → a DTBook XML document (&lt;dtbook&gt;…).
    ///   • DAISY 2.02 → the content XHTML file(s) beside ncc.html.
    /// Audio DAISY keeps going through <see cref="DaisyParser"/>; this is only for
    /// the text-only case. Page markers (&lt;pagenum&gt;) are stripped from the
    /// reading flow for now (page navigation for text DAISY is a later step).
    /// </summary>
    public static class DaisyTextExtractor
    {
        private static readonly Regex RxPagenum =
            new Regex("<pagenum\\b[^>]*>(.*?)</pagenum>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxTags =
            new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex RxEncoding =
            new Regex("encoding=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True if the folder is a DAISY book with no audio — i.e. a text
        /// DAISY that should be read by TTS rather than played.</summary>
        public static bool IsTextDaisy(DaisyBook book)
        {
            return book != null && book.AudioPlayOrder.Count == 0;
        }

        /// <summary>Sets up an imported text DAISY as a text book: flattens the
        /// content to the book root, extracts the reading text to content.txt,
        /// records the heading structure + DAISY metadata, and labels the format.
        /// The audio DAISY path (BuildChaptersFromDaisy) is deliberately NOT used.
        /// On the next load the book is detected as text (no audio → BuildDaisyNav
        /// leaves IsDaisy false → DetectTextBook picks up content.txt).</summary>
        public static void SetupTextBook(BookData imported, string destFolder, DaisyBook daisy, bool useMetadata)
        {
            try { LibraryScanner.FlattenDaisyToRoot(destFolder, daisy.ContentRoot); } catch { }
            DaisyBook d2 = DaisyParser.TryParse(destFolder) ?? daisy;

            TextDoc doc = Extract(destFolder);
            try
            {
                File.WriteAllText(Path.Combine(destFolder, "content.txt"),
                    doc.Text ?? "", new UTF8Encoding(false));
            }
            catch { }

            if (useMetadata)
            {
                if (!string.IsNullOrWhiteSpace(d2.Title)) imported.Title = d2.Title;
                if (!string.IsNullOrWhiteSpace(d2.Author)) imported.Author = d2.Author;
            }
            imported.SetTextHeadings(doc.Headings);
            imported.SetTextPages(doc.Pages);
            string ver = string.IsNullOrEmpty(d2.Version) ? "" : d2.Version + " ";
            imported.Format = "DAISY " + ver + "— Digital Accessible Information System";
        }

        /// <summary>Extracts text + headings from a text DAISY folder. Never throws;
        /// an unreadable book yields an empty TextDoc.</summary>
        public static TextDoc Extract(string folder)
        {
            var doc = new TextDoc();
            try
            {
                List<string> contentFiles = FindContentFiles(folder);
                if (contentFiles.Count == 0) return doc;

                StringBuilder full = new StringBuilder();
                var headings = new List<(int Level, string Title, int Offset)>();
                var pages = new List<(string Label, int Offset)>();
                int pageSeq = 0;
                foreach (string cf in contentFiles)
                {
                    string raw = ReadText(cf);
                    if (string.IsNullOrEmpty(raw)) continue;
                    // Replace each <pagenum>N</pagenum> with an empty anchor we can
                    // locate after assembly (and remember N). This records the page
                    // marker's position and takes the number out of the reading flow.
                    var pageLabels = new Dictionary<string, string>();
                    raw = RxPagenum.Replace(raw, m =>
                    {
                        string label = RxTags.Replace(m.Groups[1].Value, "").Trim();
                        string id = "__nbrpage" + (pageSeq++) + "__";
                        pageLabels[id] = label;
                        return "<span id=\"" + id + "\"></span>";
                    });

                    TextParsing.Assemble(TextParsing.HtmlBlocks(raw), out string text, out var heads, out var ids);
                    if (text.Length == 0) continue;
                    int start = full.Length;
                    foreach (var h in heads) headings.Add((h.Level, h.Title, start + h.Offset));
                    foreach (var kv in pageLabels)
                        if (ids.TryGetValue(kv.Key, out int off))
                            pages.Add((kv.Value, start + off));
                    full.Append(text).Append("\n\n");
                }
                doc.Text = full.ToString().TrimEnd('\n');
                doc.Headings = headings;
                doc.Pages = pages.OrderBy(p => p.Offset).ToList();

                // Same global structure-vs-flat rule as the file parsers.
                if (doc.Headings.Count < TextExtractor.MinStructureHeadings)
                    doc.Headings.Clear();
            }
            catch { return new TextDoc(); }
            return doc;
        }

        // ── Locate the content document(s) ────────────────────────────────────
        private static List<string> FindContentFiles(string folder)
        {
            var result = new List<string>();

            // DAISY 3: the DTBook XML — an .xml whose root is <dtbook>.
            string dtbook = AllFiles(folder)
                .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f => LooksLikeDtbook(f));
            if (dtbook != null) { result.Add(dtbook); return result; }

            // DAISY 2.02: the content XHTML beside ncc.html. Usually one file; take
            // every non-ncc html in name order (covers books split into sections).
            var htmls = AllFiles(folder)
                .Where(f => (f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
                .Where(f => !Path.GetFileName(f).Equals("ncc.html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.AddRange(htmls);
            return result;
        }

        private static bool LooksLikeDtbook(string path)
        {
            try
            {
                // Sniff the first part of the file for the dtbook root element.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    byte[] buf = new byte[2048];
                    int n = fs.Read(buf, 0, buf.Length);
                    string head = Encoding.UTF8.GetString(buf, 0, n);
                    return head.IndexOf("<dtbook", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static IEnumerable<string> AllFiles(string folder)
        {
            try { return Directory.GetFiles(folder, "*", SearchOption.AllDirectories); }
            catch { return Enumerable.Empty<string>(); }
        }

        // ── Read a content file honouring its declared XML encoding ────────────
        private static string ReadText(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                // BOM → let StreamReader decide; else read the XML declaration.
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

                Encoding enc = Encoding.UTF8;
                string sniff = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));
                Match m = RxEncoding.Match(sniff);
                if (m.Success)
                {
                    try { enc = Encoding.GetEncoding(m.Groups[1].Value); } catch { enc = Encoding.UTF8; }
                }
                return enc.GetString(bytes);
            }
            catch { return ""; }
        }
    }
}
