using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Nemoviz_Book_Reader
{
    public enum DaisyNavType { Heading, Page }

    /// <summary>One navigable point in a DAISY book: a heading (with a depth
    /// level) or a page. Resolved to a concrete audio file + start offset.</summary>
    public class DaisyNavPoint
    {
        public DaisyNavType Type;
        public int Level;          // heading depth 1..6; 0 for a page
        public string Label;       // heading title or page number, plain text
        public string AudioFile;   // audio filename as referenced in the SMIL (src)
        public double ClipBegin;   // start offset within that audio file, in seconds
    }

    /// <summary>
    /// The parsed structure of a DAISY audio book — enough to overlay
    /// navigation (headings, pages) onto the plain concatenated-audio timeline
    /// the player already uses. Supports DAISY 2.02 (NCC.HTML + SMIL) and
    /// DAISY 3 / Z39.86 (OPF + NCX + SMIL). Audio playback itself is unchanged:
    /// AudioPlayOrder gives the files in reading order; each nav point maps to
    /// (audio file, clip-begin) which the player turns into a virtual position.
    /// </summary>
    public class DaisyBook
    {
        public string ContentRoot;                 // folder holding the nav file (and audio)
        public string Version;                     // "2.02" or "3.0"
        public string Title;
        public string Author;
        public string Publisher;                   // dc:publisher (print-edition publisher)
        public string Language;                    // dc:language, as the producer declared it
        public string Producer;                    // ncc:producer (audio-producing institution)
        /// <summary>dc:description, cleaned. Rarer in DAISY than in EPUB — the
        /// producing institutions fill in what they need for navigation and often
        /// nothing else — but free where it is there.</summary>
        public string Description;
        public string Isbn;                        // dc:identifier, when it is one
        /// <summary>dc:date, in whatever shape the producer wrote it. DAISY 2.02
        /// also has ncc:sourceDate — the date of the PRINT edition the talking
        /// book was made from, which is the one a reader means by "the year", so
        /// it is preferred where both exist.</summary>
        public string Date;
        public string TotalTime;                   // as declared in metadata (string)
        public List<string> AudioPlayOrder = new List<string>();
        public List<DaisyNavPoint> Headings = new List<DaisyNavPoint>();
        public List<DaisyNavPoint> Pages = new List<DaisyNavPoint>();
    }

    public static class DaisyParser
    {
        private const RegexOptions RO = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        /// <summary>Detects and parses a DAISY book rooted anywhere under
        /// <paramref name="folder"/> (the nav file may sit in a subfolder).
        /// Returns null if the folder is not a DAISY book. Never throws — a
        /// malformed book yields whatever could be parsed (possibly null).</summary>
        public static DaisyBook TryParse(string folder)
        {
            try
            {
                DaisyBook book = null;
                string ncc = FindFile(folder, n => n.Equals("ncc.html", StringComparison.OrdinalIgnoreCase));
                if (ncc != null)
                    book = Parse202(ncc);
                else
                {
                    string ncx = FindFile(folder, n => n.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase));
                    string opf = FindFile(folder, n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
                    if (ncx != null || opf != null)
                        book = Parse3(ncx, opf);
                }
                if (book != null)
                    book.Title = PrettifyTitle(book.Title);
                return book;
            }
            catch
            {
                // Defensive: a broken book must not crash import/scan.
            }
            return null;
        }

        /// <summary>Shows the producer's title as-is (per Gordan: for produced
        /// formats like DAISY we surface the real metadata rather than guessing
        /// — a bad producer's "Untitled Obi Project" is shown, not rescued).
        /// The only touch-up is prettifying a purely underscore-separated
        /// string ("Trop_de_chefs…") into spaces for readability.</summary>
        private static string PrettifyTitle(string title)
        {
            string t = (title ?? "").Trim();
            if (t.IndexOf(' ') < 0 && t.IndexOf('_') >= 0)
                t = t.Replace('_', ' ').Trim();
            return t;
        }

        /// <summary>True if the folder looks like a DAISY book (cheap check).</summary>
        public static bool IsDaisy(string folder)
        {
            return FindFile(folder, n => n.Equals("ncc.html", StringComparison.OrdinalIgnoreCase)) != null
                || FindFile(folder, n => n.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase)) != null
                || FindFile(folder, n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)) != null;
        }

        // ──────────────────────────────────────────────
        // DAISY 2.02 (NCC.HTML + SMIL)
        // ──────────────────────────────────────────────
        private static DaisyBook Parse202(string nccPath)
        {
            string root = Path.GetDirectoryName(nccPath);
            var files = FileIndex(root);
            var smilCache = new Dictionary<string, SmilData>(StringComparer.OrdinalIgnoreCase);

            string ncc = ReadSmart(nccPath);
            var book = new DaisyBook { ContentRoot = root, Version = "2.02" };
            book.Title = FirstNonEmpty(MetaContent(ncc, "dc:title"), TagText(ncc, "title"));
            book.Author = MetaContent(ncc, "dc:creator");
            book.Publisher = MetaContent(ncc, "dc:publisher");
            book.Language = MetaContent(ncc, "dc:language");
            book.Producer = MetaContent(ncc, "ncc:producer");
            book.Description = BookDescription.Clean(MetaContent(ncc, "dc:description"));
            book.Isbn = BookDescription.NormaliseIsbn(MetaContent(ncc, "dc:identifier"));
            book.Date = FirstNonEmpty(MetaContent(ncc, "ncc:sourceDate"),
                                      MetaContent(ncc, "dc:date"));
            book.TotalTime = MetaContent(ncc, "ncc:totalTime");

            // Headings: <h1..h6 ...><a href="file.smil#frag">Title</a></h1..>
            foreach (Match m in Regex.Matches(ncc,
                @"<(h[1-6])\b[^>]*>\s*<a\b[^>]*?href\s*=\s*""([^""]+)""[^>]*>(.*?)</a>", RO))
            {
                int level = m.Groups[1].Value[1] - '0';
                var np = ResolveHref(m.Groups[2].Value, files, smilCache);
                if (np == null) continue;
                np.Type = DaisyNavType.Heading;
                np.Level = level;
                np.Label = CleanText(m.Groups[3].Value);
                book.Headings.Add(np);
            }

            // Pages: <span ... class="...page...">...<a href="file.smil#frag">N</a></span>
            foreach (Match m in Regex.Matches(ncc,
                @"<span\b[^>]*?class\s*=\s*""[^""]*page[^""]*""[^>]*>\s*<a\b[^>]*?href\s*=\s*""([^""]+)""[^>]*>(.*?)</a>", RO))
            {
                var np = ResolveHref(m.Groups[1].Value, files, smilCache);
                if (np == null) continue;
                np.Type = DaisyNavType.Page;
                np.Level = 0;
                np.Label = CleanText(m.Groups[2].Value);
                book.Pages.Add(np);
            }

            book.AudioPlayOrder = BuildAudioOrder202(ncc, root, files, smilCache);
            return book;
        }

        private static List<string> BuildAudioOrder202(string ncc, string root,
            Dictionary<string, string> files, Dictionary<string, SmilData> smilCache)
        {
            // Play order of the SMIL files: master.smil if present, else the
            // order the NCC first references them in.
            var smilOrder = new List<string>();
            string master = files.Keys.FirstOrDefault(k => k.Equals("master.smil", StringComparison.OrdinalIgnoreCase));
            if (master != null)
            {
                string mtext = ReadSmart(files[master]);
                foreach (Match r in Regex.Matches(mtext, @"<ref\b[^>]*?src\s*=\s*""([^""#]+)""", RO))
                    AddDistinct(smilOrder, r.Groups[1].Value);
            }
            if (smilOrder.Count == 0)
            {
                foreach (Match h in Regex.Matches(ncc, @"href\s*=\s*""([^""#]+\.smil)", RO))
                    AddDistinct(smilOrder, h.Groups[1].Value);
            }

            var audio = new List<string>();
            foreach (string sf in smilOrder)
            {
                var sd = GetSmil(sf, files, smilCache);
                if (sd == null) continue;
                foreach (string a in sd.AudioOrder) AddDistinct(audio, a);
            }
            return audio;
        }

        // ──────────────────────────────────────────────
        // DAISY 3 / Z39.86 (OPF + NCX + SMIL)
        // ──────────────────────────────────────────────
        private static DaisyBook Parse3(string ncxPath, string opfPath)
        {
            string root = Path.GetDirectoryName(ncxPath ?? opfPath);
            var files = FileIndex(root);
            var smilCache = new Dictionary<string, SmilData>(StringComparer.OrdinalIgnoreCase);
            var book = new DaisyBook { ContentRoot = root, Version = "3.0" };

            // Metadata + audio play order from the OPF (spine → SMIL files).
            if (opfPath != null)
            {
                XmlDocument opf = LoadXml(opfPath);
                book.Title = FirstNonEmpty(MetaByName(opf, "dc:title"), ElemText(opf, "Title"));
                book.Author = FirstNonEmpty(MetaByName(opf, "dc:creator"), ElemText(opf, "Creator"));
                book.Publisher = FirstNonEmpty(MetaByName(opf, "dc:publisher"), ElemText(opf, "Publisher"));
                book.Language = FirstNonEmpty(MetaByName(opf, "dc:language"), ElemText(opf, "Language"));
                book.Producer = MetaByName(opf, "dtb:producer");
                book.Description = BookDescription.Clean(
                    FirstNonEmpty(MetaByName(opf, "dc:description"), ElemText(opf, "Description")));
                book.Isbn = BookDescription.NormaliseIsbn(
                    FirstNonEmpty(MetaByName(opf, "dc:identifier"), ElemText(opf, "Identifier")));
                book.Date = FirstNonEmpty(MetaByName(opf, "dtb:sourceDate"),
                                          MetaByName(opf, "dc:date"),
                                          ElemText(opf, "Date"));
                book.TotalTime = MetaAttr(opf, "dtb:totalTime");

                var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlNode item in ByLocalName(opf, "item"))
                {
                    string id = Attr(item, "id"); string href = Attr(item, "href");
                    if (id != null && href != null) manifest[id] = href;
                }
                var smilOrder = new List<string>();
                foreach (XmlNode iref in ByLocalName(opf, "itemref"))
                {
                    string idref = Attr(iref, "idref");
                    if (idref != null && manifest.TryGetValue(idref, out string href) &&
                        href.EndsWith(".smil", StringComparison.OrdinalIgnoreCase))
                        AddDistinct(smilOrder, Path.GetFileName(href));
                }
                foreach (string sf in smilOrder)
                {
                    var sd = GetSmil(sf, files, smilCache);
                    if (sd == null) continue;
                    foreach (string a in sd.AudioOrder) AddDistinct(book.AudioPlayOrder, a);
                }
            }

            // Navigation from the NCX (navMap headings + optional pageList).
            if (ncxPath != null)
            {
                XmlDocument ncx = LoadXml(ncxPath);
                if (string.IsNullOrEmpty(book.Title))
                    book.Title = ElemText(ncx, "docTitle");

                var navMap = ByLocalName(ncx, "navMap").FirstOrDefault();
                if (navMap != null) WalkNavPoints(navMap, 1, book, files, smilCache);

                var pageList = ByLocalName(ncx, "pageList").FirstOrDefault();
                if (pageList != null)
                    foreach (XmlNode pt in ChildrenByLocalName(pageList, "pageTarget"))
                    {
                        var np = ResolveContentSrc(pt, files, smilCache);
                        if (np == null) continue;
                        np.Type = DaisyNavType.Page; np.Level = 0;
                        np.Label = NavLabelText(pt);
                        book.Pages.Add(np);
                    }
            }

            // Fallback: if the OPF gave no audio order, derive it from the SMILs
            // referenced by the nav points.
            if (book.AudioPlayOrder.Count == 0)
                foreach (var np in book.Headings.Concat(book.Pages))
                    if (np.AudioFile != null) AddDistinct(book.AudioPlayOrder, np.AudioFile);

            return book;
        }

        private static void WalkNavPoints(XmlNode parent, int depth, DaisyBook book,
            Dictionary<string, string> files, Dictionary<string, SmilData> smilCache)
        {
            foreach (XmlNode np in ChildrenByLocalName(parent, "navPoint"))
            {
                var pt = ResolveContentSrc(np, files, smilCache);
                if (pt != null)
                {
                    pt.Type = DaisyNavType.Heading;
                    pt.Level = depth;
                    pt.Label = NavLabelText(np);
                    book.Headings.Add(pt);
                }
                WalkNavPoints(np, depth + 1, book, files, smilCache); // nested = deeper headings
            }
        }

        private static DaisyNavPoint ResolveContentSrc(XmlNode navNode,
            Dictionary<string, string> files, Dictionary<string, SmilData> smilCache)
        {
            var content = ChildrenByLocalName(navNode, "content").FirstOrDefault();
            string src = content != null ? Attr(content, "src") : null;
            return src != null ? ResolveHref(src, files, smilCache) : null;
        }

        private static string NavLabelText(XmlNode navNode)
        {
            var label = ChildrenByLocalName(navNode, "navLabel").FirstOrDefault();
            if (label == null) return "";
            var text = ByLocalName(label, "text").FirstOrDefault();
            return text != null ? CleanText(text.InnerText) : "";
        }

        // ──────────────────────────────────────────────
        // SMIL — resolve a "file.smil#fragment" to (audio file, clip-begin)
        // ──────────────────────────────────────────────
        private class SmilData
        {
            public Dictionary<string, DaisyNavPoint> ByFragment =
                new Dictionary<string, DaisyNavPoint>(StringComparer.OrdinalIgnoreCase);
            public List<string> AudioOrder = new List<string>();
        }

        private static DaisyNavPoint ResolveHref(string href,
            Dictionary<string, string> files, Dictionary<string, SmilData> smilCache)
        {
            if (string.IsNullOrEmpty(href)) return null;
            string file = href; string frag = null;
            int hash = href.IndexOf('#');
            if (hash >= 0) { file = href.Substring(0, hash); frag = href.Substring(hash + 1); }
            file = Path.GetFileName(file);

            var sd = GetSmil(file, files, smilCache);
            if (sd == null) return null;

            if (frag != null && sd.ByFragment.TryGetValue(frag, out DaisyNavPoint hit))
                return new DaisyNavPoint { AudioFile = hit.AudioFile, ClipBegin = hit.ClipBegin };

            // No fragment (or not found): start of the SMIL's first audio.
            if (sd.AudioOrder.Count > 0 && sd.ByFragment.Count > 0)
            {
                var first = sd.ByFragment.Values.First();
                return new DaisyNavPoint { AudioFile = first.AudioFile, ClipBegin = first.ClipBegin };
            }
            return null;
        }

        private static SmilData GetSmil(string smilFileName,
            Dictionary<string, string> files, Dictionary<string, SmilData> smilCache)
        {
            string key = smilFileName.ToLowerInvariant();
            if (smilCache.TryGetValue(key, out SmilData cached)) return cached;
            if (!files.TryGetValue(key, out string path)) { smilCache[key] = null; return null; }

            var sd = ParseSmil(path);
            smilCache[key] = sd;
            return sd;
        }

        private static SmilData ParseSmil(string path)
        {
            var sd = new SmilData();
            string text = ReadSmart(path);

            // Every <audio> with its position, src and clip-begin, in order.
            var audios = new List<AudioRef>();
            foreach (Match a in Regex.Matches(text, @"<audio\b[^>]*>", RO))
            {
                string src = Attr(a.Value, "src");
                if (src == null) continue;
                audios.Add(new AudioRef
                {
                    Start = a.Index,
                    End = a.Index + a.Length,
                    Src = src,
                    Begin = ParseClip(ClipBegin(a.Value))
                });
                AddDistinct(sd.AudioOrder, src);
            }
            if (audios.Count == 0) return sd;

            // Map every id="..." to its section's first audio: the first audio
            // at or after the id's position (so a fragment on a <seq>/<par>/
            // <text> resolves correctly — DAISY 3 puts the nav id on the <seq>,
            // DAISY 2.02 on the <text>). An id sitting inside an <audio> tag
            // maps to that audio.
            foreach (Match id in Regex.Matches(text, @"\bid\s*=\s*""([^""]+)""", RO))
            {
                AudioRef inside = audios.FirstOrDefault(x => id.Index >= x.Start && id.Index <= x.End);
                AudioRef hit = inside ?? audios.FirstOrDefault(x => x.Start >= id.Index);
                if (hit == null) continue;
                sd.ByFragment[id.Groups[1].Value] =
                    new DaisyNavPoint { AudioFile = hit.Src, ClipBegin = hit.Begin };
            }
            return sd;
        }

        private class AudioRef
        {
            public int Start; public int End; public string Src; public double Begin;
        }

        // ──────────────────────────────────────────────
        // Small helpers
        // ──────────────────────────────────────────────
        private static string ClipBegin(string audioTag)
        {
            var m = Regex.Match(audioTag, @"clip-begin\s*=\s*""([^""]*)""", RO);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(audioTag, @"clipBegin\s*=\s*""([^""]*)""", RO);
            return m.Success ? m.Groups[1].Value : "0";
        }

        private static double ParseClip(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            v = v.Trim();
            if (v.StartsWith("npt=", StringComparison.OrdinalIgnoreCase)) v = v.Substring(4).Trim();
            if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 1);
            v = v.Trim();
            if (v.Contains(":"))
            {
                double sec = 0;
                foreach (string part in v.Split(':'))
                {
                    double.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out double p);
                    sec = sec * 60 + p;
                }
                return sec;
            }
            double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d);
            return d;
        }

        private static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, name + @"\s*=\s*""([^""]*)""", RO);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string Attr(XmlNode node, string name)
        {
            if (node == null || node.Attributes == null) return null;
            foreach (XmlAttribute a in node.Attributes)
                if (a.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) return a.Value;
            return null;
        }

        private static string MetaContent(string html, string metaName)
        {
            var m = Regex.Match(html,
                @"<meta\b[^>]*?name\s*=\s*""" + Regex.Escape(metaName) + @"""[^>]*?content\s*=\s*""([^""]*)""", RO);
            if (m.Success) return CleanText(m.Groups[1].Value);
            m = Regex.Match(html,
                @"<meta\b[^>]*?content\s*=\s*""([^""]*)""[^>]*?name\s*=\s*""" + Regex.Escape(metaName) + @"""", RO);
            return m.Success ? CleanText(m.Groups[1].Value) : null;
        }

        private static string TagText(string html, string tag)
        {
            var m = Regex.Match(html, @"<" + tag + @"\b[^>]*>(.*?)</" + tag + ">", RO);
            return m.Success ? CleanText(m.Groups[1].Value) : null;
        }

        private static string CleanText(string s)
        {
            if (s == null) return null;
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = System.Net.WebUtility.HtmlDecode(s);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static void AddDistinct(List<string> list, string value)
        {
            if (value != null && !list.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)))
                list.Add(value);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        private static Dictionary<string, string> FileIndex(string root)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in Directory.GetFiles(root))
                d[Path.GetFileName(p).ToLowerInvariant()] = p;
            return d;
        }

        private static string FindFile(string folder, Func<string, bool> nameMatch)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                foreach (string p in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    if (nameMatch(Path.GetFileName(p))) return p;
            }
            catch { }
            return null;
        }

        // ── encoding-aware text read ──
        private static string ReadSmart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Encoding enc = DetectEncoding(bytes) ?? Encoding.UTF8;
            try { return enc.GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }

        private static Encoding DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
            string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));

            string declared = null;
            var m = Regex.Match(head, @"(?:encoding|charset)\s*=\s*[""']?\s*([A-Za-z0-9\-_]+)", RegexOptions.IgnoreCase);
            if (m.Success) declared = m.Groups[1].Value;

            // Central-European DAISY (Croatian, Serbian, Czech…) is very often
            // mislabeled as iso-8859-1/us-ascii by Windows producers even though
            // the bytes are Windows-1250. Trust the declared language over the
            // (wrong) charset: iso-8859-1 has no č/ć/š/ž, so honoring it would
            // corrupt every title. Detect language from dc:language / xml:lang.
            string lang = null;
            var lm = Regex.Match(head, @"dc:language[^>]*?content\s*=\s*[""']?\s*([A-Za-z\-]+)", RegexOptions.IgnoreCase);
            if (!lm.Success) lm = Regex.Match(head, @"xml:lang\s*=\s*[""']([A-Za-z\-]+)", RegexOptions.IgnoreCase);
            if (!lm.Success) lm = Regex.Match(head, @"<html\b[^>]*?\blang\s*=\s*[""']([A-Za-z\-]+)", RegexOptions.IgnoreCase);
            if (lm.Success) lang = lm.Groups[1].Value.ToLowerInvariant();

            bool declaredLatin1 = declared == null ||
                Regex.IsMatch(declared, @"^(iso-?8859-?1|latin-?1|us-?ascii|ascii|windows-?1252|cp1252)$", RegexOptions.IgnoreCase);
            string[] centralEuropean = { "hr", "sr", "bs", "cs", "sk", "pl", "sl", "hu", "ro" };
            if (declaredLatin1 && lang != null && centralEuropean.Any(c => lang.StartsWith(c)))
                try { return Encoding.GetEncoding(1250); } catch { }

            if (declared != null)
                try { return Encoding.GetEncoding(declared); } catch { }
            return null;
        }

        // ── XML (NCX / OPF) helpers, namespace-agnostic ──
        private static XmlDocument LoadXml(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, settings))
                doc.Load(reader);
            return doc;
        }

        private static IEnumerable<XmlNode> ByLocalName(XmlNode root, string localName)
        {
            foreach (XmlNode n in Descendants(root))
                if (n.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                    yield return n;
        }

        private static IEnumerable<XmlNode> Descendants(XmlNode node)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    yield return child;
                    foreach (XmlNode d in Descendants(child)) yield return d;
                }
            }
        }

        private static IEnumerable<XmlNode> ChildrenByLocalName(XmlNode node, string localName)
        {
            foreach (XmlNode child in node.ChildNodes)
                if (child.NodeType == XmlNodeType.Element &&
                    child.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                    yield return child;
        }

        private static string MetaByName(XmlDocument doc, string name)
        {
            // Dublin Core as elements (<dc:Title>text</dc:Title>).
            foreach (XmlNode n in Descendants(doc.DocumentElement))
                if (n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    ("dc:" + n.LocalName).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return CleanText(n.InnerText);
            return null;
        }

        private static string MetaAttr(XmlDocument doc, string name)
        {
            // <meta name="dtb:totalTime" content="..."/>
            foreach (XmlNode n in ByLocalName(doc.DocumentElement, "meta"))
                if (name.Equals(Attr(n, "name"), StringComparison.OrdinalIgnoreCase))
                    return Attr(n, "content");
            return null;
        }

        private static string ElemText(XmlNode root, string localName)
        {
            var n = ByLocalName(root is XmlDocument d ? d.DocumentElement : root, localName).FirstOrDefault();
            return n != null ? CleanText(n.InnerText) : null;
        }
    }
}
