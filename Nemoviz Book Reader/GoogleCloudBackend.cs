using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The Google Cloud voices as an ordinary speech backend.
    ///
    /// <para>Built on <see cref="OneCoreBackend"/>, almost line for line, because
    /// the two do the same thing: render a sentence to a WAV in memory, then hand
    /// it to <see cref="SapiWavPlayer"/>, which plays it on the sound card chosen
    /// in Settings and can purge it instantly. Everything that touches the player
    /// runs on the UI thread through a timer; only the rendering is on a worker.
    /// That shape is what made an Azure or Google backend "no new architecture"
    /// when it was first looked at, and it held.</para>
    ///
    /// <para><b>What is different is that rendering goes over the network</b> —
    /// measured at about 0.8 s a sentence. So the look-ahead is not an
    /// optimisation here, it is the whole difference between reading and
    /// stuttering: <see cref="PreRender"/> fetches the next sentence while this
    /// one plays, and <see cref="TtsReader"/> already calls it.</para>
    ///
    /// <para><b>It reports its voices whatever the reader's "use cloud voices"
    /// setting says, and that is deliberate.</b> The switch governs what the
    /// PICKER offers, not what can be played — <see cref="CompositeSpeechBackend"/>
    /// learns which backend owns which voice once, when it is built, so a silent
    /// backend would leave a book that already has a cloud voice with nothing
    /// able to speak it. Settings filters them out always, Properties unless the
    /// switch is on.</para>
    /// </summary>
    public class GoogleCloudBackend : ISpeechBackend, ISpeechCacheAware
    {
        /// <summary>Where to keep what is made. Set when a book is loaded; empty
        /// while nothing is loaded, and then nothing is kept — a test utterance
        /// from Settings belongs to no book.</summary>
        public string BookFolder { get; set; }

        /// <summary>The fastest this voice may be driven, and it is the player's
        /// own ceiling rather than Google's. Gordan, 2026-08-15, after trying it:
        /// *"do 4 x vjerojatno neće biti smisla ali možemo ostaviti do 3 x kao i
        /// sve ostalo"* — the transport has run 50–300% since it was built, and a
        /// voice that could go faster than the player would be the odd one
        /// out.</summary>
        private const double TopSpeed = 3.0;

        // mpv rather than SAPI's player, and the cache is the whole reason: audio
        // kept on disk must have no speed printed into it, so the speeding up
        // happens here instead — scaletempo2, pitch intact, and it applies to
        // what is already sounding.
        private readonly MpvClipPlayer player = new MpvClipPlayer();
        private readonly System.Windows.Forms.Timer poll;

        // display name -> (google id, language)
        private readonly List<(string Name, string Google, string Language)> voices =
            new List<(string, string, string)>();

        private string currentVoice = "";
        private string currentGoogle = "";
        private string currentLanguage = "";
        private int rate;
        private int volume = 100;

        private volatile bool speaking, cancelled, paused;
        private volatile bool pendingReady;
        private volatile byte[] pendingWav;
        private bool playbackStarted;
        private volatile int generation;

        private readonly object aheadLock = new object();
        private string aheadText;
        private byte[] aheadWav;

        public event Action<bool> Completed;

        public GoogleCloudBackend()
        {
            LoadVoices();
            poll = new System.Windows.Forms.Timer { Interval = 60 };
            poll.Tick += Poll_Tick;
        }

        private void LoadVoices()
        {
            voices.Clear();
            if (!GoogleCloudVoices.Have) return;
            foreach (var v in GoogleCloudVoices.Voices())
            {
                string display = GoogleCloudVoices.DisplayName(v.Name, v.Language);
                voices.Add((display, v.Name, v.Language));
            }
            if (voices.Count > 0) SelectVoice(voices[0].Name);
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
            foreach (var v in voices)
                list.Add((v.Name, GoogleCloudVoices.Vendor, v.Language));
            return list;
        }

        public string CurrentVoiceName { get { return currentVoice; } }

        public void SelectVoice(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var v in voices)
                if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    currentVoice = v.Name;
                    currentGoogle = v.Google;
                    currentLanguage = v.Language;
                    DropAhead();
                    return;
                }

            // Not in the list — but a book saved with a cloud voice must still be
            // able to speak it when the catalogue has not been fetched on this
            // machine yet. The name carries everything the request needs.
            string google, lang;
            if (GoogleCloudVoices.Split(name, out google, out lang))
            {
                currentVoice = name;
                currentGoogle = google;
                currentLanguage = lang;
                DropAhead();
            }
        }

        // NEITHER DROPS THE LOOK-AHEAD ANY MORE, and that is the change worth
        // noticing. While speed and volume were printed into the audio, a
        // sentence fetched a moment earlier was stale the instant either moved,
        // so every nudge threw away work already paid for. Both are properties of
        // the PLAYER now, so they apply to what is already sounding and to
        // everything held ahead of it.
        public void SetRate(int r) { rate = Clamp(r, -10, 10); player.SetSpeed(Speed); }
        public void SetVolume(int v) { volume = Clamp(v, 0, 100); player.SetVolume(volume); }

        /// <summary>Ignored, and it has to be: Google documents pitch control as
        /// unavailable for hr-HR, so sending it would make a control that does
        /// nothing look like a control that is broken. The dialog dims it.</summary>
        public void SetPitch(int percent) { }

        public void SetAudioDevice(string deviceId) { player.SetDevice(deviceId); }

        /// <summary>NBR's −10…10 as Google's multiplier.
        ///
        /// <para><b>Geometric, not linear, and it has to be.</b> Rate 0 is the
        /// voice's own natural speed — Gordan's rule, that every voice sits where
        /// it was built to read until someone moves it — and the top is 3×. A
        /// straight step of that size would put the bottom of the scale at −1.0,
        /// which is not a speed. Halving and doubling are what the ear hears as
        /// symmetric anyway, so ±10 comes out 3× and one third.</para>
        ///
        /// <para>The WPM-versus-multiplier question is still open — Gordan is
        /// weighing showing every voice on a plain 1.0, 1.1, 1.2 scale. It changes
        /// this one expression and nothing else.</para></summary>
        private double Speed { get { return Math.Pow(TopSpeed, rate / 10.0); } }

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
                    byte[] wav = Render(t);
                    if (myGen != generation) return;      // superseded while fetching
                    pendingWav = wav;
                    pendingReady = true;
                }) { IsBackground = true };
                worker.Start();
            }
            poll.Start();
        }

        /// <summary>Fetches the next sentence while this one plays. Over a network
        /// this is what keeps the reading continuous rather than merely tidy.</summary>
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
                byte[] wav = Render(text);
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

        /// <summary>One sentence, however long, as one WAV.
        ///
        /// <para>A request may carry 5000 BYTES and a sentence almost never comes
        /// near that — but "almost never" is how a book with one runaway paragraph
        /// becomes a book that silently skips it. An over-long piece is split and
        /// the audio joined, so the reader hears the whole sentence and the
        /// position still advances by exactly one.</para></summary>
        private byte[] Render(string text)
        {
            if (string.IsNullOrEmpty(currentGoogle)) return null;
            List<string> parts = SplitForRequest(text);

            // ALREADY PAID FOR ONCE. The stored piece is MP3 and mpv plays that
            // as readily as a WAV, so a second reading of a book costs nothing
            // and needs no network at all.
            byte[] kept = SpeechCache.Get(BookFolder, currentVoice, text);
            if (kept != null) return kept;

            byte[] wav;
            if (parts.Count == 1)
                wav = GoogleCloudVoices.Synthesize(parts[0], currentGoogle, currentLanguage,
                                                   NaturalSpeed, NaturalVolumeDb);
            else
            {
                var wavs = new List<byte[]>();
                foreach (string p in parts)
                {
                    byte[] w = GoogleCloudVoices.Synthesize(p, currentGoogle, currentLanguage,
                                                            NaturalSpeed, NaturalVolumeDb);
                    if (w == null) return null;      // a hole in a sentence is worse than none of it
                    wavs.Add(w);
                }
                // Joined first, trimmed after: the silence between two halves of
                // one sentence is the pause its punctuation asks for.
                wav = Concat(wavs);
            }
            wav = SapiWavPlayer.TrimTrailingSilence(wav);

            // Kept on the way past, on the worker thread that made it — and what
            // gets PLAYED is what was kept, so the first hearing of a sentence
            // and every later one are the same audio. A failure to store is not a
            // failure to read: the sentence is spoken from the original and
            // simply made again next time.
            if (wav == null) return null;
            byte[] kept2 = SpeechCache.Put(BookFolder, currentVoice, text, wav);
            return kept2 ?? wav;
        }

        /// <summary>Made and played at the voice's OWN pace and level, always.
        ///
        /// <para>They used to be sent to Google, printed into the audio that came
        /// back. That is fine until the audio is kept: a sentence stored at one
        /// speed is useless at another, so a reader who nudged the speed once
        /// would have stranded a whole prepared book and paid for it again. Speed
        /// and volume now happen at playback, where <see cref="MpvClipPlayer"/>
        /// can change them on what is already sounding.</para></summary>
        private const double NaturalSpeed = 1.0;
        private const double NaturalVolumeDb = 0.0;

        /// <summary>Cuts a piece too big for one request, preferring a sentence
        /// end, then a space, and only then a hard cut. Measured in UTF-8 bytes,
        /// because the limit is bytes and Croatian is not ASCII.</summary>
        internal static List<string> SplitForRequest(string text)
        {
            var parts = new List<string>();
            string s = text ?? "";
            while (s.Length > 0)
            {
                if (Encoding.UTF8.GetByteCount(s) <= GoogleCloudVoices.MaxRequestBytes)
                {
                    parts.Add(s);
                    break;
                }
                int take = s.Length;
                while (take > 0 && Encoding.UTF8.GetByteCount(s.Substring(0, take))
                                   > GoogleCloudVoices.MaxRequestBytes)
                    take = take * 9 / 10;

                int cut = s.LastIndexOfAny(new[] { '.', '!', '?', ';' }, Math.Max(0, take - 1));
                if (cut < take / 2) cut = s.LastIndexOf(' ', Math.Max(0, take - 1));
                if (cut < take / 2) cut = take - 1;

                parts.Add(s.Substring(0, cut + 1));
                s = s.Substring(cut + 1);
            }
            return parts;
        }

        /// <summary>Joins LINEAR16 WAVs of the same shape: the first one's 44-byte
        /// header, then everybody's samples, with the two length fields put right.
        /// They come from one voice at one sample rate, so the formats cannot
        /// disagree.</summary>
        internal static byte[] Concat(List<byte[]> wavs)
        {
            const int Header = 44;
            if (wavs == null || wavs.Count == 0) return null;
            if (wavs.Count == 1) return wavs[0];

            int data = 0;
            foreach (byte[] w in wavs)
                if (w != null && w.Length > Header) data += w.Length - Header;

            byte[] outp = new byte[Header + data];
            Buffer.BlockCopy(wavs[0], 0, outp, 0, Header);
            int at = Header;
            foreach (byte[] w in wavs)
            {
                if (w == null || w.Length <= Header) continue;
                Buffer.BlockCopy(w, Header, outp, at, w.Length - Header);
                at += w.Length - Header;
            }
            Put(outp, 4, outp.Length - 8);      // RIFF chunk size
            Put(outp, 40, data);                // data chunk size
            return outp;
        }

        private static void Put(byte[] b, int at, int value)
        {
            b[at] = (byte)(value & 0xFF);
            b[at + 1] = (byte)((value >> 8) & 0xFF);
            b[at + 2] = (byte)((value >> 16) & 0xFF);
            b[at + 3] = (byte)((value >> 24) & 0xFF);
        }

        private void Poll_Tick(object sender, EventArgs e)
        {
            if (!speaking || paused) return;

            if (!playbackStarted)
            {
                if (!pendingReady) return;             // still fetching
                byte[] wav = pendingWav;
                pendingWav = null;
                pendingReady = false;
                // Nothing to play — the network refused, or the credential died —
                // counts as a finished utterance so the reader moves on instead of
                // stopping dead in the middle of a book.
                if (wav == null || !player.Play(wav)) { Finish(); return; }
                playbackStarted = true;
                return;
            }
            if (!player.IsPlaying) Finish();
        }

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

        public void Pause() { paused = true; }
        public void Resume() { paused = false; }

        public void Cancel()
        {
            generation++;
            cancelled = true;
            speaking = false;
            playbackStarted = false;
            paused = false;
            poll.Stop();
            DropAhead();
            player.Stop();
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
