// TtsHost32 — a tiny 32-bit console host for SAPI5 speech, so the x64 player
// can use 32-bit-only voices (e.g. eSpeak) out-of-process. It hosts a
// System.Speech SpeechSynthesizer (which, compiled x86, sees the 32-bit voice
// registry), plays audio itself, and talks to the parent over stdin/stdout with
// a simple line protocol. Mirrors Sapi5Backend's behaviour (rate/volume/pitch).
//
// This file is NOT part of the main (x64) project build — it is compiled
// separately to TtsHost32.exe (x86) into the app output; see the build notes.
//
// Protocol (tab-separated, one command/event per line):
//   parent -> host : VOICE<TAB>name | RATE<TAB>n | VOL<TAB>n | PITCH<TAB>n
//                    | SPEAK<TAB>base64utf8 | PRERENDER<TAB>base64utf8
//                    | PAUSE | RESUME | CANCEL | QUIT
//                    VOICE<TAB>name<TAB>vendor<TAB>language  (per voice)
//                    READY             (startup enumeration done)
//                    DONE<TAB>natural | DONE<TAB>cancelled  (utterance ended)
using System;
using System.IO;
using System.Media;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;

class TtsHost32
{
    private static SpeechSynthesizer synth;
    private static int pitchPercent;
    private static readonly object outLock = new object();

    // Audio is NOT rendered straight to the sound card. Each utterance is
    // synthesised into a WAV buffer first and then played as one continuous
    // stream. Rendering in real time made the output crackle: the odd underrun
    // with any voice, and constantly between words with eSpeak, whose SAPI driver
    // emits audio in per-word chunks and restarts the stream at every boundary.
    // Buffering removes the real-time constraint and the chunk seams alike.
    private static readonly object synthLock = new object();
    private static readonly object playLock = new object();
    private static SoundPlayer player;
    // Bumped by every new utterance and by CANCEL; a worker whose generation is no
    // longer current reports its utterance as cancelled. (This also replaces the
    // old SpeakCompleted handling: some SAPI drivers — eSpeak again — report a
    // natural end as "cancelled", which used to stop the reader after a sentence.)
    private static int generation;
    // Voices whose driver can't render to a stream (eSpeak) — they use the
    // real-time path instead, and this remembers them after the first attempt.
    private static readonly System.Collections.Generic.HashSet<string> noBuffer =
        new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly ManualResetEvent spoken = new ManualResetEvent(false);

    // Look-ahead: the parent sends the NEXT sentence while the current one plays, so
    // its audio is already rendered when the time comes and the gap between
    // sentences disappears. Only one sentence is held — that's all the reader needs.
    private static string aheadText;
    private static byte[] aheadWav;
    private static readonly ManualResetEvent aheadReady = new ManualResetEvent(false);
    private static readonly object aheadLock = new object();

