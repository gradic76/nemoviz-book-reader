// TtsHost32 — a tiny 32-bit console host for SAPI5 speech, so the x64 player can
// use 32-bit-only voices (eSpeak and friends) out-of-process. It talks to the
// parent over stdin/stdout with a simple line protocol.
//
// This file is NOT part of the main (x64) project build — it is compiled
// separately to TtsHost32.exe (x86) into the app output, together with
// SapiWavPlayer.cs; see the build notes / the BuildTtsHost32 target.
//
// It drives SAPI through the COM automation object (SAPI.SpVoice, late-bound),
// NOT System.Speech, for three reasons the old System.Speech host could not
// solve:
//   * System.Speech gives no way to choose the OUTPUT DEVICE, so 32-bit voices
//     were stuck on the system default while the rest of the app followed
//     Settings → Device. SpVoice exposes AudioOutput, the same token the x64
//     backend maps the mpv device id onto.
//   * System.Speech could not render eSpeak to a stream (it threw), which forced
//     a real-time path for it — the one that crackles. SpVoice renders every
//     voice here to a wave file, so all of them get the buffered, gapless path.
//   * System.Speech reports a voice by a different name than SAPI's own token
//     ("Microsoft Zira Desktop" vs its description), which made the composite
//     backend see one voice as two. Both sides now read the token's Name.
//
// Audio is rendered to a temp WAV first and then played as one stream (see
// SapiWavPlayer): rendering straight to the card made eSpeak crackle, because
// its driver emits audio in per-word chunks and restarts the stream at every
// word boundary. Completion is reported by this host, from playback, not from
// the driver's own "cancelled" flag — some engines (eSpeak again) call a natural
// end a cancellation.
//
// Protocol (tab-separated, one command/event per line):
//   parent -> host : VOICE<TAB>name | RATE<TAB>n | VOL<TAB>n | PITCH<TAB>n
//                    | DEVICE<TAB>mpv-device-id
//                    | SPEAK<TAB>base64utf8 | PRERENDER<TAB>base64utf8
//                    | PAUSE | RESUME | CANCEL | QUIT
//   host -> parent : VOICE<TAB>name<TAB>vendor<TAB>language  (per voice)
//                    READY             (startup enumeration done)
//                    DONE<TAB>natural | DONE<TAB>cancelled  (utterance ended)
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Nemoviz_Book_Reader;

class TtsHost32
{
    private static dynamic synth;                 // SAPI.SpVoice — synthesis only
    private static SapiWavPlayer player;          // playback (device + instant stop)
    private static int pitchPercent;
    private static readonly object outLock = new object();
    private static readonly object synthLock = new object();   // SpVoice is not re-entrant
    private static readonly object playLock = new object();

    // Bumped by every new utterance and by CANCEL; a worker whose generation is no
    // longer current reports its utterance as cancelled.
    private static int generation;

    // Look-ahead: the parent sends the NEXT sentence while the current one plays,
    // so its audio is already rendered when the time comes and the gap between
    // sentences disappears. Only one sentence is held — all the reader needs.
    private static string aheadText;
    private static byte[] aheadWav;
    private static readonly ManualResetEvent aheadReady = new ManualResetEvent(false);
    private static readonly object aheadLock = new object();

