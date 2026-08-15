using System;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Speech already made, kept beside the book.
    ///
    /// <para><b>Why it exists at all is money, and only for the cloud voices.</b>
    /// A sentence read by Google is paid for once; hearing it again should not be
    /// paid for twice, and the free allowance is about two average books a month
    /// — measured on Gordan's own shelf, 472 000 characters a book, so 2.1 books
    /// per million. A local voice is free and faster than listening, so caching
    /// one buys nothing for ordinary reading and costs a quarter of a gigabyte a
    /// book; there the cache fills only when it has been ASKED for, by preparing
    /// a book or exporting one.</para>
    ///
    /// <para><b>The key is the text that reaches the ENGINE</b> — what
    /// <c>TtsReader.Spoken</c> hands over, i.e. after the pronunciation
    /// dictionary has had its say — together with the voice. That falls out
    /// right on its own: change a dictionary rule and the spoken text changes,
    /// so the cache misses and the sentence is made afresh, which is exactly
    /// what a reader who just rewrote a rule expects to hear.</para>
    ///
    /// <para><b>Speed and volume are deliberately NOT in the key</b> (Gordan,
    /// 2026-08-15). They are a listening habit and they change; the audio is an
    /// asset bought once. Were they in the key, nudging the speed once would
    /// strand a whole prepared book on disk — not even deleted, just never
    /// matched again — and the next reading would be paid for a second time. The
    /// cost of keeping them out is that the audio is stored at the voice's own
    /// natural speed and the speeding up has to happen at PLAYBACK, which
    /// <see cref="SapiWavPlayer"/> cannot do and mpv can, through the same
    /// <c>scaletempo2</c> that has been speeding up audiobooks all along.</para>
    ///
    /// <para><b>Stored as MP3</b>, because as LINEAR16 a nine-hour book is about
    /// 1.5 GB and there is no arguing with that. See <see cref="Mp3Encoder"/>.</para>
    /// </summary>
    internal static class SpeechCache
    {
        private const string FolderName = "speech";

        public static string FolderFor(string bookFolder)
        {
            return string.IsNullOrEmpty(bookFolder) ? null : Path.Combine(bookFolder, FolderName);
        }

        /// <summary>The file a sentence would be in, whether or not it is there.
        ///
        /// <para><b>The name carries the LENGTH as well as the hash.</b> A 64-bit
        /// FNV over the few thousand sentences of a book collides at odds not
        /// worth thinking about — but the cost of the one collision that does
        /// happen is the wrong sentence spoken in the right place, which is
        /// exactly the kind of fault nobody diagnoses. Requiring the length to
        /// match as well is free and removes the whole class.</para></summary>
        public static string PathFor(string bookFolder, string voice, string spoken)
        {
            string dir = FolderFor(bookFolder);
            if (dir == null || spoken == null) return null;
            return Path.Combine(dir, Key(voice, spoken) + ".mp3");
        }

        public static string Key(string voice, string spoken)
        {
            string s = (voice ?? "") + "\n" + (spoken ?? "");
            ulong h = 14695981039346656037UL;                 // FNV-1a, 64 bit
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("x16") + "-" + (spoken == null ? 0 : spoken.Length);
        }

        /// <summary>The stored audio, or null. Never throws: a cache that cannot
        /// be read is a cache miss, and the reader hears the sentence made
        /// again rather than an error.</summary>
        public static byte[] Get(string bookFolder, string voice, string spoken)
        {
            try
            {
                string p = PathFor(bookFolder, voice, spoken);
                if (p == null || !File.Exists(p)) return null;
                byte[] b = File.ReadAllBytes(p);
                return b.Length > 0 ? b : null;
            }
            catch { return null; }
        }

        /// <summary>Encodes and stores. Returns false if it could not be kept —
        /// which is never allowed to stop the reading, only to mean it will be
        /// made again next time.
        ///
        /// <para>Written to a temporary name and moved into place, so a reader
        /// who closes the player mid-write is left with no file rather than half
        /// a one. A half file is worse than none: it would be found, read, and
        /// played as a truncated sentence for ever.</para></summary>
        public static bool Put(string bookFolder, string voice, string spoken, byte[] wav)
        {
            try
            {
                string p = PathFor(bookFolder, voice, spoken);
                if (p == null || wav == null) return false;

                byte[] mp3 = Mp3Encoder.FromWav(wav);
                if (mp3 == null || mp3.Length == 0) return false;

                Directory.CreateDirectory(Path.GetDirectoryName(p));
                string tmp = p + ".part";
                File.WriteAllBytes(tmp, mp3);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
                return true;
            }
            catch { return false; }
        }

        public static bool Has(string bookFolder, string voice, string spoken)
        {
            try
            {
                string p = PathFor(bookFolder, voice, spoken);
                return p != null && File.Exists(p);
            }
            catch { return false; }
        }

        /// <summary>How much of a book is already made: how many pieces and how
        /// many bytes. What the player's info line reports while a book is being
        /// prepared, and what tells a reader whether an export is worth
        /// asking for.</summary>
        public static void Measure(string bookFolder, out int pieces, out long bytes)
        {
            pieces = 0;
            bytes = 0;
            try
            {
                string dir = FolderFor(bookFolder);
                if (dir == null || !Directory.Exists(dir)) return;
                foreach (string f in Directory.EnumerateFiles(dir, "*.mp3"))
                {
                    pieces++;
                    bytes += new FileInfo(f).Length;
                }
            }
            catch { }
        }

        /// <summary>Throws the whole thing away. The book is untouched — this is
        /// only audio that can be made again, which for a local voice costs
        /// nothing and for a cloud voice costs what it costs.</summary>
        public static void Clear(string bookFolder)
        {
            try
            {
                string dir = FolderFor(bookFolder);
                if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