    static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.InputEncoding = Encoding.UTF8; } catch { }

        try { synth = new SpeechSynthesizer(); }
        catch { Emit("READY"); return 1; }

        // Completion is reported by this host (see SpeakWorker), not from the
        // driver's own flag — some engines call a natural end "cancelled". The
        // event only releases the real-time path's wait.
        synth.SpeakCompleted += (s, e) => spoken.Set();

        // Announce the voices this (32-bit) host can see, then READY.
        try
        {
            foreach (InstalledVoice v in synth.GetInstalledVoices())
                if (v.Enabled) Emit("VOICE\t" + v.VoiceInfo.Name + "\t" + Vendor(v.VoiceInfo) + "\t" + Lang(v.VoiceInfo));
        }
        catch { }
        Emit("READY");

        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            try { if (!Handle(line)) break; } catch { }
        }
        lock (playLock) { generation++; StopPlayback(); }
        try { synth.Dispose(); } catch { }
        return 0;
    }

    // Returns false to quit.
    private static bool Handle(string line)
    {
        int tab = line.IndexOf('\t');
        string cmd = tab >= 0 ? line.Substring(0, tab) : line;
        string arg = tab >= 0 ? line.Substring(tab + 1) : "";
        switch (cmd)
        {
            case "VOICE": try { synth.SelectVoice(arg); } catch { } break;
            case "RATE": synth.Rate = Clamp(ParseInt(arg), -10, 10); break;
            case "VOL": synth.Volume = Clamp(ParseInt(arg), 0, 100); break;
            case "PITCH": pitchPercent = Clamp(ParseInt(arg), -50, 50); break;
            case "SPEAK": Speak(DecodeB64(arg)); break;
            case "PRERENDER": PreRender(DecodeB64(arg)); break;
            // Buffered playback can't pause mid-stream; the reader doesn't use
            // these anyway (it pauses by cancelling and re-speaking the sentence).
            case "PAUSE": break;
            case "RESUME": break;
            case "CANCEL":
                lock (playLock) { generation++; StopPlayback(); }
                try { synth.SpeakAsyncCancelAll(); } catch { }   // real-time path
                spoken.Set();
                break;
            case "QUIT": return false;
        }
        return true;
    }

    /// <summary>Starts an utterance: it is rendered to a WAV buffer and played on a
    /// worker thread, so the command loop stays responsive.</summary>
    private static void Speak(string text)
    {
        int myGen;
        lock (playLock)
        {
            myGen = ++generation;   // supersedes anything still playing
            StopPlayback();
        }
        try { synth.SpeakAsyncCancelAll(); } catch { }   // stop a real-time utterance
        spoken.Set();
        Thread t = new Thread(() => SpeakWorker(text, myGen));
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>Renders a sentence ahead of time (the parent sends the next one while
    /// the current is still playing) so it can start instantly. Ignored for voices
    /// that can't render to a stream — they have nothing to gain.</summary>
    private static void PreRender(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (noBuffer) { if (noBuffer.Contains(CurrentVoice())) return; }
        lock (aheadLock)
        {
            if (aheadText == text) return;      // already prepared or preparing
            aheadText = text;
            aheadWav = null;
            aheadReady.Reset();
        }
        Thread t = new Thread(() =>
        {
            byte[] wav = TryRender(text);
            lock (aheadLock)
            {
                if (aheadText == text) { aheadWav = wav; aheadReady.Set(); }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>Takes the pre-rendered audio for this sentence if we have it, waiting
    /// briefly when it is still being prepared.</summary>
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
        byte[] wav = TakeAhead(text) ?? TryRender(text);
        if (wav == null) { SpeakRealtime(text, myGen); return; }

        if (wav.Length > 44 && IsCurrent(myGen))
        {
            SoundPlayer p = null;
            try
            {
                p = new SoundPlayer(new MemoryStream(wav));
                lock (playLock)
                {
                    if (myGen != generation) { Report(myGen); return; }
                    player = p;
                }
                p.PlaySync();             // returns early if Stop() is called
            }
            catch { }
            finally
            {
                lock (playLock) { if (player == p) player = null; }
                try { if (p != null) p.Dispose(); } catch { }
            }
        }
        Report(myGen);
    }

    /// <summary>Renders the utterance to a WAV buffer, or null when this voice's
    /// driver can't do that. Not every SAPI engine can render to a stream — eSpeak
    /// throws — so those voices keep the real-time path (see SpeakRealtime).</summary>
    private static byte[] TryRender(string text)
    {
        string voice = CurrentVoice();
        lock (noBuffer) { if (noBuffer.Contains(voice)) return null; }
        try
        {
            using (MemoryStream ms = new MemoryStream())
            {
                lock (synthLock)          // the synthesizer is not thread-safe
                {
                    synth.SetOutputToWaveStream(ms);
                    if (pitchPercent == 0) synth.Speak(text);
                    else synth.SpeakSsml(BuildSsml(text));
                    synth.SetOutputToNull();   // finalises the WAV header
                }
                if (ms.Length > 44) return TrimTrailingSilence(ms.ToArray());
            }
        }
        catch { }
        lock (noBuffer) { noBuffer.Add(voice); }   // remember: this one can't buffer
        return null;
    }

    /// <summary>
    /// Cuts the silence engines leave at the end of a rendered utterance — Zira pads
    /// roughly three quarters of a second — which would otherwise be played out in
    /// full and heard as a long gap between sentences. A short tail is kept so the
    /// last word doesn't sound clipped. Returns the buffer unchanged if the WAV
    /// isn't the plain 8/16-bit PCM shape we understand.
    /// </summary>
    private static byte[] TrimTrailingSilence(byte[] w)
    {
        try
        {
            if (w.Length < 44 || Encoding.ASCII.GetString(w, 0, 4) != "RIFF") return w;
            int rate = BitConverter.ToInt32(w, 24);
            int channels = BitConverter.ToInt16(w, 22);
            int bits = BitConverter.ToInt16(w, 34);
            if ((bits != 16 && bits != 8) || channels < 1 || rate <= 0) return w;

            int pos = 12, dataOff = -1, dataLen = 0;
            while (pos + 8 <= w.Length)
            {
                string id = Encoding.ASCII.GetString(w, pos, 4);
                int len = BitConverter.ToInt32(w, pos + 4);
                if (len < 0) return w;
                if (id == "data") { dataOff = pos + 8; dataLen = len; break; }
                pos += 8 + len + (len & 1);
            }
            if (dataOff < 0) return w;
            if (dataOff + dataLen > w.Length) dataLen = w.Length - dataOff;

            int frame = (bits / 8) * channels;
            if (frame <= 0 || dataLen < frame) return w;

            int last = dataLen;
            for (int i = dataLen - frame; i >= 0; i -= frame)
            {
                int s = bits == 16 ? BitConverter.ToInt16(w, dataOff + i) : (w[dataOff + i] - 128) << 8;
                if (Math.Abs(s) > 150) { last = i + frame; break; }
            }
            int keep = last + (rate / 25) * frame;          // ~40 ms of tail
            if (keep >= dataLen) return w;                   // nothing worth cutting
            if (dataLen - keep < (rate / 10) * frame) return w;   // less than 100 ms — leave it

            byte[] outBuf = new byte[dataOff + keep];
            Buffer.BlockCopy(w, 0, outBuf, 0, dataOff + keep);
            Buffer.BlockCopy(BitConverter.GetBytes(keep), 0, outBuf, dataOff - 4, 4);          // data size
            Buffer.BlockCopy(BitConverter.GetBytes(outBuf.Length - 8), 0, outBuf, 4, 4);       // RIFF size
            return outBuf;
        }
        catch { return w; }
    }

    /// <summary>The original path: let the driver render straight to the sound card.
    /// Used for voices that can't render to a stream; keeps them working, crackle
    /// and all, rather than leaving them silent.</summary>
    private static void SpeakRealtime(string text, int myGen)
    {
        try
        {
            lock (synthLock)
            {
                synth.SetOutputToDefaultAudioDevice();
                spoken.Reset();
                if (pitchPercent == 0) synth.SpeakAsync(text);
                else synth.SpeakSsmlAsync(BuildSsml(text));
            }
            spoken.WaitOne(120000);       // SpeakCompleted, or a cancel, releases this
        }
        catch { }
        Report(myGen);
    }

    private static string CurrentVoice()
    {
        try { lock (synthLock) return synth.Voice.Name ?? ""; }
        catch { return ""; }
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

    /// <summary>Stops any playback in progress. Caller holds playLock.</summary>
    private static void StopPlayback()
    {
        try { if (player != null) player.Stop(); } catch { }
        player = null;
    }

    private static string BuildSsml(string text)
    {
        string lang = "en-US";
        try { lang = synth.Voice.Culture.Name; } catch { }
        string esc = System.Security.SecurityElement.Escape(text) ?? "";
        string pitch = (pitchPercent >= 0 ? "+" : "") + pitchPercent + "%";
        return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"" + lang + "\">"
             + "<prosody pitch=\"" + pitch + "\">" + esc + "</prosody></speak>";
    }

    private static void Emit(string msg)
    {
        lock (outLock) { Console.Out.WriteLine(msg); Console.Out.Flush(); }
    }

    // The culture the voice speaks, e.g. "hr-HR" — used to group voices by
    // language in Settings.
    private static string Lang(VoiceInfo v)
    {
        try { return v.Culture != null ? v.Culture.Name : ""; } catch { return ""; }
    }

    private static string Vendor(VoiceInfo v)
    {
        try { string s; return v.AdditionalInfo != null && v.AdditionalInfo.TryGetValue("Vendor", out s) ? (s ?? "") : ""; }
        catch { return ""; }
    }

    private static string DecodeB64(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; }
    }

    private static int ParseInt(string s) { int n; return int.TryParse(s, out n) ? n : 0; }
    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
}