    static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.InputEncoding = Encoding.UTF8; } catch { }

        try
        {
            Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
            synth = Activator.CreateInstance(t);
            player = new SapiWavPlayer();
            SapiWavPlayer.Log = HostLog;      // TEMPORARY, see HostLog
            HostLog("---- host started ----");
        }
        catch { Emit("READY"); return 1; }

        // Announce the voices this (32-bit) host can see, then READY. A voice is
        // named by its token Name — the same name the in-process backend reports,
        // so the two lists merge instead of duplicating.
        try
        {
            dynamic toks = synth.GetVoices();
            int n = toks.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic tok = toks.Item(i);
                string name = VoiceName(tok);
                if (string.IsNullOrEmpty(name)) continue;
                Emit("VOICE\t" + name + "\t" + Attr(tok, "Vendor") + "\t" + CultureOfLcid(Attr(tok, "Language")));
            }
        }
        catch { }
        Emit("READY");

        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            try { if (!Handle(line)) break; } catch { }
        }
        lock (playLock) { generation++; try { player.Stop(); } catch { } }
        try { player.Dispose(); } catch { }
        return 0;
    }

    /// <summary>TEMPORARY. The host is a separate process, so the player's
    /// in-memory recorder cannot see it — and the player's own recording showed
    /// the reading happening HERE, with no events to look at. Writing to a file
    /// costs nothing that matters: it is not the process whose message pump the
    /// speech depends on.</summary>
    private static readonly object logLock = new object();
    internal static void HostLog(string s)
    {
        try
        {
            lock (logLock)
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "NBR-host32.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
        }
        catch { }
    }

    // Returns false to quit.
    private static bool Handle(string line)
    {
        int tab = line.IndexOf('\t');
        string cmd = tab >= 0 ? line.Substring(0, tab) : line;
        string arg = tab >= 0 ? line.Substring(tab + 1) : "";
        // Every command the parent sends, so it is plain whether a sentence is cut
        // from OUTSIDE (a CANCEL arriving mid-utterance) or from within the host.
        if (cmd != "PRERENDER")
            HostLog("CMD " + cmd + (cmd == "SPEAK" ? "  (" + DecodeB64(arg).Length + " chars)" : ""));
        else
            HostLog("CMD PRERENDER");
        switch (cmd)
        {
            // Everything that changes how the text will sound has to (a) drop the
            // look-ahead — it was rendered with the OLD settings, and playing it is
            // heard as the previous voice reading one more sentence — and (b) take
            // synthLock, because a look-ahead render in flight owns the voice.
            case "VOICE":
                DropAhead();
                lock (synthLock) { SelectVoice(arg); }
                break;
            case "RATE":
                DropAhead();
                lock (synthLock) { try { synth.Rate = Clamp(ParseInt(arg), -10, 10); } catch { } }
                break;
            case "VOL":
                DropAhead();
                lock (synthLock) { try { synth.Volume = Clamp(ParseInt(arg), 0, 100); } catch { } }
                break;
            case "PITCH":
                DropAhead();
                pitchPercent = Clamp(ParseInt(arg), -50, 50);
                break;
            // The output device is the player's business, not the synthesizer's —
            // the audio is already rendered by then, so a device change costs
            // nothing and applies from the next utterance.
            case "DEVICE":
                player.SetDevice(arg);
                break;
            case "SPEAK": Speak(DecodeB64(arg)); break;
            case "PRERENDER": PreRender(DecodeB64(arg)); break;
            // Buffered playback can't pause mid-stream; the reader doesn't use
            // these anyway (it pauses by cancelling and re-speaking the sentence).
            case "PAUSE": break;
            case "RESUME": break;
            case "CANCEL":
                lock (playLock) { generation++; try { player.Stop(); } catch { } }
                break;
            case "QUIT": return false;
        }
        return true;
    }

    private static void SelectVoice(string name)
    {
        try
        {
            dynamic toks = synth.GetVoices();
            int n = toks.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic tok = toks.Item(i);
                if (string.Equals(VoiceName(tok), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Desc(tok), name, StringComparison.OrdinalIgnoreCase))
                {
                    synth.Voice = tok;
                    return;
                }
            }
        }
        catch { }
    }

    /// <summary>Starts an utterance: rendered to a WAV buffer and played on a
    /// worker thread, so the command loop stays responsive.</summary>
    private static void Speak(string text)
    {
        int myGen;
        lock (playLock)
        {
            myGen = ++generation;   // supersedes anything still playing
            try { player.Stop(); } catch { }
        }
        Thread t = new Thread(() => SpeakWorker(text, myGen)) { IsBackground = true };
        t.Start();
    }

    /// <summary>Renders a sentence ahead of time (the parent sends the next one
    /// while the current is still playing) so it can start instantly.</summary>
    private static void PreRender(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (aheadLock)
        {
            if (aheadText == text) return;      // already prepared or preparing
            aheadText = text;
            aheadWav = null;
            aheadReady.Reset();
        }
        Thread t = new Thread(() =>
        {
            byte[] wav = Render(text);
            lock (aheadLock)
            {
                if (aheadText == text) { aheadWav = wav; aheadReady.Set(); }
            }
        }) { IsBackground = true };
        t.Start();
    }

    /// <summary>Throws the look-ahead away, waking anyone waiting on it (they
    /// re-check, find nothing, and render the sentence fresh with the settings
    /// that are current now).</summary>
    private static void DropAhead()
    {
        lock (aheadLock) { aheadText = null; aheadWav = null; }
        aheadReady.Set();
    }

    /// <summary>Takes the pre-rendered audio for this sentence if we have it,
    /// waiting briefly when it is still being prepared.</summary>
    private static byte[] TakeAhead(string text)
    {
        bool mine;
        lock (aheadLock) mine = aheadText == text;
        if (!mine) return null;
        aheadReady.WaitOne(5000);
        lock (aheadLock)
        {
            byte[] wav = aheadText == text ? aheadWav : null;
            if (wav != null) { aheadText = null; aheadWav = null; aheadReady.Reset(); }
            return wav;
        }
    }

    private static void SpeakWorker(string text, int myGen)
    {
        byte[] wav = TakeAhead(text) ?? Render(text);
        if (wav != null && IsCurrent(myGen))
        {
            bool started;
            lock (playLock)
            {
                if (myGen != generation) { Report(myGen); return; }
                started = player.Play(wav);
            }
            if (started)
            {
                // Wait out the audio, checking often enough that a CANCEL is
                // noticed at once (Stop() ends the playback, and IsPlaying then
                // goes false on the next look).
                while (IsCurrent(myGen) && player.IsPlaying) Thread.Sleep(20);
                if (IsCurrent(myGen)) player.ReleaseFinished();
            }
        }
        Report(myGen);
    }

    /// <summary>Renders the utterance to a WAV buffer through SAPI's own file
    /// stream. Every SAPI voice can do this — including eSpeak, which
    /// System.Speech could not render — so there is no real-time fallback any
    /// more, and with it went the crackle and the missing device choice.</summary>
    private static byte[] Render(string text)
    {
        string path = null;
        try
        {
            path = Path.Combine(Path.GetTempPath(), "nbr-render-" + Guid.NewGuid().ToString("N") + ".wav");
            lock (synthLock)          // the voice is not thread-safe
            {
                dynamic fs = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream"));
                fs.Open(path, 3, false);              // SSFMCreateForWrite
                try
                {
                    synth.AudioOutputStream = fs;
                    if (pitchPercent == 0) synth.Speak(text ?? "", 16);        // SVSFIsNotXML
                    else synth.Speak(BuildSsml(text ?? ""), 8);                // SVSFIsXML
                }
                finally
                {
                    try { synth.AudioOutputStream = null; } catch { }
                    try { fs.Close(); } catch { }
                }
            }
            byte[] wav = File.ReadAllBytes(path);
            return wav.Length > 44 ? SapiWavPlayer.TrimTrailingSilence(wav) : null;
        }
        catch { return null; }
        finally
        {
            if (path != null) { try { File.Delete(path); } catch { } }
        }
    }

    private static bool IsCurrent(int myGen)
    {
        lock (playLock) return myGen == generation;
    }

    /// <summary>Reports the utterance's end — "cancelled" if it was superseded or
    /// stopped, so the reader knows not to advance to the next sentence.</summary>
    private static void Report(int myGen)
    {
        Emit("DONE\t" + (IsCurrent(myGen) ? "natural" : "cancelled"));
    }

    private static string BuildSsml(string text)
    {
        string esc = System.Security.SecurityElement.Escape(text) ?? "";
        string pitch = (pitchPercent >= 0 ? "+" : "") + pitchPercent + "%";
        return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">"
             + "<prosody pitch=\"" + pitch + "\">" + esc + "</prosody></speak>";
    }

    private static void Emit(string msg)
    {
        lock (outLock) { Console.Out.WriteLine(msg); Console.Out.Flush(); }
    }

    private static string Desc(dynamic token)
    {
        try { return (string)token.GetDescription(); } catch { return ""; }
    }

    /// <summary>The token's own Name attribute — what the voice is called on both
    /// sides of the process boundary — falling back to its description.</summary>
    private static string VoiceName(dynamic token)
    {
        string name = Attr(token, "Name");
        return string.IsNullOrEmpty(name) ? Desc(token) : name;
    }

    private static string Attr(dynamic token, string name)
    {
        try { return (string)token.GetAttribute(name) ?? ""; } catch { return ""; }
    }

    /// <summary>SAPI reports a voice's language as a hex LCID (possibly several,
    /// semicolon-separated); the first one becomes a culture name like "hr-HR".</summary>
    private static string CultureOfLcid(string attribute)
    {
        if (string.IsNullOrEmpty(attribute)) return "";
        string first = attribute.Split(';')[0].Trim();
        try { return new CultureInfo(int.Parse(first, NumberStyles.HexNumber)).Name; }
        catch { return ""; }
    }

    private static string DecodeB64(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; }
    }

    private static int ParseInt(string s) { int n; return int.TryParse(s, out n) ? n : 0; }
    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
}
