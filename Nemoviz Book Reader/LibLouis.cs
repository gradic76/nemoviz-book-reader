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

        [DllImport(Dll, CallingConvention = Conv)]
        private static extern int lou_charSize();

        [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        private static extern int lou_backTranslateString(
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

        public BrailleTableInfo(string id, string file, string display)
        {
            Id = id; File = file; Display = display;
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
            new BrailleTableInfo("hr-old",  "hr-old.ctb",         "Croatian — pre-2020 standard"),
            new BrailleTableInfo("hr-2020", "hr-2020.ctb",        "Croatian — 2020 standard"),
            new BrailleTableInfo("en-g2",   "en-ueb-g2.ctb",      "English (UEB) — contracted"),
            new BrailleTableInfo("en-g1",   "en-ueb-g1.ctb",      "English (UEB) — uncontracted"),
            // EBAE, the pre-UEB American standard. §10g measured it as the better
            // reading of the American samples ("Incarnation" where UEB gave
            // "IncarnN !Ascension") and held it out only while a wrong automatic
            // pick had no remedy. It has one now.
            new BrailleTableInfo("en-us-g2", "en-us-g2.ctb",      "English (EBAE, American) — contracted"),
            new BrailleTableInfo("en-us-g1", "en-us-g1.ctb",      "English (EBAE, American) — uncontracted"),
            new BrailleTableInfo("en-gb-g2", "en-GB-g2.ctb",      "English (British) — contracted"),
            new BrailleTableInfo("fr-g2",   "fr-bfu-g2.ctb",      "French — contracted"),
            new BrailleTableInfo("fr-g1",   "fr-bfu-comp6.utb",   "French — uncontracted"),
        };

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

                    string type = null, dir = null, name = null, index = null;
                    try
                    {
                        int n = 0;
                        foreach (string raw in File.ReadLines(path))
                        {
                            if (++n > 60) break;
                            string s = raw.Trim();
                            if (s.StartsWith("#+type:", StringComparison.OrdinalIgnoreCase))
                                type = s.Substring(7).Trim();
                            else if (s.StartsWith("#+direction:", StringComparison.OrdinalIgnoreCase))
                                dir = s.Substring(12).Trim();
                            else if (s.StartsWith("#-display-name:", StringComparison.OrdinalIgnoreCase))
                                name = s.Substring(15).Trim();
                            else if (s.StartsWith("#-index-name:", StringComparison.OrdinalIgnoreCase))
                                index = s.Substring(13).Trim();
                        }
                    }
                    catch { continue; }

                    if (!string.Equals(type, "literary", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(dir, "forward", StringComparison.OrdinalIgnoreCase)) continue;

                    string display = !string.IsNullOrEmpty(name) ? name
                                   : !string.IsNullOrEmpty(index) ? index
                                   : Path.GetFileNameWithoutExtension(file);
                    // The file name IS the id here: stable, unique, and already
                    // what Book.ini would have to store anyway.
                    seen.Add(file);
                    list.Add(new BrailleTableInfo(file, file, display));
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
