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
                foreach (Match m in RxItem.Matches(xml))
                {
                    var a = Attrs(m.Value);
                    if (!a.TryGetValue("id", out string id) || !a.TryGetValue("href", out string href)) continue;
                    a.TryGetValue("media-overlay", out string ov);
                    byId[id] = (href, ov);
                }

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
                foreach (string d in docs)
                {
                    string raw;
                    try { raw = File.ReadAllText(d); } catch { continue; }
                    TextParsing.Assemble(TextParsing.HtmlBlocks(raw),
                                         out string text, out var heads, out var ids);
                    if (text.Length == 0) continue;
                    int start = full.Length;
                    foreach (var h in heads) headings.Add((h.Level, h.Title, start + h.Offset));
                    foreach (var kv in ids)
                        if (!syncIds.ContainsKey(kv.Key)) syncIds[kv.Key] = start + kv.Value;
                    full.Append(text).Append("\n\n");
                }
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

                var ordered = new List<string>();
                foreach (string name in order)
                {
                    string path = Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories)
                                           .FirstOrDefault();
                    if (path != null) ordered.Add(path);
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

        private static string MetaLanguage(string opfXml)
        {
            Match m = Regex.Match(opfXml, @"<dc:language[^>]*>([^<]+)<", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
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
