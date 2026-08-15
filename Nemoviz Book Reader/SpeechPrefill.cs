using System;
using System.Collections.Generic;
using System.Threading;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Makes the rest of a book's speech while the reader listens to the
    /// beginning of it.
    ///
    /// <para><b>Gordan's idea, and it is better than the separate preparation
    /// step it replaces</b> (2026-08-15): *"mogu li ja krenuti slušati knjigu a
    /// da taj look ahead doslovno odjuri ahead do kraja knjige"*. Nobody waits
    /// forty minutes to start; reading begins at once and the book is finished
    /// behind them. Measured, synthesis runs about ten times faster than
    /// listening, so a nine-hour book is complete in something like
    /// three quarters of an hour.</para>
    ///
    /// <para><b>It is a deliberate act and never automatic.</b> Running ahead
    /// commits the reader to paying for the WHOLE book the moment they press
    /// play, where reading normally costs only what was heard — and the free
    /// allowance is about two average books a month, measured on this library at
    /// 472 000 characters a book. Someone who opens five to sample them would
    /// have spent the month.</para>
    ///
    /// <para><b>It never touches the backend the reader is listening through.</b>
    /// It has its own path to the service and writes only to the cache, so a
    /// sentence being fetched for the ear is never queued behind two hundred
    /// being fetched for the disk. The two meet only where they should: the
    /// reading finds pieces already made and plays them off the disk.</para>
    /// </summary>
    internal sealed class SpeechPrefill
    {
        /// <summary>A pause between requests, because there is no hurry.
        ///
        /// <para>Ten times faster than listening means about a hundred requests a
        /// minute at full tilt, and a service with a per-minute limit would
        /// answer that by refusing — which would cost the reading, not just the
        /// preparing. Half a second still finishes a nine-hour book inside an
        /// hour and asks for nothing anyone would notice.</para></summary>
        private const int RestMs = 500;

        private readonly string bookFolder;
        private readonly string voice;
        private readonly string googleName;
        private readonly string language;
        private readonly List<string> spoken;

        private Thread worker;
        private volatile bool stop;
        private volatile int done;
        private volatile int made;
        private volatile int failed;

        public int Done { get { return done; } }
        public int Made { get { return made; } }
        public int Failed { get { return failed; } }
        public int Total { get { return spoken.Count; } }
        public bool Running { get { return worker != null && worker.IsAlive; } }

        /// <summary>Fires when the run ends, however it ended.</summary>
        public event Action Finished;

        private SpeechPrefill(string bookFolder, string voice, string googleName,
                              string language, List<string> spoken)
        {
            this.bookFolder = bookFolder;
            this.voice = voice;
            this.googleName = googleName;
            this.language = language;
            this.spoken = spoken;
        }

        /// <summary>Builds one for a book, or null when there is nothing it could
        /// do — no cloud voice, no book, no sentences. Null is an answer the
        /// caller must handle rather than an error: preparing a book read by a
        /// local voice would spend a quarter of a gigabyte to save nothing, since
        /// that speech is free and faster than listening already.</summary>
        public static SpeechPrefill For(string bookFolder, string voiceName, List<string> spokenSentences)
        {
            if (string.IsNullOrEmpty(bookFolder) || spokenSentences == null || spokenSentences.Count == 0)
                return null;
            string google, lang;
            if (!GoogleCloudVoices.Split(voiceName, out google, out lang)) return null;
            if (!GoogleCloudVoices.Have) return null;
            return new SpeechPrefill(bookFolder, voiceName, google, lang, spokenSentences);
        }

        /// <summary>How much of this book is already made, so the reader is told
        /// what is left rather than what there is.</summary>
        public int AlreadyMade()
        {
            int n = 0;
            foreach (string s in spoken)
                if (SpeechCache.Has(bookFolder, voice, s)) n++;
            return n;
        }

        public void Start()
        {
            if (Running) return;
            stop = false;
            done = made = failed = 0;
            worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        public void Stop() { stop = true; }

        private void Run()
        {
            try
            {
                foreach (string s in spoken)
                {
                    if (stop) break;
                    done++;

                    // Already there — from an earlier run, or from the reader
                    // having listened past this point. Nothing is ever made twice.
                    if (string.IsNullOrWhiteSpace(s) || SpeechCache.Has(bookFolder, voice, s)) continue;

                    byte[] wav = GoogleCloudVoices.Synthesize(s, googleName, language, 1.0, 0.0);
                    if (wav == null) { failed++; }
                    else
                    {
                        wav = SapiWavPlayer.TrimTrailingSilence(wav);
                        if (SpeechCache.Put(bookFolder, voice, s, wav) != null) made++;
                        else failed++;
                    }

                    // Only after one that was actually fetched. Walking past
                    // thousands of pieces already on disk should take seconds,
                    // not an hour of politeness to a service nobody asked.
                    for (int slept = 0; slept < RestMs && !stop; slept += 50) Thread.Sleep(50);
                }
            }
            catch { }
            finally
            {
                Action f = Finished;
                if (f != null) try { f(); } catch { }
            }
        }
    }
}
