using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Out-of-process SAPI5 backend: drives a 32-bit host (TtsHost32.exe) so the
    /// x64 player can use 32-bit-only voices (eSpeak, RHVoice, …) that the
    /// in-process <see cref="Sapi5Backend"/> can't see. The host plays audio
    /// itself and reports utterance completion; we exchange the simple line
    /// protocol documented in TtsHost32.cs over stdin/stdout. If the host can't
    /// be launched the backend is simply inert (no voices), so the player still
    /// works with the in-process voices.
    /// </summary>
    public class Sapi5SatelliteBackend : ISpeechBackend, ISpeechRenderer
    {
        private Process proc;
        private StreamWriter toHost;
        private Thread reader;
        private readonly List<(string Name, string Vendor, string Language)> voices = new List<(string, string, string)>();
        private readonly object writeLock = new object();
        private string currentVoice = "";
        private bool paused;

        public event Action<bool> Completed;

        public Sapi5SatelliteBackend()
        {
            try
            {
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TtsHost32.exe");
                if (!File.Exists(exe)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                proc = Process.Start(psi);
                if (proc == null) return;
                toHost = proc.StandardInput;

                // Read the startup voice list synchronously up to READY.
                var sw = Stopwatch.StartNew();
                string line;
                while (sw.ElapsedMilliseconds < 8000 && (line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line == "READY") break;
                    if (line.StartsWith("VOICE\t"))
                    {
                        string[] parts = line.Substring(6).Split('\t');
                        voices.Add((parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : ""));
                    }
                }

                // Subsequent events (DONE) come on a background reader.
                reader = new Thread(ReadLoop) { IsBackground = true };
                reader.Start();
            }
            catch { proc = null; toHost = null; }
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("DONE\t"))
                    {
                        bool cancelled = line.Substring(5) == "cancelled";
                        Completed?.Invoke(cancelled);
                    }
                    else if (line.StartsWith("AUDIO\t"))
                    {
                        // The answer to the one command that has one. Handed over
                        // through the field the waiting thread reads, then the
                        // gate is opened — in that order, or it wakes to nothing.
                        renderedPath = line.Substring(6);
                        try { rendered.Set(); } catch { }
                    }
                }
            }
            catch { }
        }

        private void Send(string msg)
        {
            if (toHost == null) return;
            try { lock (writeLock) { toHost.WriteLine(msg); toHost.Flush(); } }
            catch { }
        }

        public List<string> GetVoices()
        {
            var list = new List<string>();
            foreach (var v in voices) list.Add(v.Name);
            return list;
        }

        public List<(string Name, string Vendor, string Language)> GetVoiceInfos() { return new List<(string, string, string)>(voices); }

        public string CurrentVoiceName { get { return currentVoice; } }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            currentVoice = name;
            Send("VOICE\t" + name);
        }

        // ── Rendering, for the export ─────────────────────────────────────────

        private readonly System.Threading.ManualResetEvent rendered =
            new System.Threading.ManualResetEvent(false);
        private readonly object renderLock = new object();
        private volatile string renderedPath;

        /// <summary>A sentence as a WAV, from the 32-bit host.
        ///
        /// <para><b>The one place this protocol asks a question and waits for the
        /// answer.</b> Everything else is send-and-forget with DONE arriving later
        /// as an event; here the caller has nothing to do until the audio exists.
        /// One at a time, because there is one host and one gate.</para>
        ///
        /// <para><b>With a deadline, and that is not defensive habit.</b> The host
        /// is another process: if it dies mid-render, or a voice hangs — which is
        /// the sort of thing 32-bit engines do — a wait with no end would freeze
        /// an export for ever with a progress bar that never moves. A minute is
        /// far past any real sentence; measured, the slowest local voice here
        /// takes half a second.</para>
        ///
        /// <para>The file is the host's, made for us and left behind on purpose;
        /// it is read once and deleted here.</para></summary>
        /// <summary>How many timeouts running mean the host is gone rather than
        /// slow. Same number and the same reasoning as the translation chain's
        /// stand-down: an engine that has refused three in a row is not having a
        /// bad minute.</summary>
        private const int GiveUpAfter = 3;
        private int timeouts;

        /// <summary>Whether the host has stopped answering altogether. Public so a
        /// caller can say so rather than silently producing a book of gaps.</summary>
        public bool RenderingGaveUp { get { return timeouts >= GiveUpAfter; } }

        public byte[] Render(string text)
        {
            if (proc == null || string.IsNullOrEmpty(text)) return null;
            // A DEAD HOST MUST NOT BE PAID FOR ONCE PER SENTENCE, and this is what
            // Gordan hit on 2026-08-16: an eSpeak export that "blocked", with the
            // player still running afterwards. Nothing was deadlocked — the
            // deadline below was working exactly as written. But it is a minute
            // EACH, and a book of five thousand passages against a host that has
            // stopped answering is eighty-three hours of perfectly correct
            // waiting. Indistinguishable, from outside, from a hang.
            //
            // So the first three timeouts are treated as bad luck and the rest as
            // the answer. After that every call returns at once and the export
            // finishes and says how many passages are missing, instead of running
            // until somebody kills it.
            if (RenderingGaveUp) return null;
            lock (renderLock)
            {
                string path = null;
                try
                {
                    renderedPath = null;
                    rendered.Reset();
                    Send("RENDERFILE\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
                    if (!rendered.WaitOne(60000)) { timeouts++; return null; }
                    timeouts = 0;       // it answered, so whatever that was is over

                    path = renderedPath;
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                    byte[] wav = System.IO.File.ReadAllBytes(path);
                    return wav.Length > 44 ? SapiWavPlayer.TrimTrailingSilence(wav) : null;
                }
                catch { return null; }
                finally
                {
                    if (!string.IsNullOrEmpty(path))
                        try { System.IO.File.Delete(path); } catch { }
                }
            }
        }

        public void SetRate(int rate) { Send("RATE\t" + rate); }
        public void SetVolume(int volume) { Send("VOL\t" + volume); }
        public void SetPitch(int pitchPercent) { Send("PITCH\t" + pitchPercent); }
        // The host plays through SAPI's own output token, so it follows the sound
        // card picked in Settings → Device exactly like the in-process backend.
        public void SetAudioDevice(string deviceId) { Send("DEVICE\t" + (deviceId ?? "")); }

        public void Speak(string text)
        {
            paused = false;
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
            Send("SPEAK\t" + b64);
        }

        /// <summary>Asks the host to render the next sentence while this one plays,
        /// so buffered voices start it with no gap.</summary>
        public void PreRender(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Send("PRERENDER\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        }

        public void Pause() { paused = true; Send("PAUSE"); }
        public void Resume() { paused = false; Send("RESUME"); }
        public void Cancel() { paused = false; Send("CANCEL"); }

        public bool IsPaused { get { return paused; } }

        public void Dispose()
        {
            try { Send("QUIT"); } catch { }
            try { if (proc != null && !proc.WaitForExit(500)) proc.Kill(); } catch { }
            try { proc?.Dispose(); } catch { }
            proc = null; toHost = null;
        }
    }
}
