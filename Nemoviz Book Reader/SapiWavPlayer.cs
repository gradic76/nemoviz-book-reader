using System;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Plays already-rendered speech audio (a WAV) through SAPI's
    /// <c>SpVoice.SpeakStream</c>, so it comes out of the sound card chosen in
    /// Settings → Device and can be stopped the instant the reader asks.
    ///
    /// Why SAPI and not <c>SoundPlayer</c>: SoundPlayer always uses the system
    /// default device and cannot be interrupted reliably mid-buffer. SAPI already
    /// owns an output-token concept (<c>AudioOutput</c>), the same one
    /// <see cref="Sapi5Backend"/> maps the mpv device id onto, and
    /// <c>SVSFPurgeBeforeSpeak</c> stops playback immediately. It costs nothing
    /// extra: SAPI is present wherever the app runs.
    ///
    /// This file is compiled into BOTH the x64 app and the 32-bit host
    /// (TtsHost32.exe), which is why it carries the WAV helpers too — everything
    /// that plays synthesized audio needs them.
    /// </summary>
    public class SapiWavPlayer : IDisposable
    {
        private const int SVSFlagsAsync = 1;
        private const int SVSFPurgeBeforeSpeak = 2;
        private const int SRSEIsSpeaking = 2;
        private const int SSFMOpenForRead = 0;

        private readonly dynamic voice;      // SAPI.SpVoice used only for playback
        private readonly object gate = new object();
        private dynamic stream;              // the SpFileStream being played
        private string playingPath;          // temp file to delete when done
        private bool started;                // SpeakStream issued
        private bool sawPlaying;             // SAPI reached the speaking state
        private int startTick;
        private int audioMs;                 // length of this utterance, 0 if unknown

        /// <summary>TEMPORARY diagnostic hook. Null unless someone is measuring,
        /// and a null check is the entire cost when nobody is — which matters,
        /// because these points sit in the audio path. Set by the player to an
        /// in-memory recorder; left null in the 32-bit host, which compiles this
        /// same file. Remove with the rest of the diagnostics.</summary>
        internal static Action<string> Log;
        private void L(string what)
        {
            Action<string> f = Log;
            if (f == null) return;
            try { f(string.Format("PLAYER {0,-16} +{1,5} ms  audio={2} ms  sawPlaying={3}",
                                  what, Environment.TickCount - startTick, audioMs, sawPlaying)); }
            catch { }
        }

        private string desiredDeviceId = "";
        private string appliedDeviceId = null;

        public SapiWavPlayer()
        {
            Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (t == null) throw new NotSupportedException("SAPI.SpVoice not available");
            voice = Activator.CreateInstance(t);
        }

        /// <summary>The output to play on, as an mpv device id
        /// ("wasapi/{guid}"); empty or "auto" means the system default. Applied
        /// on the next utterance.</summary>
        public void SetDevice(string mpvDeviceId)
        {
            lock (gate) desiredDeviceId = mpvDeviceId ?? "";
        }

        private void ApplyDeviceIfNeeded()
        {
            if (string.Equals(desiredDeviceId, appliedDeviceId, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string guid = ExtractGuid(desiredDeviceId);
                if (guid == null)
                {
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

        /// <summary>Starts playing a WAV file. Returns false if it couldn't start —
        /// the caller then treats the utterance as finished rather than hanging.
        /// <paramref name="deleteWhenDone"/> is for a temp file we own.</summary>
        public bool PlayFile(string wavPath, bool deleteWhenDone)
        {
            return PlayFile(wavPath, deleteWhenDone, 0);
        }

        /// <param name="knownDurationMs">How long the audio actually is, when the
        /// caller knows (it just rendered it). Nought means unknown.</param>
        public bool PlayFile(string wavPath, bool deleteWhenDone, int knownDurationMs)
        {
            lock (gate)
            {
                audioMs = knownDurationMs;
                StopLocked();
                try
                {
                    ApplyDeviceIfNeeded();
                    dynamic fs = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream"));
                    fs.Open(wavPath, SSFMOpenForRead, false);
                    stream = fs;
                    playingPath = deleteWhenDone ? wavPath : null;
                    started = true;
                    sawPlaying = false;
                    startTick = Environment.TickCount;
                    voice.SpeakStream(fs, SVSFlagsAsync);
                    L("SpeakStream");
                    return true;
                }
                catch
                {
                    started = false;
                    CloseStreamLocked();
                    return false;
                }
            }
        }

        /// <summary>Plays a WAV held in memory (what a synthesizer hands back) by
        /// staging it in a temp file — SAPI reads a stream, not a byte array.</summary>
        public bool Play(byte[] wav)
        {
            if (wav == null || wav.Length <= 44) return false;
            string path;
            try
            {
                path = Path.Combine(Path.GetTempPath(),
                    "nbr-tts-" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(path, wav);
            }
            catch { return false; }
            KeepForInspection(wav);
            return PlayFile(path, true, WavDurationMs(wav));
        }

        /// <summary>TEMPORARY. Keeps the first few buffers actually handed to the
        /// sound card, so their CONTENT can be measured.
        ///
        /// <para>It lives HERE, not in the 32-bit host where it was first put,
        /// because that was the wrong side: the host received no SPEAK at all
        /// during the run, the voice in use being an in-process one. This file is
        /// compiled into both, so wherever the reading goes, the audio is
        /// caught.</para>
        ///
        /// <para>The file name carries the process id, so the two cannot collide
        /// and it is plain which side produced which.</para></summary>
        private static int keptCount;
        private static void KeepForInspection(byte[] wav)
        {
            try
            {
                if (keptCount >= 14) return;
                int n = keptCount++;
                string dir = Path.Combine(Path.GetTempPath(), "NBR-wavs");
                Directory.CreateDirectory(dir);
                string name = string.Format("{0:00}-pid{1}.wav", n,
                    System.Diagnostics.Process.GetCurrentProcess().Id);
                File.WriteAllBytes(Path.Combine(dir, name), wav);
                Action<string> f = Log;
                if (f != null) try { f("KEPT " + name + "  " + WavDurationMs(wav) + " ms"); } catch { }
            }
            catch { }
        }

        /// <summary>How many milliseconds of audio a rendered WAV holds, from its
        /// own header. Used by <see cref="IsPlaying"/> to know that an utterance
        /// CANNOT be finished yet — see the note there. Nought if the header is
        /// not the plain PCM shape we write.</summary>
        private static int WavDurationMs(byte[] wav)
        {
            try
            {
                if (wav == null || wav.Length < 44) return 0;
                // Byte rate lives at offset 28 of a canonical RIFF/WAVE header.
                int byteRate = wav[28] | (wav[29] << 8) | (wav[30] << 16) | (wav[31] << 24);
                if (byteRate <= 0) return 0;
                long data = wav.Length - 44;
                if (data <= 0) return 0;
                long ms = data * 1000L / byteRate;
                return ms > 0 && ms < 3600000 ? (int)ms : 0;
            }
            catch { return 0; }
        }

        /// <summary>True while audio is still coming out. False once SAPI has left
        /// the speaking state (with a short grace at the start, before it has
        /// entered it).</summary>
        public bool IsPlaying
        {
            get
            {
                lock (gate)
                {
                    if (!started) return false;
                    int rs;
                    try { rs = (int)voice.Status.RunningState; } catch { return false; }
                    if (rs == SRSEIsSpeaking)
                    {
                        if (!sawPlaying) { sawPlaying = true; L("first-speaking"); }
                        return true;
                    }

                    // NOT speaking, and we have never seen it speak. That means
                    // "has not started yet", NOT "has finished" — and the two are
                    // indistinguishable from SAPI's running state alone.
                    //
                    // This used to be a flat 400 ms of grace, and that is the bug
                    // Gordan spent an afternoon hearing. Starting an utterance
                    // means writing the WAV to a temp file and having SAPI open it
                    // as a stream, and on a LONG sentence that is a bigger file
                    // and takes longer. Past 400 ms the player declared the
                    // sentence finished before a sound had come out: the caller's
                    // wait loop fell straight through, ReleaseFinished deleted the
                    // very file SAPI was about to read, and the parent moved to
                    // the next sentence — which purged this one. What survived was
                    // the tail. The first word was gone.
                    //
                    // The audio's own length settles it: an utterance cannot be
                    // finished before it could physically have played. Where the
                    // caller rendered the WAV it tells us how long it is, and the
                    // old 400 ms remains only as the floor for the file-open, and
                    // as the whole answer when the length is unknown.
                    if (!sawPlaying)
                    {
                        bool waiting = Environment.TickCount - startTick < 400 + audioMs;
                        if (!waiting) L("gave-up-waiting");
                        return waiting;
                    }
                    L("ended");          // SAPI has left the speaking state
                    return false;
                }
            }
        }

        /// <summary>Stops playback immediately (SAPI purges what is queued).</summary>
        public void Stop() { lock (gate) StopLocked(); }

        private void StopLocked()
        {
            if (started)
            {
                L("PURGE");
                try { voice.Speak("", SVSFPurgeBeforeSpeak | SVSFlagsAsync); } catch { }
                started = false;
            }
            CloseStreamLocked();
        }

        private void CloseStreamLocked()
        {
            if (stream != null)
            {
                try { stream.Close(); } catch { }
                stream = null;
            }
            if (playingPath != null)
            {
                try { File.Delete(playingPath); } catch { }
                playingPath = null;
            }
        }

        /// <summary>Called when playback has finished naturally, to release the
        /// stream and the temp file.</summary>
        public void ReleaseFinished()
        {
            lock (gate)
            {
                L("ReleaseFinished");
                started = false;
                CloseStreamLocked();
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice); } catch { }
        }

        // Pulls the WASAPI endpoint guid "{…}" out of an mpv id like
        // "wasapi/{0.0.0.0}.{guid}" or "wasapi/{guid}". Null for auto/empty.
        internal static string ExtractGuid(string mpvId)
        {
            if (string.IsNullOrEmpty(mpvId)) return null;
            int last = mpvId.LastIndexOf('{');
            int close = last >= 0 ? mpvId.IndexOf('}', last) : -1;
            if (last < 0 || close < 0) return null;
            return mpvId.Substring(last, close - last + 1);
        }

        /// <summary>
        /// Cuts the silence engines leave at the end of a rendered utterance — Zira
        /// pads roughly three quarters of a second — which would otherwise be played
        /// out in full and heard as a long gap between sentences. A short tail is
        /// kept so the last word doesn't sound clipped. Returns the buffer unchanged
        /// if the WAV isn't the plain 8/16-bit PCM shape we understand.
        /// </summary>
        public static byte[] TrimTrailingSilence(byte[] w)
        {
            try
            {
                if (w == null || w.Length < 44) return w;
                if (System.Text.Encoding.ASCII.GetString(w, 0, 4) != "RIFF") return w;
                int rate = BitConverter.ToInt32(w, 24);
                int channels = BitConverter.ToInt16(w, 22);
                int bits = BitConverter.ToInt16(w, 34);
                if ((bits != 16 && bits != 8) || channels < 1 || rate <= 0) return w;

                int pos = 12, dataOff = -1, dataLen = 0;
                while (pos + 8 <= w.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(w, pos, 4);
                    int len = BitConverter.ToInt32(w, pos + 4);
                    if (len < 0) return w;
                    if (id == "data") { dataOff = pos + 8; dataLen = len; break; }
                    pos += 8 + len + (len & 1);
                }
                if (dataOff < 0) return w;
                if (dataOff + dataLen > w.Length) dataLen = w.Length - dataOff;

                int frame = (bits / 8) * channels;
                if (frame <= 0 || dataLen < frame) return w;

                int last = dataLen;
                for (int i = dataLen - frame; i >= 0; i -= frame)
                {
                    int s = bits == 16 ? BitConverter.ToInt16(w, dataOff + i) : (w[dataOff + i] - 128) << 8;
                    if (Math.Abs(s) > 150) { last = i + frame; break; }
                }
                int keep = last + (rate / 25) * frame;                 // ~40 ms of tail
                if (keep >= dataLen) return w;                         // nothing worth cutting
                if (dataLen - keep < (rate / 10) * frame) return w;     // under 100 ms — leave it

                byte[] outBuf = new byte[dataOff + keep];
                Buffer.BlockCopy(w, 0, outBuf, 0, dataOff + keep);
                Buffer.BlockCopy(BitConverter.GetBytes(keep), 0, outBuf, dataOff - 4, 4);        // data size
                Buffer.BlockCopy(BitConverter.GetBytes(outBuf.Length - 8), 0, outBuf, 4, 4);     // RIFF size
                return outBuf;
            }
            catch { return w; }
        }
    }
}
