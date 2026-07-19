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

        private ISpeechBackend active;
        private int rate, volume = 100, pitch;

        public event Action<bool> Completed;

        public CompositeSpeechBackend()
        {
            // In-process (64-bit) first so it wins duplicate voice names, then the
            // 32-bit satellite for the voices only it can see.
            Add(new Sapi5Backend());
            try { Add(new Sapi5SatelliteBackend()); } catch { }

            active = backends.Count > 0 ? backends[0] : null;
        }

        private void Add(ISpeechBackend b)
        {
            if (b == null) return;
            backends.Add(b);
            b.Completed += cancelled => Completed?.Invoke(cancelled);
            foreach (string v in b.GetVoices())
            {
                if (string.IsNullOrEmpty(v) || owner.ContainsKey(v)) continue; // 64-bit wins dupes
                owner[v] = b;
                mergedVoices.Add(v);
            }
        }

        public List<string> GetVoices() { return new List<string>(mergedVoices); }

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
