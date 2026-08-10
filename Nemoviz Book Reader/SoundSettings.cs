using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nemoviz_Book_Reader
{
    /// <summary>Per-book sound-processing settings, persisted in Book.ini's
    /// [Sound] section. The processing stages are exposed to the user as simple
    /// named presets (a level index) rather than raw DSP parameters; the preset
    /// tables below hold the real values behind each level, shared by the
    /// Properties dialog (labels + the technical read-out) and, later, the code
    /// that builds the mpv/ffmpeg af chain. The equalizer stays free-form
    /// (three dB bands). The safety limiter is not user-adjustable — it is
    /// always applied at a fixed ceiling (see <see cref="LimiterCeilingDb"/>).
    /// The whole subsystem is inert while <see cref="Enabled"/> is false.</summary>
    public class SoundSettings
    {
        // ── Preset tables (index = level) ─────────────────────────────────
        // Highpass cutoff (Hz), five levels (Minimal…Maximum) like the rest —
        // higher cutoff removes more low end but starts thinning deep voices.
        public static readonly int[] HighpassHz = { 50, 65, 80, 100, 120 };

        // Noise reduction strength (dB): Minimal / Light / Medium / Strong / Maximum.
        public static readonly int[] DenoiseDb = { 6, 10, 14, 20, 26 };

        // De-esser intensity (0..1), five levels.
        public static readonly double[] DeesserIntensity = { 0.15, 0.30, 0.45, 0.60, 0.80 };

        // Compressor presets (threshold dB, ratio n:1, makeup dB, attack ms, release ms).
        public static readonly (int Threshold, double Ratio, int Makeup, int Attack, int Release)[] Compressor =
        {
            (-12, 2.0, 1, 20, 250),
            (-16, 2.5, 2, 20, 250),
            (-18, 3.0, 3, 15, 200),
            (-22, 4.0, 4, 10, 180),
            (-26, 6.0, 6,  5, 150),
        };

        // Normalization aggressiveness per level: the speechnorm expansion
        // factor. Five levels. Speech normalisation is the only method there is
        // (Gordan) - a book is a voice, and the music-safe alternative asked the
        // reader a question they had no way to answer.
        public static readonly double[] SpeechnormExpansion = { 1.5, 2.0, 2.5, 3.0, 3.5 };

        /// <summary>The gate's threshold in dBFS, per level. Everything quieter
        /// than this is pushed down by up to <see cref="GateRangeDb"/>.
        ///
        /// <para><b>Absolute numbers only mean something because the loudness
        /// target now stands FIRST</b> (see BuildAf). Every book arrives here at
        /// about −16 LUFS, so one threshold means one thing; on the old chain the
        /// same number met books running −7 to −22 LUFS.</para>
        ///
        /// <para><b>Measured, not chosen.</b> A level trace in 0.2 s windows over
        /// Gordan's six samples — nothing coarser can see a pause at all — puts
        /// the noise floor of the two he complains about at −40.0 and −46.4 dBFS
        /// raw, which their own loudness gain lifts to about −36 and −42. His
        /// clean reference measures −99.2, i.e. digital silence, and wants none
        /// of this. The scale spans that working region. Four decibels of
        /// threshold was the difference between the gate doing nothing (0.7 dB)
        /// and doing its job (6.1 dB), so the steps are deliberately small.</para>
        /// </summary>
        public static readonly double[] GateThresholdDb = { -40, -37, -34, -31, -28 };

        /// <summary><b>It attenuates; it never silences.</b> A gate that closes
        /// hard is the classic way this goes wrong: constant noise is something
        /// the ear stops hearing, while noise that switches off and on around
        /// every phrase keeps re-alerting it — and a hard close chops the tails
        /// off words. Capping the reduction at 20 dB leaves a bed under the
        /// pauses so there is no on and off. Whether that is enough is an ear's
        /// question and Gordan's to answer.</summary>
        public const double GateRangeDb = -20;

        /// <summary>Slow enough not to chew word endings, fast enough to catch
        /// the start of a pause. A gate's release is what is heard when it is
        /// wrong.</summary>
        public const int GateAttackMs = 10;
        public const int GateReleaseMs = 250;
        public const double GateRatio = 4;

        /// <summary>The five EQ bands, in Hz. The last one is a SHELF; the other
        /// four are bells.
        ///
        /// <para><b>Where they are, and why each.</b> Measured across the six
        /// samples, third-octave, dB relative to each file's own level:</para>
        /// <code>
        ///              150-400  400-1k   1k-2k   2k-4k   4k-8k    >8k
        ///  the good two   -3.9    -4.4   -10.0   -14.2   -17.4   -21.5
        ///  the bad four   -2.8    -5.5   -13.8   -21.1   -30.3   -44.1
        /// </code>
        /// <list type="bullet">
        /// <item><b>300</b> — body and boxiness. Chosen so it CANNOT fight the
        /// highpass: measured, a bell here touches 0.8 dB at 100 Hz and 0.5 at
        /// 80, where the highpass is working. Gordan's question is what settled
        /// it — "nema smisla da nešto otkinemo hipassom a da se to može vratiti
        /// ekvilajzerom". The old 120 Hz SHELF did exactly that: measured, its
        /// whole action was below 200 Hz, a weaker duplicate of the highpass in
        /// the same place.</item>
        /// <item><b>800</b> — nasality and mud. The measurement shows good and
        /// bad recordings do NOT differ here, so nothing sets it automatically;
        /// it is kept because four bad samples are a thin basis for removing a
        /// control (Gordan: "tko zna na što se sve može naletjeti").</item>
        /// <item><b>1800</b> — where the loss actually begins, 3.8 dB.</item>
        /// <item><b>3500</b> — consonants and intelligibility, 7 dB.</item>
        /// <item><b>5000, a shelf</b> — 13 dB, and a shelf because a damaged
        /// recording needs a RAMP rising to the top, which two adjacent bells
        /// would turn into a hump. Not higher than 5 kHz because half this
        /// library is sampled at 22.05 kHz and so carries nothing at all above
        /// 11 kHz — the ceiling is half the sample rate, per channel, whether
        /// the file is mono or stereo.</item>
        /// </list>
        ///
        /// <para><b>The bands overlap, and that is the point.</b> Measured, the
        /// 300 Hz bell at +6 still gives +1.0 at 800 Hz, and the 800 Hz bell
        /// gives +3.2 at 1250. Bands with hard edges would produce a lumpy
        /// response with dips between them; overlapping ones let five points
        /// build a smooth curve. The cost is that <b>they SUM</b> — aiming for
        /// +2/+4/+6 at 800/1250/1800 measured +4.2/+6.7/+9.6 — so any rule that
        /// sets them must think about the composite, never one band at a
        /// time.</para>
        ///
        /// <para><b>Honest limit:</b> what was never recorded cannot be restored.
        /// A 5 kHz shelf reaches real content; nothing reaches what a cassette or
        /// a 64 kbps encoder threw away.</para></summary>
        /// <para><b>The lowest band moved 300 → 200 Hz on 2026-08-09, and the
        /// measurement is the whole argument.</b> A third-octave profile of the
        /// six samples put the bad recordings' real excess at <b>160–250 Hz</b> —
        /// +5.6 and +7.9 dB over the reference on Loše 3, +5.5 and +6.0 on
        /// Loše 4, +2.3 and +5.6 on Loše 2 — and NOTHING reached it: the highpass
        /// stops at 120 and the lowest bell started at 300. That gap is why
        /// Gordan pinned the rumble control at maximum on all four of his books
        /// AND cut 300 Hz by 10–15 dB: he was attacking a 200 Hz problem from
        /// both sides because nothing addressed it directly.
        ///
        /// A bell at 200 with Q=1 spans about 140–283 Hz, which is the excess.
        /// It also overlaps the highpass a little, and that is accepted rather
        /// than avoided — the highpass is 3 dB down at its own 120 Hz ceiling
        /// and 12 dB down an octave lower, so the two barely meet. Unlike the
        /// highpass this can also LIFT, and it takes nothing below it with
        /// it.</para>
        public static readonly int[] EqBandHz = { 200, 800, 1800, 3500, 5000 };

        /// <summary>The last band is a high shelf, the rest are bells.</summary>
        public static int EqShelfIndex { get { return EqBandHz.Length - 1; } }

        /// <summary>How far a band may be moved, either way. One number in one
        /// place, because it was written out as a literal in three.</summary>
        public const int EqMaxDb = 20;

        /// <summary>Bell width, in Q. About 1.4 octaves — wide enough that five
        /// bands cover the range without dips, narrow enough that each still has
        /// a recognisable centre.</summary>
        public const double EqBellQ = 1.0;

        // The always-on safety limiter ceiling (dBFS). Fixed, not user-editable:
        // "set and forget", and we want the hottest clean output possible.
        public const double LimiterCeilingDb = -0.1;

        /// <summary>Where every book is brought to, LUFS.
        ///
        /// <para><b>Why −16 and not the loudest recording in the library.</b>
        /// Gordan pointed at a sample measuring −7.8 LUFS and said that level
        /// would be ideal for everything. It is a fine level; the problem is what
        /// it costs to REACH it. That sample also true-peaks at +2.8 dBFS — it is
        /// already clipped — and its crest is 10.6 dB where a clean reading of
        /// the same library measures 18.8. Lifting the good recording to −7.8
        /// needs +13.8 dB, which puts its peaks 11 dB over the ceiling and hands
        /// the limiter 11 dB to remove. That is precisely the treatment he
        /// described hearing on the loud sample: "kao da je komprimirana sa svim
        /// dodatnim šumovima i mutnoćom". Its loudness and its muddiness are not
        /// two properties, one is the price of the other.</para>
        ///
        /// <para>−16 asks +5.6 dB of that same recording and about 4 dB of gentle
        /// limiting, which is inaudible. The library's own median is −18.6, so
        /// nearly every book comes UP.</para></summary>
        public const double TargetLufs = -16.0;

        /// <summary>How much peak reduction the target may spend before it stops
        /// chasing. Past this the book is left quieter rather than squashed —
        /// evening out the shelf is not worth doing the damage the whole feature
        /// exists to avoid.</summary>
        public const double MaxLimitingDb = 5.0;

        // ── Stored settings ───────────────────────────────────────────────
        public bool Enabled;            // master switch

        public bool HighpassEnabled;
        public int HighpassLevel;       // 0..2

        public bool DenoiseEnabled;
        public int DenoiseLevel;        // 0..4

        public bool DeesserEnabled;
        public int DeesserLevel;        // 0..4

        public bool CompressorEnabled;
        public int CompressorLevel;     // 0..4

        public bool EqEnabled;

        /// <summary>One gain in dB per band of <see cref="EqBandHz"/>. An array
        /// rather than five named fields: the bands are data, and every piece of
        /// code that touches them — the chain, the advisor, the dialog, the
        /// read-out — then works for any number of them.</summary>
        public int[] EqGain = new int[EqBandHz.Length];

        public bool NormalizeEnabled;
        public int NormalizeLevel;      // 0..4

        /// <summary>The noise gate — what Gordan asked for as "another
        /// compressor that pushes everything below x dB into inaudibility". It
        /// is the opposite of a compressor and the word for it is a gate, or a
        /// downward expander: a compressor works on what is ABOVE its threshold,
        /// this on what is below. It only ever acts in the gaps, which is
        /// exactly the complaint — noise that does not bother him under the
        /// voice and does in a long pause.
        ///
        /// <para><b>Off by default, deliberately.</b> Every other stage has a
        /// rule in SoundAdvisor that sets it from the measurement; this one does
        /// not yet, because the noise floor it would key on is not measured yet
        /// (§8d — astats' RMS_trough is −inf in 99% of segments, and the 0.2 s
        /// window trace that DOES measure it is not in the analyser). Shipping a
        /// rule that cannot be calibrated would be worse than shipping
        /// none.</para></summary>
        public bool GateEnabled;
        public int GateLevel;           // 0..4

        /// <summary>Gain in dB that brings this book to <see cref="TargetLufs"/>,
        /// worked out once from the measurement. 0 means the book is already there
        /// or was never measured. Negative for a book that is too LOUD -- the
        /// point is that books stop jumping, which cuts both ways.</summary>
        public double GainDb;
        public bool GainEnabled;

        public SoundSettings()
        {
            SetDefaults();
        }

        public void SetDefaults()
        {
            Enabled = false;

            HighpassEnabled = true;
            HighpassLevel = 2;          // Medium (80 Hz)

            DenoiseEnabled = true;
            DenoiseLevel = 2;           // Medium

            DeesserEnabled = false;
            DeesserLevel = 1;

            CompressorEnabled = true;
            CompressorLevel = 2;        // Medium

            EqEnabled = true;
            for (int i = 0; i < EqGain.Length; i++) EqGain[i] = 0;

            NormalizeEnabled = true;
            NormalizeLevel = 2;         // Medium
        }

        public void Load(IniFile ini)
        {
            SetDefaults();
            Enabled = ReadBool(ini, "Enabled", Enabled);

            HighpassEnabled = ReadBool(ini, "HighpassEnabled", HighpassEnabled);
            HighpassLevel = ClampLevel(ReadInt(ini, "HighpassLevel", HighpassLevel), HighpassHz.Length);

            DenoiseEnabled = ReadBool(ini, "DenoiseEnabled", DenoiseEnabled);
            DenoiseLevel = ClampLevel(ReadInt(ini, "DenoiseLevel", DenoiseLevel), DenoiseDb.Length);

            DeesserEnabled = ReadBool(ini, "DeesserEnabled", DeesserEnabled);
            DeesserLevel = ClampLevel(ReadInt(ini, "DeesserLevel", DeesserLevel), DeesserIntensity.Length);

            CompressorEnabled = ReadBool(ini, "CompressorEnabled", CompressorEnabled);
            CompressorLevel = ClampLevel(ReadInt(ini, "CompressorLevel", CompressorLevel), Compressor.Length);

            EqEnabled = ReadBool(ini, "EqEnabled", EqEnabled);
            for (int i = 0; i < EqGain.Length; i++)
                EqGain[i] = ReadInt(ini, "EqGain" + i, 0);
            MigrateOldEq(ini);

            NormalizeEnabled = ReadBool(ini, "NormalizeEnabled", NormalizeEnabled);
            NormalizeLevel = ClampLevel(ReadInt(ini, "NormalizeLevel", NormalizeLevel), SpeechnormExpansion.Length);
            GateEnabled = ReadBool(ini, "GateEnabled", GateEnabled);
            GateLevel = ClampLevel(ReadInt(ini, "GateLevel", GateLevel), GateThresholdDb.Length);
            GainEnabled = ReadBool(ini, "GainEnabled", GainEnabled);
            GainDb = ReadDouble(ini, "GainDb", GainDb);
        }

        public void Save(IniFile ini)
        {
            WriteBool(ini, "Enabled", Enabled);

            WriteBool(ini, "HighpassEnabled", HighpassEnabled);
            WriteInt(ini, "HighpassLevel", HighpassLevel);

            WriteBool(ini, "DenoiseEnabled", DenoiseEnabled);
            WriteInt(ini, "DenoiseLevel", DenoiseLevel);

            WriteBool(ini, "DeesserEnabled", DeesserEnabled);
            WriteInt(ini, "DeesserLevel", DeesserLevel);

            WriteBool(ini, "CompressorEnabled", CompressorEnabled);
            WriteInt(ini, "CompressorLevel", CompressorLevel);

            WriteBool(ini, "EqEnabled", EqEnabled);
            for (int i = 0; i < EqGain.Length; i++) WriteInt(ini, "EqGain" + i, EqGain[i]);

            WriteBool(ini, "NormalizeEnabled", NormalizeEnabled);
            WriteInt(ini, "NormalizeLevel", NormalizeLevel);
            WriteBool(ini, "GateEnabled", GateEnabled);
            WriteInt(ini, "GateLevel", GateLevel);
            WriteBool(ini, "GainEnabled", GainEnabled);
            ini.Write("Sound", "GainDb", GainDb.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private static int ClampLevel(int v, int count)
        {
            if (v < 0) return 0;
            if (v > count - 1) return count - 1;
            return v;
        }

        // ── Filter chain ──────────────────────────────────────────────────

        /// <summary>Builds the mpv <c>af</c> value (a single lavfi filtergraph)
        /// for these settings, mapping the friendly preset units to the actual
        /// ffmpeg parameters. Returns "" — which clears mpv's filters — when
        /// processing is off or bypassed. The safety limiter is always appended
        /// while processing is on. All numbers are formatted invariant (ffmpeg
        /// needs a '.' decimal separator regardless of the system locale).</summary>
        public static string BuildAf(SoundSettings s, bool bypass)
        {
            if (s == null || !s.Enabled || bypass)
                return "";

            CultureInfo ic = CultureInfo.InvariantCulture;
            List<string> f = new List<string>();

            // ── The loudness target goes FIRST (Gordan, 2026-08-09) ──────────
            //
            // His words: "Nekako bi tu normalizaciju trebalo staviti na početak
            // pa da se zahvati rade na maksimalnom volumenu jer je, osim šumova,
            // krckanja, šuštanja i tko zna čega, glasnoća veliki problem kod
            // zvučnih knjiga." He is right, and the reason is sharper than the
            // intuition: three stages below have thresholds written in ABSOLUTE
            // dB — afftdn's noise floor, the deesser's, the compressor's — while
            // the books they meet run from −7 to −22 LUFS. So "noise reduction,
            // medium" has meant something different on every book. Bringing the
            // level to the target first makes one preset mean one thing.
            //
            // Highpass and the EQ are LINEAR and do not care where the gain sits;
            // nothing about them changes. This is for the three that do.
            //
            // Only the STATIC gain moves. speechnorm stays at the far end, and
            // that distinction is the whole of it: speechnorm rides the gain over
            // time, so in front it would lift the quiet passages — the gaps
            // between sentences, which is where the noise lives — and hand
            // afftdn a noise floor that moves. That is exactly the "neke stvari
            // koje su bile uklonjene su se na većoj glasnoći pojačale" Gordan
            // hit, and he hit it because the static stage was being dropped
            // (see PropertiesForm.FillSettings) and speechnorm was the only
            // loudness control left to him.
            //
            // Clipping is not a concern at the front: mpv's graph is float, so a
            // lift here cannot overflow, and alimiter still owns the way out.
            if (s.GainEnabled && Math.Abs(s.GainDb) >= 0.5)
                f.Add("volume=" + s.GainDb.ToString("0.##", ic) + "dB");

            if (s.HighpassEnabled)
                f.Add("highpass=f=" + HighpassHz[ClampLevel(s.HighpassLevel, HighpassHz.Length)]);

            if (s.DenoiseEnabled)
                f.Add("afftdn=nr=" + DenoiseDb[ClampLevel(s.DenoiseLevel, DenoiseDb.Length)]);

            if (s.DeesserEnabled)
                f.Add("deesser=i=" + DeesserIntensity[ClampLevel(s.DeesserLevel, DeesserIntensity.Length)]
                    .ToString("0.###", ic));

            if (s.CompressorEnabled)
            {
                var c = Compressor[ClampLevel(s.CompressorLevel, Compressor.Length)];
                double thr = Math.Pow(10.0, c.Threshold / 20.0);   // dB → linear amplitude
                double makeup = Math.Pow(10.0, c.Makeup / 20.0);   // dB → gain factor
                f.Add("acompressor=threshold=" + thr.ToString("0.#####", ic) +
                      ":ratio=" + c.Ratio.ToString("0.##", ic) +
                      ":makeup=" + makeup.ToString("0.###", ic) +
                      ":attack=" + c.Attack.ToString(ic) +
                      ":release=" + c.Release.ToString(ic));
            }

            if (s.EqEnabled)
            {
                for (int i = 0; i < EqBandHz.Length && i < s.EqGain.Length; i++)
                {
                    if (s.EqGain[i] == 0) continue;
                    string hz = EqBandHz[i].ToString(ic);
                    string g = s.EqGain[i].ToString(ic);
                    f.Add(i == EqShelfIndex
                          ? "treble=g=" + g + ":f=" + hz
                          : "equalizer=f=" + hz + ":t=q:w="
                            + EqBellQ.ToString("0.##", ic) + ":g=" + g);
                }
            }

            if (s.NormalizeEnabled)
            {
                int nl = ClampLevel(s.NormalizeLevel, SpeechnormExpansion.Length);
                f.Add("speechnorm=e=" + SpeechnormExpansion[nl].ToString("0.0", ic) + ":p=0.95");
            }

            // The gate stands LAST of the shaping stages, and each half of that
            // is a decision:
            //   after afftdn and the highpass, so it works on what is left
            //     rather than having its threshold chase two moving things;
            //   after the EQ, because the treble ramp lifts as much as +8 dB and
            //     lifts the hiss with it — ahead of the EQ the gate would be
            //     handing back what it had just taken;
            //   after speechnorm, which is the important one. speechnorm raises
            //     quiet passages, which IS the pause. Ahead of it the gate would
            //     do work speechnorm immediately undoes. Last, the gate has the
            //     final word on the gaps.
            if (s.GateEnabled)
            {
                double thr = Math.Pow(10.0, GateThresholdDb[ClampLevel(s.GateLevel, GateThresholdDb.Length)] / 20.0);
                double range = Math.Pow(10.0, GateRangeDb / 20.0);
                f.Add("agate=threshold=" + thr.ToString("0.######", ic) +
                      ":ratio=" + GateRatio.ToString("0.##", ic) +
                      ":attack=" + GateAttackMs.ToString(ic) +
                      ":release=" + GateReleaseMs.ToString(ic) +
                      ":range=" + range.ToString("0.######", ic));
            }

            // Always-on safety limiter (level=false so it only caps peaks and
            // doesn't re-normalize loudness back up, undoing our chain).
            double limit = Math.Pow(10.0, LimiterCeilingDb / 20.0);
            f.Add("alimiter=limit=" + limit.ToString("0.#####", ic) + ":level=false");

            return "lavfi=[" + string.Join(",", f) + "]";
        }

        /// <summary>InvariantCulture, always: a decimal comma written on one
        /// machine must not read back as a different number on another.</summary>
        private static double ReadDouble(IniFile ini, string key, double def)
        {
            double v;
            return double.TryParse(ini.Read("Sound", key, null), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out v) ? v : def;
        }

        /// <summary>Carries a book saved with the old three-control equaliser
        /// over to the five bands, once.
        ///
        /// <para>A book analysed before 2026-08-09 has <c>EqVoice</c> (a bell at
        /// 3 kHz) and <c>EqTreble</c> (a shelf at 4 kHz) in its Book.ini. Those
        /// land on the 3500 bell and the 5000 shelf, which are the same controls
        /// in the same places to within half an octave. <c>EqBass</c> is
        /// deliberately DROPPED: it was a SHELF at 120 Hz, so it acted on
        /// everything below itself without limit, which is the highpass's job
        /// and not a band's. The 200 Hz bell that now stands in that slot is not
        /// its replacement — it is bounded on both sides and sits an octave
        /// higher, on the 160–250 Hz excess the samples actually measure — so
        /// carrying a shelf's number onto it would put the wrong correction in
        /// the wrong place.</para>
        ///
        /// <para>Only when the new keys are absent, so it cannot overwrite
        /// anything the reader has since set.</para></summary>
        private void MigrateOldEq(IniFile ini)
        {
            if (ini.Read("Sound", "EqGain0", null) != null) return;   // already migrated
            int voice = ReadInt(ini, "EqVoice", 0);
            int treble = ReadInt(ini, "EqTreble", 0);
            if (voice == 0 && treble == 0) return;
            for (int i = 0; i < EqBandHz.Length; i++)
            {
                if (EqBandHz[i] == 3500) EqGain[i] = voice;
                if (i == EqShelfIndex) EqGain[i] = treble;
            }
        }

        private static bool ReadBool(IniFile ini, string key, bool def)
        {
            return ini.Read("Sound", key, def ? "1" : "0") == "1";
        }

        private static int ReadInt(IniFile ini, string key, int def)
        {
            return int.TryParse(ini.Read("Sound", key, def.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
        }

        private static void WriteBool(IniFile ini, string key, bool value)
        {
            ini.Write("Sound", key, value ? "1" : "0");
        }

        private static void WriteInt(IniFile ini, string key, int value)
        {
            ini.Write("Sound", key, value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
