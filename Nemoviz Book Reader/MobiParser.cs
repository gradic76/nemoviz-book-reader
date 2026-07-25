using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Kindle MOBI / AZW / AZW3(KF8) reader. All three are PalmDB containers whose
    /// text records hold an HTML document, PalmDOC(LZ77)-compressed (or stored).
    /// This decompresses the text and feeds the resulting HTML through the shared
    /// <see cref="TextParsing.HtmlBlocks"/> pipeline, so headings and offsets come
    /// out exactly like the HTML/EPUB parsers. A DRM-protected book (encryption
    /// type ≠ 0) is flagged and never decrypted; HUFF/CDIC compression (unhandled)
    /// yields no text — both fall back gracefully. Verified against 12 real samples
    /// (11 MOBI6 → flat, 1 KF8/AZW3 → structured; all DRM-free, UTF-8).
    /// </summary>
    public class MobiParser : ITextFormatParser
    {
        public bool Handles(string extension)
        {
            return extension == ".mobi" || extension == ".azw" || extension == ".azw3";
        }

        public TextDoc Parse(string filePath)
        {
            var doc = new TextDoc();
            byte[] all;
            try { all = File.ReadAllBytes(filePath); }
            catch { return doc; }
            try { ParseInto(all, doc); }
            catch { /* truncated/odd file → return whatever we extracted (often empty) */ }
            return doc;
        }

        static ushort BE16(byte[] b, int o) { return (ushort)((b[o] << 8) | b[o + 1]); }
        static uint BE32(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }

        private void ParseInto(byte[] all, TextDoc doc)
        {
            if (all.Length < 80) return;
            int numRecs = BE16(all, 76);
            if (numRecs < 2 || 78 + numRecs * 8 > all.Length) return;

            // PalmDB record-offset table (+ a sentinel end offset).
            var recOff = new List<int>(numRecs + 1);
            for (int i = 0; i < numRecs; i++)
            {
                int off = (int)BE32(all, 78 + i * 8);
                if (off < 0 || off > all.Length) off = all.Length;
                recOff.Add(off);
            }
            recOff.Add(all.Length);

            // Record 0 = the PalmDOC + MOBI header record.
            int r0 = recOff[0];
            if (r0 + 16 > all.Length) return;
            ushort compression = BE16(all, r0 + 0);
            ushort textRecCount = BE16(all, r0 + 8);
            ushort encryption = BE16(all, r0 + 12);

            if (encryption != 0) { doc.DrmProtected = true; return; }   // real DRM → never strip
            if (compression != 1 && compression != 2 && compression != 17480) return; // unknown codec

            // MOBI header (present on every real book): text encoding, the
            // extra-trailing-bytes flags, and the EXTH metadata block.
            Encoding enc = Encoding.UTF8;
            ushort extraFlags = 0;
            bool hasMobi = r0 + 20 <= all.Length && Encoding.ASCII.GetString(all, r0 + 16, 4) == "MOBI";
            if (hasMobi)
            {
                int hdrLen = (int)BE32(all, r0 + 20);
                uint textEnc = BE32(all, r0 + 28);
                if (textEnc == 1252) { try { enc = Encoding.GetEncoding(1252); } catch { enc = Encoding.UTF8; } }
                // EXTRA_DATA_FLAGS lives at record0 offset 0xF2 (exists only when
                // the MOBI header reaches that far).
                if (hdrLen > 0xE2 && r0 + 0xF2 + 2 <= all.Length)
                    extraFlags = BE16(all, r0 + 0xF2);
                ReadMetadata(all, r0, hdrLen, enc, doc);
            }

            // HUFF/CDIC needs its Huffman/dictionary records built first (the
            // record indices sit at record0 0x70/0x74). Can't build → no text.
            HuffCdic huff = null;
            if (compression == 17480)
            {
                int huffIdx = (int)BE32(all, r0 + 0x70);
                int huffCount = (int)BE32(all, r0 + 0x74);
                if (huffIdx > 0 && huffCount >= 2 && huffIdx + huffCount <= recOff.Count - 1)
                {
                    var h = new HuffCdic();
                    if (h.LoadHuff(all, recOff[huffIdx]))
                    {
                        bool ok = true;
                        for (int c = 1; c < huffCount && ok; c++)
                            ok = h.LoadCdic(all, recOff[huffIdx + c]);
                        if (ok) huff = h;
                    }
                }
                if (huff == null) return;
            }

            // Decompress text records 1..textRecCount, trimming per-record trailing
            // bytes first (or the compressed stream corrupts).
            var outBytes = new List<byte>(textRecCount * 4096);
            for (int i = 1; i <= textRecCount && i + 1 < recOff.Count; i++)
            {
                int start = recOff[i], end = recOff[i + 1];
                int len = end - start;
                if (len <= 0 || start < 0 || end > all.Length) continue;
                byte[] rec = new byte[len];
                Array.Copy(all, start, rec, 0, len);
                int trimmed = TrimTrailing(rec, len, extraFlags);
                if (compression == 2) PalmDocDecompress(rec, trimmed, outBytes);
                else if (compression == 17480) huff.Unpack(rec, trimmed, outBytes);
                else for (int k = 0; k < trimmed; k++) outBytes.Add(rec[k]);
            }

            string html = enc.GetString(outBytes.ToArray());
            // The payload is an HTML document; any bytes before the first tag are
            // conversion junk (e.g. a leaked source path at the head of a KF8 flow).
            int lt = html.IndexOf('<');
            if (lt > 0) html = html.Substring(lt);

            var blocks = TextParsing.HtmlBlocks(html);
            TextParsing.Assemble(blocks, out string text, out var headings, out _);
            doc.Text = text;
            doc.Headings = headings;
        }

        /// <summary>EXTH metadata (author/title/publisher), plus the PalmDB
        /// full-name field as a title fallback. Encoding follows the book's
        /// declared text encoding.</summary>
        private void ReadMetadata(byte[] all, int r0, int hdrLen, Encoding enc, TextDoc doc)
        {
            try
            {
                int exth = r0 + 16 + hdrLen;
                if (exth + 12 <= all.Length && Encoding.ASCII.GetString(all, exth, 4) == "EXTH")
                {
                    int recCount = (int)BE32(all, exth + 8);
                    int p = exth + 12;
                    for (int i = 0; i < recCount && p + 8 <= all.Length; i++)
                    {
                        int type = (int)BE32(all, p);
                        int len = (int)BE32(all, p + 4);
                        if (len < 8 || p + len > all.Length) break;
                        string val = enc.GetString(all, p + 8, len - 8).Trim();
                        switch (type)
                        {
                            case 100: if (string.IsNullOrEmpty(doc.Author)) doc.Author = val; break; // creator
                            case 101: if (string.IsNullOrEmpty(doc.Publisher)) doc.Publisher = val; break;
                            case 503: if (!IsHexHash(val)) doc.Title = val; break; // updated title (preferred)
                        }
                        p += len;
                    }
                }

                // Full-name field (offset/length relative to record 0) → title fallback.
                if (string.IsNullOrEmpty(doc.Title) && r0 + 0x5C <= all.Length)
                {
                    int fnOff = (int)BE32(all, r0 + 0x54);
                    int fnLen = (int)BE32(all, r0 + 0x58);
                    int abs = r0 + fnOff;
                    if (fnLen > 0 && fnLen < 1024 && abs >= 0 && abs + fnLen <= all.Length)
                    {
                        string fn = enc.GetString(all, abs, fnLen).Trim();
                        // Converters (e.g. z-library scans) leave the internal
                        // asset hash here as the "name" — a bare hex string, not a
                        // real title. Reject it so the file name stands instead.
                        if (!IsHexHash(fn)) doc.Title = fn;
                    }
                }
            }
            catch { /* metadata is best-effort; the file name stands otherwise */ }
        }

        /// <summary>True for a bare hex string of ≥16 digits (no spaces) — an
        /// internal asset hash left as a pseudo-title, not a real book title.</summary>
        private static bool IsHexHash(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 16) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        // ── PalmDOC record trailing-byte trim ─────────────────────────────────
        // The high bits of EXTRA_DATA_FLAGS each mark a backward var-length-encoded
        // trailing region; bit 0 marks a multibyte-overlap tail. All must be removed
        // before decompressing the record.
        private static int TrimTrailing(byte[] rec, int len, int flags)
        {
            int size = len;
            for (int bit = 15; bit > 0; bit--)
                if ((flags & (1 << bit)) != 0)
                    size -= BackwardVarLen(rec, size);
            if ((flags & 1) != 0 && size > 0)
                size -= (rec[size - 1] & 0x3) + 1;
            return size < 0 ? 0 : size;
        }

        private static int BackwardVarLen(byte[] rec, int size)
        {
            int bitpos = 0, result = 0;
            while (size > 0)
            {
                byte v = rec[size - 1];
                result |= (v & 0x7f) << bitpos;
                bitpos += 7;
                size--;
                if ((v & 0x80) != 0) break;
                if (bitpos >= 28) break;
            }
            return result;
        }

        // ── PalmDOC / LZ77 decompression (appends into shared output) ──────────
        private static void PalmDocDecompress(byte[] data, int len, List<byte> outBuf)
        {
            int baseCount = outBuf.Count; // LZ77 back-references stay within this record
            int i = 0;
            while (i < len)
            {
                int c = data[i++];
                if (c == 0) outBuf.Add(0);
                else if (c >= 1 && c <= 8) { for (int j = 0; j < c && i < len; j++) outBuf.Add(data[i++]); }
                else if (c <= 0x7f) outBuf.Add((byte)c);
                else if (c >= 0xc0) { outBuf.Add((byte)' '); outBuf.Add((byte)(c ^ 0x80)); }
                else // 0x80..0xbf : LZ77 pair
                {
                    if (i >= len) break;
                    int pair = (c << 8) | data[i++];
                    int distance = (pair >> 3) & 0x7ff;
                    int length = (pair & 0x7) + 3;
                    int srcStart = outBuf.Count - distance;
                    for (int j = 0; j < length; j++)
                    {
                        int idx = srcStart + j;
                        outBuf.Add(idx >= baseCount && idx < outBuf.Count ? outBuf[idx] : (byte)' ');
                    }
                }
            }
        }

        /// <summary>
        /// HUFF/CDIC decompressor (compression id 17480) — the other MOBI text
        /// codec, common in commercial Kindle books. One HUFF record holds the
        /// Huffman code tables; the following CDIC records hold the phrase
        /// dictionary. Each text record is a Huffman bit-stream of phrase indices;
        /// a phrase may itself be Huffman-encoded (decoded once, then cached).
        /// Ported from the well-known KindleUnpack algorithm.
        /// </summary>
        private sealed class HuffCdic
        {
            private readonly int[] dictCodelen = new int[256];
            private readonly bool[] dictTerm = new bool[256];
            private readonly ulong[] dictMaxcode = new ulong[256];
            private readonly ulong[] mincode = new ulong[33];
            private readonly ulong[] maxcode = new ulong[33];
            private readonly List<byte[]> phrases = new List<byte[]>();
            private readonly List<bool> phraseCompressed = new List<bool>();
            private int loadedPhrases;

            public bool LoadHuff(byte[] b, int r)
            {
                // "HUFF" + header length 0x18.
                if (r + 24 > b.Length || Encoding.ASCII.GetString(b, r, 4) != "HUFF") return false;
                int off1 = (int)BE32(b, r + 8);
                int off2 = (int)BE32(b, r + 12);
                if (r + off1 + 256 * 4 > b.Length || r + off2 + 64 * 4 > b.Length) return false;

                for (int i = 0; i < 256; i++)
                {
                    uint v = BE32(b, r + off1 + i * 4);
                    int codelen = (int)(v & 0x1f);
                    bool term = (v & 0x80) != 0;
                    ulong mc = v >> 8;
                    if (codelen == 0) return false;
                    dictCodelen[i] = codelen;
                    dictTerm[i] = term;
                    dictMaxcode[i] = ((mc + 1) << (32 - codelen)) - 1;
                }
                // dict2: 32 (min,max) uint32 pairs. The tables are indexed by code
                // length 1..32 with a leading 0 slot, so code length N maps to
                // pair N-1 (matching Calibre's HuffReader — an off-by-one here
                // silently corrupts every non-terminal code).
                mincode[0] = 0; maxcode[0] = (1UL << 32) - 1;
                for (int cl = 1; cl <= 32; cl++)
                {
                    int di = cl - 1;
                    uint mn = BE32(b, r + off2 + (di * 2) * 4);
                    uint mx = BE32(b, r + off2 + (di * 2 + 1) * 4);
                    mincode[cl] = ((ulong)mn) << (32 - cl);
                    maxcode[cl] = (((ulong)mx + 1) << (32 - cl)) - 1;
                }
                return true;
            }

            public bool LoadCdic(byte[] b, int r)
            {
                // "CDIC" + header length 0x10.
                if (r + 16 > b.Length || Encoding.ASCII.GetString(b, r, 4) != "CDIC") return false;
                int total = (int)BE32(b, r + 8);
                int bits = (int)BE32(b, r + 12);
                if (bits < 0 || bits > 16) return false;
                int n = Math.Min(1 << bits, total - loadedPhrases);
                if (n < 0) n = 0;
                if (r + 16 + n * 2 > b.Length) return false;

                for (int i = 0; i < n; i++)
                {
                    int off = BE16(b, r + 16 + i * 2);
                    int at = r + 16 + off;
                    if (at + 2 > b.Length) { phrases.Add(new byte[0]); phraseCompressed.Add(false); continue; }
                    int blen = BE16(b, at);
                    int plen = blen & 0x7fff;
                    // High bit SET = terminal phrase; CLEAR = the phrase is itself
                    // a Huffman stream to decode (Calibre semantics, verified).
                    bool comp = (blen & 0x8000) == 0;
                    int pstart = at + 2;
                    if (pstart + plen > b.Length) plen = Math.Max(0, b.Length - pstart);
                    byte[] phrase = new byte[plen];
                    Array.Copy(b, pstart, phrase, 0, plen);
                    phrases.Add(phrase);
                    phraseCompressed.Add(comp);
                }
                loadedPhrases += n;
                return true;
            }

            private static ulong ReadU64BE(byte[] d, int pos)
            {
                ulong v = 0;
                for (int i = 0; i < 8; i++)
                    v = (v << 8) | (pos + i < d.Length ? d[pos + i] : (byte)0);
                return v;
            }

            /// <summary>Decode one Huffman text record into the shared output.</summary>
            public void Unpack(byte[] rec, int len, List<byte> outBuf)
            {
                byte[] data = new byte[len + 16];      // pad so a 64-bit read never runs off the end
                Array.Copy(rec, 0, data, 0, len);
                long bitsleft = (long)len * 8;
                int pos = 0;
                ulong x = ReadU64BE(data, pos);
                int n = 32;

                while (true)
                {
                    if (n <= 0) { pos += 4; x = ReadU64BE(data, pos); n += 32; }
                    ulong code = (x >> n) & 0xffffffffUL;

                    int idx = (int)(code >> 24);
                    int codelen = dictCodelen[idx];
                    ulong mc = dictMaxcode[idx];
                    if (!dictTerm[idx])
                    {
                        while (codelen < 32 && code < mincode[codelen]) codelen++;
                        mc = maxcode[codelen];
                    }
                    if (codelen <= 0 || codelen > 32) break;   // corrupt stream guard

                    n -= codelen;
                    bitsleft -= codelen;
                    if (bitsleft < 0) break;

                    ulong r = (mc - code) >> (32 - codelen);
                    int ri = (int)r;
                    if (ri < 0 || ri >= phrases.Count) break;

                    byte[] slice = phrases[ri];
                    if (phraseCompressed[ri])
                    {
                        phraseCompressed[ri] = false;       // clear first → a self-reference can't recurse forever
                        var tmp = new List<byte>();
                        Unpack(slice, slice.Length, tmp);   // a phrase may itself be Huffman-encoded
                        slice = tmp.ToArray();
                        phrases[ri] = slice;
                    }
                    outBuf.AddRange(slice);
                }
            }
        }
    }
}
