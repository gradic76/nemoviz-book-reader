using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Thin P/Invoke wrapper over liblouis (LGPL 2.1+), the braille translator that
    /// screen readers use. NBR needs only BACK-translation: braille cells → text, so
    /// an electronic-braille book (.brf) can be read aloud by TTS like any other text
    /// book. Tables live in <c>louis\tables</c> beside the executable; a table is
    /// passed by absolute path so liblouis resolves its <c>include</c>s from that
    /// folder without depending on any environment variable.
    ///
    /// Note on the ABI: this Windows build uses <c>__stdcall</c> and a 32-bit
    /// <c>widechar</c> (UCS-4), so buffers marshal as uint[], not ushort[].
    /// </summary>
    public static class LibLouis
    {
        private const string Dll = "liblouis.dll";
        private const CallingConvention Conv = CallingConvention.StdCall;

        [DllImport(Dll, CallingConvention = Conv)]
        private static extern IntPtr lou_version();

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        private static extern int lou_backTranslateString(
            [MarshalAs(UnmanagedType.LPStr)] string tableList,
            uint[] inbuf, ref int inlen,
            uint[] outbuf, ref int outlen,
            ushort[] typeform, byte[] spacing, int mode);

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        private static extern int lou_translateString(
            [MarshalAs(UnmanagedType.LPStr)] string tableList,
            uint[] inbuf, ref int inlen,
            uint[] outbuf, ref int outlen,
            ushort[] typeform, byte[] spacing, int mode);

        [DllImport(Dll, CallingConvention = Conv)]
        private static extern void lou_free();

        /// <summary>Folder holding the bundled tables (next to the executable).</summary>
        public static string TablesFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("louis", "tables")); }
        }

        private static bool? available;

        /// <summary>True if liblouis loaded and its tables are present. Never throws,
        /// so a missing DLL just means braille books can't be read.</summary>
        public static bool Available
        {
            get
            {
                if (available.HasValue) return available.Value;
                try
                {
                    IntPtr v = lou_version();
                    available = v != IntPtr.Zero && Directory.Exists(TablesFolder);
                }
                catch { available = false; }
                return available.Value;
            }
        }

        public static string Version
        {
            get
            {
                try { return Marshal.PtrToStringAnsi(lou_version()) ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>Absolute path of a bundled table file, or null if absent.</summary>
        public static string TablePath(string fileName)
        {
            try
            {
                string p = Path.Combine(TablesFolder, fileName);
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Back-translates braille cells to text. <paramref name="braille"/> holds
        /// Unicode braille patterns (U+2800…U+28FF); <paramref name="tableFile"/> is a
        /// table file name from <see cref="TablesFolder"/>. Returns null when liblouis
        /// or the table is unavailable, or the call fails — callers treat that as
        /// "can't read this book" rather than crashing.
        /// </summary>
        public static string BackTranslate(string braille, string tableFile)
        {
            if (string.IsNullOrEmpty(braille)) return "";
            if (!Available) return null;
            string table = TablePath(tableFile);
            if (table == null) return null;

            try
            {
                uint[] inbuf = new uint[braille.Length];
                for (int i = 0; i < braille.Length; i++) inbuf[i] = braille[i];
                int inlen = inbuf.Length;

                // Back-translation can expand (a contraction becomes a whole word),
                // so allow generous headroom.
                int outlen = Math.Max(64, inlen * 4);
                uint[] outbuf = new uint[outlen];

                int rc = lou_backTranslateString(table, inbuf, ref inlen, outbuf, ref outlen,
                                                 null, null, 0);
                if (rc == 0) return null;

                var sb = new StringBuilder(outlen);
                for (int i = 0; i < outlen; i++)
                {
                    uint c = outbuf[i];
                    if (c == 0) continue;
                    if (c <= 0xFFFF) sb.Append((char)c);
                    else sb.Append(char.ConvertFromUtf32((int)c));
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>Text to braille cells -- the direction NBR does not read books
        /// in, and needs for exactly one thing: asking a table whether it explains
        /// the cells in front of it.
        ///
        /// <para>A .brf declares no standard and no grade, and back-translation
        /// alone cannot tell them apart -- EBAE reading a UEB book produces
        /// confident nonsense that scores WELL, because it expands indicator cells
        /// into contractions and so yields more letters and more common words than
        /// the right table does. Translating the result back and comparing it with
        /// the cells we started from asks the question the other way round, where
        /// the wrong table has nowhere to hide: it wrote "by" as one cell and the
        /// book spells it out.</para>
        ///
        /// <para>Output is in the table's own display convention -- braille ASCII
        /// for the English and French tables, Unicode cells for the Croatian ones --
        /// so a caller comparing it against anything must reduce both sides to dot
        /// patterns first. See <see cref="BrfParser"/>.</para></summary>
        public static string Translate(string text, string tableFile)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (!Available) return null;
            string table = TablePath(tableFile);
            if (table == null) return null;

            try
            {
                uint[] inbuf = new uint[text.Length];
                for (int i = 0; i < text.Length; i++) inbuf[i] = text[i];
                int inlen = inbuf.Length;

                // Forward translation contracts, so it shrinks -- but an
                // uncontracted table can add capital and number signs, so this
                // still needs headroom rather than the input length.
                int outlen = Math.Max(64, inlen * 2);
                uint[] outbuf = new uint[outlen];

                int rc = lou_translateString(table, inbuf, ref inlen, outbuf, ref outlen,
                                             null, null, 0);
                if (rc == 0) return null;

                var sb = new StringBuilder(outlen);
                for (int i = 0; i < outlen; i++)
                {
                    uint c = outbuf[i];
                    if (c == 0) continue;
                    if (c <= 0xFFFF) sb.Append((char)c);
                    else sb.Append(char.ConvertFromUtf32((int)c));
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>Releases liblouis' internal table cache (called at shutdown).</summary>
        public static void Shutdown()
        {
            try { if (available == true) lou_free(); }
            catch { }
        }
    }

    /// <summary>One selectable braille table: a language, and where relevant the
    /// grade (uncontracted / contracted) and the national standard revision. A .brf
    /// file carries no such metadata, so the choice is per book — auto-detected at
    /// import, and overridable by the user.</summary>
    public class BrailleTableInfo
    {
        public string Id;        // stable key persisted in Book.ini
        public string File;      // table file name in louis\tables
        public string Display;   // human-readable label
        /// <summary>Primary language code, from the table's own
        /// <c>#+language:</c>. What the picker filters on, so that a reader is
        /// offered the one to three tables their book could plausibly be in
        /// rather than all 148.</summary>
        public string Language;

        public BrailleTableInfo(string id, string file, string display, string language = "")
        {
            Id = id; File = file; Display = display; Language = language ?? "";
        }
    }

    /// <summary>The braille tables NBR offers. Croatian ships in two revisions
    /// because the 2020 standard reassigned cells that the older one used for the
    /// digraphs — the same cell reads "lj" under the old standard and "(" under the
    /// new one, which no file can disambiguate on its own.</summary>
    public static class BrailleTables
    {
        /// <summary>The tables AUTO-DETECTION tries, and only those.
        ///
        /// <para><b>Deliberately small, and it must stay small</b> (measured
        /// 2026-08-04). Detection back-translates a sample through every table it
        /// is offered and scores the result, and the score only knows Croatian,
        /// English and French words. Handing it the whole catalogue would make it
        /// slower — 10 ms per table, so ~2 s for 194 — and, far worse, less
        /// accurate: with a hundred languages in the running, one of the ones
        /// nothing can score wins by accident.</para>
        ///
        /// <para>So this is the set we have samples for and can judge. Everything
        /// else is offered to the READER instead, through <see cref="Catalog"/> —
        /// Gordan, 2026-08-04: *"Jezike koje nismo mogli testirati, nismo mogli,
        /// zato nudimo mogućnost reloada."*</para></summary>
        public static readonly BrailleTableInfo[] All =
        {
            new BrailleTableInfo("hr-old",  "hr-old.ctb",         "Croatian — pre-2020 standard", "hr"),
            new BrailleTableInfo("hr-2020", "hr-2020.ctb",        "Croatian — 2020 standard", "hr"),
            new BrailleTableInfo("en-g2",   "en-ueb-g2.ctb",      "English (UEB) — contracted", "en"),
            new BrailleTableInfo("en-g1",   "en-ueb-g1.ctb",      "English (UEB) — uncontracted", "en"),
            // EBAE, the pre-UEB American standard. §10g measured it as the better
            // reading of the American samples ("Incarnation" where UEB gave
            // "IncarnN !Ascension") and held it out only while a wrong automatic
            // pick had no remedy. It has one now.
            new BrailleTableInfo("en-us-g2", "en-us-g2.ctb",      "English (EBAE, American) — contracted", "en"),
            new BrailleTableInfo("en-us-g1", "en-us-g1.ctb",      "English (EBAE, American) — uncontracted", "en"),
            new BrailleTableInfo("en-gb-g2", "en-GB-g2.ctb",      "English (British) — contracted", "en"),
            new BrailleTableInfo("fr-g2",   "fr-bfu-g2.ctb",      "French — contracted", "fr"),
            new BrailleTableInfo("fr-g1",   "fr-bfu-comp6.utb",   "French — uncontracted", "fr"),
        };

        /// <summary>The tables for one language, most useful first.
        ///
        /// <para>Measured over the shipped set: 59 of 89 languages have exactly
        /// ONE table and 81 have three or fewer, so this is a short list almost
        /// always. Danish is the worst at ten, English seven.</para>
        ///
        /// <para>An unknown or empty language gives the whole catalogue rather
        /// than nothing: a book whose language could not be read is exactly the
        /// one whose table needs changing.</para></summary>
        public static BrailleTableInfo[] ForLanguage(string code)
        {
            string want = LanguageDetector.Primary(code ?? "");
            if (want.Length == 0) return Catalog;
            var hit = new List<BrailleTableInfo>();
            foreach (BrailleTableInfo t in Catalog)
                if (string.Equals(LanguageDetector.Primary(t.Language), want,
                                  StringComparison.OrdinalIgnoreCase)) hit.Add(t);
            return hit.Count > 0 ? hit.ToArray() : Catalog;
        }

        private static BrailleTableInfo[] catalog;

        /// <summary>Every table a reader may choose from — 140-odd of them, in
        /// 113 languages — read out of the shipped tables themselves rather than
        /// listed here.
        ///
        /// <para><b>Derived from each table's own metadata, never hand-picked.</b>
        /// The same rule §10e′ had to learn on the mpv build: a hand-written list
        /// silently drops the one entry a reader needs, and it surfaces on their
        /// machine rather than ours. liblouis tables carry <c>#+type:</c>,
        /// <c>#+language:</c>, <c>#+direction:</c> and <c>#-display-name:</c>, so
        /// the list is a filter over facts the authors wrote.</para>
        ///
        /// <para>Two filters, both load-bearing. <c>#+type:literary</c> keeps out
        /// the maths, chess, computer-braille and display tables, which are not
        /// books. And <b><c>#+direction:forward</c> is excluded</b>: 52 of the
        /// literary tables say they only go text→braille, and back-translating
        /// with one produces confident nonsense rather than an error.</para>
        ///
        /// <para><b>The curated set above is always in, whatever its metadata
        /// says.</b> Our two Croatian tables are hand-written (§8i) and carry no
        /// <c>#+type:</c> at all, so the mechanical filter would have thrown out
        /// precisely the tables this project built.</para>
        ///
        /// <para>Read once, on first use — not at start-up, because a reader who
        /// never opens a braille book should never pay for it.</para></summary>
        public static BrailleTableInfo[] Catalog
        {
            get { return catalog ?? (catalog = BuildCatalog()); }
        }

        private static BrailleTableInfo[] BuildCatalog()
        {
            var list = new List<BrailleTableInfo>(All);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BrailleTableInfo t in All) seen.Add(t.File);

            try
            {
                foreach (string path in Directory.GetFiles(LibLouis.TablesFolder))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".ctb" && ext != ".utb" && ext != ".tbl") continue;
                    string file = Path.GetFileName(path);
                    if (seen.Contains(file)) continue;

                    string type = null, dir = null, name = null, index = null, lang = null;
                    // An alias is a table whose body is nothing but `include`
                    // lines. liblouis ships several — en-us.tbl is a wrapper round
                    // en-us-g2.ctb — and without resolving them the picker offers
                    // the same translation twice under two names, which reads as
                    // two choices where there is one.
                    string onlyInclude = null; bool aliasOnly = true;
                    try
                    {
                        int n = 0;
                        foreach (string raw in File.ReadLines(path))
                        {
                            string s = raw.Trim();
                            if (n++ < 60)
                            {
                                if (s.StartsWith("#+type:", StringComparison.OrdinalIgnoreCase))
                                    type = s.Substring(7).Trim();
                                else if (s.StartsWith("#+direction:", StringComparison.OrdinalIgnoreCase))
                                    dir = s.Substring(12).Trim();
                                else if (s.StartsWith("#+language:", StringComparison.OrdinalIgnoreCase))
                                    lang = s.Substring(11).Trim();
                                else if (s.StartsWith("#-display-name:", StringComparison.OrdinalIgnoreCase))
                                    name = s.Substring(15).Trim();
                                else if (s.StartsWith("#-index-name:", StringComparison.OrdinalIgnoreCase))
                                    index = s.Substring(13).Trim();
                            }
                            if (s.Length == 0 || s[0] == '#') continue;
                            if (s.StartsWith("include ", StringComparison.OrdinalIgnoreCase))
                            {
                                string inc = s.Substring(8).Trim();
                                string ie = Path.GetExtension(inc).ToLowerInvariant();
                                if (ie == ".ctb" || ie == ".utb" || ie == ".tbl") onlyInclude = inc;
                            }
                            else { aliasOnly = false; }
                        }
                    }
                    catch { continue; }

                    if (!string.Equals(type, "literary", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(dir, "forward", StringComparison.OrdinalIgnoreCase)) continue;

                    // Resolved to what it actually translates with, so an alias and
                    // its target collapse to one entry — and the curated name wins,
                    // because `seen` already holds the target.
                    string canonical = aliasOnly && onlyInclude != null ? onlyInclude : file;
                    if (seen.Contains(canonical)) continue;

                    string display = !string.IsNullOrEmpty(name) ? name
                                   : !string.IsNullOrEmpty(index) ? index
                                   : Path.GetFileNameWithoutExtension(file);
                    // The file name IS the id: stable, unique, and already what
                    // Book.ini would have to store anyway.
                    seen.Add(canonical);
                    seen.Add(file);
                    list.Add(new BrailleTableInfo(file, file, display, lang ?? ""));
                }
            }
            catch { }

            list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
            return list.ToArray();
        }

        /// <summary>Looks a table up by its stored id, in the catalogue and not
        /// only in the detection set — a book may have been re-read with any of
        /// them, and then its id is a file name.</summary>
        public static BrailleTableInfo ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (BrailleTableInfo t in All)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            foreach (BrailleTableInfo t in Catalog)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }
}
