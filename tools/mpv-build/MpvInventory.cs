using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Asks a libmpv-2.dll what decoders it actually has, by walking the decoder-list
// property through the real C API. This is the ORACLE for the audio-only build:
// run it against the current DLL to record what must survive, run it against the
// new one, diff.
//
// Why not verify by playing files: there are samples for only 13 of NBR's 24
// formats on this machine. Asking the library needs no samples and covers
// everything, including codecs nobody here has a file for.
class MpvInventory
{
    const string L = "libmpv-2.dll";
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr h, byte[] n, byte[] v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_get_property(IntPtr h, byte[] n, int fmt, IntPtr data);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_free_node_contents(IntPtr node);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_error_string(int e);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern uint mpv_client_api_version();

    const int FORMAT_NODE = 6, FORMAT_NODE_ARRAY = 7, FORMAT_NODE_MAP = 8, FORMAT_STRING = 1;

    static byte[] B(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string outFile = args.Length > 0 ? args[0] : null;

        uint v = mpv_client_api_version();
        var sb = new StringBuilder();
        sb.AppendLine("client-api " + (v >> 16) + "." + (v & 0xFFFF));

        IntPtr h = mpv_create();
        mpv_set_option_string(h, B("ao"), B("null"));
        mpv_set_option_string(h, B("terminal"), B("no"));
        if (mpv_initialize(h) != 0) { Console.WriteLine("mpv_initialize failed"); return; }

        var audio = new List<string>();
        var other = new List<string>();
        try
        {
            IntPtr node = Marshal.AllocHGlobal(16);          // mpv_node: union(8) + format(4) + pad(4)
            try
            {
                int e = mpv_get_property(h, B("decoder-list"), FORMAT_NODE, node);
                if (e != 0)
                {
                    Console.WriteLine("decoder-list failed: " + Marshal.PtrToStringAnsi(mpv_error_string(e)));
                    return;
                }
                foreach (var d in ReadDecoders(node)) (d.IsAudio ? audio : other).Add(d.Line);
            }
            finally { mpv_free_node_contents(node); Marshal.FreeHGlobal(node); }
        }
        finally { mpv_terminate_destroy(h); }

        audio.Sort(StringComparer.Ordinal);
        other.Sort(StringComparer.Ordinal);

        sb.AppendLine("audio-decoders " + audio.Count);
        foreach (string s in audio) sb.AppendLine("A " + s);
        sb.AppendLine("other-decoders " + other.Count);
        foreach (string s in other) sb.AppendLine("V " + s);

        string text = sb.ToString();
        Console.WriteLine("keys on an entry: " + string.Join(", ", KeysSeen));
        Console.WriteLine("audio decoders : " + audio.Count);
        Console.WriteLine("other decoders : " + other.Count + "   (video/image — must be 0 after the cut)");
        if (outFile != null) { File.WriteAllText(outFile, text); Console.WriteLine("written        : " + outFile); }
        else Console.Write(text);
    }

    // Every key mpv actually puts on a decoder-list entry. Collected rather than
    // assumed: if one of them names the media type, the audio/video split becomes
    // mechanical and the hand-written codec list below can go.
    public static readonly List<string> KeysSeen = new List<string>();

    struct Dec { public bool IsAudio; public string Line; }

    // decoder-list is a NODE_ARRAY of NODE_MAPs: codec, driver, description.
    // mpv does not say audio or video, so the family is inferred from the codec
    // name against the map mpv itself uses for --ad/--vd; simpler and reliable
    // here: a decoder whose driver mpv can use for audio appears with a codec
    // name we cross-check below.
    static List<Dec> ReadDecoders(IntPtr node)
    {
        var result = new List<Dec>();
        int fmt = Marshal.ReadInt32(node, 8);
        if (fmt != FORMAT_NODE_ARRAY) return result;
        IntPtr list = Marshal.ReadIntPtr(node, 0);
        int num = Marshal.ReadInt32(list, 0);
        IntPtr values = Marshal.ReadIntPtr(list, 8);

        for (int i = 0; i < num; i++)
        {
            IntPtr item = values + i * 16;
            if (Marshal.ReadInt32(item, 8) != FORMAT_NODE_MAP) continue;
            IntPtr map = Marshal.ReadIntPtr(item, 0);
            int n = Marshal.ReadInt32(map, 0);
            IntPtr mv = Marshal.ReadIntPtr(map, 8);
            IntPtr mk = Marshal.ReadIntPtr(map, 16);

            string codec = "", driver = "", desc = "";
            for (int k = 0; k < n; k++)
            {
                string key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(mk, k * IntPtr.Size));
                if (!KeysSeen.Contains(key)) KeysSeen.Add(key);
                IntPtr val = mv + k * 16;
                if (Marshal.ReadInt32(val, 8) != FORMAT_STRING) continue;
                string s = Utf8(Marshal.ReadIntPtr(val, 0));
                if (key == "codec") codec = s;
                else if (key == "driver") driver = s;
                else if (key == "description") desc = s;
            }
            result.Add(new Dec { IsAudio = IsAudioCodec(codec), Line = codec + " | " + driver + " | " + desc });
        }
        return result;
    }

    static string Utf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0; while (Marshal.ReadByte(p, len) != 0) len++;
        byte[] b = new byte[len]; Marshal.Copy(p, b, 0, len);
        return Encoding.UTF8.GetString(b);
    }

    // Audio codecs, taken from FFmpeg's own naming rather than from NBR's list of
    // 24 extensions — the whole point of the cut is that nothing was hand-picked.
    static readonly string[] AudioCodecs = {
        "aac","aac_latm","ac3","eac3","mp1","mp2","mp3","mp3adu","mp3on4","mp4als",
        "flac","alac","vorbis","opus","speex","wmav1","wmav2","wmalossless","wmapro","wmavoice",
        "ape","tta","wavpack","tak","shorten","mlp","truehd","dts","dca","atrac1","atrac3",
        "atrac3p","atrac3al","atrac3pal","atrac9","cook","ra_144","ra_288","ralf","sipr",
        "qdm2","qdmc","qcelp","amrnb","amrwb","gsm","gsm_ms","g723_1","g729","ilbc","evrc",
        "nellymoser","musepack7","musepack8","dsd_lsbf","dsd_msbf","dsd_lsbf_planar","dsd_msbf_planar",
        "pcm_s16le","pcm_s16be","pcm_u8","pcm_s8","pcm_s24le","pcm_s24be","pcm_s32le","pcm_s32be",
        "pcm_f32le","pcm_f32be","pcm_f64le","pcm_f64be","pcm_alaw","pcm_mulaw","pcm_vidc",
        "adpcm_ima_wav","adpcm_ms","adpcm_swf","adpcm_g726","adpcm_g722","adpcm_yamaha",
        "sonic","binkaudio_dct","binkaudio_rdft","imc","iac","mace3","mace6","paf_audio",
        "smackaudio","truespeech","twinvq","vmdaudio","ws_snd1","xan_dpcm","dolby_e","s302m",
        "comfortnoise","interplayacm","metasound","on2avc","opus_multistream","hcom","dst",
        "apac","ftr","wavarc","rka","osq","msnsiren","bonk","misc4","dfpwm","sga","mpegh_3d_audio"
    };

    static bool IsAudioCodec(string codec)
    {
        if (string.IsNullOrEmpty(codec)) return false;
        foreach (string a in AudioCodecs) if (a == codec) return true;
        // Anything that decodes into PCM by naming convention.
        return codec.StartsWith("pcm_") || codec.StartsWith("adpcm_") || codec.StartsWith("dsd_");
    }
}
