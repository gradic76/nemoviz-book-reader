using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Legacy binary Word (.doc — Word 97-2003, an OLE2 / Compound File). No
    /// external dependency: a minimal Compound-File reader pulls the
    /// "WordDocument" and table streams, then the FIB + piece table (CLX/PlcPcd)
    /// give the text — the same approach Word itself uses, handling fast-saved
    /// files with multiple pieces and mixed CP1252/Unicode runs. A .doc carries
    /// no reliable heading structure, so it reads FLAT (text only); the reader's
    /// cleaner tidies it on load. Verified against real Word-2002/2003 books.
    /// </summary>
    public class DocParser : ITextFormatParser
    {
        public bool Handles(string extension) { return extension == ".doc"; }

        public TextDoc Parse(string filePath)
        {
            var doc = new TextDoc();
            try
            {
                byte[] file = File.ReadAllBytes(filePath);
                var cfb = new Cfb(file);
                byte[] wd = cfb.ReadStream("WordDocument");
                if (wd == null || wd.Length < 0x200) return doc;

                if (BE16le(wd, 0) != 0xA5EC) return doc;               // FIB wIdent
                ushort flags = BE16le(wd, 0x0A);
                // Word 97 encryption flag (fEncrypted) → can't read.
                if ((flags & 0x0100) != 0) { doc.DrmProtected = true; return doc; }
                string tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
                byte[] tbl = cfb.ReadStream(tableName) ?? cfb.ReadStream(tableName == "1Table" ? "0Table" : "1Table");

                uint fcClx = U32(wd, 0x01A2);
                uint lcbClx = U32(wd, 0x01A6);

                string text = null;
                if (tbl != null && lcbClx > 0 && fcClx + lcbClx <= (uint)tbl.Length)
                    text = ExtractViaPieceTable(wd, tbl, (int)fcClx, (int)lcbClx);

                if (string.IsNullOrEmpty(text))
                {
                    // Fallback: no usable piece table → the raw main text range,
                    // decoded as CP1252 (covers simple, non-fast-saved files).
                    uint fcMin = U32(wd, 0x18), fcMac = U32(wd, 0x1C);
                    if (fcMac > fcMin && fcMac <= (uint)wd.Length)
                        text = Cp1252().GetString(wd, (int)fcMin, (int)(fcMac - fcMin));
                }

                doc.Text = CleanWordText(text ?? "");
            }
            catch { /* unreadable/odd .doc → empty; caller treats as "no text" */ }
            return doc;
        }

        // ── Word piece table (CLX → PlcPcd) ───────────────────────────────────
        private static string ExtractViaPieceTable(byte[] wd, byte[] tbl, int fcClx, int lcbClx)
        {
            int p = fcClx;
            int end = fcClx + lcbClx;
            // Skip any leading Prc entries (clxt 0x01), stop at the Pcdt (0x02).
            while (p < end)
            {
                byte clxt = tbl[p];
                if (clxt == 0x02) break;
                if (clxt == 0x01)
                {
                    if (p + 3 > end) return null;
                    int cb = BE16le(tbl, p + 1);
                    p += 3 + cb;
                }
                else return null; // unexpected
            }
            if (p >= end || tbl[p] != 0x02) return null;
            p++;
            if (p + 4 > end) return null;
            int lcbPlc = (int)U32(tbl, p); p += 4;
            if (lcbPlc < 4 || p + lcbPlc > tbl.Length) return null;

            int n = (lcbPlc - 4) / 12;                 // pieces: (n+1) CPs + n PCDs
            if (n <= 0) return null;
            int cpBase = p;
            int pcdBase = p + 4 * (n + 1);

            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                int cpStart = (int)U32(tbl, cpBase + i * 4);
                int cpEnd = (int)U32(tbl, cpBase + (i + 1) * 4);
                int cch = cpEnd - cpStart;
                if (cch <= 0) continue;

                uint fc = U32(tbl, pcdBase + i * 8 + 2);  // PCD.fc (after 2 flag bytes)
                bool compressed = (fc & 0x40000000) != 0; // CP1252 8-bit run
                int realFc = compressed ? (int)((fc & 0x3FFFFFFF) / 2) : (int)fc;

                if (compressed)
                {
                    if (realFc < 0 || realFc + cch > wd.Length) continue;
                    sb.Append(Cp1252().GetString(wd, realFc, cch));
                }
                else
                {
                    int bytes = cch * 2;
                    if (realFc < 0 || realFc + bytes > wd.Length) continue;
                    sb.Append(Encoding.Unicode.GetString(wd, realFc, bytes));
                }
            }
            return sb.ToString();
        }

        /// <summary>Turns Word's control characters into readable text: paragraph
        /// / line / page / cell marks → newlines, field instruction codes dropped
        /// (result kept), stray control chars removed. Compared by code point so
        /// no invisible control-char literals live in the source.</summary>
        private static string CleanWordText(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inFieldCode = false;
            foreach (char c in s)
            {
                int u = c;
                if (u == 0x13) { inFieldCode = true; continue; }   // field begin
                if (u == 0x14) { inFieldCode = false; continue; }  // field separator (result follows)
                if (u == 0x15) { inFieldCode = false; continue; }  // field end
                if (inFieldCode) continue;
                // paragraph (\r 0x0D), line break (0x0B), page/column break (0x0C),
                // cell/row mark (0x07) → newline.
                if (u == 0x0D || u == 0x0B || u == 0x0C || u == 0x07) { sb.Append('\n'); continue; }
                if (u == 0x09 || u == 0x0A) { sb.Append(c); continue; }  // tab / newline
                if (u == 0x1E) { sb.Append('-'); continue; }            // non-breaking hyphen
                if (u == 0x1F) continue;                                // optional hyphen
                if (u < 0x20) continue;                                 // other controls
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static Encoding _cp1252;
        private static Encoding Cp1252()
        {
            if (_cp1252 == null)
            {
                try { _cp1252 = Encoding.GetEncoding(1252); } catch { _cp1252 = Encoding.Default; }
            }
            return _cp1252;
        }

        private static ushort BE16le(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        private static uint U32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        // ── Minimal Compound File (OLE2 / MS-CFB) reader ──────────────────────
        private sealed class Cfb
        {
            private readonly byte[] f;
            private readonly int sectorSize;
            private readonly int miniSectorSize;
            private readonly uint miniCutoff;
            private readonly uint[] fat;
            private readonly uint[] miniFat;
            private readonly List<int> dirEntries = new List<int>(); // file offsets of 128-byte dir entries
            private byte[] miniStream;

            private const uint ENDOFCHAIN = 0xFFFFFFFE;
            private const uint FREESECT = 0xFFFFFFFF;

            public Cfb(byte[] file)
            {
                f = file;
                if (f.Length < 512 || U32s(0) != 0xE011CFD0 || U32s(4) != 0xE11AB1A1)
                    throw new InvalidDataException("not a compound file");

                int sectorShift = U16s(0x1E);
                sectorSize = 1 << sectorShift;                 // 512 (v3) or 4096 (v4)
                int miniShift = U16s(0x20);
                miniSectorSize = 1 << miniShift;               // usually 64
                miniCutoff = U32s(0x38);                        // usually 4096
                uint firstDirSector = U32s(0x30);
                uint firstMiniFatSector = U32s(0x3C);
                int numMiniFatSectors = (int)U32s(0x40);
                uint firstDifatSector = U32s(0x44);
                int numDifatSectors = (int)U32s(0x48);

                // DIFAT → list of FAT sector numbers (first 109 inline, rest chained).
                var fatSectors = new List<uint>();
                for (int i = 0; i < 109; i++)
                {
                    uint s = U32s(0x4C + i * 4);
                    if (s == FREESECT || s == ENDOFCHAIN) break;
                    fatSectors.Add(s);
                }
                uint difat = firstDifatSector;
                int guard = 0;
                while (numDifatSectors > 0 && difat != ENDOFCHAIN && difat != FREESECT && guard++ < (1 << 20))
                {
                    int off = SectorOffset(difat);
                    int perSector = sectorSize / 4;
                    for (int i = 0; i < perSector - 1; i++)
                    {
                        uint s = U32(f, off + i * 4);
                        if (s == FREESECT || s == ENDOFCHAIN) break;
                        fatSectors.Add(s);
                    }
                    difat = U32(f, off + (perSector - 1) * 4);
                }

                // Build the FAT (concatenation of all FAT sectors).
                int entriesPerSector = sectorSize / 4;
                fat = new uint[fatSectors.Count * entriesPerSector];
                int fi = 0;
                foreach (uint fs in fatSectors)
                {
                    int off = SectorOffset(fs);
                    if (off < 0 || off + sectorSize > f.Length) { fi += entriesPerSector; continue; }
                    for (int i = 0; i < entriesPerSector; i++)
                        fat[fi++] = U32(f, off + i * 4);
                }

                // Directory chain → collect all 128-byte entries.
                foreach (int secOff in ChainOffsetsFrom(firstDirSector))
                    for (int e = 0; e + 128 <= sectorSize; e += 128)
                        dirEntries.Add(secOff + e);

                // Root entry (object type 5) → mini-stream chain + size.
                int root = -1;
                foreach (int de in dirEntries)
                    if (de + 128 <= f.Length && f[de + 0x42] == 5) { root = de; break; }
                if (root >= 0)
                {
                    uint rootStart = U32(f, root + 0x74);
                    long rootSize = (long)U32(f, root + 0x78) | ((long)U32(f, root + 0x7C) << 32);
                    miniStream = ReadChain(rootStart, rootSize, sectorSize);
                }

                // Mini-FAT.
                var mf = new List<uint>();
                if (numMiniFatSectors > 0)
                    foreach (int secOff in ChainOffsetsFrom(firstMiniFatSector))
                        for (int i = 0; i < entriesPerSector; i++)
                            mf.Add(U32(f, secOff + i * 4));
                miniFat = mf.ToArray();
            }

            /// <summary>Reads a named stream fully (root-level entries only, which
            /// is all a .doc needs), or null if absent.</summary>
            public byte[] ReadStream(string name)
            {
                foreach (int de in dirEntries)
                {
                    if (de + 128 > f.Length) continue;
                    if (f[de + 0x42] != 2) continue; // stream
                    if (!EntryName(de).Equals(name, StringComparison.Ordinal)) continue;
                    uint start = U32(f, de + 0x74);
                    long size = (long)U32(f, de + 0x78) | ((long)U32(f, de + 0x7C) << 32);
                    if (size < miniCutoff && miniStream != null)
                        return ReadMiniChain(start, size);
                    return ReadChain(start, size, sectorSize);
                }
                return null;
            }

            private string EntryName(int de)
            {
                int len = U16(f, de + 0x40);          // bytes incl. null terminator
                if (len < 2) return "";
                int chars = (len / 2) - 1;
                if (chars < 0 || de + chars * 2 > f.Length) return "";
                return Encoding.Unicode.GetString(f, de, chars * 2);
            }

            private int SectorOffset(uint sector) { return (int)((sector + 1) * (long)sectorSize); }

            private IEnumerable<int> ChainOffsetsFrom(uint startSector)
            {
                uint s = startSector;
                int guard = 0;
                while (s != ENDOFCHAIN && s != FREESECT && s < (uint)fat.Length && guard++ < (1 << 22))
                {
                    int off = SectorOffset(s);
                    if (off < 0 || off + sectorSize > f.Length) yield break;
                    yield return off;
                    s = fat[s];
                }
            }

            private byte[] ReadChain(uint startSector, long size, int unit)
            {
                var buf = new List<byte>((int)Math.Max(0, Math.Min(size, int.MaxValue)));
                foreach (int off in ChainOffsetsFrom(startSector))
                {
                    int take = (int)Math.Min(unit, size - buf.Count);
                    if (take <= 0) break;
                    for (int i = 0; i < take; i++) buf.Add(f[off + i]);
                    if (buf.Count >= size) break;
                }
                return buf.ToArray();
            }

            private byte[] ReadMiniChain(uint startMiniSector, long size)
            {
                var buf = new List<byte>((int)Math.Max(0, size));
                uint s = startMiniSector;
                int guard = 0;
                while (s != ENDOFCHAIN && s != FREESECT && s < (uint)miniFat.Length && guard++ < (1 << 22))
                {
                    long off = (long)s * miniSectorSize;
                    int take = (int)Math.Min(miniSectorSize, size - buf.Count);
                    if (take <= 0) break;
                    if (off < 0 || off + take > miniStream.Length) break;
                    for (int i = 0; i < take; i++) buf.Add(miniStream[(int)off + i]);
                    if (buf.Count >= size) break;
                    s = miniFat[s];
                }
                return buf.ToArray();
            }

            private ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
            private ushort U16s(int o) { return U16(f, o); }
            private uint U32s(int o) { return U32(f, o); }
        }
    }
}
