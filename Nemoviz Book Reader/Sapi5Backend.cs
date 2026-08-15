using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The in-process SAPI 5 speech backend, on the SAPI COM automation object
    /// (<c>SAPI.SpVoice</c>) via late binding — no interop assembly, no vendoring.
    /// System.Speech was the earlier host but it hides SAPI's audio-output
    /// selection; the COM object exposes <c>AudioOutput</c>, so this is what lets
    /// text-to-speech follow the sound-card picked in Settings → Device, exactly
    /// how screen readers do it. Sees every 64-bit SAPI 5 voice (Zira, RHVoice, …).
    ///
    /// SAPI COM raises completion through connection-point events that late binding
    /// can't reach, so end-of-utterance is detected by polling
    /// <c>Status.RunningState</c> (2 = speaking) on a UI-thread timer — the reader
    /// only needs sentence-level completion, not word boundaries. All calls stay on
    /// the creating (UI/STA) thread.
    /// </summary>
    public class Sapi5Backend : ISpeechBackend, ISpeechRenderer
    {
        private const int SVSFlagsAsync = 1;
        private const int SVSFPurgeBeforeSpeak = 2;
        private const int SVSFIsXML = 8;
        private const int SVSFIsNotXML = 16;
        private const int SRSEIsSpeaking = 2;

        private readonly dynamic voice;          // SAPI.SpVoice
        private readonly Timer poll;             // completion poller (UI thread)
        private int pitchPercent;

        // Utterance state machine (single UI thread → no locking needed).
        private bool speaking, sawSpeaking, cancelled, paused;
        private int speakStartTick;

        // Desired output device (mpv-style id) and the last one actually applied.
        private string desiredDeviceId = "";
        private string appliedDeviceId = null;

        public event Action<bool> Completed;

        public Sapi5Backend()
        {
            Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (t == null) throw new NotSupportedException("SAPI.SpVoice not available");
            voice = Activator.CreateInstance(t);
            poll = new Timer { Interval = 50 };
            poll.Tick += Poll_Tick;
        }

        public List<string> GetVoices()
        {
            var list = new List<string>();
            try
            {
                dynamic toks = voice.GetVoices();
                int n = toks.Count;
                for (int i = 0; i < n; i++)
                {
                    string name = VoiceName(toks.Item(i));
                    if (!string.IsNullOrEmpty(name)) list.Add(name);
                }
            }
            catch { }
            return list;
        }

        public List<(string Name, string Vendor, string Language)> GetVoiceInfos()
        {
            var list = new List<(string, string, string)>();
            try
            {
                dynamic toks = voice.GetVoices();
                int n = toks.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic tok = toks.Item(i);
                    string name = VoiceName(tok);
                    if (string.IsNullOrEmpty(name)) continue;
                    string vendor = "";
                    try { vendor = tok.GetAttribute("Vendor") ?? ""; } catch { }
                    string lang = "";
                    try { lang = CultureOfLcid(tok.GetAttribute("Language")); } catch { }
                    list.Add((name, vendor, lang));
                }
            }
            catch { }
            return list;
        }

        /// <summary>SAPI reports a voice's language as a hex LCID (possibly several,
        /// semicolon-separated); the first one becomes a culture name like "hr-HR".</summary>
        public static string CultureOfLcid(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute)) return "";
            string first = attribute.Split(';')[0].Trim();
            try
            {
                int lcid = int.Parse(first, System.Globalization.NumberStyles.HexNumber);
                return new System.Globalization.CultureInfo(lcid).Name;
            }
            catch { return ""; }
        }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                dynamic toks = voice.GetVoices();
                int n = toks.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic tok = toks.Item(i);
                    // The token's own Name is what the voice is known by now; the
                    // description is still accepted so a voice saved under the old
                    // (description) name in a Book.ini keeps working.
                    if (string.Equals(VoiceName(tok), name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(SafeDesc(tok), name, StringComparison.OrdinalIgnoreCase))
                    {
                        voice.Voice = tok;
                        return;
                    }
                }
            }
            catch { }
        }

        public string CurrentVoiceName
        {
            get { try { return VoiceName(voice.Voice); } catch { return ""; } }
        }

        public void SetRate(int rate) { try { voice.Rate = Clamp(rate, -10, 10); } catch { } }
        public void SetVolume(int volume) { try { voice.Volume = Clamp(volume, 0, 100); } catch { } }
        public void SetPitch(int percent) { pitchPercent = Clamp(percent, -50, 50); }

        /// <summary>Selects the SAPI audio-output token matching the mpv device id
        /// (by shared WASAPI guid). Applied on the next Speak.</summary>
        public void SetAudioDevice(string deviceId)
        {
            desiredDeviceId = deviceId ?? "";
        }

        private void ApplyDeviceIfNeeded()
        {
            if (string.Equals(desiredDeviceId, appliedDeviceId, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                string guid = ExtractGuid(desiredDeviceId);
                if (guid == null)
                {
                    // "auto"/empty/unknown → fall back to the system default output.
                    try { voice.AudioOutput = null; } catch { }
                }
                else
                {
                    dynamic outs = voice.GetAudioOutputs();
                    int n = outs.Count;
                    for (int i = 0; i < n; i++)
                    {
                        dynamic tok = outs.Item(i);
                        string id = "";
                        try { id = tok.Id ?? ""; } catch { }
                        if (id.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            voice.AudioOutput = tok;
                            break;
                        }
                    }
                }
                appliedDeviceId = desiredDeviceId;
            }
            catch { }
        }

        public bool IsPaused { get { return paused; } }

        // ── Rendering, for the export ─────────────────────────────────────────

        private readonly object renderLock = new object();
        private dynamic renderVoice;

        /// <summary>A sentence as a WAV rather than a sound.
        ///
        /// <para><b>Its own SpVoice, and that is not tidiness.</b> The one this
        /// backend speaks through may be mid-sentence; pointing its
        /// <c>AudioOutputStream</c> at a file would take the reader's voice away
        /// and give them silence. A second object costs a COM instance and
        /// nothing else.</para>
        ///
        /// <para>Rate 0 and volume 100 always — the cache holds the voice as it
        /// is, and the reader's own speed and volume happen at playback. Pitch is
        /// left alone too; see <see cref="ISpeechRenderer"/> for why that one is
        /// a limitation rather than a rule.</para>
        ///
        /// <para>Through a file rather than a memory stream because that is the
        /// shape already proven here: the 32-bit host renders every voice this
        /// way, eSpeak included, which is why it was moved off System.Speech in
        /// the first place.</para></summary>
        public byte[] Render(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string path = null;
            try
            {
                path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "nbr-render-" + Guid.NewGuid().ToString("N") + ".wav");
                lock (renderLock)
                {
                    if (renderVoice == null)
                    {
                        Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
                        if (t == null) return null;
                        renderVoice = Activator.CreateInstance(t);
                    }
                    try { renderVoice.Voice = voice.Voice; } catch { }
                    try { renderVoice.Rate = 0; renderVoice.Volume = 100; } catch { }

                    dynamic fs = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream"));
                    fs.Open(path, 3, false);          // SSFMCreateForWrite
                    try
                    {
                        renderVoice.AudioOutputStream = fs;
                        renderVoice.Speak(text, SVSFIsNotXML);   // synchronous
                    }
                    finally
                    {
                        try { renderVoice.AudioOutputStream = null; } catch { }
                        try { fs.Close(); } catch { }
                    }
                }
                byte[] wav = System.IO.File.ReadAllBytes(path);
                return wav.Length > 44 ? SapiWavPlayer.TrimTrailingSilence(wav) : null;
            }
            catch { return null; }
            finally
            {
                if (path != null) { try { System.IO.File.Delete(path); } catch { } }
            }
        }

        public void Speak(string text)
        {
            try
            {
                ApplyDeviceIfNeeded();
                cancelled = false;
                sawSpeaking = false;
                paused = false;
                speaking = true;
                speakStartTick = Environment.TickCount;
                if (pitchPercent == 0)
                    voice.Speak(text ?? "", SVSFlagsAsync | SVSFIsNotXML);
                else
                    voice.Speak(BuildSsml(text ?? ""), SVSFlagsAsync | SVSFIsXML);
                poll.Start();
            }
            catch { speaking = false; }
        }

        // Speaks straight to the device, so there is nothing to prepare ahead.
        public void PreRender(string text) { }

        public void Pause()
        {
            if (speaking && !paused)
            {
                try { voice.Pause(); paused = true; } catch { }
            }
        }

        public void Resume()
        {
            if (paused)
            {
                try { voice.Resume(); } catch { }
                paused = false;
            }
        }

        public void Cancel()
        {
            if (!speaking && !paused) return;
            if (paused) { try { voice.Resume(); } catch { } paused = false; }
            cancelled = true;
            try { voice.Speak("", SVSFPurgeBeforeSpeak | SVSFlagsAsync); } catch { }
            // Completion (cancelled=true) fires on the next poll once SAPI leaves
            // the speaking state.
        }

        private void Poll_Tick(object sender, EventArgs e)
        {
            if (!speaking || paused) return;
            int rs;
            try { rs = (int)voice.Status.RunningState; } catch { return; }
            if (rs == SRSEIsSpeaking) { sawSpeaking = true; return; }
            // Not in the speaking state: done once we've actually seen it speak, or
            // after a short grace for a very short/empty utterance that never did.
            if (!sawSpeaking && Environment.TickCount - speakStartTick < 400) return;
            speaking = false;
            poll.Stop();
            bool wasCancelled = cancelled;
            cancelled = false;
            Completed?.Invoke(wasCancelled);
        }

        private string BuildSsml(string text)
        {
            // SAPI accepts SSML with SVSFIsXML; the prosody pitch wrapper works
            // regardless of the declared language, so a fixed xml:lang is fine.
            string esc = System.Security.SecurityElement.Escape(text) ?? "";
            string pitch = (pitchPercent >= 0 ? "+" : "") + pitchPercent + "%";
            return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">"
                 + "<prosody pitch=\"" + pitch + "\">" + esc + "</prosody></speak>";
        }

        private static string SafeDesc(dynamic token)
        {
            try { return (string)token.GetDescription(); } catch { return ""; }
        }

        /// <summary>The name a voice is known by — the token's own <c>Name</c>
        /// attribute ("Microsoft Zira Desktop"), not SAPI's description, which
        /// appends the language ("Microsoft Zira Desktop - English (United
        /// States)"). The 32-bit host reports System.Speech's <c>VoiceInfo.Name</c>,
        /// i.e. the bare name, so using the description here made the SAME voice
        /// look like two different ones: the merge in
        /// <see cref="CompositeSpeechBackend"/> never recognised the duplicate and
        /// a saved "Microsoft Zira Desktop" resolved to the 32-bit satellite
        /// instead of this in-process backend. Falls back to the description for
        /// a token that doesn't expose the attribute.</summary>
        private static string VoiceName(dynamic token)
        {
            try
            {
                string name = token.GetAttribute("Name") as string;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return SafeDesc(token);
        }

        // Pulls the WASAPI endpoint guid "{…}" out of an mpv id like
        // "wasapi/{0.0.0.0}.{guid}" or "wasapi/{guid}". Null for auto/openal/empty.
        private static string ExtractGuid(string mpvId)
        {
            if (string.IsNullOrEmpty(mpvId)) return null;
            int last = mpvId.LastIndexOf('{');
            int close = last >= 0 ? mpvId.IndexOf('}', last) : -1;
            if (last < 0 || close < 0) return null;
            return mpvId.Substring(last, close - last + 1); // includes the braces
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public void Dispose()
        {
            try { poll.Stop(); poll.Dispose(); } catch { }
            try { voice.Speak("", SVSFPurgeBeforeSpeak | SVSFlagsAsync); } catch { }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice); } catch { }
        }
    }
}
