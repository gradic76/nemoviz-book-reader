using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>EPUB 3 Media Overlays — a narrated EPUB, which is the same kind
    /// of book as a text+audio DAISY and is treated as one.
    ///
    /// <para><b>Why this is small.</b> The SMIL is structurally identical to
    /// DAISY 3: a <c>&lt;par&gt;</c> pairing <c>&lt;text src="…#id"/&gt;</c> with
    /// <c>&lt;audio src clipBegin clipEnd/&gt;</c>. So the join itself is
    /// <see cref="DaisySync"/>'s, unchanged, and the text side is
    /// <c>TextParsing.Assemble</c>, which was always generic HTML rather than
    /// anything DTBook-specific. What is written here is only what EPUB does
    /// differently: where the reading order comes from, and which SMIL belongs to
    /// which document.</para>
    ///
    /// <para><b>Three differences from DAISY, all of them in the packaging.</b>
    /// The reading order is the OPF spine rather than an NCX. Each content
    /// document names its own overlay through a <c>media-overlay</c> attribute on
    /// its manifest entry, instead of there being one SMIL per audio file. And
    /// the text is XHTML, so ids sit on ordinary spans rather than on DTBook
    /// elements.</para>
    ///
    /// <para><b>media:duration is not to be trusted.</b> Measured on the two
    /// samples: one declares <c>00:00:07.299</c> for an eighty-megabyte book and
    /// the other <c>00:00:00</c>. Durations come from the audio files themselves,
    /// the same rule §8c had to adopt for DAISY.</para></summary>
    internal static class EpubOverlayImporter
    {
        private static readonly Regex RxItem = new Regex(
            @"<item\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxItemRef = new Regex(
            @"<itemref\b[^>]*\bidref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxAttr = new Regex(
            @"\b([a-zA-Z:\-]+)\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled);

        /// <summary>True if this extracted EPUB carries media overlays — i.e. it
        /// is a narrated book and not merely a document.</summary>
        public static bool HasOverlays(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder, "*.smil", SearchOption.AllDirectories).Any()
                    && Directory.EnumerateFiles(folder, "*.mp3", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        /// <summary>Turns an extracted EPUB into a hybrid book: content.txt, the
        /// sync map, and the audio timeline the transport plays. Returns false and
        /// changes nothing if the book turns out not to be one.</summary>
        public static bool Setup(BookData book, string folder)
        {
            if (book == null || folder == null) return false;
            try
            {
                string opf = FindOpf(folder);
                if (opf == null) return false;
                string opfDir = Path.GetDirectoryName(opf);
                string xml = File.ReadAllText(opf);

                // manifest: id → (href, media-overlay id)
                var byId = new Dictionary<string, (string Href, string Overlay)>(StringComparer.OrdinalIgnoreCase);
                // …and, on the way past, the two things a table of contents can
                // live in: the EPUB3 nav document (properties="nav") and the
                // EPUB2 NCX (media-type application/x-dtbncx+xml). A narrated
                // book needs them for the reason §10h gives — its chapters are
                // named nowhere else.
                string navHref = null, ncxHrefByType = null;
                var hrefById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in RxItem.Matches(xml))
                {
                    var a = Attrs(m.Value);
                    if (!a.TryGetValue("id", out string id) || !a.TryGetValue("href", out string href)) continue;
                    a.TryGetValue("media-overlay", out string ov);
                    byId[id] = (href, ov);
                    hrefById[id] = href;
                    if (a.TryGetValue("properties", out string props) && props != null
                        && props.Split(' ').Contains("nav")) navHref = href;
                    if (a.TryGetValue("media-type", out string mt) && mt != null
                        && mt.IndexOf("dtbncx", StringComparison.OrdinalIgnoreCase) >= 0)
                        ncxHrefByType = href;
                }
                // <spine toc="ncx"> names the NCX by id, which is the older and
                // more reliable way of finding it than sniffing media types.
                string ncxHref = null;
                Match sm = Regex.Match(xml, @"<spine\b[^>]*\btoc\s*=\s*[""']([^""']+)[""']",
                                       RegexOptions.IgnoreCase);
                if (sm.Success && hrefById.TryGetValue(sm.Groups[1].Value, out string byIdHref))
                    ncxHref = byIdHref;
                if (ncxHref == null) ncxHref = ncxHrefByType;

                // spine: the reading order, which is the only order that matters
                var docs = new List<string>();
                var smils = new List<string>();
                foreach (Match m in RxItemRef.Matches(xml))
                {
                    if (!byId.TryGetValue(m.Groups[1].Value, out var item)) continue;
                    string path = Resolve(opfDir, item.Href);
                    if (path == null || !File.Exists(path)) continue;
                    docs.Add(path);
                    if (!string.IsNullOrEmpty(item.Overlay)
                        && byId.TryGetValue(item.Overlay, out var ovItem))
                    {
                        string sp = Resolve(opfDir, ovItem.Href);
                        if (sp != null && File.Exists(sp)) smils.Add(sp);
                    }
                }
                if (docs.Count == 0 || smils.Count == 0) return false;

                // ── the text half ────────────────────────────────────────────
                var full = new StringBuilder();
                var headings = new List<(int Level, string Title, int Offset)>();
                var syncIds = new Dictionary<string, int>();
                // Per-document, and keyed by the path on disk, because that is what
                // a TOC href resolves to here. The flat syncIds above cannot serve:
                // it keeps the FIRST of any repeated id across the whole book, and
                // "top" or "start" appears once per chapter in a great many books.
                var fileStart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var fileIds = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (string d in docs)
                {
                    string raw;
                    try { raw = File.ReadAllText(d); } catch { continue; }
                    TextParsing.Assemble(TextParsing.HtmlBlocks(raw),
                                         out string text, out var heads, out var ids);
                    if (text.Length == 0) continue;
                    int start = full.Length;
                    fileStart[d] = start;
                    fileIds[d] = ids;
                    foreach (var h in heads) headings.Add((h.Level, h.Title, start + h.Offset));
                    foreach (var kv in ids)
                        if (!syncIds.ContainsKey(kv.Key)) syncIds[kv.Key] = start + kv.Value;
                    full.Append(text).Append("\n\n");
                }

                // ── the chapters ─────────────────────────────────────────────
                // The TOC wins over <hN>, exactly as EpubParser decides it for a
                // document (§8e: raw headings are wildly inconsistent, and a
                // narrated book routinely has none at all). Without this, Go To and
                // the seek step offered the producer's audio file names — "aud001",
                // "aud002" — which is not something anyone can navigate a book by.
                // NCX first, then the EPUB3 nav, then whatever <hN> yielded.
                var toc = ResolveTocFile(ncxHref, opfDir, true, fileStart, fileIds);
                if (toc.Count == 0) toc = ResolveTocFile(navHref, opfDir, false, fileStart, fileIds);
                if (toc.Count > 0) headings = toc.OrderBy(h => h.Offset).ToList();
                var doc = new TextDoc
                {
                    Text = full.ToString().TrimEnd('\n'),
                    Headings = headings,
                    SyncIds = syncIds,
                };
                if (doc.Text.Length == 0 || syncIds.Count == 0) return false;

                // ── the audio half ───────────────────────────────────────────
                // In the order the SMILs call for them, not alphabetically: a
                // producer's file names are not a promise about sequence.
                List<DaisySync.Pair> pairs = DaisySync.ReadPairs(folder,
                    smils.Select(Path.GetFileName).ToList());
                if (pairs.Count == 0) return false;

                var order = new List<string>();
                foreach (DaisySync.Pair p in pairs)
                {
                    string name = Path.GetFileName(p.AudioFile ?? "");
                    if (name.Length > 0 && !order.Contains(name, StringComparer.OrdinalIgnoreCase))
                        order.Add(name);
                }
                if (order.Count == 0) return false;

                // The audio is moved to the ROOT of the book folder. A chapter is
                // stored as a bare file name and the player looks for it beside
                // Book.ini, but an EPUB keeps its audio in a subfolder — so the
                // playlist pointed at files that were not there, and the book
                // imported perfectly and would not play a note.
                var ordered = new List<string>();
                foreach (string name in order)
                {
                    string path = Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories)
                                           .FirstOrDefault();
                    if (path == null) continue;
                    string root = Path.Combine(folder, name);
                    if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                    {
                        try { if (!File.Exists(root)) File.Move(path, root); }
                        catch { root = path; }        // keep the one that exists
                    }
                    ordered.Add(root);
                }
                if (ordered.Count == 0) return false;
                // The same builder a DAISY uses, which reads each file's real
                // duration — never media:duration, for the reason in the summary.
                book.BuildChaptersFromFolder(ordered.ToArray());

                // Cleaning moves every offset with the text, sync ids included, so
                // nothing points past its target afterwards.
                TextCleaner.CleanDoc(doc);

                var startAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < book.Chapters.Count && i < book.Offsets.Count; i++)
                    startAt[book.Chapters[i].FileName] = book.Offsets[i];

                SyncMap map = DaisySync.Build(pairs, doc.SyncIds,
                    f => startAt.TryGetValue(Path.GetFileName(f ?? ""), out double s) ? s : -1);
                if (map.IsEmpty) return false;

                // The book's own name and author, which nothing else on this path
                // fills in — the document branch does it and this one skips past
                // it, so the shelf and the info box had nothing to show. Set
                // before the fork below, because both outcomes want them.
                string title = Meta(xml, "title");
                string author = Meta(xml, "creator");
                if (!string.IsNullOrWhiteSpace(title)) book.Title = title;
                if (!string.IsNullOrWhiteSpace(author)) book.Author = author;
                string pub = Meta(xml, "publisher");
                if (!string.IsNullOrWhiteSpace(pub)) book.Publisher = pub;
                book.Format = BookData.FriendlyFormatName(".epub");

                // ── is there a book here, or only a skeleton? ────────────────
                // Some producers ship an EPUB3 whose text layer exists only to
                // hang the audio on. Measured on the Granta sample: 10 787
                // self-closing <span id="dtb_…"/> anchors, NONE of them with any
                // content, 712 characters of readable text in the whole book —
                // the 21 chapter titles and the nav document's own list. The
                // stylesheet is 19 bytes of div{display:none} and the producer
                // signs itself tpbnarrator.res. The text was never in the file.
                //
                // Importing that as a hybrid sets two traps, and the second is
                // Gordan's (2026-08-04) and the worse one. A reader who turns on
                // braille or the reading window is promised text there is none
                // of. And a reader who opens an .epub AT ALL expects a book to
                // read — they may not know narrated EPUBs exist — and gets an
                // audiobook instead. So it comes in as ordinary multi-file audio.
                //
                // What survives is the navigation, which is the one thing worth
                // keeping: the chapter titles go onto the audio clock through the
                // sync map we have just built, in the same store a CUE sheet uses
                // (§8f — chapters at times; the M4b name there is historical).
                // Go To then lists "A Casa Abandonada" instead of "aud005.mp3".
                //
                // The test is the body: how much text there is BEYOND the chapter
                // titles themselves, since a skeleton still carries those. The two
                // real samples sit three orders of magnitude apart — about 40
                // characters of body against ~125 800 — so the threshold is not a
                // fine judgement and is not trying to be.
                int titleChars = 0;
                foreach (var h in doc.Headings) titleChars += (h.Title ?? "").Length;
                int bodyChars = doc.Text.Length - titleChars;
                if (bodyChars < Math.Max(200, doc.Headings.Count * 20))
                {
                    var marks = new List<(string Title, double Position)>();
                    foreach (var h in doc.Headings)
                    {
                        double at = DaisySync.SecondsAt(map, h.Offset);
                        if (at >= 0) marks.Add((h.Title, at));
                    }
                    marks.Sort((x, y) => x.Position.CompareTo(y.Position));
                    if (marks.Count > 0) book.SetM4bChapters(marks);
                    // No content.txt and no sync.map: §8c makes writing the text
                    // without a map the thing that turns a narrated book silent,
                    // and here the mirror of it applies — a map with no text to
                    // follow is what would make this a hybrid.
                    return true;
                }

                File.WriteAllText(Path.Combine(folder, "content.txt"),
                                  doc.Text, new UTF8Encoding(false));
                book.TextCleaned = true;
                book.SetTextHeadings(doc.Headings);
                book.TextLanguage = LanguageDetector.Resolve(MetaLanguage(xml), doc.Text);

                book.SaveSyncMap(map);
                return true;
            }
            catch { return false; }
        }

        private static string MetaLanguage(string opfXml) { return Meta(opfXml, "language"); }

        /// <summary>A Dublin Core field from the package document. Matches with or
        /// without the dc: prefix — both are met in the wild.</summary>
        /// <summary>Reads one table of contents and turns it into headings at
        /// character offsets, or an empty list if it is not there or says nothing.
        ///
        /// <para>The parsing and the offset resolution are <c>EpubParser</c>'s —
        /// the same NCX and nav readers a plain EPUB document goes through, and
        /// the same <c>ResolveToc</c>. Only the path space differs: that parser
        /// works inside the zip, this one on a book already unpacked, which is
        /// why <c>ResolveToc</c> takes the resolver as an argument now.</para></summary>
        private static List<(int Level, string Title, int Offset)> ResolveTocFile(
            string href, string opfDir, bool ncx,
            Dictionary<string, int> fileStart, Dictionary<string, Dictionary<string, int>> fileIds)
        {
            var empty = new List<(int, string, int)>();
            try
            {
                if (string.IsNullOrEmpty(href)) return empty;
                string path = Resolve(opfDir, href);
                if (path == null || !File.Exists(path)) return empty;
                string xml = File.ReadAllText(path);
                var toc = ncx ? EpubParser.ParseNcx(xml) : EpubParser.ParseNav(xml);
                if (toc == null || toc.Count == 0) return empty;
                return EpubParser.ResolveToc(toc, Path.GetDirectoryName(path),
                                             fileStart, fileIds, Resolve);
            }
            catch { return empty; }
        }

        private static string Meta(string opfXml, string name)
        {
            Match m = Regex.Match(opfXml, @"<(?:dc:)?" + name + @"\b[^>]*>([^<]+)<",
                                  RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            return v.Length == 0 ? null : v;
        }

        private static Dictionary<string, string> Attrs(string tag)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in RxAttr.Matches(tag)) d[a.Groups[1].Value] = a.Groups[2].Value;
            return d;
        }

        private static string Resolve(string baseDir, string href)
        {
            try
            {
                if (string.IsNullOrEmpty(href)) return null;
                int hash = href.IndexOf('#');
                if (hash >= 0) href = href.Substring(0, hash);
                href = Uri.UnescapeDataString(href).Replace('/', Path.DirectorySeparatorChar);
                return Path.GetFullPath(Path.Combine(baseDir, href));
            }
            catch { return null; }
        }

        /// <summary>The package document, from META-INF/container.xml where it is
        /// declared, or by looking if that is missing — a producer's idea of
        /// where the OPF lives is not always the specification's.</summary>
        private static string FindOpf(string folder)
        {
            try
            {
                string container = Path.Combine(folder, "META-INF", "container.xml");
                if (File.Exists(container))
                {
                    Match m = Regex.Match(File.ReadAllText(container),
                        @"full-path\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string p = Resolve(folder, m.Groups[1].Value);
                        if (p != null && File.Exists(p)) return p;
                    }
                }
                return Directory.EnumerateFiles(folder, "*.opf", SearchOption.AllDirectories)
                                .FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
