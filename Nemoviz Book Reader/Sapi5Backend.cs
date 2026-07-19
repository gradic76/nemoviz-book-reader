using System;
using System.Collections.Generic;
using System.Speech.Synthesis;

namespace Nemoviz_Book_Reader
{
    /// <summary>The in-process SAPI5 speech backend (System.Speech) — the
    /// "SAPI 5 x64" equivalent. Sees every 64-bit SAPI5 voice registered on the
    /// system (Zira, RHVoice, eSpeak NG once installed, …). 32-bit-only voices
    /// and OneCore natural voices are handled by separate backends added later.
    /// Speaks straight to the speaker; pitch (which SAPI has no direct property
    /// for) is applied through SSML prosody, and plain SpeakAsync is used when
    /// pitch is 0 to avoid SSML escaping in the common case.</summary>
    public class Sapi5Backend : ISpeechBackend
    {
        private readonly SpeechSynthesizer synth;
        private int pitchPercent;

        public event Action<bool> Completed;

        public Sapi5Backend()
        {
            synth = new SpeechSynthesizer();
            synth.SpeakCompleted += (s, e) => Completed?.Invoke(e.Cancelled);
        }

        public List<string> GetVoices()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (InstalledVoice v in synth.GetInstalledVoices())
                    if (v.Enabled) list.Add(v.VoiceInfo.Name);
            }
            catch { }
            return list;
        }

        public List<(string Name, string Vendor)> GetVoiceInfos()
        {
            var list = new List<(string, string)>();
            try
            {
                foreach (InstalledVoice v in synth.GetInstalledVoices())
                {
                    if (!v.Enabled) continue;
                    string vendor = "";
                    try { string s; if (v.VoiceInfo.AdditionalInfo != null && v.VoiceInfo.AdditionalInfo.TryGetValue("Vendor", out s)) vendor = s ?? ""; }
                    catch { }
                    list.Add((v.VoiceInfo.Name, vendor));
                }
            }
            catch { }
            return list;
        }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try { synth.SelectVoice(name); } catch { /* gone/disabled */ }
        }

        public string CurrentVoiceName
        {
            get { try { return synth.Voice.Name; } catch { return ""; } }
        }

        public void SetRate(int rate) { synth.Rate = Clamp(rate, -10, 10); }
        public void SetVolume(int volume) { synth.Volume = Clamp(volume, 0, 100); }
        public void SetPitch(int percent) { pitchPercent = Clamp(percent, -50, 50); }

        public bool IsPaused { get { return synth.State == SynthesizerState.Paused; } }

        public void Speak(string text)
        {
            try
            {
                if (pitchPercent == 0)
                    synth.SpeakAsync(text);
                else
                    synth.SpeakSsmlAsync(BuildSsml(text));
            }
            catch { }
        }

        public void Pause()
        {
            if (synth.State == SynthesizerState.Speaking)
            {
                try { synth.Pause(); } catch { }
            }
        }

        public void Resume()
        {
            if (synth.State == SynthesizerState.Paused)
            {
                try { synth.Resume(); } catch { }
            }
        }

        public void Cancel()
        {
            try { synth.SpeakAsyncCancelAll(); } catch { }
        }

        private string BuildSsml(string text)
        {
            string lang = "en-US";
            try { lang = synth.Voice.Culture.Name; } catch { }
            string esc = System.Security.SecurityElement.Escape(text) ?? "";
            string pitch = (pitchPercent >= 0 ? "+" : "") + pitchPercent + "%";
            return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"" + lang + "\">"
                 + "<prosody pitch=\"" + pitch + "\">" + esc + "</prosody></speak>";
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public void Dispose()
        {
            try { synth.SpeakAsyncCancelAll(); } catch { }
            try { synth.Dispose(); } catch { }
        }
    }
}
