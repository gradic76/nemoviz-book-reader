using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Duration fallback for audio formats TagLib# has no reader for (verified:
    /// .caf and .oga fail in TagLib but play fine in mpv; the same applies to
    /// .ac3/.amr/.weba, and to .spx/.dff). mpv decodes everything the player can
    /// actually play, so it is the authoritative source when TagLib gives up —
    /// without it such a book imports with a 0:00 duration and a broken progress
    /// bar / virtual timeline.
    ///
    /// Uses its own silent (ao=null, vid=no, paused) libmpv context, created
    /// lazily on first need and reused; it never plays audio.
    /// </summary>
    internal static class MpvDuration
    {
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, string name, string data);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_get_property(IntPtr ctx, string name, int format, ref double data);

        private const int EventNone = 0, EventEndFile = 7, EventFileLoaded = 8, EventStartFile = 6;
        private const int FormatDouble = 5;

        private static readonly object gate = new object();
        private static IntPtr ctx = IntPtr.Zero;
        private static bool unavailable;

        /// <summary>Seconds, or 0 when the file can't be opened. Safe to call for
        /// any path; never throws.</summary>
        public static double TryGet(string path)
        {
            lock (gate)
            {
                if (unavailable || string.IsNullOrEmpty(path)) return 0;
                try
                {
                    if (ctx == IntPtr.Zero && !Create()) return 0;
                    return Probe(path);
                }
                catch { return 0; }
            }
        }

        private static bool Create()
        {
            ctx = mpv_create();
            if (ctx == IntPtr.Zero) { unavailable = true; return false; }
            mpv_set_property_string(ctx, "terminal", "no");
            if (mpv_initialize(ctx) < 0) { unavailable = true; return false; }
            mpv_set_property_string(ctx, "ao", "null");   // decode only, never audible
            mpv_set_property_string(ctx, "vid", "no");
            mpv_set_property_string(ctx, "pause", "yes");
            return true;
        }

        private static double Probe(string path)
        {
            Drain();
            Command("loadfile", path, "replace");

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            // Wait for this file to start, so a queued END_FILE from the previous
            // probe isn't mistaken for this one's result.
            bool started = false;
            while (DateTime.UtcNow < deadline)
                if (Marshal.ReadInt32(mpv_wait_event(ctx, 0.25)) == EventStartFile) { started = true; break; }

            double dur = 0;
            while (started && DateTime.UtcNow < deadline)
            {
                int id = Marshal.ReadInt32(mpv_wait_event(ctx, 0.25));
                if (id == EventFileLoaded)
                {
                    for (int i = 0; i < 15 && dur <= 0; i++)
                    {
                        System.Threading.Thread.Sleep(50);
                        mpv_get_property(ctx, "duration", FormatDouble, ref dur);
                    }
                    break;
                }
                if (id == EventEndFile) break;   // cannot decode
            }
            Command("stop");
            return dur > 0 ? dur : 0;
        }

        private static void Drain()
        {
            for (int i = 0; i < 200; i++)
                if (Marshal.ReadInt32(mpv_wait_event(ctx, 0)) == EventNone) break;
        }

        private static void Command(params string[] args)
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

        /// <summary>Releases the probe context (called on app shutdown).</summary>
        public static void Shutdown()
        {
            lock (gate)
            {
                if (ctx != IntPtr.Zero) { try { mpv_terminate_destroy(ctx); } catch { } ctx = IntPtr.Zero; }
                unavailable = true;
            }
        }
    }
}
