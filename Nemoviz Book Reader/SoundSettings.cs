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

        // EQ band centre frequencies (Hz).
        //
        // TREBLE MOVED 10 kHz -> 4 kHz (2026-08-08). Gordan, listening to a dull
        // recording with the treble at +8: "kao da naš ekvilajzer radi izvan
        // frekvencija koje se koriste". He was exactly right, and measuring the
        // six samples band by band shows why — dB relative to the whole signal:
        //
        //                  150-400  400-1k   1k-2k   2k-4k   4k-8k    >8k
        //   the good two      -3.9    -4.4   -10.0   -14.2   -17.4   -21.5
        //   the bad four      -2.8    -5.5   -13.8   -21.1   -30.3   -44.1
        //
        // Below 1 kHz all six are the same, which is why cutting bass changed
        // nothing he could hear. And at 10 kHz the damaged recordings are 40 to
        // 48 dB down: a shelf there lifts something inaudible to something
        // slightly less inaudible. The loss that MATTERS opens from about 1 kHz
        // and is worst from 4 kHz up, so that is where the shelf belongs.
        //
        // Honest limit, worth saying out loud: what was never recorded cannot be
        // restored. A 4 kHz shelf reaches real content; nothing reaches content
        // above 8 kHz that a cassette or a 64 kbps encoder threw away.
        public const int EqBassHz = 120;
        public const int EqVoiceHz = 3000;
        public const int EqTrebleHz = 4000;

        // The always-on safety limiter ceiling (dBFS). Fixed, not user-editable:
        // "set and forget", and we want the hottest clean output possible.
        public const double LimiterCeilingDb = -0.1;

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
        public int EqBass;              // dB
        public int EqVoice;             // dB
        public int EqTreble;            // dB

        public bool NormalizeEnabled;
        public int NormalizeLevel;      // 0..4

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
            EqBass = 0;
            EqVoice = 0;
            EqTreble = 0;

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
            EqBass = ReadInt(ini, "EqBass", EqBass);
            EqVoice = ReadInt(ini, "EqVoice", EqVoice);
            EqTreble = ReadInt(ini, "EqTreble", EqTreble);

            NormalizeEnabled = ReadBool(ini, "NormalizeEnabled", NormalizeEnabled);
            NormalizeLevel = ClampLevel(ReadInt(ini, "NormalizeLevel", NormalizeLevel), SpeechnormExpansion.Length);
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
            WriteInt(ini, "EqBass", EqBass);
            WriteInt(ini, "EqVoice", EqVoice);
            WriteInt(ini, "EqTreble", EqTreble);

            WriteBool(ini, "NormalizeEnabled", NormalizeEnabled);
            WriteInt(ini, "NormalizeLevel", NormalizeLevel);
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
                if (s.EqBass != 0)
                    f.Add("bass=g=" + s.EqBass.ToString(ic) + ":f=" + EqBassHz.ToString(ic));
                if (s.EqVoice != 0)
                    f.Add("equalizer=f=" + EqVoiceHz.ToString(ic) + ":t=q:w=1.5:g=" + s.EqVoice.ToString(ic));
                if (s.EqTreble != 0)
                    f.Add("treble=g=" + s.EqTreble.ToString(ic) + ":f=" + EqTrebleHz.ToString(ic));
            }

            if (s.NormalizeEnabled)
            {
                int nl = ClampLevel(s.NormalizeLevel, SpeechnormExpansion.Length);
                f.Add("speechnorm=e=" + SpeechnormExpansion[nl].ToString("0.0", ic) + ":p=0.95");
            }

            // Always-on safety limiter (level=false so it only caps peaks and
            // doesn't re-normalize loudness back up, undoing our chain).
            double limit = Math.Pow(10.0, LimiterCeilingDb / 20.0);
            f.Add("alimiter=limit=" + limit.ToString("0.#####", ic) + ":level=false");

            return "lavfi=[" + string.Join(",", f) + "]";
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
