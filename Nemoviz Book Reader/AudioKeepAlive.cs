using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nemoviz_Book_Reader
{
    /// <summary>Holds the card the player is using awake, so it cannot power
    /// down between sentences and swallow the start of the next one.
    ///
    /// <para><b>Why this exists (2026-08-01, §10f).</b> Gordan lost the first
    /// word of sentence after sentence, and every measurement said the software
    /// was correct. His HDMI output powers down almost the instant the signal
    /// stops, and it had been kept awake all along by NVDA and JAWS, which were
    /// on the same card. Moving the screen readers to another card removed that
    /// protection and the endpoint began sleeping in the gaps. A player must not
    /// depend on a screen reader for this.</para>
    ///
    /// <para><b>It goes to the card the player plays on, always.</b> That is the
    /// requirement (Gordan) and it is what killed the two previous attempts.
    /// The first rode on <see cref="SapiWavPlayer"/>: two SAPI voices pointed at
    /// one output token queue behind each other instead of mixing, so it was
    /// heard inserting itself BETWEEN sentences. The second opened its own
    /// waveOut stream, which mixes properly — but waveOut knows cards by a
    /// product name truncated to 31 characters, and matching that against mpv's
    /// id is guesswork that falls back to the default device. On a machine with
    /// one card that is right by luck; on Gordan's, with several, it can hold
    /// the wrong one open and let the right one sleep, which is worse than doing
    /// nothing because it looks like it is working.</para>
    ///
    /// <para>So it runs on <b>mpv</b>, in a context of its own, with
    /// <c>audio-device</c> set to the very string the player is using. No
    /// matching, no fallback, no name: the same id, so it cannot land anywhere
    /// else. libmpv is already loaded, and a second context playing one second
    /// of tone on a loop costs nothing worth counting.</para>
    ///
    /// <para><b>Not digital silence.</b> A run of zeroes is, to a device deciding
    /// whether anything is happening, indistinguishable from nothing at all.
    /// This is a 40 Hz sine at one part in 4 000 — around −72 dBFS, real signal
    /// on the wire and under the floor of anything a person hears a book
    /// through.</para></summary>
    internal sealed class AudioKeepAlive : IDisposable
    {
        private const string L = "libmpv-2.dll";
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);

        private const int Rate = 8000;

        private IntPtr ctx;
        private string deviceId = "auto";
        private string tonePath;
        private bool running;

        /// <summary>UTF-8 with the terminator mpv's C API expects. NOT
        /// <c>StringToHGlobalAnsi</c>: that mangled Č and Đ once already and cost
        /// a whole probe run to find (§10e).</summary>
        private static byte[] Z(string s)
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            var z = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw, 0, z, 0, raw.Length);
            return z;
        }

        /// <summary>The card the player is on, as mpv names it — exactly the
        /// string handed to mpv's own <c>audio-device</c>. Applied live, so a
        /// change of card in Settings takes the keep-alive with it rather than
        /// leaving it holding the one nobody is listening to.</summary>
        public void SetDevice(string mpvDeviceId)
        {
            string id = string.IsNullOrEmpty(mpvDeviceId) ? "auto" : mpvDeviceId;
            if (id == deviceId) return;
            deviceId = id;
            if (ctx != IntPtr.Zero)
                try { mpv_set_property_string(ctx, Z("audio-device"), Z(deviceId)); } catch { }
        }

        public void Start()
        {
            if (running) return;
            try
            {
                if (tonePath == null) tonePath = WriteTone();
                if (tonePath == null) return;

                ctx = mpv_create();
                if (ctx == IntPtr.Zero) return;

                mpv_set_option_string(ctx, Z("audio-device"), Z(deviceId));
                mpv_set_option_string(ctx, Z("vid"), Z("no"));          // audio only, §10e
                mpv_set_option_string(ctx, Z("video"), Z("no"));
                mpv_set_option_string(ctx, Z("loop-file"), Z("inf"));   // never ends
                mpv_set_option_string(ctx, Z("keep-open"), Z("yes"));
                mpv_set_option_string(ctx, Z("terminal"), Z("no"));
                // Left at full volume on purpose: the tone is already −72 dBFS,
                // and turning it down further risks a device deciding the signal
                // is not worth staying awake for.
                if (mpv_initialize(ctx) < 0) { Cleanup(); return; }

                Command("loadfile", tonePath);
                running = true;
            }
            catch { Cleanup(); }
        }

        public void Stop()
        {
            running = false;
            Cleanup();
        }

        private void Cleanup()
        {
            try { if (ctx != IntPtr.Zero) mpv_terminate_destroy(ctx); } catch { }
            ctx = IntPtr.Zero;
        }

        private void Command(params string[] args)
        {
            IntPtr[] p = new IntPtr[args.Length + 1];
            var pinned = new System.Collections.Generic.List<IntPtr>();
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    byte[] raw = Z(args[i]);
                    IntPtr u = Marshal.AllocHGlobal(raw.Length);
                    Marshal.Copy(raw, 0, u, raw.Length);
                    p[i] = u;
                    pinned.Add(u);
                }
                p[args.Length] = IntPtr.Zero;
                var h = GCHandle.Alloc(p, GCHandleType.Pinned);
                try { mpv_command(ctx, h.AddrOfPinnedObject()); }
                finally { h.Free(); }
            }
            catch { }
            finally { foreach (IntPtr q in pinned) try { Marshal.FreeHGlobal(q); } catch { } }
        }

        /// <summary>One second of 40 Hz, written once to a temp file. A whole
        /// number of cycles, so mpv's loop joins from zero to zero without a
        /// click.</summary>
        private static string WriteTone()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "nbr-keepalive.wav");
                int samples = Rate;
                int dataLen = samples * 2;
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var b = new BinaryWriter(fs))
                {
                    b.Write(new char[] { 'R', 'I', 'F', 'F' });
                    b.Write(36 + dataLen);
                    b.Write(new char[] { 'W', 'A', 'V', 'E' });
                    b.Write(new char[] { 'f', 'm', 't', ' ' });
                    b.Write(16);
                    b.Write((short)1);            // PCM
                    b.Write((short)1);            // mono
                    b.Write(Rate);
                    b.Write(Rate * 2);
                    b.Write((short)2);
                    b.Write((short)16);
                    b.Write(new char[] { 'd', 'a', 't', 'a' });
                    b.Write(dataLen);
                    for (int i = 0; i < samples; i++)
                        b.Write((short)(8.0 * Math.Sin(2.0 * Math.PI * 40.0 * i / Rate)));
                }
                return path;
            }
            catch { return null; }
        }

        public void Dispose() { Stop(); }
    }
}
