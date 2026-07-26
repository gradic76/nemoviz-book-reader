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
        public static readonly BrailleTableInfo[] All =
        {
            new BrailleTableInfo("hr-old",  "hr-old.ctb",         "Croatian — pre-2020 standard"),
            new BrailleTableInfo("hr-2020", "hr-2020.ctb",        "Croatian — 2020 standard"),
            new BrailleTableInfo("en-g2",   "en-ueb-g2.ctb",      "English (UEB) — contracted"),
            new BrailleTableInfo("en-g1",   "en-ueb-g1.ctb",      "English (UEB) — uncontracted"),
            new BrailleTableInfo("fr-g2",   "fr-bfu-g2.ctb",      "French — contracted"),
            new BrailleTableInfo("fr-g1",   "fr-bfu-comp6.utb",   "French — uncontracted"),
        };

        public static BrailleTableInfo ById(string id)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (BrailleTableInfo t in All)
                    if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }
}
