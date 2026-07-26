using System;
using System.IO;

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
    /// is generated here as a small WAV and played through
    /// <see cref="SapiWavPlayer"/>, exactly like speech — one output for
    /// everything NBR makes.</para>
    ///
    /// <para>It also stops blocking: <c>Console.Beep</c> holds the calling thread
    /// for the whole tone, so the five-beep bookmark series froze the UI for a
    /// second. A series is rendered as ONE buffer and played in the background,
    /// which keeps its timing exact as well.</para>
    /// </summary>
    public class SignalTones : IDisposable
    {
        private const int SampleRate = 22050;
        private const int GapMs = 30;        // silence between tones in a series

        private SapiWavPlayer player;
        private bool playerFailed;
        private string deviceId = "";

        /// <summary>Which card the tones play on (mpv-style id, as in Settings →
        /// Device). Empty = system default.</summary>
        public void SetDevice(string mpvDeviceId)
        {
            deviceId = mpvDeviceId ?? "";
            if (player != null) player.SetDevice(deviceId);
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
            try
            {
                if (!playerFailed)
                {
                    if (player == null)
                    {
                        player = new SapiWavPlayer();
                        player.SetDevice(deviceId);
                    }
                    if (player.Play(Render(tones))) return;
                }
            }
            catch { playerFailed = true; }   // no SAPI here — fall through

            // Last resort: the system beep, on whatever device Windows uses.
            foreach (var t in tones)
            {
                try { Console.Beep(Clamp(t.Freq, 37, 32767), Math.Max(1, t.Ms)); } catch { }
            }
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

        public void Dispose()
        {
            try { if (player != null) player.Dispose(); } catch { }
            player = null;
        }
    }
}
