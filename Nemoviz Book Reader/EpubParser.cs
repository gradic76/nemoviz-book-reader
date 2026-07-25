using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Nemoviz_Book_Reader
{
    /// <summary>EPUB reader (validated against ~30 real books). Handles the
    /// common library packaging where the file is a .zip (or double-zip) wrapping
    /// the actual .epub. Text comes from the spine in reading order; the heading
    /// structure comes from the TOC — NCX (present in almost every book) or the
    /// EPUB3 nav — not raw &lt;hN&gt; (which is wildly inconsistent), with each
    /// TOC target resolved to a character offset (spine-file start + the #id's
    /// position, DAISY-style). Falls back to &lt;hN&gt;, then flat. Font
    /// obfuscation (encryption.xml over .ttf/.otf) is ignored; only content
    /// encryption is treated as DRM (then the book is skipped, never stripped).</summary>
    public class EpubParser : ITextFormatParser
    {
        private static readonly string[] FontExts = { ".ttf", ".ttc", ".otf", ".woff", ".woff2" };

        public bool Handles(string extension) { return extension == ".epub"; }

        /// <summary>True if a .zip ultimately wraps an epub (so import can route
        /// it here instead of to the generic archive path).</summary>
        public static bool WrapsEpub(string path)
        {
            try
            {
                byte[] data = GetEpubBytes(path);
                if (data == null) return false;
                using (MemoryStream ms = new MemoryStream(data))
                using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read))
                    return zip.Entries.Any(e => e.FullName.ToLowerInvariant().EndsWith(".opf"))
                        || zip.Entries.Any(e => e.FullName == "mimetype");
            }
            catch { return false; }
        }

        public TextDoc Parse(string filePath)
        {
            try
            {
                byte[] data = GetEpubBytes(filePath);
                if (data == null) return new TextDoc();
                using (MemoryStream ms = new MemoryStream(data))
                using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read))
                    return ParseEpub(zip);
            }
            catch { return new TextDoc(); }
        }

        // ── Unwrap the outer zip(s) down to the real epub ─────────────────
        private static byte[] GetEpubBytes(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); } catch { return null; }
            for (int level = 0; level < 2; level++)
            {
                try
                {
                    using (MemoryStream ms = new MemoryStream(data))
                    using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read))
                    {
                        bool isEpub = zip.Entries.Any(e => e.FullName == "mimetype")
                            || zip.Entries.Any(e => e.FullName.ToLowerInvariant().EndsWith(".opf"));
                        if (isEpub) return data;
                        ZipArchiveEntry inner =
                            zip.Entries.FirstOrDefault(e => e.FullName.ToLowerInvariant().EndsWith(".epub"))
                            ?? zip.Entries.FirstOrDefault(e => e.FullName.ToLowerInvariant().EndsWith(".zip"));
                        if (inner == null) return data;
                        data = EntryBytes(inner);
                    }
                }
                catch { return data; }
            }
            return data;
        }

        private static byte[] EntryBytes(ZipArchiveEntry e)
        {
            using (Stream s = e.Open())
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // ── Parse the (unwrapped) epub ────────────────────────────────────
        private TextDoc ParseEpub(ZipArchive zip)
        {
            if (ContentEncrypted(zip)) return new TextDoc { DrmProtected = true };

            string opfPath = OpfPath(zip);
            if (opfPath == null) return new TextDoc();
            string opfXml = TextParsing.ReadZipEntry(zip, opfPath);
            if (opfXml == null) return new TextDoc();
            string opfDir = DirOf(opfPath);

            string title = "", author = "", publisher = "", ncxId = null, navHref = null;
            Dictionary<string, string> manifest = new Dictionary<string, string>(); // id → href
            Dictionary<string, string> mediaType = new Dictionary<string, string>();
            List<string> spine = new List<string>();
            try
            {
                XDocument xd = XDocument.Parse(opfXml);
                foreach (XElement el in xd.Descendants())
                {
                    string ln = el.Name.LocalName;
                    if (ln == "title" && title == "") title = (el.Value ?? "").Trim();
                    else if (ln == "creator" && author == "") author = (el.Value ?? "").Trim();
                    else if (ln == "publisher" && publisher == "") publisher = (el.Value ?? "").Trim();
                    else if (ln == "item")
                    {
                        string id = (string)el.Attribute("id");
                        string href = (string)el.Attribute("href");
                        string mt = (string)el.Attribute("media-type");
                        string props = (string)el.Attribute("properties");
                        if (id != null && href != null) { manifest[id] = href; mediaType[id] = mt ?? ""; }
                        if (props != null && props.Split(' ').Contains("nav")) navHref = href;
                    }
                    else if (ln == "itemref")
                    {
                        string idref = (string)el.Attribute("idref");
                        if (idref != null) spine.Add(idref);
                    }
                    else if (ln == "spine")
                    {
                        string toc = (string)el.Attribute("toc");
                        if (toc != null) ncxId = toc;
                    }
                }
            }
            catch { }

            // Build the text from the spine, tracking each file's start offset
            // and its element-id offsets (for #fragment TOC targets).
            StringBuilder full = new StringBuilder();
            Dictionary<string, int> fileStart = new Dictionary<string, int>();
            Dictionary<string, Dictionary<string, int>> fileIds = new Dictionary<string, Dictionary<string, int>>();
            List<(int Level, string Title, int Offset)> hn = new List<(int, string, int)>();

            foreach (string idref in spine)
            {
                if (!manifest.TryGetValue(idref, out string href)) continue;
                string entryPath = TextParsing.ResolvePath(opfDir, href);
                string html = TextParsing.ReadZipEntry(zip, entryPath);
                if (html == null) continue;

                TextParsing.Assemble(TextParsing.HtmlBlocks(html), out string text, out var heads, out var ids);
                int start = full.Length;
                fileStart[entryPath] = start;
                fileIds[entryPath] = ids;
                foreach (var h in heads) hn.Add((h.Level, h.Title, start + h.Offset));
                full.Append(text).Append("\n\n");
            }
            string body = full.ToString().TrimEnd('\n');

            // Structure: TOC (NCX preferred, then nav), resolved to offsets;
            // fall back to <hN>.
            List<(int Level, string Title, int Offset)> headings = null;
            string ncxHref = (ncxId != null && manifest.ContainsKey(ncxId)) ? manifest[ncxId]
                : manifest.FirstOrDefault(kv => mediaType[kv.Key].Contains("dtbncx")).Value;
            if (ncxHref != null)
            {
                string ncxPath = TextParsing.ResolvePath(opfDir, ncxHref);
                headings = ResolveToc(ParseNcx(TextParsing.ReadZipEntry(zip, ncxPath)), DirOf(ncxPath), fileStart, fileIds);
            }
            if ((headings == null || headings.Count == 0) && navHref != null)
            {
                string navPath = TextParsing.ResolvePath(opfDir, navHref);
                headings = ResolveToc(ParseNav(TextParsing.ReadZipEntry(zip, navPath)), DirOf(navPath), fileStart, fileIds);
            }
            if (headings == null || headings.Count == 0) headings = hn;
            headings = headings.OrderBy(h => h.Offset).ToList();

            // Print-page markers (NCX pageList preferred, then EPUB3 nav
            // page-list), resolved to char offsets the same way as the TOC.
            List<(int Level, string Title, string Src)> pageToc = null;
            string pageTocDir = null;
            if (ncxHref != null)
            {
                string ncxPath = TextParsing.ResolvePath(opfDir, ncxHref);
                pageToc = ParseNcxPages(TextParsing.ReadZipEntry(zip, ncxPath));
                if (pageToc != null && pageToc.Count > 0) pageTocDir = DirOf(ncxPath);
            }
            if ((pageToc == null || pageToc.Count == 0) && navHref != null)
            {
                string navPath = TextParsing.ResolvePath(opfDir, navHref);
                pageToc = ParseNavPages(TextParsing.ReadZipEntry(zip, navPath));
                if (pageToc != null && pageToc.Count > 0) pageTocDir = DirOf(navPath);
            }
            var pages = new List<(string Label, int Offset)>();
            if (pageToc != null && pageToc.Count > 0 && pageTocDir != null)
            {
                foreach (var r in ResolveToc(pageToc, pageTocDir, fileStart, fileIds))
                    pages.Add((r.Title, r.Offset));
                pages = pages.OrderBy(p => p.Offset).ToList();
            }

            return new TextDoc { Text = body, Headings = headings, Pages = pages, Title = title, Author = author, Publisher = publisher };
        }

        // ── DRM (only content encryption counts; fonts are obfuscation) ───
        private static bool ContentEncrypted(ZipArchive zip)
        {
            string enc = TextParsing.ReadZipEntry(zip, "META-INF/encryption.xml");
            if (string.IsNullOrEmpty(enc)) return false;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(enc, "URI=\"([^\"]+)\""))
            {
                string ext = Path.GetExtension(m.Groups[1].Value).ToLowerInvariant();
                if (Array.IndexOf(FontExts, ext) < 0) return true; // a non-font resource is encrypted
            }
            return false;
        }

        // ── Locate the OPF via container.xml (don't assume a path) ────────
        private static string OpfPath(ZipArchive zip)
        {
            string container = TextParsing.ReadZipEntry(zip, "META-INF/container.xml");
            if (container != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(container, "full-path=\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
            ZipArchiveEntry opf = zip.Entries.FirstOrDefault(e => e.FullName.ToLowerInvariant().EndsWith(".opf"));
            return opf?.FullName;
        }

        private static string DirOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(0, i + 1) : "";
        }

        // ── TOC parsing ───────────────────────────────────────────────────
        private static List<(int Level, string Title, string Src)> ParseNcx(string ncxXml)
        {
            var list = new List<(int, string, string)>();
            if (string.IsNullOrEmpty(ncxXml)) return list;
            try
            {
                XmlReaderSettings st = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
                using (XmlReader r = XmlReader.Create(new StringReader(ncxXml), st))
                {
                    int depth = 0; bool inLabel = false; StringBuilder label = new StringBuilder();
                    while (r.Read())
                    {
                        string ln = r.LocalName;
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (ln == "navPoint") { depth++; }
                            else if (ln == "navLabel") { inLabel = true; label.Clear(); }
                            else if (ln == "content")
                            {
                                string src = r.GetAttribute("src");
                                if (src != null) list.Add((depth, label.ToString().Trim(), src));
                            }
                        }
                        else if ((r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA) && inLabel)
                            label.Append(r.Value);
                        else if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (ln == "navLabel") inLabel = false;
                            else if (ln == "navPoint" && depth > 0) depth--;
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<(int Level, string Title, string Src)> ParseNav(string navXml)
        {
            var list = new List<(int, string, string)>();
            if (string.IsNullOrEmpty(navXml)) return list;
            try
            {
                XmlReaderSettings st = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
                using (XmlReader r = XmlReader.Create(new StringReader(navXml), st))
                {
                    int depth = 0; bool inToc = false, inA = false; string href = null; StringBuilder txt = new StringBuilder();
                    while (r.Read())
                    {
                        string ln = r.LocalName;
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (ln == "nav")
                            {
                                string type = r.GetAttribute("type", "http://www.idpf.org/2007/ops") ?? r.GetAttribute("epub:type");
                                inToc = type == null || type.Contains("toc");
                            }
                            else if (inToc && ln == "ol") depth++;
                            else if (inToc && ln == "a") { inA = true; href = r.GetAttribute("href"); txt.Clear(); }
                        }
                        else if ((r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA) && inA)
                            txt.Append(r.Value);
                        else if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (inToc && ln == "ol" && depth > 0) depth--;
                            else if (inToc && ln == "a") { if (href != null) list.Add((depth, txt.ToString().Trim(), href)); inA = false; }
                            else if (ln == "nav") inToc = false;
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // NCX pageList → (0, page label, src). Each <pageTarget> carries a
        // <navLabel><text> and a <content src="…">.
        private static List<(int Level, string Title, string Src)> ParseNcxPages(string ncxXml)
        {
            var list = new List<(int, string, string)>();
            if (string.IsNullOrEmpty(ncxXml)) return list;
            try
            {
                XmlReaderSettings st = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
                using (XmlReader r = XmlReader.Create(new StringReader(ncxXml), st))
                {
                    bool inTarget = false, inLabel = false; StringBuilder label = new StringBuilder();
                    while (r.Read())
                    {
                        string ln = r.LocalName;
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (ln == "pageTarget") { inTarget = true; label.Clear(); }
                            else if (ln == "navLabel" && inTarget) inLabel = true;
                            else if (ln == "content" && inTarget)
                            {
                                string src = r.GetAttribute("src");
                                if (src != null) list.Add((0, label.ToString().Trim(), src));
                            }
                        }
                        else if ((r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA) && inLabel)
                            label.Append(r.Value);
                        else if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (ln == "navLabel") inLabel = false;
                            else if (ln == "pageTarget") inTarget = false;
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // EPUB3 nav with epub:type="page-list" → (0, page label, href).
        private static List<(int Level, string Title, string Src)> ParseNavPages(string navXml)
        {
            var list = new List<(int, string, string)>();
            if (string.IsNullOrEmpty(navXml)) return list;
            try
            {
                XmlReaderSettings st = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
                using (XmlReader r = XmlReader.Create(new StringReader(navXml), st))
                {
                    bool inPages = false, inA = false; string href = null; StringBuilder txt = new StringBuilder();
                    while (r.Read())
                    {
                        string ln = r.LocalName;
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (ln == "nav")
                            {
                                string type = r.GetAttribute("type", "http://www.idpf.org/2007/ops") ?? r.GetAttribute("epub:type");
                                inPages = type != null && type.Contains("page-list");
                            }
                            else if (inPages && ln == "a") { inA = true; href = r.GetAttribute("href"); txt.Clear(); }
                        }
                        else if ((r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA) && inA)
                            txt.Append(r.Value);
                        else if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (inPages && ln == "a") { if (href != null) list.Add((0, txt.ToString().Trim(), href)); inA = false; }
                            else if (ln == "nav") inPages = false;
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<(int Level, string Title, int Offset)> ResolveToc(
            List<(int Level, string Title, string Src)> toc, string tocDir,
            Dictionary<string, int> fileStart, Dictionary<string, Dictionary<string, int>> fileIds)
        {
            var result = new List<(int, string, int)>();
            foreach (var t in toc)
            {
                // Some producers double-encode the label, so the unescaped text is
                // itself markup (e.g. "<span xml:lang=…>1</span>…"). Strip any tags
                // to the visible text before using it as a heading/page title.
                string label = StripTags(t.Title);
                if (string.IsNullOrWhiteSpace(label)) continue;
                string src = t.Src; string frag = null;
                int hash = src.IndexOf('#');
                if (hash >= 0) { frag = src.Substring(hash + 1); src = src.Substring(0, hash); }
                string entryPath = TextParsing.ResolvePath(tocDir, src);
                if (!fileStart.TryGetValue(entryPath, out int baseOff)) continue;
                int off = baseOff;
                if (frag != null && fileIds.TryGetValue(entryPath, out var ids) && ids.TryGetValue(frag, out int fo))
                    off = baseOff + fo;
                result.Add((t.Level, label, off));
            }
            return result;
        }

        /// <summary>Removes markup left in a TOC label and collapses whitespace.
        /// Handles real HTML tags (&lt;span&gt;…) and a broken-producer variant that
        /// emits tags as "{!{span…}!}" literally (seen in a real Vietnamese book).</summary>
        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            bool hasAngle = s.IndexOf('<') >= 0;
            bool hasBrace = s.IndexOf("{!{", StringComparison.Ordinal) >= 0;
            if (!hasAngle && !hasBrace) return s;
            if (hasAngle) s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
            if (hasBrace) s = System.Text.RegularExpressions.Regex.Replace(s, @"\{!\{.*?\}!\}", "");
            return System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
        }
    }
}
