using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Nemoviz_Book_Reader
{
    /// <summary>Shared building blocks for the document parsers: an HTML→blocks
    /// splitter, block assembly with exact heading/id character offsets, and a
    /// zip/XML flattener. Kept in one place so every format subsystem reuses the
    /// same, tested primitives.</summary>
    public static class TextParsing
    {
        /// <summary>A run of text tagged as a heading (with depth) or a plain
        /// paragraph, plus any element ids that mark its start (for resolving
        /// TOC #fragment targets).</summary>
        public class Block
        {
            public bool IsHeading;
            public int Level;
            public string Text;
            public List<string> Ids;
        }

        /// <summary>Assembles blocks into text, cleaning each block so the joins
        /// (and therefore the offsets) stay exact. Returns the text, the heading
        /// list (level, title, char offset) from IsHeading blocks, and a map of
        /// element id → char offset. Cleaning is idempotent, so the reader
        /// re-cleaning the file on load doesn't move the offsets.</summary>
        public static void Assemble(List<Block> blocks,
            out string text,
            out List<(int Level, string Title, int Offset)> headings,
            out Dictionary<string, int> idOffsets)
        {
            StringBuilder sb = new StringBuilder();
            headings = new List<(int, string, int)>();
            idOffsets = new Dictionary<string, int>();

            foreach (Block b in blocks)
            {
                string clean = TextCleaner.Clean(b.Text);
                if (clean.Length == 0) continue;
                int off = sb.Length;
                if (b.Ids != null)
                    foreach (string id in b.Ids)
                        if (!string.IsNullOrEmpty(id) && !idOffsets.ContainsKey(id))
                            idOffsets[id] = off;
                if (b.IsHeading) headings.Add((b.Level, clean, off));
                sb.Append(clean).Append("\n\n");
            }
            text = sb.ToString().TrimEnd('\n');
        }

        // ── HTML → blocks ─────────────────────────────────────────────────
        private static readonly Regex RxScript = new Regex("<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxStyle = new Regex("<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxHead = new Regex("<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxComment = new Regex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxToken = new Regex("<[^>]+>|[^<]+", RegexOptions.Compiled);
        private static readonly Regex RxHeading = new Regex("^<(/?)h([1-6])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxBlockEnd = new Regex(@"^</?(p|div|li|tr|blockquote|section|article|ul|ol|table|pre|dd|dt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxId = new Regex("\\sid=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Splits HTML/XHTML into blocks (paragraphs + h1–h6 headings),
        /// tagging each block with any element ids seen within it (so a TOC
        /// #fragment can be resolved to an offset).</summary>
        public static List<Block> HtmlBlocks(string html)
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
            List<string> ids = new List<string>();

            System.Action flush = () =>
            {
                if (cur.ToString().Trim().Length > 0)
                    blocks.Add(new Block { IsHeading = heading, Level = level, Text = cur.ToString(), Ids = ids });
                cur.Clear();
                heading = false;
                level = 0;
                ids = new List<string>();
            };

            foreach (Match m in RxToken.Matches(html))
            {
                string tok = m.Value;
                if (tok.Length == 0) continue;
                if (tok[0] != '<') { cur.Append(WebUtility.HtmlDecode(tok)); continue; }

                Match idm = RxId.Match(tok);
                if (idm.Success) ids.Add(idm.Groups[1].Value);

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

        /// <summary>Flattens the main XML part of a zipped office document
        /// (docx word/document.xml, odt content.xml): paragraph/heading elements
        /// become blank-line breaks; tabs/breaks/spaces are honoured. Namespace
        /// prefixes are ignored (matched by local name).</summary>
        public static string ZipXmlText(string zipPath, string partName)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
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

        public static string ReadZipEntry(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8, true))
                return r.ReadToEnd();
        }

        /// <summary>Resolves a relative href against a base directory to a
        /// zip-style path (forward slashes, "../" collapsed, no leading slash).</summary>
        public static string ResolvePath(string baseDir, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return relative;
            relative = WebUtility.UrlDecode(relative).Replace('\\', '/');
            string combined = (baseDir ?? "") + relative;
            List<string> parts = new List<string>();
            foreach (string seg in combined.Split('/'))
            {
                if (seg == "" || seg == ".") continue;
                if (seg == ".." ) { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(seg);
            }
            return string.Join("/", parts);
        }
    }
}
