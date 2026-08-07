using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Parsed chapter + metadata info for an M4B (MP4 audio) book.</summary>
    public class M4bBook
    {
        public string Title = "";
        public string Author = "";
        /// <summary>The publisher's blurb, cleaned. M4B audiobooks carry it more
        /// often than any other format measured so far — better than half the
        /// sampled files, against 45 % of EPUBs and 13 % of MOBI.</summary>
        public string Description = "";
        public double DurationSeconds = 0;
        // Chapter marks in reading order: title + start time (seconds) into the
        // single audio file.
        public List<(string Title, double Start)> Chapters = new List<(string, double)>();
        public bool HasChapters { get { return Chapters.Count > 0; } }
    }

    /// <summary>
    /// Reads chapters and basic metadata from an M4B/MP4 audio file by walking
    /// the box (atom) tree — no external dependency (TagLib# doesn't expose MP4
    /// chapters reliably). Two chapter sources, tried in order:
    ///   1. Nero <c>chpl</c> (moov/udta/chpl): titles + 100 ns start times, all
    ///      inline in moov — present in almost every file, simplest to read.
    ///   2. QuickTime text chapter track: the audio track's <c>tref/chap</c>
    ///      points at a text track whose samples are the titles (read from the
    ///      file) and whose <c>stts</c> gives the start times.
    /// Returns null when the file has no readable moov.
    /// Findings that shaped this (13 real books) live in memory: chapters exist
    /// in 13/13 (QT tref) and 12/13 also carry chpl with identical counts.
    /// </summary>
    public static class M4bParser
    {
        private class Box { public string Path; public string Type; public int Off; public int Len; public int Trak; }

        public static bool IsM4bFile(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".m4b" || ext == ".m4a";
        }

        public static M4bBook TryParse(string path)
        {
            try
            {
                byte[] moov = ReadMoov(path);
                if (moov == null) return null;

                var boxes = new List<Box>();
                int trak = 0;
                Walk(moov, 0, moov.Length, "", boxes, ref trak);

                var book = new M4bBook();
                book.DurationSeconds = ReadDuration(moov, boxes);
                ReadMetadata(moov, boxes, book);

                // 1) Nero chpl.
                var chpl = ReadChpl(moov, boxes);
                if (chpl != null && chpl.Count > 0) book.Chapters = chpl;
                // 2) QuickTime text chapter track.
                else book.Chapters = ReadQtChapters(path, moov, boxes);

                return book;
            }
            catch { return null; }
        }

        // ── Big-endian readers ────────────────────────────────────────────
        private static int BE16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        private static long BE32(byte[] b, int o) { return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3]; }
        private static long BE64(byte[] b, int o) { return (BE32(b, o) << 32) | (BE32(b, o + 4) & 0xffffffffL); }
        private static string Str4(byte[] b, int o) { return Encoding.ASCII.GetString(b, o, 4); }

        // ── Locate + read the moov box without loading the (huge) mdat ─────
        private static byte[] ReadMoov(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                long len = fs.Length;
                byte[] hdr = new byte[16];
                while (fs.Position + 8 <= len)
                {
                    long boxStart = fs.Position;
                    if (fs.Read(hdr, 0, 8) != 8) break;
                    long size = BE32(hdr, 0); string type = Str4(hdr, 4); int hsz = 8;
                    if (size == 1) { if (fs.Read(hdr, 8, 8) != 8) break; size = BE64(hdr, 8); hsz = 16; }
                    else if (size == 0) size = len - boxStart;
                    if (type == "moov")
                    {
                        int payLen = (int)(size - hsz);
                        if (payLen < 0 || payLen > 256 * 1024 * 1024) return null; // sanity
                        byte[] buf = new byte[payLen];
                        int read = 0; while (read < payLen) { int r = fs.Read(buf, read, payLen - read); if (r <= 0) break; read += r; }
                        return buf;
                    }
                    long next = boxStart + size;
                    if (size < hsz || next <= boxStart || next > len) break; // must advance
                    fs.Position = next;
                }
            }
            return null;
        }

        // ── Recursive box walk into a flat list (containers descended) ─────
        private static readonly HashSet<string> Containers =
            new HashSet<string> { "trak", "udta", "mdia", "minf", "stbl", "tref", "edts" };

        private static void Walk(byte[] b, int start, int end, string path, List<Box> outList, ref int trak)
        {
            int p = start; int guard = 0;
            while (p + 8 <= end)
            {
                if (++guard > 100000) return;
                long size = BE32(b, p); string type = Str4(b, p + 4); int hsz = 8;
                if (size == 1) { size = BE64(b, p + 8); hsz = 16; }
                else if (size == 0) size = end - p;
                if (size < hsz || p + size > end) break;
                int off = p + hsz; int dlen = (int)(size - hsz);
                if (type == "trak") trak++;
                outList.Add(new Box { Path = path + "/" + type, Type = type, Off = off, Len = dlen, Trak = trak });

                int childStart = off; bool descend = Containers.Contains(type);
                if (type == "meta") { descend = true; childStart = off + 4; }   // fullbox
                if (type == "ilst") descend = true;
                if (path.EndsWith("/ilst")) descend = true;                     // ilst items -> reach 'data'
                if (descend) Walk(b, childStart, off + dlen, path + "/" + type, outList, ref trak);
                p += (int)size;
            }
        }

        private static Box First(List<Box> boxes, string type) { return boxes.Find(x => x.Type == type); }
        private static Box First(List<Box> boxes, string type, int trak) { return boxes.Find(x => x.Type == type && x.Trak == trak); }

        // ── Duration (mvhd) ───────────────────────────────────────────────
        private static double ReadDuration(byte[] b, List<Box> boxes)
        {
            Box mvhd = First(boxes, "mvhd");
            if (mvhd == null) return 0;
            int o = mvhd.Off; int ver = b[o];
            long ts, du;
            if (ver == 1) { ts = BE32(b, o + 20); du = BE64(b, o + 24); }
            else { ts = BE32(b, o + 12); du = BE32(b, o + 16); }
            return ts > 0 ? (double)du / ts : 0;
        }

        // ── Metadata (ilst ©nam / ©ART) ───────────────────────────────────
        private static void ReadMetadata(byte[] b, List<Box> boxes, M4bBook book)
        {
            string nam = "", art = "", aart = "", desc = "", ldes = "";
            foreach (Box d in boxes.FindAll(x => x.Type == "data" && x.Path.Contains("/ilst/")))
            {
                int idx = d.Path.IndexOf("/ilst/");
                string atom = d.Path.Substring(idx + 6).Split('/')[0];
                if (d.Len <= 8) continue;
                string val = Utf8(b, d.Off + 8, d.Len - 8);
                if (atom == "©nam" && nam == "") nam = val;
                else if (atom == "©ART" && art == "") art = val;
                else if (atom == "aART" && aart == "") aart = val;  // album artist
                else if (atom == "desc" && desc == "") desc = val;  // short description
                else if (atom == "ldes" && ldes == "") ldes = val;  // long description
            }
            book.Title = nam;
            // Audiobooks commonly carry the author in Artist, else Album Artist.
            book.Author = art != "" ? art : aart;

            // LONG first: where a producer fills both, desc is a one-line teaser
            // and ldes is the blurb. Cleaned like every other source, because an
            // M4B description can carry markup too.
            string blurb = BookDescription.Clean(ldes != "" ? ldes : desc);

            // AND A FLOOR, which the measurement asked for. Twelve of thirteen
            // sampled books carry a description — the best rate of any format —
            // but two of those twelve are not descriptions at all: "Narrated by:
            // CC Hogan" (21 characters) and "Narrated by: Hollie Jackson" (27),
            // sitting in `desc` with no `ldes` beside them. A Description row
            // that opens a window to read a narrator's name is worse than no row.
            //
            // 80 characters, the same floor the trailing-text rule uses. The real
            // blurbs in the sample start at 230, so nothing genuine is anywhere
            // near it, and NBR has no narrator field for the credit to go to.
            book.Description = blurb.Length >= 80 ? blurb : "";
        }

        private static string Utf8(byte[] b, int off, int len)
        {
            if (off < 0 || len < 0 || off + len > b.Length) return "";
            return Encoding.UTF8.GetString(b, off, len).Trim();
        }

        // ── Nero chpl ─────────────────────────────────────────────────────
        // chpl payload: version/flags(4) [+ reserved(1)] + count, then per
        // chapter [8B start (100 ns)][1B title length][UTF-8 title]. Producers
        // vary the header, so we try a few record-start offsets and keep the
        // one whose first records parse cleanly (base 9 in every sampled book).
        private static List<(string, double)> ReadChpl(byte[] b, List<Box> boxes)
        {
            Box chpl = First(boxes, "chpl");
            if (chpl == null) return null;
            foreach (int baseOff in new[] { 9, 8, 5, 4 })
            {
                var result = new List<(string, double)>();
                int p = chpl.Off + baseOff; int endB = chpl.Off + chpl.Len; bool ok = true;
                for (int i = 0; i < 5000; i++)
                {
                    if (p + 9 > endB) break;
                    long start100ns = BE64(b, p); p += 8;
                    int tl = b[p]; p += 1;
                    if (tl < 1 || p + tl > endB) { ok = false; break; }
                    string title = Encoding.UTF8.GetString(b, p, tl); p += tl;
                    if (ContainsControl(title)) { ok = false; break; }
                    result.Add((title, start100ns / 1e7));
                    if (result.Count >= 5000) break;
                }
                if (ok && result.Count >= 1) return result;
            }
            return null;
        }

        private static bool ContainsControl(string s)
        {
            foreach (char c in s) if (c < 9 || (c >= 0x0e && c < 0x20)) return true;
            return false;
        }

        // ── QuickTime text chapter track ──────────────────────────────────
        private static List<(string, double)> ReadQtChapters(string path, byte[] b, List<Box> boxes)
        {
            var empty = new List<(string, double)>();
            // Map each track's track_id (tkhd) to its trak index.
            var idToTrak = new Dictionary<long, int>();
            foreach (Box tkhd in boxes.FindAll(x => x.Type == "tkhd"))
            {
                int o = tkhd.Off; int ver = b[o];
                long tid = ver == 1 ? BE32(b, o + 20) : BE32(b, o + 12);
                idToTrak[tid] = tkhd.Trak;
            }

            // The audio track's tref/chap lists the chapter track's id(s).
            int chapTrak = -1;
            Box chap = boxes.Find(x => x.Type == "chap" && x.Path.EndsWith("/tref/chap"));
            if (chap != null)
            {
                for (int o = chap.Off; o + 4 <= chap.Off + chap.Len; o += 4)
                {
                    long tid = BE32(b, o);
                    if (idToTrak.TryGetValue(tid, out int tk)) { chapTrak = tk; break; }
                }
            }
            // Fallback: any text/subtitle track (handler type text/sbtl).
            if (chapTrak < 0)
            {
                foreach (Box hdlr in boxes.FindAll(x => x.Type == "hdlr"))
                {
                    string ht = Str4(b, hdlr.Off + 8);
                    if (ht == "text" || ht == "sbtl") { chapTrak = hdlr.Trak; break; }
                }
            }
            if (chapTrak < 0) return empty;

            // Start times from stts (sample deltas) / mdhd timescale.
            double timescale = 1000;
            Box mdhd = First(boxes, "mdhd", chapTrak);
            if (mdhd != null) { int o = mdhd.Off; int ver = b[o]; timescale = ver == 1 ? BE32(b, o + 20) : BE32(b, o + 12); }
            if (timescale <= 0) timescale = 1000;

            var starts = new List<double>();
            Box stts = First(boxes, "stts", chapTrak);
            if (stts != null)
            {
                int o = stts.Off; long entries = BE32(b, o + 4); int q = o + 8; long t = 0;
                for (long e = 0; e < entries && q + 8 <= stts.Off + stts.Len; e++)
                {
                    long cnt = BE32(b, q); long delta = BE32(b, q + 4); q += 8;
                    for (long s = 0; s < cnt && starts.Count < 10000; s++) { starts.Add(t / timescale); t += delta; }
                }
            }

            // Sample titles (read from the file via the sample tables).
            var titles = ReadTextSamples(path, b, boxes, chapTrak);

            int n = Math.Min(starts.Count, titles.Count);
            if (n == 0)
            {
                // Times but no readable titles → generic labels.
                var res = new List<(string, double)>();
                for (int i = 0; i < starts.Count; i++) res.Add(("Chapter " + (i + 1), starts[i]));
                return res;
            }
            var chapters = new List<(string, double)>();
            for (int i = 0; i < n; i++)
            {
                string title = string.IsNullOrWhiteSpace(titles[i]) ? ("Chapter " + (i + 1)) : titles[i];
                chapters.Add((title, starts[i]));
            }
            return chapters;
        }

        // Reads each text sample = [2B length][UTF-8 title] using stsc/stsz/stco.
        private static List<string> ReadTextSamples(string path, byte[] b, List<Box> boxes, int trak)
        {
            var titles = new List<string>();
            Box stsz = First(boxes, "stsz", trak);
            Box stsc = First(boxes, "stsc", trak);
            Box stco = First(boxes, "stco", trak);
            Box co64 = First(boxes, "co64", trak);
            if (stsz == null || stsc == null || (stco == null && co64 == null)) return titles;

            // Sample sizes.
            long sampleSizeFixed = BE32(b, stsz.Off + 4);
            long sampleCount = BE32(b, stsz.Off + 8);
            if (sampleCount <= 0 || sampleCount > 100000) return titles;
            var sizes = new long[sampleCount];
            if (sampleSizeFixed != 0) { for (int i = 0; i < sampleCount; i++) sizes[i] = sampleSizeFixed; }
            else { int q = stsz.Off + 12; for (int i = 0; i < sampleCount && q + 4 <= stsz.Off + stsz.Len; i++, q += 4) sizes[i] = BE32(b, q); }

            // Chunk offsets.
            var chunkOffsets = new List<long>();
            if (co64 != null) { long ec = BE32(b, co64.Off + 4); int q = co64.Off + 8; for (long i = 0; i < ec && q + 8 <= co64.Off + co64.Len; i++, q += 8) chunkOffsets.Add(BE64(b, q)); }
            else { long ec = BE32(b, stco.Off + 4); int q = stco.Off + 8; for (long i = 0; i < ec && q + 4 <= stco.Off + stco.Len; i++, q += 4) chunkOffsets.Add(BE32(b, q)); }
            int nChunks = chunkOffsets.Count;
            if (nChunks == 0) return titles;

            // Samples per chunk (expand stsc).
            var stscE = new List<(long first, long spc)>();
            { long ec = BE32(b, stsc.Off + 4); int q = stsc.Off + 8; for (long i = 0; i < ec && q + 12 <= stsc.Off + stsc.Len; i++, q += 12) stscE.Add((BE32(b, q), BE32(b, q + 4))); }
            var samplesPerChunk = new int[nChunks];
            for (int e = 0; e < stscE.Count; e++)
            {
                long firstChunk = stscE[e].first;                       // 1-based
                long nextFirst = (e + 1 < stscE.Count) ? stscE[e + 1].first : nChunks + 1;
                for (long c = firstChunk; c < nextFirst && c - 1 < nChunks; c++) samplesPerChunk[c - 1] = (int)stscE[e].spc;
            }

            // Per-sample file offsets.
            var sampleOffsets = new long[sampleCount];
            long si = 0;
            for (int c = 0; c < nChunks && si < sampleCount; c++)
            {
                long off = chunkOffsets[c];
                for (int s = 0; s < samplesPerChunk[c] && si < sampleCount; s++)
                {
                    sampleOffsets[si] = off; off += sizes[si]; si++;
                }
            }
            if (si < sampleCount) return titles; // stsc/stco inconsistent — bail

            // Read the samples from the file.
            using (FileStream fs = File.OpenRead(path))
            {
                for (long i = 0; i < sampleCount; i++)
                {
                    long size = sizes[i];
                    if (size < 2 || size > 4096) { titles.Add(""); continue; }
                    byte[] buf = new byte[size];
                    fs.Position = sampleOffsets[i];
                    int read = 0; while (read < size) { int r = fs.Read(buf, read, (int)size - read); if (r <= 0) break; read += r; }
                    int tl = BE16(buf, 0);
                    if (tl < 0 || 2 + tl > size) { titles.Add(""); continue; }
                    titles.Add(Encoding.UTF8.GetString(buf, 2, tl));
                }
            }
            return titles;
        }
    }
}
