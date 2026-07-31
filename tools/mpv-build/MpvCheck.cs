using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Does the LGPL libmpv actually do NBR's job? Grepping the DLL for filter names
// says a string is present; it does not say mpv will accept the filter graph.
// This drives the real C API: build a context, hand it the whole section 8d
// chain, load a file and decode it with no audio output, and report what mpv
// itself says. It P/Invokes libmpv directly rather than going through NBR, so
// nothing about the app's own wiring can mask a failure in the library.
class MpvCheck
{
    const string L = "libmpv-2.dll";
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr h, byte[] n, byte[] v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr h, IntPtr strings);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_get_property(IntPtr h, byte[] n, int fmt, out double v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr h, double timeout);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_error_string(int err);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern uint mpv_client_api_version();

    static byte[] B(string s) { return System.Text.Encoding.UTF8.GetBytes(s + "\0"); }
    static string Err(int e) { return e == 0 ? "ok" : Marshal.PtrToStringAnsi(mpv_error_string(e)); }

    // The exact chain SoundSettings.BuildAf produces, every stage on at once —
    // the worst case, so no filter can hide behind being switched off.
    const string Chain =
        "lavfi=[highpass=f=80,afftdn=nr=12:nf=-40,deesser=i=0.4,"
      + "acompressor=threshold=0.0316:ratio=3:attack=20:release=250:makeup=1.99,"
      + "bass=g=2,equalizer=f=2500:t=q:w=1.5:g=2,treble=g=2,"
      + "speechnorm=e=6.25:r=0.0005:l=1,alimiter=limit=0.988]";

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        uint v = mpv_client_api_version();
        Console.WriteLine("client API   : " + (v >> 16) + "." + (v & 0xFFFF));

        string[] files = args.Length > 0 && Directory.Exists(args[0])
            ? Directory.GetFiles(args[0]).Where(IsAudio).OrderBy(x => x).ToArray()
            : args;
        Console.WriteLine("files        : " + files.Length);
        Console.WriteLine();
        Console.WriteLine("{0,-34} {1,10} {2,10}  {3}", "file", "duration", "decoded", "filter chain");

        int okDur = 0, okDec = 0, okAf = 0;
        foreach (string f in files)
        {
            double dur = 0, decoded = 0;
            string af = "-";
            IntPtr h = mpv_create();
            if (h == IntPtr.Zero) { Console.WriteLine("mpv_create failed"); return; }
            try
            {
                mpv_set_option_string(h, B("ao"), B("null"));       // decode, make no sound
                mpv_set_option_string(h, B("vid"), B("no"));
                mpv_set_option_string(h, B("audio-display"), B("no"));
                mpv_set_option_string(h, B("terminal"), B("no"));
                int e = mpv_set_option_string(h, B("af"), B(Chain));
                af = Err(e);
                if (mpv_initialize(h) != 0) { Console.WriteLine("init failed"); continue; }

                Command(h, "loadfile", f);
                // Let it demux, build the filter graph and decode a little.
                DateTime until = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < until)
                {
                    IntPtr ev = mpv_wait_event(h, 0.2);
                    int id = Marshal.ReadInt32(ev);
                    if (id == 7) break;                            // MPV_EVENT_END_FILE
                    mpv_get_property(h, B("duration"), 5, out dur); // MPV_FORMAT_DOUBLE
                    mpv_get_property(h, B("audio-pts"), 5, out decoded);
                    if (decoded > 1.0) break;                      // it is really decoding
                }
            }
            finally { mpv_terminate_destroy(h); }

            if (dur > 0) okDur++;
            if (decoded > 0) okDec++;
            if (af == "ok") okAf++;
            Console.WriteLine("{0,-34} {1,10} {2,10}  {3}",
                Path.GetFileName(f).Substring(0, Math.Min(33, Path.GetFileName(f).Length)),
                dur > 0 ? dur.ToString("0.0") + " s" : "FAIL",
                decoded > 0 ? decoded.ToString("0.00") + " s" : "FAIL", af);
        }
        Console.WriteLine();
        Console.WriteLine("duration read: " + okDur + "/" + files.Length
                        + "   decoded: " + okDec + "/" + files.Length
                        + "   filter chain accepted: " + okAf + "/" + files.Length);
    }

    static bool IsAudio(string f)
    {
        string e = Path.GetExtension(f).ToLowerInvariant();
        return new[] { ".mp3",".ogg",".flac",".m4a",".m4b",".wav",".opus",".aac",".wma",".ape",
                       ".mka",".spx",".oga",".dsf",".dff",".caf",".aiff",".aif",".ac3",".amr",
                       ".weba",".webm",".au",".voc" }.Contains(e);
    }

    // mpv takes UTF-8, always. StringToHGlobalAnsi converts to the system code
    // page, which mangled every path with a Č or a Đ in it — and the failures
    // looked exactly like unsupported formats until the control run showed the
    // OLD dll failing on the same three files and no others.
    static IntPtr Utf8(string s)
    {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        IntPtr p = Marshal.AllocHGlobal(b.Length);
        Marshal.Copy(b, 0, p, b.Length);
        return p;
    }

    static void Command(IntPtr h, params string[] args)
    {
        IntPtr[] p = new IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++) p[i] = Utf8(args[i]);
        p[args.Length] = IntPtr.Zero;
        GCHandle g = GCHandle.Alloc(p, GCHandleType.Pinned);
        try { mpv_command(h, g.AddrOfPinnedObject()); }
        finally
        {
            g.Free();
            for (int i = 0; i < args.Length; i++) Marshal.FreeHGlobal(p[i]);
        }
    }
}
