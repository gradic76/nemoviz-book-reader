using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// WAV in, MP3 out, through the vendored <c>libmp3lame.dll</c> (LAME 3.100,
    /// x64, LGPL — see THIRD-PARTY-NOTICES).
    ///
    /// <para><b>Why an encoder at all.</b> Speech comes back as LINEAR16 and that
    /// is 48 000 bytes a second: a nine-hour book is about 1.5 GB as WAV and
    /// around 250 MB as MP3. A speech cache that is not encoded is a cache
    /// nobody can afford to keep.</para>
    ///
    /// <para><b>Why THIS DLL, measured rather than chosen by filename.</b> Three
    /// LAME builds sat in the vendor store and two of them cannot do the job:
    /// <c>lame_enc.dll</c> exports <b>29</b> symbols — the old Blade interface
    /// plus a fragment of the native one, with no <c>lame_encode_buffer</c> and
    /// no <c>lame_set_brate</c> — while <c>libmp3lame.dll</c> exports <b>220</b>
    /// and has everything. A string search through the binaries said all three
    /// were fine; reading the PE export table said otherwise. The build that
    /// fixes a known CBR regression is one of the two that cannot be used, which
    /// is a further reason for VBR below.</para>
    ///
    /// <para><b>And why not through libmpv, which is already here.</b> Scanned:
    /// the shipped 30.2 MB libmpv carries <c>mp3float</c> and
    /// <c>mp3adufloat</c> — DECODERS — and no encoder of any kind. FFmpeg has no
    /// native MP3 encoder; it links libmp3lame or libshine. So rebuilding libmpv
    /// to encode would ship LAME anyway, buried inside it, at the cost of another
    /// CI run and of the audio-only trim that took it from 93.6 MB down.</para>
    ///
    /// <para><b>VBR, not CBR.</b> Speech is mostly quiet with short loud parts,
    /// which is exactly where variable bitrate wins: the same quality in fewer
    /// bytes. The Xing/LAME header goes in at the end
    /// (<c>lame_get_lametag_frame</c>) — without it a VBR MP3 seeks by guesswork
    /// and reports the wrong duration, which for a book is not cosmetic.</para>
    /// </summary>
    internal static class Mp3Encoder
    {
        private const string Dll = "libmp3lame.dll";

        // MPEG_mode
        private const int Mono = 3;
        // vbr_mode: 0 off, 1 mt, 2 rh, 3 abr, 4 mtrh (the modern default)
        private const int VbrMtrh = 4;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lame_init();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_set_in_samplerate(IntPtr gfp, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_set_num_channels(IntPtr gfp, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_set_mode(IntPtr gfp, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_set_VBR(IntPtr gfp, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_set_VBR_q(IntPtr gfp, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_set_quality(IntPtr gfp, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_init_params(IntPtr gfp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_encode_buffer(IntPtr gfp, short[] left, short[] right,
                                                     int samples, byte[] mp3, int mp3Size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_encode_flush(IntPtr gfp, byte[] mp3, int mp3Size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr lame_get_lametag_frame(IntPtr gfp, byte[] buffer, UIntPtr size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lame_close(IntPtr gfp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr get_lame_version();

        private static bool? available;

        /// <summary>Is the encoder there and working? Asked by calling it, not by
        /// looking for the file — a DLL of the wrong architecture exists and still
        /// cannot be used.</summary>
        public static bool Available
        {
            get
            {
                if (available.HasValue) return available.Value;
                try { available = Version != null; }
                catch { available = false; }
                return available.Value;
            }
        }

        /// <summary>What the DLL says it is. Straight from the binary, which is
        /// the only version claim worth anything.</summary>
        public static string Version
        {
            get
            {
                try
                {
                    IntPtr p = get_lame_version();
                    return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
                }
                catch { return null; }
            }
        }

        /// <summary>Encodes a PCM WAV. Returns null if the encoder is missing or
        /// the input is not something it can read — never throws into the middle
        /// of a reading.
        ///
        /// <para><paramref name="quality"/> is LAME's VBR quality, 0 best to 9
        /// worst. The default is chosen for SPEECH, not music: 6 is around 60–70
        /// kbps mono here, which for one voice on a plain background is
        /// transparent, and it is the difference between 250 MB a book and
        /// twice that.</para></summary>
        public static byte[] FromWav(byte[] wav, int quality = 6)
        {
            if (!Available || wav == null) return null;

            int channels, rate, bits, dataAt, dataLen;
            if (!ReadWavHeader(wav, out channels, out rate, out bits, out dataAt, out dataLen))
                return null;
            if (bits != 16 || channels < 1 || channels > 2) return null;

            int frames = dataLen / (2 * channels);
            if (frames <= 0) return null;

            // Interleaved 16-bit to one array per channel. Mono is the normal case
            // here — every speech backend produces it — and LAME reads only the
            // left array when it has been told there is one channel.
            short[] left = new short[frames];
            short[] right = channels == 2 ? new short[frames] : left;
            for (int i = 0; i < frames; i++)
            {
                int at = dataAt + i * 2 * channels;
                left[i] = BitConverter.ToInt16(wav, at);
                if (channels == 2) right[i] = BitConverter.ToInt16(wav, at + 2);
            }

            IntPtr gfp = IntPtr.Zero;
            try
            {
                gfp = lame_init();
                if (gfp == IntPtr.Zero) return null;

                lame_set_in_samplerate(gfp, rate);
                lame_set_num_channels(gfp, channels);
                if (channels == 1) lame_set_mode(gfp, Mono);
                lame_set_VBR(gfp, VbrMtrh);
                lame_set_VBR_q(gfp, quality < 0 ? 0 : (quality > 9 ? 9 : quality));
                lame_set_quality(gfp, 2);          // encoder effort, not output quality
                if (lame_init_params(gfp) < 0) return null;

                // LAME's own worst case, and it must be respected: a buffer even a
                // little short is not an error return, it is a corrupt stream.
                byte[] buf = new byte[(int)(1.25 * frames) + 7200];
                int n = lame_encode_buffer(gfp, left, right, frames, buf, buf.Length);
                if (n < 0) return null;

                byte[] tail = new byte[7200];
                int t = lame_encode_flush(gfp, tail, tail.Length);
                if (t < 0) t = 0;

                var ms = new MemoryStream(n + t);
                ms.Write(buf, 0, n);
                ms.Write(tail, 0, t);
                byte[] mp3 = ms.ToArray();

                // THE XING/LAME HEADER, and it goes in LAST because only now are
                // the frame sizes known. LAME reserved a blank frame at the front
                // for it; this overwrites that reservation in place, so the length
                // does not change. Without it a VBR file seeks by assuming a
                // constant bitrate it does not have, and reports a duration to
                // match - which in a nine-hour book is minutes out.
                byte[] tag = new byte[7200];
                UIntPtr got = lame_get_lametag_frame(gfp, tag, (UIntPtr)tag.Length);
                int tagLen = (int)got;
                if (tagLen > 0 && tagLen <= mp3.Length) Buffer.BlockCopy(tag, 0, mp3, 0, tagLen);

                return mp3;
            }
            catch { return null; }
            finally { if (gfp != IntPtr.Zero) try { lame_close(gfp); } catch { } }
        }

        /// <summary>Walks the RIFF chunks rather than assuming a 44-byte header.
        /// Google's WAVs are the plain 44 bytes, but a local synthesiser may put a
        /// LIST or fact chunk in front of the samples, and reading those as audio
        /// produces a click at the start of every sentence.</summary>
        private static bool ReadWavHeader(byte[] w, out int channels, out int rate,
                                          out int bits, out int dataAt, out int dataLen)
        {
            channels = rate = bits = dataAt = dataLen = 0;
            if (w.Length < 44) return false;
            if (w[0] != 'R' || w[1] != 'I' || w[2] != 'F' || w[3] != 'F') return false;
            if (w[8] != 'W' || w[9] != 'A' || w[10] != 'V' || w[11] != 'E') return false;

            int at = 12;
            bool haveFmt = false;
            while (at + 8 <= w.Length)
            {
                string id = Encoding.ASCII.GetString(w, at, 4);
                int size = BitConverter.ToInt32(w, at + 4);
                int body = at + 8;
                if (size < 0 || body + size > w.Length) size = w.Length - body;

                if (id == "fmt " && size >= 16)
                {
                    channels = BitConverter.ToInt16(w, body + 2);
                    rate = BitConverter.ToInt32(w, body + 4);
                    bits = BitConverter.ToInt16(w, body + 14);
                    haveFmt = true;
                }
                else if (id == "data")
                {
                    dataAt = body;
                    dataLen = size;
                    return haveFmt && rate > 0;
                }
                at = body + size + (size & 1);   // chunks are word-aligned
            }
            return false;
        }
    }
}
