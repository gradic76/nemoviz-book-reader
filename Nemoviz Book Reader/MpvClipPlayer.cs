using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Plays one rendered utterance through libmpv, with the same surface
    /// <see cref="SapiWavPlayer"/> offers, so a speech backend changes players by
    /// changing one field.
    ///
    /// <para><b>Why not keep SAPI's player.</b> Because of the SPEECH CACHE.
    /// Audio kept on disk must not have a speed baked into it: were it baked in,
    /// nudging the speed once would strand a whole prepared book — not deleted,
    /// just never matched again — and the next reading would be paid for a second
    /// time. So the cache stores each sentence at the voice's own natural speed
    /// and the speeding up has to happen at PLAYBACK. <c>SpVoice.SpeakStream</c>
    /// cannot do that. mpv can, through the very <c>scaletempo2</c> that has been
    /// speeding up audiobooks in this player all along, and it keeps the pitch
    /// where it belongs.</para>
    ///
    /// <para><b>Volume leaves the key for the same reason and at no cost</b> —
    /// mpv has a volume property, so it need not be printed into the audio the
    /// way Google's gain-in-decibels was.</para>
    ///
    /// <para><b>It plays a FILE, never a buffer</b>, and that suits the cache
    /// rather than fighting it: a cached sentence already IS a file, so the
    /// common case copies nothing. Only a sentence that has just been made and is
    /// not being kept goes through a scratch file, reused and overwritten, and
    /// removed on <see cref="Dispose"/>.</para>
    ///
    /// <para>Its own context, like <see cref="MpvDuration"/>: the player's own mpv
    /// is busy being the transport for audio books, and a text book's speech has
    /// nothing to do with it.</para>
    /// </summary>
    internal sealed class MpvClipPlayer : IDisposable
    {
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, string name, string data);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        private const int EventNone = 0, EventStartFile = 6, EventEndFile = 7;

        private readonly object gate = new object();
        private IntPtr ctx = IntPtr.Zero;
        private bool dead;
        private bool playing;
        private string scratch;

        private string device = "";
        private double speed = 1.0;
        private int volume = 100;

        // ── Setup ─────────────────────────────────────────────────────────────

        private static bool swept;

        /// <summary>Removes scratch files an earlier run left behind.
        ///
        /// <para><b>Disposing properly is not enough and cannot be.</b> A process
        /// that is killed, or that faults, never reaches its tidying — so the only
        /// reliable moment to clear these is the next start. Found by looking:
        /// four were sitting in the temp folder while this was being
        /// built.</para>
        ///
        /// <para>An hour old, so a second copy of the player running right now
        /// keeps its own.</para></summary>
        private static void SweepOldScratch()
        {
            if (swept) return;
            swept = true;
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (string f in Directory.GetFiles(Path.GetTempPath(), "nbr-speech-*.bin"))
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
            catch { }
        }

        private bool Ready()
        {
            if (dead) return false;
            if (ctx != IntPtr.Zero) return true;
            SweepOldScratch();

            ctx = mpv_create();
            if (ctx == IntPtr.Zero) { dead = true; return false; }
            mpv_set_property_string(ctx, "terminal", "no");
            if (mpv_initialize(ctx) < 0) { dead = true; return false; }

            mpv_set_property_string(ctx, "vid", "no");
            mpv_set_property_string(ctx, "audio-display", "no");
            // Without this the file sits at its end instead of reporting one, and
            // a sentence would never be seen to finish.
            mpv_set_property_string(ctx, "keep-open", "no");
            // The whole reason this class exists: change the speed, keep the
            // pitch. It is mpv's default, set here so it cannot be inherited
            // from a config file that says otherwise.
            mpv_set_property_string(ctx, "audio-pitch-correction", "yes");
            ApplyAll();
            return true;
        }

        private void ApplyAll()
        {
            if (ctx == IntPtr.Zero) return;
            mpv_set_property_string(ctx, "audio-device",
                string.IsNullOrEmpty(device) ? "auto" : device);
            mpv_set_property_string(ctx, "speed",
                speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            mpv_set_property_string(ctx, "volume", volume.ToString());
        }

        /// <summary>The sound card, as the mpv id the rest of the player already
        /// uses. Empty means the system default.</summary>
        public void SetDevice(string mpvDeviceId)
        {
            lock (gate)
            {
                device = mpvDeviceId ?? "";
                if (ctx != IntPtr.Zero)
                    mpv_set_property_string(ctx, "audio-device",
                        device.Length == 0 ? "auto" : device);
            }
        }

        /// <summary>A multiplier, 1.0 being the voice's own pace. Takes effect on
        /// what is playing NOW as well as on what comes next — which is the
        /// difference from a rendered-in speed, and the point.</summary>
        public void SetSpeed(double multiplier)
        {
            lock (gate)
            {
                if (multiplier < 0.25) multiplier = 0.25;
                if (multiplier > 4.0) multiplier = 4.0;
                speed = multiplier;
                if (ctx != IntPtr.Zero)
                    mpv_set_property_string(ctx, "speed",
                        speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public void SetVolume(int percent)
        {
            lock (gate)
            {
                volume = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
                if (ctx != IntPtr.Zero) mpv_set_property_string(ctx, "volume", volume.ToString());
            }
        }

        // ── Playing ───────────────────────────────────────────────────────────

        /// <summary>Plays a file already on disk — the cache's own case, where
        /// nothing is copied.</summary>
        public bool PlayFile(string path)
        {
            lock (gate)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                if (!Ready()) return false;
                Drain();
                Command("loadfile", path, "replace");
                playing = true;
                return true;
            }
        }

        /// <summary>Plays audio that exists only in memory, through a scratch file
        /// that is reused rather than piling up.</summary>
        public bool Play(byte[] audio)
        {
            lock (gate)
            {
                if (audio == null || audio.Length == 0) return false;
                try
                {
                    if (scratch == null)
                        scratch = Path.Combine(Path.GetTempPath(),
                            "nbr-speech-" + Guid.NewGuid().ToString("N") + ".bin");
                    File.WriteAllBytes(scratch, audio);
                }
                catch { return false; }
            }
            return PlayFile(scratch);
        }

        /// <summary>Still going? Asked from the backend's timer, and this is where
        /// mpv's events are collected — so the answer and the end-of-file arrive
        /// on the same thread that started the utterance.</summary>
        public bool IsPlaying
        {
            get
            {
                lock (gate)
                {
                    if (!playing || ctx == IntPtr.Zero) return false;
                    for (int i = 0; i < 64; i++)
                    {
                        int id = Marshal.ReadInt32(mpv_wait_event(ctx, 0));
                        if (id == EventNone) break;
                        if (id == EventEndFile) { playing = false; return false; }
                    }
                    return playing;
                }
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (ctx == IntPtr.Zero) { playing = false; return; }
                Command("stop");
                playing = false;
                Drain();
            }
        }

        /// <summary>Nothing to release — the context is kept for the next
        /// sentence, since building one per utterance would cost more than it
        /// saves. Present so the two players answer the same calls.</summary>
        public void ReleaseFinished() { }

        // ── Plumbing ──────────────────────────────────────────────────────────

        private void Drain()
        {
            for (int i = 0; i < 200; i++)
                if (Marshal.ReadInt32(mpv_wait_event(ctx, 0)) == EventNone) break;
        }

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
                try { if (ctx != IntPtr.Zero) mpv_terminate_destroy(ctx); } catch { }
                ctx = IntPtr.Zero;
                playing = false;
                try { if (scratch != null && File.Exists(scratch)) File.Delete(scratch); } catch { }
                scratch = null;
            }
        }
    }
}
