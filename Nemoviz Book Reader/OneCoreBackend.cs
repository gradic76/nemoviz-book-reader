using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The OneCore (WinRT) speech backend — the "mobile"/Narrator voice family
    /// that SAPI 5 cannot see at all, which on a Croatian machine is the only way
    /// to reach <b>Microsoft Matej (hr-HR)</b>. It lives in
    /// <c>HKLM\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens</c>, invisible to
    /// both the in-process SAPI backend and the 32-bit host.
    ///
    /// <para><b>No Windows SDK, no vendored winmd.</b> Everything is late-bound:
    /// the synthesizer type comes from
    /// <c>Type.GetType("…, Windows, ContentType=WindowsRuntime")</c>, and the async
    /// result — which reflection cannot inspect, it arrives as a bare
    /// <c>__ComObject</c> — is unwrapped with <c>AsTask</c> from
    /// <c>System.Runtime.WindowsRuntime</c> (part of the framework, in the GAC).
    /// The synthesizer hands back a finished <c>audio/wav</c> in memory, which is
    /// exactly what <see cref="SapiWavPlayer"/> wants: the audio then follows the
    /// sound card chosen in Settings → Device and stops instantly on Cancel.</para>
    ///
    /// <para>Synthesis runs on a worker thread (it takes real time), but every COM
    /// call stays on the thread that created this backend: the worker only parks
    /// the finished WAV, and a UI-thread timer starts playback and watches for the
    /// end. The next sentence is rendered ahead so sentences follow without a
    /// gap.</para>
    /// </summary>
    public class OneCoreBackend : ISpeechBackend, ISpeechRenderer
    {
        private readonly object synth;                 // Windows.Media.SpeechSynthesis.SpeechSynthesizer
        private readonly Type tSynth;
        private readonly MethodInfo miSynthesize, miAsTask, miAsStream;
        private readonly List<(string Name, string Vendor, string Language, object Voice)> voices =
            new List<(string, string, string, object)>();
        private readonly SapiWavPlayer player;
        private readonly System.Windows.Forms.Timer poll;                   // UI-thread state machine

        private string currentVoice = "";
        private int rate, volume = 100, pitchPercent;

        // Utterance state (all touched on the UI thread except pendingWav, which a
        // worker fills once and the timer picks up).
        private volatile byte[] pendingWav;
        private volatile bool pendingReady;
        private int generation;                        // bumped by every Speak/Cancel
        private bool speaking, cancelled, paused;

        // One sentence rendered ahead: the text it belongs to plus its audio.
        private readonly object aheadLock = new object();
        private string aheadText;
        private byte[] aheadWav;

        public event Action<bool> Completed;

        /// <summary>The engine name these voices group under in the pickers. They
        /// expose no vendor of their own, and calling them plain "Microsoft" would
        /// merge them with the SAPI 5 Microsoft voices, which are a different
        /// engine with different quality.</summary>
        public const string EngineVendor = "Microsoft OneCore";

        public OneCoreBackend()
        {
            tSynth = Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows, ContentType=WindowsRuntime");
            if (tSynth == null) throw new NotSupportedException("WinRT speech synthesis not available");
            synth = Activator.CreateInstance(tSynth);
            miSynthesize = tSynth.GetMethod("SynthesizeTextToStreamAsync");

            Type tStream = Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesisStream, Windows, ContentType=WindowsRuntime");
            Type tSysExt = Type.GetType("System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            Type tStreamExt = Type.GetType("System.IO.WindowsRuntimeStreamExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            if (miSynthesize == null || tStream == null || tSysExt == null || tStreamExt == null)
                throw new NotSupportedException("WinRT speech interop not available");

            foreach (MethodInfo mi in tSysExt.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (mi.Name == "AsTask" && mi.IsGenericMethodDefinition
                    && mi.GetParameters().Length == 1 && mi.GetGenericArguments().Length == 1)
                { miAsTask = mi.MakeGenericMethod(tStream); break; }
            }
            foreach (MethodInfo mi in tStreamExt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (mi.Name == "AsStreamForRead" && mi.GetParameters().Length == 1) { miAsStream = mi; break; }
            if (miAsTask == null || miAsStream == null)
                throw new NotSupportedException("WinRT stream interop not available");

            LoadVoices();
            if (voices.Count == 0) throw new NotSupportedException("no OneCore voices installed");

            player = new SapiWavPlayer();
            poll = new System.Windows.Forms.Timer { Interval = 50 };
            poll.Tick += Poll_Tick;
        }

        private void LoadVoices()
        {
            try
            {
                object all = tSynth.GetProperty("AllVoices", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                foreach (object v in (System.Collections.IEnumerable)all)
                {
                    Type tv = v.GetType();
                    string name = tv.GetProperty("DisplayName").GetValue(v) as string;
                    string lang = tv.GetProperty("Language").GetValue(v) as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    voices.Add((name, EngineVendor, lang ?? "", v));
                }
            }
            catch { }
        }

        public List<string> GetVoices()
        {
            var list = new List<string>();
            foreach (var v in voices) list.Add(v.Name);
            return list;
        }

        public List<(string Name, string Vendor, string Language)> GetVoiceInfos()
        {
            var list = new List<(string, string, string)>();
            foreach (var v in voices) list.Add((v.Name, v.Vendor, v.Language));
            return list;
        }

        public string CurrentVoiceName { get { return currentVoice; } }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var v in voices)
            {
                if (!string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                try { tSynth.GetProperty("Voice").SetValue(synth, v.Voice); currentVoice = v.Name; } catch { }
                DropAhead();
                return;
            }
        }

        // WinRT takes multipliers, not SAPI's -10..10 / 0..100 / ±50 %. The reader
        // speaks in SAPI units, so map them here: rate 0 = 1.0 (the voice's normal
        // speed), +10 ≈ 3× (WinRT allows up to 6), −10 = 0.5× (its floor).
        public void SetRate(int r)
        {
            rate = Clamp(r, -10, 10);
            SetOption("SpeakingRate", rate >= 0 ? 1.0 + rate * 0.2 : 1.0 + rate * 0.05);
        }

        public void SetVolume(int v)
        {
            volume = Clamp(v, 0, 100);
            SetOption("AudioVolume", volume / 100.0);
        }

        public void SetPitch(int percent)
        {
            pitchPercent = Clamp(percent, -50, 50);
            SetOption("AudioPitch", 1.0 + pitchPercent / 100.0);
        }

        private void SetOption(string name, double value)
        {
            try
            {
                object opts = tSynth.GetProperty("Options").GetValue(synth);
                PropertyInfo p = opts.GetType().GetProperty(name);
                if (p != null) p.SetValue(opts, value);
                DropAhead();   // anything already rendered used the old value
            }
            catch { }
        }

        /// <summary>Reads one back, so a render can set it aside and put it
        /// where it was. Answers 1.0 — the neutral value for all three of these —
        /// when it cannot be read at all.</summary>
        private double GetOption(string name)
        {
            try
            {
                object opts = tSynth.GetProperty("Options").GetValue(synth);
                PropertyInfo p = opts.GetType().GetProperty(name);
                if (p == null) return 1.0;
                return Convert.ToDouble(p.GetValue(opts));
            }
            catch { return 1.0; }
        }

        public void SetAudioDevice(string deviceId) { player.SetDevice(deviceId); }

        public void Speak(string text)
        {
            int myGen = ++generation;
            cancelled = false;
            paused = false;
            speaking = true;
            playbackStarted = false;
            player.Stop();
            pendingWav = null;
            pendingReady = false;

            byte[] ready = TakeAhead(text);
            if (ready != null)
            {
                pendingWav = ready;
                pendingReady = true;
            }
            else
            {
                string t = text ?? "";
                var worker = new Thread(() =>
                {
                    byte[] wav = Synthesize(t);
                    if (myGen != generation) return;      // superseded while rendering
                    pendingWav = wav;
                    pendingReady = true;
                }) { IsBackground = true };
                worker.Start();
            }
            poll.Start();
        }

        /// <summary>Renders the next sentence while this one plays, so the sentences
        /// run into each other instead of pausing for the synthesizer.</summary>
        public void PreRender(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (aheadLock)
            {
                if (aheadText == text) return;
                aheadText = text;
                aheadWav = null;
            }
            var worker = new Thread(() =>
            {
                byte[] wav = Synthesize(text);
                lock (aheadLock) { if (aheadText == text) aheadWav = wav; }
            }) { IsBackground = true };
            worker.Start();
        }

        private byte[] TakeAhead(string text)
        {
            lock (aheadLock)
            {
                if (aheadText != text || aheadWav == null) return null;
                byte[] wav = aheadWav;
                aheadText = null; aheadWav = null;
                return wav;
            }
        }

        private void DropAhead()
        {
            lock (aheadLock) { aheadText = null; aheadWav = null; }
        }

        /// <summary>Already renders to a buffer for its own playback, so an export
        /// costs it nothing new. The speaking rate and volume it carries are the
        /// reader's, though — see the note below.</summary>
        public byte[] Render(string text)
        {
            // The cache holds the voice as it IS: speed and volume belong to the
            // listening and are applied at playback. Set aside and put back, so a
            // render never disturbs a reading in progress.
            double wasRate = 1.0, wasVolume = 1.0;
            try { wasRate = GetOption("SpeakingRate"); wasVolume = GetOption("AudioVolume"); } catch { }
            try
            {
                SetOption("SpeakingRate", 1.0);
                SetOption("AudioVolume", 1.0);
                return Synthesize(text);
            }
            finally
            {
                try { SetOption("SpeakingRate", wasRate); SetOption("AudioVolume", wasVolume); } catch { }
            }
        }

        private byte[] Synthesize(string text)
        {
            try
            {
                object op = miSynthesize.Invoke(synth, new object[] { text ?? "" });
                var task = (Task)miAsTask.Invoke(null, new object[] { op });
                if (!task.Wait(60000)) return null;
                object stream = task.GetType().GetProperty("Result").GetValue(task);
                using (var ms = new System.IO.MemoryStream())
                {
                    var s = (System.IO.Stream)miAsStream.Invoke(null, new object[] { stream });
                    s.CopyTo(ms);
                    return SapiWavPlayer.TrimTrailingSilence(ms.ToArray());
                }
            }
            catch { return null; }
        }

        // Drives one utterance: start playing as soon as the audio is rendered,
        // then watch for the end. Everything here runs on the UI thread, so the
        // SAPI player is only ever touched from the thread that created it.
        private void Poll_Tick(object sender, EventArgs e)
        {
            if (!speaking || paused) return;

            if (!playbackStarted)
            {
                if (!pendingReady) return;             // still rendering
                byte[] wav = pendingWav;
                pendingWav = null;
                pendingReady = false;
                // Nothing to play (synthesis failed) counts as a finished
                // utterance, so the reader moves on instead of stopping dead.
                if (wav == null || !player.Play(wav)) { Finish(); return; }
                playbackStarted = true;
                return;
            }
            if (!player.IsPlaying) Finish();
        }

        private bool playbackStarted;

        private void Finish()
        {
            speaking = false;
            playbackStarted = false;
            poll.Stop();
            player.ReleaseFinished();
            bool wasCancelled = cancelled;
            cancelled = false;
            Completed?.Invoke(wasCancelled);
        }

        public void Pause()
        {
            // Buffered playback can't pause mid-stream; the reader pauses by
            // cancelling and re-speaking the sentence (see TtsReader.Pause).
            paused = true;
        }

        public void Resume() { paused = false; }

        public void Cancel()
        {
            if (!speaking && !paused) return;
            generation++;              // any render in flight is now stale
            cancelled = true;
            paused = false;
            player.Stop();
            pendingWav = null;
            pendingReady = false;
            speaking = false;
            playbackStarted = false;
            poll.Stop();
            Completed?.Invoke(true);
        }

        public bool IsPaused { get { return paused; } }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public void Dispose()
        {
            try { poll.Stop(); poll.Dispose(); } catch { }
            try { player.Dispose(); } catch { }
        }
    }
}
