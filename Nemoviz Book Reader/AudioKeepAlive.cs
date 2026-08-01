using System;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>Holds the chosen sound card awake, so it cannot power down
    /// between sentences and swallow the start of the next one.
    ///
    /// <para><b>Why this exists (2026-08-01, §10f).</b> Gordan lost the first
    /// word of sentence after sentence, and every measurement said the software
    /// was correct: the utterance was handed to SAPI, SAPI reported speaking,
    /// and it played for its full length. The loss was past the last point
    /// software can see. His HDMI output powers down almost the instant the
    /// signal stops, and it had been kept awake all along by NVDA and JAWS,
    /// which have settings for exactly this and were on the same card. Moving
    /// the screen readers to another card removed that protection and the
    /// endpoint began sleeping in the gaps.</para>
    ///
    /// <para><b>A player must not depend on a screen reader for this.</b> A
    /// sighted reader has none, and a braille reader may well put speech on
    /// another card — which is precisely the arrangement that exposed it. So NBR
    /// keeps its own device awake.</para>
    ///
    /// <para><b>Not digital silence.</b> A run of zeroes is, to a device deciding
    /// whether anything is happening, indistinguishable from nothing at all, and
    /// several endpoints sleep straight through it. This plays a dither of one
    /// or two least-significant bits — around 90 dB below full scale, inaudible
    /// on any system a person would listen to a book on, but a signal.</para>
    ///
    /// <para>It rides on the same <see cref="SapiWavPlayer"/> the reading uses,
    /// so it follows the chosen device by the same route and needs no second way
    /// of naming a sound card.</para></summary>
    internal sealed class AudioKeepAlive : IDisposable
    {
        private const int Rate = 22050;          // enough to be a signal; tiny
        private const int Seconds = 20;          // one buffer, replayed
        private SapiWavPlayer player;
        private System.Windows.Forms.Timer tick;
        private byte[] buffer;
        private string deviceId;
        private bool running;

        public void SetDevice(string id)
        {
            deviceId = id;
            if (player != null) player.SetDevice(id);
        }

        public void Start()
        {
            if (running) return;
            running = true;
            try
            {
                if (player == null)
                {
                    player = new SapiWavPlayer();
                    player.SetDevice(deviceId);
                }
                if (buffer == null) buffer = BuildDither();
                // A little short of the buffer's own length, so the next one is
                // already going before the last has drained — a gap here is the
                // very thing being prevented.
                if (tick == null)
                {
                    tick = new System.Windows.Forms.Timer();
                    tick.Interval = (Seconds - 2) * 1000;
                    tick.Tick += (s, e) => Pulse();
                }
                tick.Start();
                Pulse();
            }
            catch { running = false; }
        }

        public void Stop()
        {
            running = false;
            try { if (tick != null) tick.Stop(); } catch { }
            try { if (player != null) player.Stop(); } catch { }
        }

        private void Pulse()
        {
            if (!running || player == null || buffer == null) return;
            try { player.Play(buffer); } catch { }
        }

        /// <summary>Twenty seconds of 16-bit mono dither: samples alternating
        /// between -1 and +1. Two hundred kilobytes, built once.</summary>
        private static byte[] BuildDither()
        {
            int samples = Rate * Seconds;
            int dataLen = samples * 2;
            var w = new byte[44 + dataLen];
            using (var ms = new MemoryStream(w))
            using (var b = new BinaryWriter(ms))
            {
                b.Write(new char[] { 'R', 'I', 'F', 'F' });
                b.Write(36 + dataLen);
                b.Write(new char[] { 'W', 'A', 'V', 'E' });
                b.Write(new char[] { 'f', 'm', 't', ' ' });
                b.Write(16);                 // PCM header size
                b.Write((short)1);           // PCM
                b.Write((short)1);           // mono
                b.Write(Rate);
                b.Write(Rate * 2);           // byte rate
                b.Write((short)2);           // block align
                b.Write((short)16);          // bits
                b.Write(new char[] { 'd', 'a', 't', 'a' });
                b.Write(dataLen);
                for (int i = 0; i < samples; i++) b.Write((short)(i % 2 == 0 ? 1 : -1));
            }
            return w;
        }

        public void Dispose()
        {
            Stop();
            try { if (tick != null) { tick.Dispose(); tick = null; } } catch { }
            try { if (player != null) { player.Dispose(); player = null; } } catch { }
        }
    }
}
