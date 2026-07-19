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
    public class Sapi5SatelliteBackend : ISpeechBackend
    {
        private Process proc;
        private StreamWriter toHost;
        private Thread reader;
        private readonly List<(string Name, string Vendor)> voices = new List<(string, string)>();
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
                        voices.Add((parts[0], parts.Length > 1 ? parts[1] : ""));
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

        public List<(string Name, string Vendor)> GetVoiceInfos() { return new List<(string, string)>(voices); }

        public string CurrentVoiceName { get { return currentVoice; } }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            currentVoice = name;
            Send("VOICE\t" + name);
        }

        public void SetRate(int rate) { Send("RATE\t" + rate); }
        public void SetVolume(int volume) { Send("VOL\t" + volume); }
        public void SetPitch(int pitchPercent) { Send("PITCH\t" + pitchPercent); }

        public void Speak(string text)
        {
            paused = false;
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
            Send("SPEAK\t" + b64);
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
