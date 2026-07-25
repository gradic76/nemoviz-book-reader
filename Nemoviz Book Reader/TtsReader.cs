using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>Text-book playback engine: reads a document aloud through a
    /// pluggable <see cref="ISpeechBackend"/>, one sentence at a time, so a text
    /// book behaves like an audio book — play/pause/stop, seek by sentence /
    /// paragraph / standard page / time, a rememberable position (the character
    /// offset), and adjustable voice, rate and pitch.
    ///
    /// A backend can't seek inside an utterance, so seeking = cancel + speak from
    /// the target sentence; the position (the current sentence's start offset)
    /// is therefore always known. The sentence is the smallest reading unit;
    /// paragraph and character-based (standard page / time) jumps snap to the
    /// nearest sentence. Everything is a no-op when no voice is available.</summary>
    public class TtsReader : IDisposable
    {
        // A "standard page" (translation/journalism unit): 1800 characters.
        public const int StandardPageChars = 1800;
        // Rough characters-per-second at rate 0, used to estimate time seeks.
        private const int BaseCharsPerSecond = 15;

        private readonly ISpeechBackend backend;

        private string fullText = "";
        private readonly List<int> sentenceStart = new List<int>();
        private readonly List<string> sentenceText = new List<string>();
        // Sentence index at which each paragraph begins.
        private readonly List<int> paragraphStart = new List<int>();

        private int index;      // current sentence
        private bool reading;   // actively speaking (not paused/stopped)
        private int rate;       // remembered for time-seek estimation

        /// <summary>Raised whenever the current sentence changes (read the
        /// position properties for the new values).</summary>
        public event Action PositionChanged;
        /// <summary>Raised when the last sentence finishes (natural end).</summary>
        public event Action Finished;

        public int Count { get { return sentenceText.Count; } }
        public int CurrentSentence { get { return index; } }
        public bool IsReading { get { return reading; } }
        public int TotalChars { get { return fullText.Length; } }
        public int CharPosition
        {
            get { return (index >= 0 && index < sentenceStart.Count) ? sentenceStart[index] : 0; }
        }
        public string CurrentText
        {
            get { return (index >= 0 && index < sentenceText.Count) ? sentenceText[index] : ""; }
        }

        public TtsReader() : this(new CompositeSpeechBackend()) { }

        public TtsReader(ISpeechBackend backend)
        {
            this.backend = backend;
            backend.Completed += OnCompleted;
        }

        public List<string> GetVoices() { return backend.GetVoices(); }
        public string CurrentVoice { get { return backend.CurrentVoiceName; } }
        public void SetVoice(string name) { backend.SelectVoice(name); RestartCurrent(); }
        public void SetRate(int r) { rate = r; backend.SetRate(r); RestartCurrent(); }
        public void SetVolume(int v) { backend.SetVolume(v); RestartCurrent(); }
        public void SetPitch(int p) { backend.SetPitch(p); RestartCurrent(); }
        /// <summary>Routes speech to a specific output device (mpv-style id;
        /// empty/"auto" = system default). Restarts the current sentence so the
        /// switch is immediate — and so the device is applied with no utterance in
        /// flight, which some voices (RHVoice) need to re-init their audio cleanly
        /// instead of dropping the first sentence into silence.</summary>
        public void SetAudioDevice(string deviceId) { backend.SetAudioDevice(deviceId); RestartCurrent(); }

        // SAPI applies rate/volume/voice to the NEXT utterance, not the one in
        // progress, so re-speak the current sentence to make a live change
        // audible immediately.
        private void RestartCurrent()
        {
            if (!reading) return;
            backend.Cancel();
            SpeakCurrent();
        }

        /// <summary>Loads document text, splits into sentences/paragraphs, and
        /// resets to the start. Stops any current reading.</summary>
        public void LoadText(string text)
        {
            Stop();
            // Tidy the raw text (collapse big gaps, de-hyphenate, strip noise)
            // so TTS reads smoothly without long pauses or "stumbling".
            fullText = TextCleaner.Clean(text);
            Split();
            index = 0;
            RaisePosition();
        }

        public void Play()
        {
            if (sentenceText.Count == 0) return;
            if (reading) return;
            // Always (re)start from the beginning of the current sentence —
            // resuming mid-sentence (SAPI Resume) sounds odd.
            SpeakCurrent();
        }

        public void Pause()
        {
            if (!reading) return;
            reading = false;
            // Cancel rather than SAPI-pause: the sentence index stays put, so
            // the next Play re-speaks this sentence from its start.
            backend.Cancel();
        }

        public void Stop()
        {
            // Only cancel if something is actually speaking/paused. Cancelling an
            // idle synth queues a SAPI cancel that can swallow the very next
            // SpeakAsync (the autoplay race) — so skip it when there's nothing
            // to stop.
            bool active = reading || backend.IsPaused;
            reading = false;
            if (active) backend.Cancel();
        }

        // ── Navigation (keeps play/pause state) ───────────────────────────
        public void NextSentence() { SeekToSentence(index + 1); }
        public void PrevSentence() { SeekToSentence(index - 1); }

        public void NextParagraph()
        {
            foreach (int p in paragraphStart)
                if (p > index) { SeekToSentence(p); return; }
            SeekToSentence(sentenceText.Count - 1);
        }

        public void PrevParagraph()
        {
            // The paragraph start at/just before the current sentence, with a
            // one-sentence grace so a repeat goes to the previous paragraph.
            int target = 0;
            for (int i = paragraphStart.Count - 1; i >= 0; i--)
            {
                if (paragraphStart[i] < index) { target = paragraphStart[i]; break; }
            }
            SeekToSentence(target);
        }

        /// <summary>Jump by a character delta (standard page = ±1800), snapped
        /// to the sentence containing the target offset.</summary>
        public void SeekChars(int delta) { SeekToChar(CharPosition + delta); }

        /// <summary>Jump by an estimated number of seconds of speech (time seek),
        /// using the current rate to gauge characters per second.</summary>
        public void SeekSeconds(int seconds)
        {
            int cps = BaseCharsPerSecond + rate; // ~15 at rate 0
            if (cps < 5) cps = 5;
            SeekChars(seconds * cps);
        }

        public void SeekToSentence(int i)
        {
            if (sentenceText.Count == 0) return;
            bool wasReading = reading;
            Stop();
            index = Clamp(i, 0, sentenceText.Count - 1);
            RaisePosition();
            if (wasReading) SpeakCurrent();
        }

        public void SeekToChar(int offset)
        {
            if (sentenceStart.Count == 0) return;
            int target = 0;
            for (int i = sentenceStart.Count - 1; i >= 0; i--)
            {
                if (sentenceStart[i] <= offset) { target = i; break; }
            }
            SeekToSentence(target);
        }

        private void SpeakCurrent()
        {
            if (index < 0 || index >= sentenceText.Count)
            {
                reading = false;
                Finished?.Invoke();
                return;
            }
            reading = true;
            RaisePosition();
            backend.Speak(sentenceText[index]);
        }

        private void OnCompleted(bool cancelled)
        {
            if (cancelled || !reading) return; // stop/seek/pause
            index++;
            if (index >= sentenceText.Count)
            {
                index = sentenceText.Count - 1;
                reading = false;
                RaisePosition();
                Finished?.Invoke();
                return;
            }
            SpeakCurrent();
        }

        private void RaisePosition() { PositionChanged?.Invoke(); }

        /// <summary>Splits fullText into sentences (with absolute start offsets)
        /// and records where each paragraph (blank-line separated) begins.</summary>
        private void Split()
        {
            sentenceStart.Clear();
            sentenceText.Clear();
            paragraphStart.Clear();

            int p = 0;
            int len = fullText.Length;
            while (p < len)
            {
                while (p < len && char.IsWhiteSpace(fullText[p])) p++;
                if (p >= len) break;

                int pe = fullText.IndexOf("\n\n", p, StringComparison.Ordinal);
                if (pe < 0) pe = len;
                string para = fullText.Substring(p, pe - p);

                paragraphStart.Add(sentenceText.Count);
                foreach (Match m in Regex.Matches(para, @"[^.!?…]*[.!?…]+[""')\]]*|\S[^.!?…]*$"))
                {
                    string val = m.Value;
                    int lead = val.Length - val.TrimStart().Length;
                    string t = val.Trim();
                    if (t.Length == 0) continue;
                    sentenceStart.Add(p + m.Index + lead);
                    sentenceText.Add(t);
                }
                p = pe + 2;
            }

            if (sentenceText.Count == 0 && fullText.Trim().Length > 0)
            {
                paragraphStart.Add(0);
                sentenceStart.Add(0);
                sentenceText.Add(fullText.Trim());
            }
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>Maps a nominal words-per-minute onto SAPI's -10..10 rate
        /// (175 WPM → 0). Nominal because the real spoken rate depends on the
        /// voice, but it gives a familiar, monotonic control.</summary>
        public static int WpmToRate(int wpm)
        {
            int r = (int)Math.Round((wpm - 175) / 17.5);
            return Clamp(r, -10, 10);
        }

        // Nominal characters per minute for a given WPM (≈ 6 chars/word incl.
        // spaces), used to estimate reading time.
        public static int CharsPerMinute(int wpm) { return wpm * 6; }

        /// <summary>Reads a text file, honouring a BOM; without one, tries strict
        /// UTF-8 and falls back to Windows-1250 (Central-European ANSI, common
        /// for Croatian .txt) when the bytes aren't valid UTF-8.</summary>
        public static string ReadFile(string path)
        {
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                using (StreamReader r = new StreamReader(path, strict, true))
                    return r.ReadToEnd();
            }
            catch (DecoderFallbackException)
            {
                try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
                return File.ReadAllText(path, Encoding.GetEncoding(1250));
            }
            catch
            {
                return "";
            }
        }

        public void Dispose()
        {
            try { backend.Dispose(); } catch { }
        }
    }
}
