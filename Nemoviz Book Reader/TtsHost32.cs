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
//                    | SPEAK<TAB>base64utf8 | PAUSE | RESUME | CANCEL | QUIT
//   host  -> parent: VOICE<TAB>name   (one per installed voice, at startup)
//                    READY             (startup enumeration done)
//                    DONE<TAB>natural | DONE<TAB>cancelled  (utterance ended)
using System;
using System.Speech.Synthesis;
using System.Text;

class TtsHost32
{
    private static SpeechSynthesizer synth;
    private static int pitchPercent;
    private static readonly object outLock = new object();
    // Some SAPI drivers (notably eSpeak) set e.Cancelled = true even on a natural
    // end, which would make the reader stop after every sentence. So don't trust
    // e.Cancelled: an utterance is "cancelled" only if WE asked to cancel it, or
    // it was superseded by a newer Speak (its prompt is no longer the current one).
    private static bool cancelRequested;
    private static Prompt currentPrompt;

    static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.InputEncoding = Encoding.UTF8; } catch { }

        try { synth = new SpeechSynthesizer(); }
        catch { Emit("READY"); return 1; }

        synth.SpeakCompleted += (s, e) =>
            Emit("DONE\t" + ((e.Prompt == currentPrompt && !cancelRequested) ? "natural" : "cancelled"));
        try { synth.SetOutputToDefaultAudioDevice(); } catch { }

        // Announce the voices this (32-bit) host can see, then READY.
        try
        {
            foreach (InstalledVoice v in synth.GetInstalledVoices())
                if (v.Enabled) Emit("VOICE\t" + v.VoiceInfo.Name + "\t" + Vendor(v.VoiceInfo));
        }
        catch { }
        Emit("READY");

        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            try { if (!Handle(line)) break; } catch { }
        }
        try { synth.SpeakAsyncCancelAll(); } catch { }
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
            case "PAUSE": if (synth.State == SynthesizerState.Speaking) { try { synth.Pause(); } catch { } } break;
            case "RESUME": if (synth.State == SynthesizerState.Paused) { try { synth.Resume(); } catch { } } break;
            case "CANCEL": cancelRequested = true; try { synth.SpeakAsyncCancelAll(); } catch { } break;
            case "QUIT": return false;
        }
        return true;
    }

    private static void Speak(string text)
    {
        try
        {
            // New utterance: invalidate the previous prompt first (so a stale
            // completion in the gap counts as cancelled), then clear the flag.
            currentPrompt = null;
            cancelRequested = false;
            currentPrompt = pitchPercent == 0 ? synth.SpeakAsync(text) : synth.SpeakSsmlAsync(BuildSsml(text));
        }
        catch { }
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
