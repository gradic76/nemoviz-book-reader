using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The player's own beeps — "nothing loaded", volume floor and ceiling, the
    /// bookmark confirmation, the sleep timer's five-minute warning.
    ///
    /// <para><b>They come out of the same sound card as the book.</b>
    /// <c>Console.Beep</c> does not: it goes wherever Windows sends system sounds,
    /// which for someone listening on headphones or a second card means the
    /// feedback lands in a different room from the audio it belongs to. So a tone
    /// is generated here as a small WAV and played on its own libmpv context,
    /// pointed at the same <c>audio-device</c> as everything else.</para>
    ///
    /// <para><b>Why mpv and not SAPI</b> (which is how speech is played): SAPI
    /// output is not shareable across processes. Playing a tone through a second
    /// <c>SpVoice</c> on the SAME output token **killed the 32-bit speech host's
    /// playback** — measured: with eSpeak reading, sentences after a beep were
    /// reported finished in ~420 ms instead of being spoken, i.e. silence. On the
    /// default device, or through <c>Console.Beep</c>, the same test read normally.
    /// mpv opens WASAPI in shared mode, so its tones simply mix with the book, the
    /// speech host and everything else.</para>
    ///
    /// <para>It also stops blocking: <c>Console.Beep</c> holds the calling thread
    /// for the whole tone, so the five-beep bookmark series froze the UI for a
    /// second. A series is rendered as ONE buffer and played in the background,
    /// which keeps its timing exact as well.</para>
    /// </summary>
    public class SignalTones : IDisposable
    {
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, string name, string data);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);

        private const int SampleRate = 22050;
        private const int GapMs = 30;        // silence between tones in a series

        private readonly object gate = new object();
        private IntPtr ctx = IntPtr.Zero;
        private bool unavailable;
        private string deviceId = "";
        // The tone file currently playing; kept until the next one so mpv is never
        // reading a file we have just deleted.
        private string lastFile;

        /// <summary>Which card the tones play on (mpv-style id, as in Settings →
        /// Device). Empty = system default.</summary>
        public void SetDevice(string mpvDeviceId)
        {
            lock (gate)
            {
                deviceId = mpvDeviceId ?? "";
                if (ctx != IntPtr.Zero)
                    mpv_set_property_string(ctx, "audio-device",
                        deviceId.Length == 0 ? "auto" : deviceId);
            }
        }

        /// <summary>One tone.</summary>
        public void Play(int frequency, int milliseconds)
        {
            Play(new[] { (frequency, milliseconds) });
        }

        /// <summary>A series of tones, as one uninterrupted sound.</summary>
        public void Play((int Freq, int Ms)[] tones)
        {
            if (tones == null || tones.Length == 0) return;
            lock (gate)
            {
                try
                {
                    if (!unavailable && (ctx != IntPtr.Zero || Create()))
                    {
                        string path = Write(Render(tones));
                        if (path != null)
                        {
                            Command("loadfile", path, "replace");
                            CleanUp(path);
                            return;
                        }
                    }
                }
                catch { unavailable = true; }
            }

            // Last resort: the system beep, on whatever device Windows uses.
            foreach (var t in tones)
            {
                try { Console.Beep(Clamp(t.Freq, 37, 32767), Math.Max(1, t.Ms)); } catch { }
            }
        }

        private bool Create()
        {
            ctx = mpv_create();
            if (ctx == IntPtr.Zero) { unavailable = true; return false; }
            mpv_set_property_string(ctx, "terminal", "no");
            if (mpv_initialize(ctx) < 0) { unavailable = true; ctx = IntPtr.Zero; return false; }
            mpv_set_property_string(ctx, "vid", "no");
            mpv_set_property_string(ctx, "audio-display", "no");
            // Keep the device open between tones: reopening it for every beep costs
            // a noticeable delay before the sound starts.
            mpv_set_property_string(ctx, "audio-keep-open", "yes");
            mpv_set_property_string(ctx, "audio-device", deviceId.Length == 0 ? "auto" : deviceId);
            return true;
        }

        private string Write(byte[] wav)
        {
            if (wav == null) return null;
            try
            {
                string path = Path.Combine(Path.GetTempPath(),
                    "nbr-tone-" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(path, wav);
                return path;
            }
            catch { return null; }
        }

        /// <summary>Deletes the previous tone file now that mpv has moved on to a
        /// new one. (Deleting the file we just handed it would be a race.)</summary>
        private void CleanUp(string current)
        {
            if (lastFile != null && lastFile != current)
                try { File.Delete(lastFile); } catch { }
            lastFile = current;
        }

        /// <summary>Builds a 16-bit mono WAV holding the whole series. Each tone
        /// fades in and out over five milliseconds — a sine that starts or stops at
        /// full amplitude clicks, and a click is exactly what an attentive listener
        /// hears instead of the tone.</summary>
        private static byte[] Render((int Freq, int Ms)[] tones)
        {
            int total = 0;
            foreach (var t in tones) total += Samples(t.Ms) + Samples(GapMs);
            if (total <= 0) return null;

            byte[] wav = new byte[44 + total * 2];
            WriteHeader(wav, total);

            int at = 44;
            foreach (var t in tones)
            {
                int n = Samples(t.Ms);
                int fade = Math.Min(Samples(5), n / 2);
                double step = 2 * Math.PI * Math.Max(1, t.Freq) / SampleRate;
                for (int i = 0; i < n; i++)
                {
                    double amp = 0.35;
                    if (i < fade) amp *= (double)i / fade;
                    else if (i > n - fade) amp *= (double)(n - i) / fade;
                    short s = (short)(Math.Sin(step * i) * amp * short.MaxValue);
                    wav[at++] = (byte)(s & 0xFF);
                    wav[at++] = (byte)((s >> 8) & 0xFF);
                }
                at += Samples(GapMs) * 2;      // silence: the buffer is already zero
            }
            return wav;
        }

        private static int Samples(int ms) { return SampleRate * Math.Max(0, ms) / 1000; }

        private static void WriteHeader(byte[] w, int sampleCount)
        {
            int dataBytes = sampleCount * 2;
            Write(w, 0, "RIFF");
            WriteInt(w, 4, 36 + dataBytes);
            Write(w, 8, "WAVE");
            Write(w, 12, "fmt ");
            WriteInt(w, 16, 16);                       // PCM header size
            WriteShort(w, 20, 1);                      // PCM
            WriteShort(w, 22, 1);                      // mono
            WriteInt(w, 24, SampleRate);
            WriteInt(w, 28, SampleRate * 2);           // byte rate
            WriteShort(w, 32, 2);                      // block align
            WriteShort(w, 34, 16);                     // bits
            Write(w, 36, "data");
            WriteInt(w, 40, dataBytes);
        }

        private static void Write(byte[] w, int at, string ascii)
        {
            for (int i = 0; i < ascii.Length; i++) w[at + i] = (byte)ascii[i];
        }

        private static void WriteInt(byte[] w, int at, int v)
        {
            w[at] = (byte)v; w[at + 1] = (byte)(v >> 8); w[at + 2] = (byte)(v >> 16); w[at + 3] = (byte)(v >> 24);
        }

        private static void WriteShort(byte[] w, int at, int v)
        {
            w[at] = (byte)v; w[at + 1] = (byte)(v >> 8);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private void Command(params string[] args)
        {
            IntPtr[] ptrs = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++) ptrs[i] = Utf8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            GCHandle h = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
            try { mpv_command(ctx, h.AddrOfPinnedObject()); }
            finally
            {
                h.Free();
                foreach (IntPtr p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }

        private static IntPtr Utf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            Marshal.Copy(b, 0, p, b.Length);
            Marshal.WriteByte(p, b.Length, 0);
            return p;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (ctx != IntPtr.Zero) { try { mpv_terminate_destroy(ctx); } catch { } ctx = IntPtr.Zero; }
                unavailable = true;
                if (lastFile != null) { try { File.Delete(lastFile); } catch { } lastFile = null; }
            }
        }
    }
}
