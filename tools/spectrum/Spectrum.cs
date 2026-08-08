using System;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>A third-octave profile of a recording, measured through the libmpv
/// NBR already ships. No extra library, no licence question, no download.
///
/// <para><b>Why this exists.</b> Gordan asked whether a small free analyser
/// could be downloaded to look at a recording "from different angles". The
/// honest answer was that we already ship one: the audio-only libmpv carries
/// ffmpeg's whole audio filter set, and its values come back as an mpv property
/// (see SoundAnalysis.cs for why the property and not the log). Everything the
/// candidates offered that mattered is reachable from it — and the small
/// candidates were GPL or AGPL, which §10e's LGPL build exists precisely to
/// avoid.</para>
///
/// <para><b>What it is for.</b> Deciding where EQ bands belong from measurement
/// rather than from convention. The 10 kHz treble shelf NBR shipped was a hi-fi
/// convention, and on damaged speech it was lifting a region 40 dB below the
/// signal — audible to nobody. A profile like this is what says so.</para>
///
/// <para>Build with csc /platform:x64, and run from the folder holding
/// libmpv-2.dll.</para></summary>
static class Spectrum
{
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr c);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr c);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_property_string(IntPtr c, string n, string v);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr c, IntPtr a);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr c, double t);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_get_property_string(IntPtr c, string n);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mpv_free(IntPtr p);

    const string Key = "\"lavfi.astats.Overall.RMS_level\":\"";

    static IntPtr Utf8(string s)
    {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
        IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
        Marshal.Copy(b, 0, p, b.Length);
        Marshal.WriteByte(p, b.Length, 0);
        return p;
    }

    /// <summary>RMS through one filter, dB, or NaN. The metadata is polled WHILE
    /// the segment plays — at end of file the graph is gone and the property is
    /// empty.</summary>
    static double Rms(string file, double start, double seconds, string filter)
    {
        IntPtr c = mpv_create();
        try
        {
            mpv_set_property_string(c, "terminal", "no");
            mpv_set_property_string(c, "ao", "null");
            mpv_set_property_string(c, "vid", "no");
            if (mpv_initialize(c) < 0) return double.NaN;
            mpv_set_property_string(c, "untimed", "yes");
            mpv_set_property_string(c, "speed", "100");
            mpv_set_property_string(c, "audio-pitch-correction", "no");
            mpv_set_property_string(c, "start", start.ToString("0.###", CultureInfo.InvariantCulture));
            mpv_set_property_string(c, "length", seconds.ToString("0.###", CultureInfo.InvariantCulture));
            string g = "@st:lavfi=[" + (filter.Length > 0 ? filter + "," : "")
                     + "astats=metadata=1:reset=0:measure_perchannel=none]";
            if (mpv_set_property_string(c, "af", g) < 0) return double.NaN;

            IntPtr[] p = { Utf8("loadfile"), Utf8(file), Utf8("replace"), IntPtr.Zero };
            GCHandle h = GCHandle.Alloc(p, GCHandleType.Pinned);
            mpv_command(c, h.AddrOfPinnedObject());
            h.Free();
            foreach (IntPtr q in p) if (q != IntPtr.Zero) Marshal.FreeHGlobal(q);

            double best = double.NaN;
            DateTime end = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < end)
            {
                IntPtr ev = mpv_wait_event(c, 0.02);
                IntPtr s = mpv_get_property_string(c, "af-metadata/st");
                if (s != IntPtr.Zero)
                {
                    int len = 0;
                    while (Marshal.ReadByte(s, len) != 0) len++;
                    byte[] bb = new byte[len];
                    Marshal.Copy(s, bb, 0, len);
                    mpv_free(s);
                    string j = System.Text.Encoding.UTF8.GetString(bb);
                    int i = j.IndexOf(Key, StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        string v = j.Substring(i + Key.Length);
                        v = v.Substring(0, v.IndexOf('"'));
                        double d;
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) best = d;
                    }
                }
                if (Marshal.ReadInt32(ev) == 7) break;
            }
            return best;
        }
        finally { try { mpv_terminate_destroy(c); } catch { } }
    }

    // ISO third-octave centres. It stops at 12.5 kHz because a 22.05 kHz file —
    // half of this project's own samples — cannot carry anything above 11.
    static readonly int[] Centres =
    { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250,
      1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500 };

    static void Main(string[] argv)
    {
        if (argv.Length < 1)
        {
            Console.WriteLine("usage: Spectrum <file-or-folder> [startSeconds] [segmentSeconds]");
            return;
        }
        double start = argv.Length > 1 ? double.Parse(argv[1], CultureInfo.InvariantCulture) : 200;
        double secs = argv.Length > 2 ? double.Parse(argv[2], CultureInfo.InvariantCulture) : 20;

        string[] files = System.IO.Directory.Exists(argv[0])
            ? System.IO.Directory.GetFiles(argv[0])
            : new[] { argv[0] };
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        Console.Write("Hz".PadLeft(7));
        foreach (string f in files)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(f);
            Console.Write((n.Length > 9 ? n.Substring(0, 9) : n).PadLeft(10));
        }
        Console.WriteLine();

        var full = new double[files.Length];
        for (int i = 0; i < files.Length; i++) full[i] = Rms(files[i], start, secs, "");

        Console.Write("full".PadLeft(7));
        for (int i = 0; i < files.Length; i++) Console.Write(full[i].ToString("N1").PadLeft(10));
        Console.WriteLine();

        foreach (int hz in Centres)
        {
            Console.Write(hz.ToString(CultureInfo.InvariantCulture).PadLeft(7));
            for (int i = 0; i < files.Length; i++)
            {
                // One third of an octave, by the filter's own width_type rather
                // than by arithmetic on a pair of cutoffs.
                double v = Rms(files[i], start, secs,
                    "bandpass=f=" + hz.ToString(CultureInfo.InvariantCulture) + ":width_type=o:w=0.3333");
                Console.Write((double.IsNaN(v) || double.IsNaN(full[i])
                               ? "--" : (v - full[i]).ToString("N1")).PadLeft(10));
            }
            Console.WriteLine();
        }
        Console.WriteLine();
        Console.WriteLine("Each band in dB relative to that file's own whole-signal RMS, so the");
        Console.WriteLine("columns compare recordings of different loudness directly.");
    }
}
