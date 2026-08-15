using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// A book's cached speech joined into one MP3 — a real audiobook, playable
    /// anywhere, not only inside NBR.
    ///
    /// <para><b>Laying the pieces end to end is NOT enough, and this was measured
    /// before it was written.</b> MP3 frames are self-contained, so a plain
    /// concatenation does play — mpv reported 10.23 s for a file whose pieces sum
    /// to 10.20. But every piece carries its own Xing header, and the first one
    /// describes only the first piece: it announced <b>106 frames and 17 688
    /// bytes, meaning 2.54 seconds</b>, for a file of 73 296 bytes and 10.23
    /// seconds. mpv survives that by noticing the byte count disagrees and
    /// rescanning. A player that trusts the header would say two and a half
    /// seconds for a whole book and seek accordingly — and this file exists
    /// precisely to be played somewhere else.</para>
    ///
    /// <para>So every piece's Xing frame comes out, the frames are counted, and
    /// ONE correct header goes in at the front. The header frame is not built
    /// from nothing: the first piece's own is reused as a template and its
    /// numbers overwritten, because a hand-made frame is one wrong bit away from
    /// a file that no longer starts.</para>
    /// </summary>
    internal static class SpeechExport
    {
        /// <summary>One frame's worth of facts, or Length 0 when the bytes at
        /// <paramref name="at"/> are not a frame header.</summary>
        private struct Frame
        {
            public int Length;
            public int Samples;
            public bool Mpeg1;
            public bool Mono;
        }

        private static readonly int[] Mpeg1Rates =
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] Mpeg2Rates =
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        private static readonly int[] Mpeg1Hz = { 44100, 48000, 32000, 0 };
        private static readonly int[] Mpeg2Hz = { 22050, 24000, 16000, 0 };
        private static readonly int[] Mpeg25Hz = { 11025, 12000, 8000, 0 };

        private static Frame Read(byte[] b, int at)
        {
            var f = new Frame();
            if (at + 4 > b.Length) return f;
            if (b[at] != 0xFF || (b[at + 1] & 0xE0) != 0xE0) return f;

            int versionBits = (b[at + 1] >> 3) & 3;      // 0 = 2.5, 2 = 2, 3 = 1
            int layer = (b[at + 1] >> 1) & 3;            // 1 = Layer III
            if (versionBits == 1 || layer != 1) return f;

            int rateIndex = (b[at + 2] >> 4) & 0xF;
            int hzIndex = (b[at + 2] >> 2) & 3;
            int padding = (b[at + 2] >> 1) & 1;
            int mode = (b[at + 3] >> 6) & 3;             // 3 = mono
            if (rateIndex == 0 || rateIndex == 15 || hzIndex == 3) return f;

            f.Mpeg1 = versionBits == 3;
            f.Mono = mode == 3;
            int kbps = (f.Mpeg1 ? Mpeg1Rates : Mpeg2Rates)[rateIndex];
            int hz = (f.Mpeg1 ? Mpeg1Hz : versionBits == 2 ? Mpeg2Hz : Mpeg25Hz)[hzIndex];
            if (kbps == 0 || hz == 0) return f;

            f.Samples = f.Mpeg1 ? 1152 : 576;
            // Layer III: 144 000 * kbps / hz for MPEG 1, half that below it.
            f.Length = (f.Mpeg1 ? 144000 : 72000) * kbps / hz + padding;
            return f;
        }

        /// <summary>Where the audio really starts: past an ID3v2 block if there is
        /// one, and past the Xing/Info frame if the first frame is one.</summary>
        private static int AudioStart(byte[] b, out int tagFrameAt, out int tagFrameLen)
        {
            tagFrameAt = -1;
            tagFrameLen = 0;

            int at = 0;
            if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
                at = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 |
                           (b[8] & 0x7F) << 7 | (b[9] & 0x7F));

            // Find the first real frame from there.
            while (at + 4 <= b.Length && Read(b, at).Length == 0) at++;
            Frame first = Read(b, at);
            if (first.Length == 0) return b.Length;

            // A Xing or Info tag lives inside that first frame, at a distance that
            // depends on the version and the channels. Rather than trust the
            // table, look for the word inside the frame itself.
            int end = Math.Min(b.Length, at + first.Length);
            for (int i = at + 4; i + 4 <= end; i++)
            {
                if ((b[i] == 'X' && b[i + 1] == 'i' && b[i + 2] == 'n' && b[i + 3] == 'g') ||
                    (b[i] == 'I' && b[i + 1] == 'n' && b[i + 2] == 'f' && b[i + 3] == 'o'))
                {
                    tagFrameAt = at;
                    tagFrameLen = first.Length;
                    return at + first.Length;
                }
            }
            return at;
        }

        /// <summary>Joins the pieces in reading order. Returns how many sentences
        /// were written; <paramref name="missing"/> is how many had nothing on
        /// disk and were passed over.
        ///
        /// <para>A missing piece is skipped rather than refused. A book prepared
        /// while a few sentences failed is still a book, and the count says so —
        /// which is more use than nothing at all and an error message.</para></summary>
        public static int ToFile(string bookFolder, string voice, IList<string> spoken,
                                 string outPath, out int missing, Func<int, int, bool> progress)
        {
            missing = 0;
            if (spoken == null || spoken.Count == 0 || string.IsNullOrEmpty(outPath)) return 0;

            byte[] template = null;
            int templateLen = 0;
            var audio = new MemoryStream();
            int written = 0;

            for (int i = 0; i < spoken.Count; i++)
            {
                if (progress != null && !progress(i, spoken.Count)) return -1;   // gave up

                byte[] piece = SpeechCache.Get(bookFolder, voice, spoken[i]);
                if (piece == null) { missing++; continue; }

                int tagAt, tagLen;
                int start = AudioStart(piece, out tagAt, out tagLen);
                if (template == null && tagAt >= 0)
                {
                    template = new byte[tagLen];
                    Buffer.BlockCopy(piece, tagAt, template, 0, tagLen);
                    templateLen = tagLen;
                }
                if (start < piece.Length) audio.Write(piece, start, piece.Length - start);
                written++;
            }

            byte[] body = audio.ToArray();
            if (body.Length == 0) return 0;

            try
            {
                using (var w = File.Create(outPath))
                {
                    if (template != null)
                    {
                        Patch(template, body, templateLen);
                        w.Write(template, 0, templateLen);
                    }
                    w.Write(body, 0, body.Length);
                }
            }
            catch { return 0; }
            return written;
        }

        /// <summary>Rewrites the borrowed header so it describes the WHOLE file:
        /// how many frames, how many bytes, and the hundred-point table a player
        /// seeks by. Everything else in the frame — the bitrate, the sample rate,
        /// the channel mode — is already right, because it came from audio of
        /// exactly this shape.</summary>
        private static void Patch(byte[] tag, byte[] body, int tagLen)
        {
            int at = -1;
            for (int i = 4; i + 4 <= tagLen; i++)
                if ((tag[i] == 'X' && tag[i + 1] == 'i' && tag[i + 2] == 'n' && tag[i + 3] == 'g') ||
                    (tag[i] == 'I' && tag[i + 1] == 'n' && tag[i + 2] == 'f' && tag[i + 3] == 'o'))
                { at = i; break; }
            if (at < 0) return;

            // Count the frames, and remember where each hundredth of the file is.
            var marks = new List<int>();
            int frames = 0, p = 0;
            while (p < body.Length)
            {
                Frame f = Read(body, p);
                if (f.Length == 0) { p++; continue; }
                marks.Add(p);
                frames++;
                p += f.Length;
            }

            int total = tagLen + body.Length;
            int flags = BigInt(tag, at + 4);
            // Say that frames, bytes and the table are all present now, whatever
            // the piece we borrowed this from happened to claim.
            PutBig(tag, at + 4, flags | 1 | 2 | 4);
            PutBig(tag, at + 8, frames);
            PutBig(tag, at + 12, total);

            int toc = at + 16;
            if (toc + 100 <= tagLen && marks.Count > 0)
                for (int i = 0; i < 100; i++)
                {
                    int mark = marks[(int)((long)i * marks.Count / 100)];
                    int v = (int)((long)(mark + tagLen) * 256 / total);
                    tag[toc + i] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                }
        }

        private static int BigInt(byte[] b, int at)
        {
            return (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        }

        private static void PutBig(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
