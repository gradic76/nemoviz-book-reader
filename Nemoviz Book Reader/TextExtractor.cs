using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace Nemoviz_Book_Reader
{
    /// <summary>The result of extracting a document: the reading text, its
    /// heading structure (level, title, character offset into the text) if any,
    /// and title/author metadata if the format carries them.</summary>
    public class TextDoc
    {
        public string Text = "";
        public List<(int Level, string Title, int Offset)> Headings = new List<(int, string, int)>();
        public string Title = "";
        public string Author = "";
    }

    /// <summary>Pulls plain reading text out of document formats. Two groups
    /// (see CLAUDE.md 8e):
    ///  • Editable (txt, rtf, docx, odt) — flattened to text, behave like a plain
    ///    txt; no reliable structure.
    ///  • Read-only / produced (html, fb2, epub) — usually structured, so their
    ///    headings are captured (level + offset) for DAISY-style navigation; if
    ///    none are found, they simply fall back to a flat book.
    /// Zero-dependency (RichTextBox for RTF, System.IO.Compression + Xml for the
    /// rest). Never throws — returns null / an empty TextDoc on failure.</summary>
    public static class TextExtractor
    {
        public static bool IsTextFormat(string extension)
        {
            switch ((extension ?? "").ToLowerInvariant())
            {
                case ".txt":
                case ".rtf":
                case ".docx":
                case ".odt":
                case ".htm":
                case ".html":
                case ".fb2":
                case ".epub":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Extracts a document to text (+ structure/metadata). Never
        /// null; an unreadable file yields an empty TextDoc.</summary>
        public static TextDoc Extract(string filePath)
        {
            try
            {
                switch (Path.GetExtension(filePath).ToLowerInvariant())
                {
                    case ".txt": return Flat(TtsReader.ReadFile(filePath));
                    case ".rtf": return Flat(ExtractRtf(filePath));
                    case ".docx": return Flat(ExtractZipXml(filePath, "word/document.xml"));
                    case ".odt": return Flat(ExtractZipXml(filePath, "content.xml"));
                    case ".htm":
                    case ".html": return Assemble(HtmlBlocks(TtsReader.ReadFile(filePath)), "", "");
                    case ".fb2": return ExtractFb2(filePath);
                    case ".epub": return ExtractEpub(filePath);
                    default: return new TextDoc();
                }
            }
            catch
            {
                return new TextDoc();
            }
        }

        // ── Editable group (flat) ─────────────────────────────────────────
        private static TextDoc Flat(string text)
        {
            return new TextDoc { Text = text ?? "" };
        }

        private static string ExtractRtf(string path)
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
            string rtf = File.ReadAllText(path, Encoding.GetEncoding(1252));
            using (RichTextBox rtb = new RichTextBox())
            {
                rtb.Rtf = rtf;
                return rtb.Text;
            }
        }

        private static string ExtractZipXml(string path, string partName)
        {
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry entry = zip.GetEntry(partName);
                if (entry == null) return "";
                StringBuilder sb = new StringBuilder();
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    CheckCharacters = false,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    DtdProcessing = DtdProcessing.Ignore
                };
                using (Stream s = entry.Open())
                using (XmlReader reader = XmlReader.Create(s, settings))
                {
                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                                sb.Append(reader.Value);
                                break;
                            case XmlNodeType.Element:
                                string el = reader.LocalName;
                                if (el == "tab") sb.Append(' ');
                                else if (el == "br" || el == "line-break") sb.Append('\n');
                                else if (el == "s") sb.Append(' ');
                                else if ((el == "p" || el == "h") && reader.IsEmptyElement) sb.Append("\n\n");
                                break;
                            case XmlNodeType.EndElement:
                                if (reader.LocalName == "p" || reader.LocalName == "h") sb.Append("\n\n");
                                break;
                        }
                    }
                }
                return sb.ToString();
            }
        }

        // ── Block model (read-only group) ─────────────────────────────────
        private class Block
        {
            public bool IsHeading;
            public int Level;
            public string Text;
        }

        /// <summary>Assembles blocks into the final text, cleaning each block and
        /// recording each heading's character offset in the result. Cleaning per
        /// block (with the joins we control) keeps the offsets exact, and since
        /// TextCleaner is idempotent, the reader re-cleaning the file on load
        /// doesn't move them.</summary>
        private static TextDoc Assemble(List<Block> blocks, string title, string author)
        {
            TextDoc doc = new TextDoc { Title = title ?? "", Author = author ?? "" };
            StringBuilder sb = new StringBuilder();
            foreach (Block b in blocks)
            {
                string clean = TextCleaner.Clean(b.Text);
                if (clean.Length == 0) continue;
                if (b.IsHeading) doc.Headings.Add((b.Level, clean, sb.Length));
                sb.Append(clean).Append("\n\n");
            }
            doc.Text = sb.ToString().TrimEnd('\n');
            return doc;
        }

        // ── HTML → blocks ─────────────────────────────────────────────────
        private static readonly Regex RxScript = new Regex("<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxStyle = new Regex("<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxHead = new Regex("<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxComment = new Regex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxToken = new Regex("<[^>]+>|[^<]+", RegexOptions.Compiled);
        private static readonly Regex RxHeading = new Regex("^<(/?)h([1-6])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxBlockEnd = new Regex(@"^</?(p|div|li|tr|blockquote|section|article|ul|ol|table|pre|dd|dt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<Block> HtmlBlocks(string html)
        {
            List<Block> blocks = new List<Block>();
            if (string.IsNullOrEmpty(html)) return blocks;

            html = RxScript.Replace(html, " ");
            html = RxStyle.Replace(html, " ");
            html = RxHead.Replace(html, " ");
            html = RxComment.Replace(html, " ");

            StringBuilder cur = new StringBuilder();
            bool heading = false;
            int level = 0;

            System.Action flush = () =>
            {
                if (cur.ToString().Trim().Length > 0)
                    blocks.Add(new Block { IsHeading = heading, Level = level, Text = cur.ToString() });
                cur.Clear();
                heading = false;
                level = 0;
            };

            foreach (Match m in RxToken.Matches(html))
            {
                string tok = m.Value;
                if (tok.Length == 0) continue;
                if (tok[0] != '<') { cur.Append(WebUtility.HtmlDecode(tok)); continue; }

                Match hm = RxHeading.Match(tok);
                if (hm.Success)
                {
                    flush();
                    if (hm.Groups[1].Value != "/") { heading = true; level = int.Parse(hm.Groups[2].Value); }
                    continue;
                }
                if (Regex.IsMatch(tok, "^<br", RegexOptions.IgnoreCase)) { cur.Append('\n'); continue; }
                if (RxBlockEnd.IsMatch(tok)) { flush(); continue; }
                // inline tag → ignore
            }
            flush();
            return blocks;
        }

        // ── FB2 → blocks ──────────────────────────────────────────────────
        private static TextDoc ExtractFb2(string path)
        {
            string xml;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            // .fb2 is plain XML; a .fb2.zip / zipped fb2 holds one .fb2 inside.
            if (ext == ".zip" || IsZip(path))
            {
                using (ZipArchive zip = ZipFile.OpenRead(path))
                {
                    ZipArchiveEntry e = zip.Entries.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(".fb2"));
                    if (e == null) return new TextDoc();
                    using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8, true)) xml = r.ReadToEnd();
                }
            }
            else
            {
                xml = TtsReader.ReadFile(path);
            }

            List<Block> blocks = new List<Block>();
            string title = "", author = "";
            StringBuilder cur = new StringBuilder();
            int depth = 0;
            bool inTitle = false, inBody = false, inDesc = false;
            string authorFirst = "", authorLast = "";

            System.Action flushPara = () =>
            {
                if (cur.ToString().Trim().Length > 0)
                    blocks.Add(new Block { IsHeading = false, Level = 0, Text = cur.ToString() });
                cur.Clear();
            };

            XmlReaderSettings settings = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
            try
            {
                using (XmlReader reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    StringBuilder headBuf = new StringBuilder();
                    StringBuilder metaBuf = new StringBuilder();
                    string metaField = null;
                    while (reader.Read())
                    {
                        string ln = reader.LocalName;
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (ln == "body") { inBody = true; }
                            else if (ln == "title-info") inDesc = true;
                            else if (inBody)
                            {
                                if (ln == "section") { flushPara(); depth++; }
                                else if (ln == "title") { flushPara(); inTitle = true; headBuf.Clear(); }
                                else if (ln == "p" && !inTitle) { flushPara(); }
                                else if (ln == "empty-line") { flushPara(); }
                            }
                            else if (inDesc && (ln == "book-title" || ln == "first-name" || ln == "last-name"))
                            {
                                metaField = ln; metaBuf.Clear();
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                        {
                            if (inTitle) headBuf.Append(reader.Value);
                            else if (metaField != null) metaBuf.Append(reader.Value);
                            else if (inBody) cur.Append(reader.Value);
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            if (ln == "body") inBody = false;
                            else if (ln == "title-info") inDesc = false;
                            else if (inBody)
                            {
                                if (ln == "section") { flushPara(); if (depth > 0) depth--; }
                                else if (ln == "title")
                                {
                                    string ht = headBuf.ToString().Trim();
                                    if (ht.Length > 0) blocks.Add(new Block { IsHeading = true, Level = depth > 0 ? depth : 1, Text = ht });
                                    inTitle = false;
                                }
                                else if (ln == "p" && !inTitle) { cur.Append('\n'); }
                            }
                            else if (metaField != null)
                            {
                                string val = metaBuf.ToString().Trim();
                                if (ln == "book-title") title = val;
                                else if (ln == "first-name") authorFirst = val;
                                else if (ln == "last-name") authorLast = val;
                                metaField = null;
                            }
                        }
                    }
                }
            }
            catch { }
            flushPara();
            author = (authorFirst + " " + authorLast).Trim();
            return Assemble(blocks, title, author);
        }

        // ── EPUB → blocks ─────────────────────────────────────────────────
        private static TextDoc ExtractEpub(string path)
        {
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry opf = zip.Entries.FirstOrDefault(e => e.FullName.ToLowerInvariant().EndsWith(".opf"));
                if (opf == null) return new TextDoc();

                string opfXml;
                using (StreamReader r = new StreamReader(opf.Open(), Encoding.UTF8, true)) opfXml = r.ReadToEnd();

                string baseDir = "";
                int slash = opf.FullName.LastIndexOf('/');
                if (slash >= 0) baseDir = opf.FullName.Substring(0, slash + 1);

                string title = "", author = "";
                Dictionary<string, string> manifest = new Dictionary<string, string>();
                List<string> spine = new List<string>();
                try
                {
                    XDocument xd = XDocument.Parse(opfXml);
                    foreach (XElement el in xd.Descendants())
                    {
                        string ln = el.Name.LocalName;
                        if (ln == "title" && title == "") title = (el.Value ?? "").Trim();
                        else if (ln == "creator" && author == "") author = (el.Value ?? "").Trim();
                        else if (ln == "item")
                        {
                            string id = (string)el.Attribute("id");
                            string href = (string)el.Attribute("href");
                            if (id != null && href != null) manifest[id] = href;
                        }
                        else if (ln == "itemref")
                        {
                            string idref = (string)el.Attribute("idref");
                            if (idref != null) spine.Add(idref);
                        }
                    }
                }
                catch { }

                List<Block> blocks = new List<Block>();
                foreach (string idref in spine)
                {
                    if (!manifest.TryGetValue(idref, out string href)) continue;
                    string full = baseDir + href.Split('#')[0];
                    ZipArchiveEntry e = zip.GetEntry(full) ?? zip.GetEntry(WebUtility.UrlDecode(full));
                    if (e == null) continue;
                    string html;
                    using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8, true)) html = r.ReadToEnd();
                    blocks.AddRange(HtmlBlocks(html));
                }
                return Assemble(blocks, title, author);
            }
        }

        private static bool IsZip(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                    return fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B; // "PK"
            }
            catch { return false; }
        }
    }
}
