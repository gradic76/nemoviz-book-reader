using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>What a recording actually measures, so the six stages of §8d can
    /// be set from the book in front of the reader instead of from a default.
    ///
    /// <para><b>It runs when the reader switches sound processing ON, not at
    /// import</b> (Gordan, 2026-08-07: <i>"Ako Sound processing ne treba tj.
    /// čitatelj je zadovoljan zvukom ne rade se bespotrebne radnje na
    /// uvozu."</i>). CLAUDE.md §8d argued for import and that argument is
    /// superseded — its three reasons were about not disturbing PLAYBACK and
    /// about amortising the cost, and neither survives here. The measurement
    /// never touches the player: it runs in its own silent libmpv context and
    /// seeks where it likes, so "the opening moments give the wrong answer" is
    /// not a constraint, it is a choice of segment. And switching processing on
    /// already rebuilds mpv's filter graph, so the break §8d worried about is
    /// something the reader has just asked for. What is left is the plain point
    /// that most readers never turn processing on at all, and analysing every
    /// book at import spends their time on a question they never asked.</para>
    ///
    /// <para><b>No ffmpeg.exe, and that was worth proving before designing
    /// anything.</b> The shipped libmpv carries astats, ebur128 and
    /// aspectralstats, and their values are read back as an mpv property. Nothing
    /// new to install or ship. Verified through the real C API on real books,
    /// not by finding the strings in the binary.</para>
    ///
    /// <para><b>The numbers are read as a PROPERTY, never off the log</b> — see
    /// <see cref="SoundAnalyser"/>. The log route worked in a harness and would
    /// have failed in the running player every time, which is the kind of fault
    /// worth reading about before touching any of this.</para></summary>
    public sealed class SoundAnalysis
    {
        /// <summary>Integrated loudness, LUFS. Negative; −16 is a well-made
        /// audiobook, −30 is a quiet one.</summary>
        public double Lufs;

        /// <summary>Loudness range, LU. Small means evenly read, large means the
        /// quiet parts are much quieter than the loud ones.</summary>
        public double Lra;

        /// <summary>True peak, dBFS. Above about −0.1 the recording is already
        /// touching the ceiling.</summary>
        public double TruePeakDb;

        public double RmsDb = double.NaN;
        public double PeakDb = double.NaN;

        /// <summary>astats' own noise-floor estimate, dB, or NaN when it did not
        /// produce one — which is most of the time. <b>Measured over 103 real
        /// books: 49 % came back <c>-inf</c> and another 6 % had no such line at
        /// all.</b> So this is the secondary measure, not the one to build a
        /// decision on.</summary>
        public double NoiseFloorDb = double.NaN;

        /// <summary>The quietest RMS window in the segment, dB — astats' "RMS
        /// trough".
        ///
        /// <para><b>This was called "the real noise measure" here and that was
        /// WRONG.</b> The claim rested on one favourable reading. Measured across
        /// <b>2003 segments of six real books it is available in 0–1 % of
        /// them</b>, and the reason is structural: the trough is the MINIMUM over
        /// astats' windows, and in twenty seconds of speech there is almost
        /// always an instant of true digital silence, which makes it
        /// <c>-inf</c>. It is kept because where it does appear it is the right
        /// quantity, but nothing may lean on it.</para></summary>
        public double RmsTroughDb = double.NaN;

        /// <summary>How many of the segments produced ANY noise reading at all.
        ///
        /// <para>Without this the mean is a lottery: <see cref="SoundAnalyser"/>
        /// averages only the readings that exist, so a book where two segments in
        /// six hundred yield a noise figure has its denoise decided by those two
        /// — and their median came out at 122 dB, which is not a measurement of
        /// anything. Knowing the SHARE is what lets a rule decline to use a
        /// number it should not trust.</para></summary>
        public int NoiseSamples;

        /// <summary>Long runs of identical samples — a clipping tell.</summary>
        public double FlatFactor;

        /// <summary>RMS of everything below 300 Hz, dB. Its distance from
        /// <see cref="RmsDb"/> says whether the recording is muddy.</summary>
        public double LowBandDb = double.NaN;

        /// <summary>RMS of everything above 6 kHz, dB. Its distance from
        /// <see cref="RmsDb"/> says whether it is bright or sibilant.</summary>
        public double HighBandDb = double.NaN;

        /// <summary>Peak of each band, dB. Peak minus RMS in the sub-300 Hz band
        /// is the CREST of the low end: a plosive is a short low-frequency burst,
        /// which an average says nothing about.</summary>
        public double LowPeakDb = double.NaN;
        public double HighPeakDb = double.NaN;

        /// <summary>Spectral centroid, Hz — where the weight of the sound sits.
        /// Higher is brighter.
        ///
        /// <para><b>Recorded, and deliberately NOT used to decide anything —
        /// because as sampled here it is not stable enough.</b> Measured twice
        /// over the same six files with only the sampling points moved, it swung
        /// 1759→3553 on one and 3404→1352 on another, while the band ratio
        /// measuring the same property moved by at most 2.3 dB. The reason is
        /// structural: astats and ebur128 publish values accumulated over the
        /// whole segment, aspectralstats publishes PER FRAME, so a poll reads one
        /// essentially random frame.</para>
        ///
        /// <para>It would become usable by averaging every frame rather than the
        /// last of each poll, which needs the values collected as they pass
        /// rather than sampled. Worth doing if a decision ever needs it — it is
        /// the one quantity the reference tool and NBR compute the same way, so
        /// it is where that tool's 1500 Hz threshold could honestly be
        /// tested.</para></summary>
        public double CentroidHz = double.NaN;

        /// <summary>How many segments went into the averages. 0 means nothing was
        /// measured and every other field is meaningless.</summary>
        public int Segments;

        public bool Measured { get { return Segments > 0; } }

        /// <summary>Signal to noise, dB, or NaN when neither noise measure came
        /// back. The trough is preferred — see <see cref="RmsTroughDb"/>.
        ///
        /// <para><b>NaN rather than a number, deliberately.</b> A missing key
        /// used to read back as 0, which made the SNR come out as the RMS level
        /// itself — about −21 dB, a confident and entirely invented answer. Six
        /// books in the sample did exactly that.</para></summary>
        public double Snr
        {
            get
            {
                double noise = Usable(RmsTroughDb) ? RmsTroughDb
                             : Usable(NoiseFloorDb) ? NoiseFloorDb : double.NaN;
                return Usable(RmsDb) && !double.IsNaN(noise) ? RmsDb - noise : double.NaN;
            }
        }

        /// <summary>What share of the segments produced a noise reading. Below
        /// <see cref="SoundAdvisor.MinNoiseShare"/> the signal-to-noise figure is
        /// an average of a handful of freak readings and must not decide
        /// anything.</summary>
        public double NoiseShare
        {
            get { return Segments > 0 ? (double)NoiseSamples / Segments : 0; }
        }

        /// <summary>How far the low band PEAKS above its own average, dB. A
        /// plosive is a burst, so the average hides it and the crest does not.</summary>
        public double LowCrest
        {
            get { return Usable(LowPeakDb) && Usable(LowBandDb) ? LowPeakDb - LowBandDb : double.NaN; }
        }

        /// <summary>How far the sub-300 Hz band sits below the whole signal, dB.
        /// Smaller means more low end.</summary>
        public double LowBandBelow
        {
            get { return Usable(RmsDb) && Usable(LowBandDb) ? RmsDb - LowBandDb : double.NaN; }
        }

        /// <summary>How far the 6 kHz-and-up band sits below the whole signal,
        /// dB. Smaller means brighter.</summary>
        public double HighBandBelow
        {
            get { return Usable(RmsDb) && Usable(HighBandDb) ? RmsDb - HighBandDb : double.NaN; }
        }

        /// <summary>A finite dB reading, as opposed to a missing one or a
        /// <c>-inf</c>. Both occur, and neither may be averaged.</summary>
        internal static bool Usable(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        // ---- persistence -------------------------------------------------

        private const string Section = "SoundAnalysis";

        public void Load(IniFile ini)
        {
            Segments = (int)Read(ini, "Segments", 0);
            Lufs = Read(ini, "Lufs", 0);
            Lra = Read(ini, "Lra", 0);
            TruePeakDb = Read(ini, "TruePeak", 0);
            RmsDb = Read(ini, "Rms", 0);
            PeakDb = Read(ini, "Peak", 0);
            NoiseFloorDb = Read(ini, "NoiseFloor", double.NaN);
            RmsTroughDb = Read(ini, "RmsTrough", double.NaN);
            FlatFactor = Read(ini, "FlatFactor", 0);
            LowBandDb = Read(ini, "LowBand", double.NaN);
            HighBandDb = Read(ini, "HighBand", double.NaN);
            LowPeakDb = Read(ini, "LowPeak", double.NaN);
            HighPeakDb = Read(ini, "HighPeak", double.NaN);
            CentroidHz = Read(ini, "Centroid", double.NaN);
        }

        /// <summary>Kept in Book.ini by Gordan's instruction — the MEASUREMENTS,
        /// not only the levels they produced. A stored measurement is why the
        /// analysis runs once rather than on every visit to Properties, and it is
        /// what a technical read-out can show when someone asks why a stage is
        /// set the way it is.</summary>
        public void Save(IniFile ini)
        {
            Write(ini, "Segments", Segments);
            Write(ini, "Lufs", Lufs);
            Write(ini, "Lra", Lra);
            Write(ini, "TruePeak", TruePeakDb);
            Write(ini, "Rms", RmsDb);
            Write(ini, "Peak", PeakDb);
            Write(ini, "NoiseFloor", NoiseFloorDb);
            Write(ini, "RmsTrough", RmsTroughDb);
            Write(ini, "FlatFactor", FlatFactor);
            Write(ini, "LowBand", LowBandDb);
            Write(ini, "HighBand", HighBandDb);
            Write(ini, "LowPeak", LowPeakDb);
            Write(ini, "HighPeak", HighPeakDb);
            Write(ini, "Centroid", CentroidHz);
        }

        private static double Read(IniFile ini, string key, double dflt)
        {
            string s = ini.Read(Section, key, null);
            double v;
            // InvariantCulture, always: a decimal comma written on one machine
            // must not read back as a different number on another. Same rule as
            // sync.map (§8c).
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        /// <summary>A measure that was never taken is left OUT of the file, not
        /// written as a number. Reading a key that is not there gives NaN again,
        /// so "not measured" survives the round trip instead of coming back as a
        /// plausible reading.</summary>
        private static void Write(IniFile ini, string key, double v)
        {
            if (!Usable(v)) return;
            ini.Write(Section, key, v.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Turns a measurement into the six stage levels of §8d.
    ///
    /// <para><b>Every threshold here comes from OUR OWN distribution</b>, taken
    /// over 113 real audiobooks measured through the shipped analyser (Test
    /// naslovi + two OneDrive collections). The percentiles quoted against each
    /// rule are from that sweep, and the rules are written so that a book at the
    /// median of the library gets close to the defaults the dialog already
    /// shipped — the analysis should move a book that is unusual, not re-decide
    /// every book.</para>
    ///
    /// <para><b>The reference tool's numbers were NOT copied, and measurement is
    /// why.</b> §8d suggested taking SlušajKnjigu's thresholds as a free starting
    /// point — SNR 14 dB for denoise, centroid 1500 Hz, low-frequency ratio 0.55.
    /// Run against this sample, its 14 dB denoise threshold fires on <b>zero of
    /// 113 books</b>: the noisiest recording here measures 20.9 dB. Their SNR is
    /// a different quantity computed a different way, so the number is
    /// meaningless on our scale, and the centroid we cannot measure at all.
    /// A borrowed constant is only free if the two scales agree.</para>
    ///
    /// <para><b>What this sets is a starting point for the ear, not a verdict.</b>
    /// §8d's split stands: the measurement is mine, the judgement is
    /// Gordan's.</para></summary>
    public static class SoundAdvisor
    {
        /// <summary>How many of a book's segments must yield a noise reading
        /// before the signal-to-noise figure is allowed to decide anything.
        ///
        /// <para>Half, because that is the point at which the figure describes
        /// the recording rather than a few unusual moments in it — and because
        /// the SHARE converges with sampling where the mean of a handful of
        /// outliers does not, which is what makes the decision reproducible.
        /// Measured on six books: only one clears it (79 %); the rest sit at
        /// 0–33 % and fall to the damage rule, which is stable.</para></summary>
        public const double MinNoiseShare = 0.5;


        /// <summary>Sets the stages from the measurement. The master switch is
        /// not touched — the reader has just turned it on, which is what caused
        /// the analysis.</summary>
        public static void Apply(SoundAnalysis a, SoundSettings s)
        {
            if (a == null || s == null || !a.Measured) return;

            // Rumble. Speech carries nothing below 50 Hz, so this stage is always
            // on; how high it reaches depends on how much low end there is.
            // LowBandBelow over the sample: min 1.2, p25 2.9, median 3.7,
            // p75 4.6, max 9.2 dB. Smaller means more low end.
            s.HighpassEnabled = true;
            s.HighpassLevel = Pick(a.LowBandBelow, 2, new[] { 2.2, 2.9, 3.7, 4.6 }, true);

            // Noise. NO measurable noise at all is the commonest answer and it
            // means CLEAN, not unknown -- Gordan's reference recording, made in
            // a dead room, produces none. Where there is one, 35 dB below the
            // speech is what he calls "a little room, almost imperceptible", and
            // that single anchor is what sets this scale: 35 must be mild, not
            // maximum. An earlier version read the library's own median of 65 as
            // the "leave it alone" point and so applied FULL denoise at 35 -- to
            // a recording its owner had just called acceptable.
            if (SoundAnalysis.Usable(a.Snr) && a.NoiseShare >= MinNoiseShare)
            {
                s.DenoiseLevel = Pick(a.Snr, 0, new[] { 15.0, 22.0, 28.0, 34.0 }, true);
                s.DenoiseEnabled = a.Snr < 34;
            }
            // Otherwise the noise figure is not trustworthy, whether because no
            // segment produced one or because too few did. Measured over 2003
            // segments of six real books: four of them yield a noise reading in
            // 0 to 10 % of segments, and for two the whole decision rested on ONE
            // OR TWO readings out of six hundred whose median was 122 dB — a
            // number that is not a measurement of anything. A rule that averages
            // whatever happens to exist is a lottery, and it is why the same book
            // came out with denoise on or off depending on how many samples were
            // taken.
            else if (Damaged(a))
            {
                // No measurable noise, but the recording is measurably damaged.
                //
                // This is a CORRELATION and is labelled as one. Yesterday's
                // reading — "no measurable noise floor means clean" — is wrong:
                // Gordan asked for noise reduction on two recordings whose noise
                // this code cannot see at all, strong on one and medium on the
                // other, and a third got it automatically and he called the
                // result excellent. Three of the four damaged samples wanted it
                // and none of the good ones did. There is no mechanism behind
                // that, only the observation, so it takes a middle setting
                // rather than guessing a level from a number that does not
                // exist.
                s.DenoiseEnabled = true;
                s.DenoiseLevel = 2;
            }
            else
            {
                s.DenoiseEnabled = false;
            }

            // Sibilance. This fired on healthy recordings and is now the other
            // way round: BOTH of Gordan's good samples measure 17.8 and 20.0,
            // so anything at or above about 16 is a normal voice and must not be
            // touched. Only a genuinely harsh recording -- high band close to the
            // signal -- gets a de-esser.
            s.DeesserLevel = Pick(a.HighBandBelow, 0, new[] { 9.0, 11.0, 13.0, 15.0 }, true);
            s.DeesserEnabled = SoundAnalysis.Usable(a.HighBandBelow) && a.HighBandBelow < 15;

            // Dynamics. Lra: min 1.1, p25 3.2, median 4.3, p75 5.7, max 11.7 LU.
            // Larger means the quiet passages are much quieter than the loud.
            // Worst first, so the widest range comes first here -- the opposite
            // order to the measures where small is bad.
            //
            // LRA does NOT separate good from bad, which was worth finding out:
            // the two good samples measure 5.9 and 5.6 while three of the four
            // bad ones measure 3.7, 3.0 and 2.7. Set from the library median the
            // rule therefore compressed the REFERENCE recording and left most of
            // the bad ones alone. Only one sample stands out at 8.5, so the gate
            // sits above the good pair and catches that.
            s.CompressorLevel = Pick(a.Lra, 0, new[] { 10.0, 8.5, 7.0, 6.0 }, false);
            s.CompressorEnabled = SoundAnalysis.Usable(a.Lra) && a.Lra > 6.0;

            // Level. Lufs over the library: min -34.1, p25 -20.6, median -18.6,
            // p75 -16.7, max -7.2 -- but the two recordings Gordan calls good
            // sit at -22.3 and -21.8, BELOW that median. So the median is not
            // the target: a book a little quieter than average is not a book
            // that needs its level rebuilt, and the gate goes under the good
            // pair rather than at the middle of the shelf.
            //
            // NORMALISATION FOLLOWS DAMAGE, NOT LEVEL, and Gordan's own answers
            // are what forced that. Asked which recordings wanted it he said:
            // not at -15.9, yes at -19.3, yes at -20.7 (medium), and he would
            // touch neither of the good pair at -21.6 and -22.0. The two he
            // wanted sit BETWEEN the one he did not need and the two he would
            // leave alone — so no threshold on loudness can express it, and
            // every gate tried on loudness alone got at least one of the six
            // wrong.
            //
            // What those two have and the good pair has not is that they are
            // measurably damaged. Gated on that plus "not already loud", the
            // rule reproduces all six of his answers. Same signal the denoise
            // fallback uses, which makes it one idea rather than two patches.
            s.NormalizeLevel = Pick(a.Lufs, 0, new[] { -28.0, -24.0, -20.0, -18.0 }, true);
            s.NormalizeEnabled = Damaged(a) && SoundAnalysis.Usable(a.Lufs) && a.Lufs < -18;

            // Tone -- and DULLNESS is the fault that actually separates a bad
            // recording from a good one here, which is not what the library
            // distribution suggested at all.
            //
            //             high band below   centroid
            //   bad x4       27.6 .. 42.5   987 .. 1583 Hz
            //   good x2      17.8 .. 20.0  1759 .. 3404 Hz
            //
            // A clean gap on both, with nobody in between, so the cut goes in the
            // middle of it. And the lift has to SCALE: +2 dB was the whole
            // correction before, which is nothing to a recording sitting 42 dB
            // down. The centroid agrees independently and is the one quantity
            // measured the same way as the reference tool -- whose 1500 Hz
            // threshold lands inside the bad range but misclassifies the worst of
            // them, so it is used as a second opinion and not as the test.
            // The loudness target. Independent of every stage above -- it is not
            // a repair, it is the answer to "why does the next book jump".
            //
            // It CUTS as well as lifts, and that is the point rather than a side
            // effect: the loudest sample in the set comes DOWN by 8 dB. Gordan
            // named its level as the one he wanted everywhere; what he actually
            // wants is for books to stop jumping, and −7.8 LUFS cannot be reached
            // by the rest of the shelf without doing to them what was done to it.
            s.GainDb = GainFor(a);
            s.GainEnabled = s.GainDb != 0;

            // THE BASS CUT IS GONE, and it was a duplicate twice over. It cut a
            // shelf at 120 Hz whose whole measured action was below 200 -- the
            // highpass's region -- and it keyed on LowBandBelow, which is what
            // the highpass level above already keys on. One measurement was
            // driving two controls at the same frequencies. The low end belongs
            // to the highpass, which has five levels for it.
            int lift = Dullness(a);
            for (int i = 0; i < s.EqGain.Length; i++) s.EqGain[i] = 0;
            if (lift > 0) Ramp(s, lift);
            else if (lift < 0) s.EqGain[SoundSettings.EqShelfIndex] = lift;   // harsh: pull the top down
            s.EqEnabled = false;
            foreach (int g in s.EqGain) if (g != 0) s.EqEnabled = true;
        }

        /// <summary>How many dB this book needs to reach
        /// <see cref="SoundSettings.TargetLufs"/>, or 0 when it cannot be worked
        /// out.
        ///
        /// <para><b>It stops chasing the target rather than squashing to reach
        /// it.</b> A lift is capped so the peaks end no more than
        /// <see cref="SoundSettings.MaxLimitingDb"/> above the limiter's ceiling
        /// — past that the book is simply left quieter. Chasing a number by
        /// handing the limiter 11 dB to remove is exactly the treatment that made
        /// the loudest sample in the set sound compressed and muddy, and doing it
        /// deliberately to every quiet book would be worse than the jumping it
        /// set out to cure.</para>
        ///
        /// <para>A CUT is never capped. Bringing a too-loud book down costs
        /// nothing and risks nothing.</para></summary>
        /// <summary>Public because the loudness target is NOT a setting: no
        /// control holds it, so it is a function of the measurement and nothing
        /// else. <c>BookData.Load</c> derives it on every load rather than
        /// trusting what is in the ini — which also repairs the books whose gain
        /// was wiped while <c>FillSettings</c> was dropping it.</summary>
        public static double GainFor(SoundAnalysis a)
        {
            if (!SoundAnalysis.Usable(a.Lufs)) return 0;
            double want = SoundSettings.TargetLufs - a.Lufs;
            if (want > 0 && SoundAnalysis.Usable(a.TruePeakDb))
            {
                double headroom = SoundSettings.LimiterCeilingDb + SoundSettings.MaxLimitingDb - a.TruePeakDb;
                if (want > headroom) want = headroom;
            }
            if (want < 0.5 && want > -0.5) return 0;
            return Math.Round(want, 1);
        }

        /// <summary>Is this recording measurably damaged? The high band sitting
        /// 24 dB or more below the signal is the one test that separated Gordan.s
        /// four bad samples from his two good ones with nobody in the gap, and it
        /// is what the denoise fallback and the normalisation gate both hang on.</summary>
        private static bool Damaged(SoundAnalysis a)
        {
            return SoundAnalysis.Usable(a.HighBandBelow) && a.HighBandBelow >= 24;
        }

        /// <summary>Spreads a dullness lift across the top three bands as a
        /// rising ramp, which is the shape a dull recording actually needs.
        ///
        /// <para>Measured, the loss is a slope and not a step: the damaged
        /// recordings track the good ones to about 800 Hz and then fall away, 4 dB
        /// down at 1 kHz, 8 at 2 kHz, 12 at 3–4 kHz, 15 by 8 kHz. A single shelf
        /// gives a FLAT lift above its corner, which under-corrects the middle
        /// and over-corrects the top where there is nothing left to lift.</para>
        ///
        /// <para><b>The fractions are deliberately conservative because the bands
        /// SUM.</b> A trial set aiming for +2/+4/+6 at 800/1250/1800 measured
        /// +4.2/+6.7/+9.6 — roughly double at the bottom of the ramp. So each
        /// band is given a fraction of the target rather than the target, and the
        /// composite is what the reader hears. Verifying the composite against
        /// the intended curve, and solving for exact gains rather than
        /// approximating them, is the piece still owed here.</para></summary>
        private static void Ramp(SoundSettings s, int lift)
        {
            int n = s.EqGain.Length;
            if (n < 3) return;
            s.EqGain[n - 1] = lift;                                  // 5 kHz shelf: the full lift
            s.EqGain[n - 2] = (int)Math.Round(lift * 0.5);           // 3500
            s.EqGain[n - 3] = (int)Math.Round(lift * 0.25);          // 1800
        }

        /// <summary>How much treble a dull recording gets back, in dB, capped at
        /// the EQ's own ±15.
        ///
        /// <para><b>The band ratio decides alone. The centroid was given a veto
        /// and it had to be taken away.</b> Both are honest measures of the same
        /// thing, but they are not equally STABLE: measured twice over the same
        /// six files with only the sampling points moved, the band ratio shifted
        /// by at most 2.3 dB while the centroid swung 1759→3553 on one file and
        /// 3404→1352 on another. The cause is structural — astats and ebur128
        /// publish values accumulated over the whole segment, while
        /// aspectralstats publishes PER FRAME, so what gets read is one
        /// essentially random frame rather than the segment. As a veto it had
        /// already done damage, cancelling the treble lift on a recording that
        /// needs it.</para></summary>
        private static int Dullness(SoundAnalysis a)
        {
            if (!SoundAnalysis.Usable(a.HighBandBelow)) return 0;
            if (a.HighBandBelow < 15) return -2;                 // harsh, not dull
            if (a.HighBandBelow < 24) return 0;                  // both good samples land here

            // 24 dB down is the edge of normal; every 6 dB past it buys 2 dB
            // back — but SMOOTHLY, rounded to the nearest dB rather than jumping
            // in twos at 6 dB boundaries.
            //
            // THE STEPS WERE THE INSTABILITY, and the corpus says so almost
            // exactly. How reliably a book's settings could be reproduced turned
            // out to be predicted by nothing but its distance from a step edge:
            //
            //   Torton Vajlder  42.0 dB — exactly on an edge — 45 % reproducible
            //   luj aragon      36.4 dB — 0.4 from an edge   — 58 %
            //   Jevtušenko      31.5 dB — 1.5 from an edge   — 85 %
            //   Barbara         39.4 dB — 2.6 from an edge   — 95 %
            //   hallmarked man  19.8 dB — 4.2 from an edge   — 98 %
            //
            // A book sitting on a boundary flips for ever, however well it is
            // measured, and no number of samples can help it: the measurement
            // converges and the STEP turns what is left into a categorical
            // difference. Rounded to 1 dB the same curve passes through the same
            // anchors (24→2, 30→4, 36→6, 42→8) and a borderline book now differs
            // by a decibel, which nobody can hear.
            double lift = 2 + (a.HighBandBelow - 24) / 3.0;
            if (lift > 8) lift = 8;
            return (int)Math.Round(lift, MidpointRounding.AwayFromZero);
        }

        /// <summary>Which of five levels a reading falls into. Level 4 is the
        /// strongest treatment, 0 the mildest.
        ///
        /// <para><b>Cuts are listed WORST FIRST</b> and the first one crossed
        /// wins. <paramref name="smallerIsWorse"/> says which way the measure
        /// runs: for signal-to-noise a smaller number is worse, for loudness
        /// range a larger one is. Getting either wrong silently inverts a whole
        /// stage — treating the cleanest recordings as the noisiest — which is
        /// why the direction is a named argument and not four hand-written
        /// comparisons per stage.</para></summary>
        private static int Pick(double v, int ifUnknown, double[] worstFirst, bool smallerIsWorse)
        {
            if (!SoundAnalysis.Usable(v)) return ifUnknown;
            for (int i = 0; i < worstFirst.Length; i++)
            {
                bool worse = smallerIsWorse ? v < worstFirst[i] : v > worstFirst[i];
                if (worse) return worstFirst.Length - i;
            }
            return 0;
        }
    }

    /// <summary>Runs the measurement. Its own silent libmpv context, exactly the
    /// way <see cref="MpvDuration"/> works — it never plays a sound and never
    /// touches the player's context.</summary>
    public static class SoundAnalyser
    {
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, string name, string data);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);
        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr data);

        private const int EventEndFile = 7;

        /// <summary>Seconds taken from each sampled point.</summary>
        public const double SegmentSeconds = 20;

        /// <summary>Ten tenths of the book, two readings in each: twenty in all.
        /// See PickSegments for why twenty and not three, and not thirty.</summary>
        public const int Tenths = 10;
        public const int PerTenth = 2;

        /// <summary>The measurements are read as a mpv PROPERTY, not off the log.
        ///
        /// <para><b>The log route was wrong, and only a harness could ever have
        /// made it look right.</b> FFmpeg's log callback is process-global: the
        /// FIRST mpv context created in the process captures it, and every later
        /// one receives nothing. Measured — alone, a segment yields 83 ffmpeg log
        /// lines; with one earlier context alive, <b>zero</b>. In the running
        /// player Form1's context is created at start-up and lives for the whole
        /// session, so the analysis would have returned null every single time.
        /// It passed its end-to-end test only because the harness was the one
        /// context in the process. Gordan's six samples are what exposed it,
        /// because that script called <see cref="MpvDuration"/> first.</para>
        ///
        /// <para><b>Filter metadata is per-context and has none of that.</b> A
        /// labelled filter (<c>@st:</c>) publishes its values as
        /// <c>af-metadata/st</c>, verified identical with and without an earlier
        /// context alive. It must be read WHILE the segment plays — at
        /// end-of-file the graph is gone and the property is empty.</para>
        ///
        /// <para><b>Three things got better, not just fixed.</b> The
        /// <c>asplit</c>/<c>amix</c> graph is gone, and with it the trap of
        /// <c>ebur128</c> silently measuring the mixed-down signal.
        /// <c>aspectralstats</c> now works — it publishes exactly the per-frame
        /// metadata this route reads, so the <b>spectral centroid</b> we had
        /// written off is available, which also makes the reference tool's
        /// 1500 Hz threshold comparable on the same quantity for the first time.
        /// And the keys are prefixed by filter name, so three measurements share
        /// one decode without colliding.</para></summary>
        private const string GraphFull =
            "@st:lavfi=[astats=metadata=1:reset=0:measure_perchannel=none,"
            + "aspectralstats=measure=centroid+flatness+rolloff,"
            + "ebur128=metadata=1:peak=true:framelog=quiet]";

        /// <summary>The two band passes keep their own decode. Inside one lavfi
        /// entry three astats instances would all publish under
        /// <c>lavfi.astats.*</c> and overwrite one another, and mpv's label is
        /// per filter ENTRY, not per filter inside a graph. Chaining them in one
        /// entry is worse still — the low-pass would filter the audio the
        /// high-pass then measures.</summary>
        private const string GraphLow = "@st:lavfi=[lowpass=f=300,astats=metadata=1:reset=0:measure_perchannel=none]";
        private const string GraphHigh = "@st:lavfi=[highpass=f=6000,astats=metadata=1:reset=0:measure_perchannel=none]";

        /// <summary>Measures the book and returns what it found, or null when
        /// nothing could be measured. Never throws.</summary>
        /// <summary>How many segments are done. Read by the progress dialog;
        /// a plain int because it is written by one thread and read by one
        /// other, and neither cares about the odd stale value.</summary>
        public static volatile int Progress;

        public static SoundAnalysis Measure(BookData book)
        {
            return Measure(book, null, null);
        }

        /// <summary>The same, watched and interruptible. <paramref name="stop"/>
        /// is asked between segments -- never inside one, so a cancel costs at
        /// most the segment in flight rather than leaving a half-read file.</summary>
        public static SoundAnalysis Measure(BookData book, Func<bool> stop, Action<int> onProgress)
        {
            try
            {
                if (book == null || book.Chapters == null || book.Chapters.Count == 0) return null;
                var points = PickSegments(book);
                if (points.Count == 0) return null;

                var got = new List<SoundAnalysis>();
                Progress = 0;
                foreach (var p in points)
                {
                    if (stop != null && stop()) return null;
                    SoundAnalysis r = MeasureSegment(p.Path, p.Start);
                    if (r != null) got.Add(r);
                    Progress++;
                    if (onProgress != null) onProgress(Progress);
                }
                return Combine(got);
            }
            catch { return null; }
        }

        /// <summary>Folds a set of segment readings into one answer for the book.
        ///
        /// <para>The mean over the segments, except for the peaks and the flat
        /// factor, where the WORST is what matters — one clipped passage is a
        /// clipped book, and averaging it away is how it would be missed.</para>
        ///
        /// <para>Every average takes only the readings that HAVE that measure.
        /// Half of a real library has no noise floor at all, so a mean taken over
        /// every segment regardless would be a mean of two real numbers and a
        /// placeholder.</para>
        ///
        /// <para>Public because the harness that asks "would more samples change
        /// the answer?" has to fold ITS samples the same way this does, and a
        /// second copy of these rules would be a second thing to keep in
        /// step.</para></summary>
        public static SoundAnalysis Combine(List<SoundAnalysis> got)
        {
            if (got == null || got.Count == 0) return null;
            var a = new SoundAnalysis();
            a.Segments = got.Count;
            a.Lufs = Mean(got, r => r.Lufs);
            a.Lra = Mean(got, r => r.Lra);
            a.RmsDb = Mean(got, r => r.RmsDb);
            a.NoiseFloorDb = Mean(got, r => r.NoiseFloorDb);
            a.RmsTroughDb = Mean(got, r => r.RmsTroughDb);
            // How many segments actually produced a noise reading, so a rule can
            // tell "quiet" from "not measured".
            foreach (SoundAnalysis r in got)
                if (SoundAnalysis.Usable(r.RmsTroughDb) || SoundAnalysis.Usable(r.NoiseFloorDb))
                    a.NoiseSamples++;
            a.LowBandDb = Mean(got, r => r.LowBandDb);
            a.HighBandDb = Mean(got, r => r.HighBandDb);
            a.LowPeakDb = Worst(got, r => r.LowPeakDb);
            a.HighPeakDb = Worst(got, r => r.HighPeakDb);
            a.CentroidHz = Mean(got, r => r.CentroidHz);
            a.TruePeakDb = Worst(got, r => r.TruePeakDb);
            a.PeakDb = Worst(got, r => r.PeakDb);
            a.FlatFactor = Worst(got, r => r.FlatFactor);
            if (double.IsNaN(a.FlatFactor)) a.FlatFactor = 0;   // nothing measured is no flatness
            return a;
        }

        /// <summary>One segment of one file, as a finished result. Exposed for
        /// the harness that sets the thresholds — the numbers behind them have to
        /// be re-runnable when new samples arrive, and a harness that measures a
        /// whole book cannot say which segment a reading came from. Returns null
        /// when nothing could be measured.</summary>
        public static SoundAnalysis MeasureSegment(string path, double startSeconds)
        {
            Reading r = MeasureOne(path, startSeconds);
            if (r == null) return null;
            return new SoundAnalysis
            {
                Segments = 1,
                Lufs = r.Lufs,
                Lra = r.Lra,
                TruePeakDb = r.TruePeakDb,
                RmsDb = r.RmsDb,
                PeakDb = r.PeakDb,
                NoiseFloorDb = r.NoiseFloorDb,
                RmsTroughDb = r.RmsTroughDb,
                FlatFactor = r.FlatFactor,
                LowBandDb = r.LowBandDb,
                HighBandDb = r.HighBandDb,
                CentroidHz = r.CentroidHz,
                LowPeakDb = r.LowPeakDb,
                HighPeakDb = r.HighPeakDb
            };
        }

        private static double Mean(List<SoundAnalysis> rs, Func<SoundAnalysis, double> pick)
        {
            double sum = 0; int n = 0;
            foreach (SoundAnalysis r in rs) { double v = pick(r); if (SoundAnalysis.Usable(v)) { sum += v; n++; } }
            return n > 0 ? sum / n : double.NaN;
        }

        private static double Worst(List<SoundAnalysis> rs, Func<SoundAnalysis, double> pick)
        {
            double worst = double.NaN;
            foreach (SoundAnalysis r in rs)
            {
                double v = pick(r);
                if (!SoundAnalysis.Usable(v)) continue;
                if (double.IsNaN(worst) || v > worst) worst = v;
            }
            return worst;
        }

        private struct Point { public string Path; public double Start; }

        /// <summary>Where to listen. Spread through the book, and never at the
        /// very start of a file: an opening carries the publisher's announcement
        /// and often music, which is not the voice the settings are for.</summary>
        /// <summary>Where to listen: TWO points inside each TENTH of the book,
        /// twenty in all, placed on the book's own timeline rather than on its
        /// file list.
        ///
        /// <para><b>Three points was measured and is not defensible.</b> Over six
        /// real books, twenty independent triples produced on average six
        /// different sets of settings, and the loudness they implied spanned
        /// 10.5 dB at worst — the difference between comfortable and unusable,
        /// decided by which three seconds happened to be drawn.</para>
        ///
        /// <para><b>Twenty, not thirty, and the corpus chose that.</b> The worst
        /// loudness error falls 10.5 dB → 4.1 → 2.7 going from 3 to 10 to 20
        /// samples, and then stops: thirty gives 3.1 and ninety gives 2.1. Twenty
        /// buys essentially all of the convergence there is, and a third less of
        /// the reader's time than thirty. (Gordan's proposal was thirty; the
        /// measurement is what trimmed it.)</para>
        ///
        /// <para><b>Tenths of the TIMELINE, not of the file list.</b> Files
        /// differ enormously in length — one book here has five files, another
        /// 156 — so picking by file index samples a long file and a short one
        /// equally, which is not what "spread through the book" means.</para>
        ///
        /// <para>The two points inside a tenth sit at a third and two thirds of
        /// it. Deliberately NOT random: random means the same book measured twice
        /// gives different settings, and then "why did it change?" has no
        /// answer.</para></summary>
        private static List<Point> PickSegments(BookData book)
        {
            var pts = new List<Point>();
            int n = book.Chapters.Count;
            if (n == 0) return pts;

            // The book's own timeline: cumulative start of each file.
            var starts = new double[n];
            double total = 0;
            for (int i = 0; i < n; i++) { starts[i] = total; total += book.Chapters[i].Duration; }
            if (total <= SegmentSeconds) return pts;

            for (int tenth = 0; tenth < Tenths; tenth++)
                for (int k = 1; k <= PerTenth; k++)
                {
                    double at = total * (tenth + (double)k / (PerTenth + 1)) / Tenths;
                    Point p;
                    if (Locate(book, starts, at, out p)) pts.Add(p);
                }
            return pts;
        }

        /// <summary>Turns a position on the book's timeline into a file and an
        /// offset inside it, pulled back from the end so a whole segment
        /// fits.</summary>
        private static bool Locate(BookData book, double[] starts, double at, out Point p)
        {
            p = new Point();
            for (int i = starts.Length - 1; i >= 0; i--)
            {
                if (at < starts[i]) continue;
                double dur = book.Chapters[i].Duration;
                if (dur <= SegmentSeconds + 1) return false;   // too short to sample
                double inFile = at - starts[i];
                if (inFile + SegmentSeconds > dur) inFile = dur - SegmentSeconds - 1;
                p.Path = System.IO.Path.Combine(book.FolderPath, book.Chapters[i].FileName);
                p.Start = inFile < 0 ? 0 : inFile;
                return true;
            }
            return false;
        }

        private class Reading
        {
            public double Lufs = double.NaN, Lra = double.NaN, TruePeakDb = double.NaN;
            public double RmsDb = double.NaN, PeakDb = double.NaN, NoiseFloorDb = double.NaN;
            public double RmsTroughDb = double.NaN, FlatFactor = double.NaN;
            public double LowBandDb = double.NaN, HighBandDb = double.NaN;
            public double LowPeakDb = double.NaN, HighPeakDb = double.NaN;
            public double CentroidHz = double.NaN;
        }

        /// <summary>Below this a segment is silence, not a sample of the reading.
        /// <b>7 of 103 books in the sweep landed on one</b> — a gap between
        /// chapters, or a file shorter than the point asked for. Such a segment
        /// says nothing about the recording and must not be averaged into it;
        /// left in, it dragged the mean level down by its whole depth.</summary>
        private const double SilenceFloorDb = -60;

        private static Reading MeasureOne(string path, double start)
        {
            var full = RunGraph(path, start, GraphFull);
            if (full == null) return null;

            var r = new Reading();
            r.RmsDb = Get(full, "lavfi.astats.Overall.RMS_level");
            r.PeakDb = Get(full, "lavfi.astats.Overall.Peak_level");
            r.NoiseFloorDb = Get(full, "lavfi.astats.Overall.Noise_floor");
            r.RmsTroughDb = Get(full, "lavfi.astats.Overall.RMS_trough");
            r.FlatFactor = Get(full, "lavfi.astats.Overall.Flat_factor");
            r.Lufs = Get(full, "lavfi.r128.I");
            r.Lra = Get(full, "lavfi.r128.LRA");
            r.CentroidHz = Get(full, "lavfi.aspectralstats.1.centroid");
            // r128 publishes the true peak as a LINEAR amplitude here, where the
            // log printed it in dBFS. Same measurement, different unit, and
            // taking one for the other would have read 0.564 as -0.564 dB.
            double tp = Get(full, "lavfi.r128.true_peak");
            r.TruePeakDb = SoundAnalysis.Usable(tp) && tp > 0 ? 20 * Math.Log10(tp) : double.NaN;

            // Silence is not a reading. A segment that landed in a gap tells us
            // nothing, and its level would drag the book's mean down with it.
            if (!SoundAnalysis.Usable(r.RmsDb) || r.RmsDb <= SilenceFloorDb) return null;

            var low = RunGraph(path, start, GraphLow);
            if (low != null) r.LowBandDb = Get(low, "lavfi.astats.Overall.RMS_level");
            if (low != null) r.LowPeakDb = Get(low, "lavfi.astats.Overall.Peak_level");
            var high = RunGraph(path, start, GraphHigh);
            if (high != null) r.HighBandDb = Get(high, "lavfi.astats.Overall.RMS_level");
            if (high != null) r.HighPeakDb = Get(high, "lavfi.astats.Overall.Peak_level");
            return r;
        }

        /// <summary>Plays one segment through one filter graph and returns the
        /// filter's published metadata, or null.</summary>
        private static Dictionary<string, double> RunGraph(string path, double start, string graph)
        {
            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = mpv_create();
                if (ctx == IntPtr.Zero) return null;
                mpv_set_property_string(ctx, "terminal", "no");
                mpv_set_property_string(ctx, "ao", "null");
                mpv_set_property_string(ctx, "vid", "no");
                if (mpv_initialize(ctx) < 0) return null;

                mpv_set_property_string(ctx, "audio-display", "no");
                mpv_set_property_string(ctx, "untimed", "yes");
                // A null audio output still paces to the clock, so 20 s of audio
                // would cost 20 s of the reader's time. This is what makes the
                // whole feature affordable: measured at about 50x real time.
                mpv_set_property_string(ctx, "speed", "100");
                mpv_set_property_string(ctx, "audio-pitch-correction", "no");
                mpv_set_property_string(ctx, "start", start.ToString("0.###", CultureInfo.InvariantCulture));
                mpv_set_property_string(ctx, "length", SegmentSeconds.ToString(CultureInfo.InvariantCulture));
                if (mpv_set_property_string(ctx, "af", graph) < 0) return null;

                Command(ctx, "loadfile", path, "replace");

                // Polled WHILE it plays: astats and ebur128 publish cumulative
                // values on every frame, and at end-of-file the graph is torn
                // down and the property comes back empty. So the LAST non-empty
                // read is the answer, not the one after EOF.
                Dictionary<string, double> last = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    IntPtr ev = mpv_wait_event(ctx, 0.02);
                    Dictionary<string, double> now = ReadMetadata(ctx);
                    if (now != null && now.Count > 0) last = now;
                    if (Marshal.ReadInt32(ev) == EventEndFile) break;
                }
                return last;
            }
            catch { return null; }
            finally { if (ctx != IntPtr.Zero) try { mpv_terminate_destroy(ctx); } catch { } }
        }

        /// <summary>Reads <c>af-metadata/st</c> and pulls the numbers out of it.
        ///
        /// <para>mpv hands this back as JSON. It is scanned for quoted
        /// key/value pairs rather than parsed as a document — the shape is fixed
        /// and flat, and the project has no JSON library to reach for.</para></summary>
        private static Dictionary<string, double> ReadMetadata(IntPtr ctx)
        {
            IntPtr p = mpv_get_property_string(ctx, "af-metadata/st");
            if (p == IntPtr.Zero) return null;
            string json;
            try
            {
                int len = 0;
                while (Marshal.ReadByte(p, len) != 0) len++;
                byte[] b = new byte[len];
                Marshal.Copy(p, b, 0, len);
                json = Encoding.UTF8.GetString(b);
            }
            finally { try { mpv_free(p); } catch { } }

            var map = new Dictionary<string, double>();
            var parts = json.Split('"');
            // "key":"value" -> the quoted tokens alternate key, value.
            for (int i = 1; i + 2 < parts.Length; i += 4)
            {
                double v;
                if (Num(parts[i + 2], out v)) map[parts[i]] = v;
            }
            return map;
        }


        /// <summary>NaN when the key is absent — never 0. A missing key read back
        /// as 0 is how six books in the sweep reported a signal-to-noise ratio of
        /// about −21 dB: arithmetic on a value that was never measured.</summary>
        private static double Get(Dictionary<string, double> d, string k)
        {
            double v;
            return d.TryGetValue(k, out v) ? v : double.NaN;
        }

        /// <summary><c>-inf</c> is a real answer from astats, not a parse
        /// failure — it means the measurement found nothing there, and it arrives
        /// for the noise floor in <b>49 % of real books</b>. It is kept as an
        /// infinity so the caller can tell "silent" from "not measured" from a
        /// number; an earlier version clamped it to −120 dB, which made half the
        /// sample report a plausible noise floor that had never been
        /// measured.</summary>
        private static bool Num(string s, out double v)
        {
            v = double.NaN;
            if (s == null) return false;
            s = s.Trim();
            int sp = s.IndexOf(' ');
            if (sp > 0) s = s.Substring(0, sp);     // "-15.1 LUFS" -> "-15.1"
            if (s == "-inf") { v = double.NegativeInfinity; return true; }
            if (s == "inf") { v = double.PositiveInfinity; return true; }
            if (s == "nan" || s == "-nan") { v = double.NaN; return true; }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static void Command(IntPtr ctx, params string[] args)
        {
            IntPtr[] ptrs = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++) ptrs[i] = Utf8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            GCHandle h = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
            try { mpv_command(ctx, h.AddrOfPinnedObject()); }
            finally
            {
                h.Free();
                foreach (IntPtr p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>mpv takes UTF-8 always. StringToHGlobalAnsi uses the system
        /// code page and silently fails to open any path with a Č or a Đ in it —
        /// which looks exactly like an unsupported format (§10e).</summary>
        private static IntPtr Utf8(string s)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            Marshal.Copy(b, 0, p, b.Length);
            Marshal.WriteByte(p, b.Length, 0);
            return p;
        }
    }
}
