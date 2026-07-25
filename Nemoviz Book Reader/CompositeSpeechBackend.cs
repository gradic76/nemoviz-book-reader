using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Presents several speech backends as one. Merges their voices into a single
    /// list (the in-process 64-bit SAPI5 backend first, so a voice present in
    /// both — e.g. Zira — resolves to the 64-bit copy), remembers which backend
    /// owns each voice, and routes everything at the currently-selected voice's
    /// backend. This is what lets a 32-bit-only voice (eSpeak, RHVoice) be picked
    /// alongside the in-process voices. Rate/volume/pitch are cached so they carry
    /// over when the active backend changes.
    /// </summary>
    public class CompositeSpeechBackend : ISpeechBackend
    {
        private readonly List<ISpeechBackend> backends = new List<ISpeechBackend>();
        // Voice name (case-insensitive) → owning backend, in merge order.
        private readonly Dictionary<string, ISpeechBackend> owner =
            new Dictionary<string, ISpeechBackend>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> mergedVoices = new List<string>();
        // Merged voice metadata for grouping (name + vendor + architecture bits).
        private readonly List<(string Name, string Vendor, int Arch)> catalog =
            new List<(string, string, int)>();

        private ISpeechBackend active;
        private int rate, volume = 100, pitch;
        private string audioDevice = "";

        public event Action<bool> Completed;

        public CompositeSpeechBackend()
        {
            // In-process (64-bit) first so it wins duplicate voice names, then the
            // 32-bit satellite for the voices only it can see.
            try { Add(new Sapi5Backend(), 64); } catch { }
            try { Add(new Sapi5SatelliteBackend(), 32); } catch { }

            active = backends.Count > 0 ? backends[0] : null;
        }

        private void Add(ISpeechBackend b, int arch)
        {
            if (b == null) return;
            backends.Add(b);
            b.Completed += cancelled => Completed?.Invoke(cancelled);
            foreach (var vi in b.GetVoiceInfos())
            {
                if (string.IsNullOrEmpty(vi.Name) || owner.ContainsKey(vi.Name)) continue; // 64-bit wins dupes
                owner[vi.Name] = b;
                mergedVoices.Add(vi.Name);
                catalog.Add((vi.Name, vi.Vendor, arch));
            }
        }

        public List<string> GetVoices() { return new List<string>(mergedVoices); }

        public List<(string Name, string Vendor)> GetVoiceInfos()
        {
            var list = new List<(string, string)>();
            foreach (var c in catalog) list.Add((c.Name, c.Vendor));
            return list;
        }

        /// <summary>Voices paired with a friendly engine group ("eSpeak (32-bit)",
        /// "Microsoft (64-bit)", …) for the Settings engine/voice pickers.</summary>
        public List<(string Name, string Engine)> GetVoiceCatalog()
        {
            var list = new List<(string, string)>();
            foreach (var c in catalog) list.Add((c.Name, EngineLabel(c.Name, c.Vendor, c.Arch)));
            return list;
        }

        // The engine group is whatever the voice's own Vendor attribute reports
        // (e.g. "Microsoft", "Olga Yakovleva"), verbatim — no hard-coded renaming.
        // A vendor given as a URL (some engines, e.g. eSpeak) is shown as its
        // readable host; a missing vendor falls back to "SAPI 5".
        private static string EngineLabel(string name, string vendor, int arch)
        {
            string b = (vendor ?? "").Trim();
            if (b.Length == 0)
                b = "SAPI 5";
            else if (b.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                b = b.Replace("https://", "").Replace("http://", "").Replace("www.", "").TrimEnd('/');
                int slash = b.IndexOf('/');
                if (slash > 0) b = b.Substring(0, slash);
            }
            return b + " (" + arch + "-bit)";
        }

        public string CurrentVoiceName { get { return active != null ? active.CurrentVoiceName : ""; } }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (owner.TryGetValue(name, out ISpeechBackend b) && b != null)
            {
                active = b;
                // Carry the current rate/volume/pitch onto the newly-active backend.
                active.SetRate(rate);
                active.SetVolume(volume);
                active.SetPitch(pitch);
            }
            active?.SelectVoice(name);
        }

        public void SetRate(int r) { rate = r; active?.SetRate(r); }
        public void SetVolume(int v) { volume = v; active?.SetVolume(v); }
        public void SetPitch(int p) { pitch = p; active?.SetPitch(p); }

        // Output device applies to every backend (independent of the active voice)
        // so switching voice keeps the chosen sound card.
        public void SetAudioDevice(string deviceId)
        {
            audioDevice = deviceId ?? "";
            foreach (ISpeechBackend b in backends) b.SetAudioDevice(audioDevice);
        }

        public void Speak(string text) { active?.Speak(text); }
        public void Pause() { active?.Pause(); }
        public void Resume() { active?.Resume(); }
        public void Cancel() { active?.Cancel(); }
        public bool IsPaused { get { return active != null && active.IsPaused; } }

        public void Dispose()
        {
            foreach (ISpeechBackend b in backends)
                try { b.Dispose(); } catch { }
            backends.Clear();
        }
    }
}
