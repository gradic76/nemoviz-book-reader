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

        // ── Silent reading ────────────────────────────────────────────────
        /// <summary>Read without speaking: the position still walks the book,
        /// sentence by sentence, paced by <see cref="SilentWpm"/> instead of by a
        /// synthesiser finishing an utterance.
        ///
        /// <para>For a reader on braille or on the screen who does not want a
        /// voice over their reading, and for a book NO installed voice can speak
        /// — where the alternative was a book that opens and then refuses to
        /// move.</para>
        ///
        /// <para>It lives here rather than as a timer in the player because
        /// everything upstream — play, pause, seek, the position events the
        /// reading window and braille follow — then goes on working untouched.
        /// The one thing that changes is what marks a sentence finished: a clock
        /// rather than the backend.</para></summary>
        public bool Silent
        {
            get { return silent; }
            set
            {
                if (silent == value) return;
                silent = value;
                // Whichever way it just switched, the old mechanism is mid-flight.
                bool wasReading = reading;
                Stop();
                if (wasReading) SpeakCurrent();
            }
        }
        private bool silent;

        /// <summary>Words per minute for silent reading. Fingers and eyes are not
        /// ears, so this is not the speech rate and does not travel with it.</summary>
        public int SilentWpm { get { return silentWpm; } set { silentWpm = value; RestartPace(); } }
        private int silentWpm = 180;
        private System.Windows.Forms.Timer pace;

        /// <summary>Raised whenever the current sentence changes (read the
        /// position properties for the new values).</summary>
        public event Action PositionChanged;
        /// <summary>Raised when the last sentence finishes (natural end).</summary>
        public event Action Finished;

        public int Count { get { return sentenceText.Count; } }
        public int CurrentSentence { get { return index; } }
        public int TotalChars { get { return fullText.Length; } }
        public int CharPosition
        {
            get { return (index >= 0 && index < sentenceStart.Count) ? sentenceStart[index] : 0; }
        }
        /// <summary>The whole book as the reader holds it — cleaned, and so in the
        /// same character coordinates as CharPosition. The reading surface needs
        /// it because it shows the book and moves a selection through it rather
        /// than being handed one sentence at a time.</summary>
        public string FullText { get { return fullText ?? ""; } }

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

        /// <summary>The book being read, for the backends that keep what they
        /// make. Empty while nothing is loaded — and it must be CLEARED then, or
        /// the next book's speech would be filed under the last one's.</summary>
        public string BookFolder
        {
            set
            {
                var keeper = backend as ISpeechCacheAware;
                if (keeper != null) keeper.BookFolder = value ?? "";
            }
        }

        public List<string> GetVoices() { return backend.GetVoices(); }
        /// <summary>Voices with their vendor and the language they speak — what
        /// picking a voice for a book's language needs.</summary>
        public List<(string Name, string Vendor, string Language)> GetVoiceInfos()
        {
            return backend.GetVoiceInfos();
        }
        public string CurrentVoice { get { return backend.CurrentVoiceName; } }
        public void SetVoice(string name) { backend.SelectVoice(name); RestartCurrent(); }
        public void SetRate(int r) { rate = r; backend.SetRate(r); RestartCurrent(); }
        public void SetVolume(int v) { backend.SetVolume(v); RestartCurrent(); }
        /// <summary>Volume for the sentences still to come, without re-speaking the
        /// one in progress. The sleep timer's fadeout uses it: restarting the
        /// sentence on every step of a 45-second ramp would be unbearable, so the
        /// fade steps down at each sentence boundary instead.</summary>
        public void SetVolumeQuiet(int v) { backend.SetVolume(v); }
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
        /// resets to the start. Stops any current reading.
        ///
        /// <para><paramref name="alreadyClean"/> is true for a book whose
        /// content.txt was cleaned at import, with its heading and page offsets
        /// moved to match. Cleaning it again would remove a few more characters and
        /// shift the reader against those stored marks — the drift this whole
        /// arrangement exists to prevent — so it is left exactly as written.</para></summary>
        public void LoadText(string text, bool alreadyClean = false)
        {
            Stop();
            // Tidy the raw text (collapse big gaps, de-hyphenate, strip noise)
            // so TTS reads smoothly without long pauses or "stumbling".
            fullText = alreadyClean ? (text ?? "") : TextCleaner.Clean(text);
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
            if (pace != null) pace.Stop();
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
            if (pace != null) pace.Stop();
            // Nothing was ever handed to the backend in silent mode, so there is
            // nothing there to cancel — and cancelling an idle synth is exactly
            // the autoplay race this guard exists to avoid.
            if (active && !silent) backend.Cancel();
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
        /// to the sentence containing the target offset. Returns whether the WHOLE
        /// step fitted in the book: a jump that runs off either end still lands on
        /// it, but says so, which is how the player knows to sound its "that is as
        /// far as it goes" beep.</summary>
        public bool SeekChars(int delta)
        {
            int want = CharPosition + delta;
            SeekToChar(want);
            return want >= 0 && want <= TotalChars;
        }

        /// <summary>Jump by an estimated number of seconds of speech (time seek),
        /// using the current rate to gauge characters per second.</summary>
        public bool SeekSeconds(int seconds)
        {
            int cps = BaseCharsPerSecond + rate; // ~15 at rate 0
            if (cps < 5) cps = 5;
            return SeekChars(seconds * cps);
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

        /// <summary>The opening words of the sentence a character offset falls in.
        /// A bookmark in a text book is a number of characters, which tells the
        /// reader nothing — the words it sits on tell them exactly where they
        /// were. Ends with an ellipsis when the sentence goes on.</summary>
        public string SnippetAt(int charOffset, int words)
        {
            if (sentenceStart.Count == 0 || words <= 0) return "";
            int target = 0;
            for (int i = sentenceStart.Count - 1; i >= 0; i--)
                if (sentenceStart[i] <= charOffset) { target = i; break; }

            // Splitting a real book leaves the odd fragment that is nothing but
            // punctuation (a stray full stop after a page number, say). Naming the
            // place "." helps nobody, so look ahead a little for words.
            string sentence = "";
            for (int i = target; i < sentenceText.Count && i <= target + 3; i++)
            {
                sentence = CleanEdges(sentenceText[i]);
                if (HasWordCharacter(sentence)) break;
            }
            if (!HasWordCharacter(sentence)) return "";

            string[] parts = sentence.Split(new[] { ' ', '\t', '\n', '\r' },
                                            StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= words) return sentence;
            return string.Join(" ", parts, 0, words) + "…";
        }

        // Trim() alone leaves the invisible characters that litter converted books
        // (zero-width spaces, joiners, a stray BOM), which then show up as a gap in
        // front of the snippet.
        private static string CleanEdges(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int a = 0, b = s.Length - 1;
            while (a <= b && IsInvisible(s[a])) a++;
            while (b >= a && IsInvisible(s[b])) b--;
            return s.Substring(a, b - a + 1);
        }

        private static bool IsInvisible(char c)
        {
            return char.IsWhiteSpace(c)
                || (c >= '\u200B' && c <= '\u200F')   // zero-width space … RTL mark
                || c == '\uFEFF';                     // BOM left in mid-file
        }

        private static bool HasWordCharacter(string s)
        {
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        /// <summary>The user's speech dictionaries in force for the current voice
        /// and book language, most specific first. Null or empty = nothing to
        /// apply, which is how NBR ships.</summary>
        public List<SpeechDictionary> Dictionaries { get; set; }

        /// <summary>The sentence as the engine should hear it: the book's own words
        /// with the user's dictionary applied. This is the ONLY place the text is
        /// rewritten — what is stored, displayed, brailled or counted for a
        /// position is always the book's own.</summary>
        /// <summary>Every sentence of the book exactly as the engine will hear it
        /// — the pronunciation dictionary already applied.
        ///
        /// <para>For preparing a book ahead of the reader. It has to come from
        /// HERE and not from the raw sentences, because the speech cache is keyed
        /// on what reaches the engine: filled from the untouched text, every
        /// entry would sit under a key nothing ever looks up, and a book prepared
        /// for forty minutes would be paid for a second time on the first
        /// listening.</para></summary>
        public List<string> SpokenSentences()
        {
            var list = new List<string>(sentenceText.Count);
            foreach (string s in sentenceText) list.Add(Spoken(s));
            return list;
        }

        private string Spoken(string sentence)
        {
            return Dictionaries == null || Dictionaries.Count == 0
                ? sentence : SpeechDictionaries.Apply(Dictionaries, sentence);
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
            if (silent) { RaisePosition(); RestartPace(); return; }

            // SPEAK FIRST, then say the position moved. The order used to be the
            // other way round, and it cost the start of every sentence.
            //
            // PositionChanged is what drives the reading surface, and everything
            // hanging off it — the caret move, a ScrollToCaret over a whole book
            // of text, the braille push, the screen-reader announcement. Raised
            // BEFORE the utterance was handed to the backend, all of that landed
            // in the instant between deciding to speak and speaking, and NBR's
            // voice comes from the 32-bit satellite over IPC, which needs the
            // message pump in exactly that instant. Gordan heard it as the
            // beginning of sentences being stolen.
            //
            // Nothing downstream wants the earlier order: the position is more
            // truthful raised here anyway, since it now means "this sentence has
            // started" rather than "is about to".
            backend.Speak(Spoken(sentenceText[index]));
            RaisePosition();
            // Hint the next sentence so a backend that renders before playing can
            // have it ready — otherwise every sentence starts with a synthesis gap.
            if (index + 1 < sentenceText.Count)
                backend.PreRender(Spoken(sentenceText[index + 1]));
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

        /// <summary>How long the current sentence should be left on screen, and
        /// the timer that ends it.
        ///
        /// <para>Timed by WORDS, not by characters: words per minute is the unit
        /// the reader is given and the one every reading-speed figure they know
        /// is quoted in. A sentence of one word still gets a floor, or a book of
        /// short lines would flicker past faster than anything could be read from
        /// a display, and the last thing a braille reader needs is a line that
        /// leaves before their hand arrives.</para></summary>
        private void RestartPace()
        {
            if (pace != null) pace.Stop();
            if (!silent || !reading) return;
            if (index < 0 || index >= sentenceText.Count) return;

            int words = 0;
            foreach (string w in sentenceText[index].Split((char[])null,
                                                           StringSplitOptions.RemoveEmptyEntries))
                if (w.Length > 0) words++;
            if (words < 1) words = 1;

            int wpm = silentWpm > 0 ? silentWpm : 180;
            int ms = (int)(words * 60000.0 / wpm);
            if (ms < 400) ms = 400;

            if (pace == null)
            {
                pace = new System.Windows.Forms.Timer();
                // The backend raises completion on the UI thread and everything
                // downstream assumes that; a Forms timer keeps the promise, where
                // a threading timer would move sentence changes onto a pool
                // thread and put the reading surface at risk.
                pace.Tick += (s, e) => { pace.Stop(); OnCompleted(false); };
            }
            pace.Interval = ms;
            pace.Start();
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
            try { if (pace != null) { pace.Stop(); pace.Dispose(); pace = null; } } catch { }
            try { backend.Dispose(); } catch { }
        }
    }
}
