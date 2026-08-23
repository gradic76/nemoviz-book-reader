using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>How one voice is set up: reading speed (a percentage of the
    /// voice's own natural speed, 50..300, exactly like an audio book's),
    /// volume (0..100) and pitch (-10..10, SAPI-style).</summary>
    public struct VoicePrefs
    {
        public int Speed;
        public int Volume;
        public int Pitch;

        public VoicePrefs(int speed, int volume, int pitch) { Speed = speed; Volume = volume; Pitch = pitch; }

        /// <summary>What a voice starts at when nothing is remembered for it —
        /// deliberately NOT the settings of whatever voice was selected before.
        /// Voices differ enormously in how fast they sound at the same nominal
        /// speed, so carrying numbers across from another voice is worse than
        /// starting from the neutral middle.</summary>
        public static VoicePrefs Default { get { return new VoicePrefs(100, 100, 0); } }

        public VoicePrefs Clamped()
        {
            return new VoicePrefs(Clamp(Speed, 50, 300), Clamp(Volume, 0, 100), Clamp(Pitch, -10, 10));
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    /// <summary>
    /// Remembers speed / volume / pitch <b>per voice</b>. Both the global
    /// defaults (Settings.ini) and a single book (Book.ini) keep one of these, so
    /// picking a voice restores how that voice was last set up rather than
    /// inheriting the previous voice's numbers.
    ///
    /// Stored as an indexed list, not "name=value", because a voice name is user
    /// data ("eSpeak-hr+michael", "Microsoft Zira Desktop") and has no business
    /// being an INI key:
    /// <code>
    /// [TextVoices]
    /// Count=2
    /// S0=Microsoft Matej|115|90|0
    /// </code>
    /// The three numbers are read from the END of the line, so a name containing
    /// the separator still round-trips.
    ///
    /// <para><b>S, not V, and the letter is the whole migration.</b> Until
    /// 2026-08-23 the speed was written as nominal words per minute, and a file
    /// from then reads its lines under V. The two scales overlap — 200 is a
    /// sensible number under either — so a version marker was not optional: read
    /// as a percentage, a book set to 200 WPM would have come back at double
    /// speed. A V line is therefore converted through the RATE, which is what
    /// the engine was really being given, so the voice keeps sounding exactly as
    /// it did. The section is rewritten whole on save, so the old lines go.</para>
    /// </summary>
    public class VoicePrefsTable
    {
        private const string Section = "TextVoices";

        private readonly Dictionary<string, VoicePrefs> byVoice =
            new Dictionary<string, VoicePrefs>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> order = new List<string>();

        public int Count { get { return order.Count; } }

        public bool TryGet(string voice, out VoicePrefs prefs)
        {
            prefs = VoicePrefs.Default;
            if (string.IsNullOrEmpty(voice)) return false;
            if (!byVoice.TryGetValue(voice, out prefs)) return false;
            prefs = prefs.Clamped();
            return true;
        }

        /// <summary>The remembered setup for a voice, or <paramref name="fallback"/>
        /// when this table has never seen it.</summary>
        public VoicePrefs Get(string voice, VoicePrefs fallback)
        {
            VoicePrefs p;
            return TryGet(voice, out p) ? p : fallback;
        }

        public void Set(string voice, VoicePrefs prefs)
        {
            if (string.IsNullOrEmpty(voice)) return;
            if (!byVoice.ContainsKey(voice)) order.Add(voice);
            byVoice[voice] = prefs.Clamped();
        }

        /// <summary>Records a voice's setup only if it isn't known yet — used to
        /// carry the pre-existing single set of numbers onto the voice they were
        /// actually used with, so upgrading doesn't lose them.</summary>
        public void SetIfAbsent(string voice, VoicePrefs prefs)
        {
            if (string.IsNullOrEmpty(voice) || byVoice.ContainsKey(voice)) return;
            Set(voice, prefs);
        }

        /// <summary>Every voice this table knows, in the order they were added.</summary>
        public IEnumerable<KeyValuePair<string, VoicePrefs>> All()
        {
            foreach (string name in order)
                yield return new KeyValuePair<string, VoicePrefs>(name, byVoice[name]);
        }

        public void Load(IniFile ini)
        {
            byVoice.Clear();
            order.Clear();
            int n;
            int.TryParse(ini.Read(Section, "Count", "0"), out n);
            for (int i = 0; i < n; i++)
            {
                // S is this scale, V is the words-per-minute one. Per LINE and
                // not per file: a Settings.ini can hold both while a machine is
                // half-migrated, and each line says for itself which it is.
                bool old = false;
                string line = ini.Read(Section, "S" + i, "");
                if (string.IsNullOrEmpty(line)) { line = ini.Read(Section, "V" + i, ""); old = true; }
                if (string.IsNullOrEmpty(line)) continue;
                string[] p = line.Split('|');
                if (p.Length < 4) continue;
                int speed, vol, pitch;
                if (!int.TryParse(p[p.Length - 3], out speed)) continue;
                if (!int.TryParse(p[p.Length - 2], out vol)) continue;
                if (!int.TryParse(p[p.Length - 1], out pitch)) continue;
                if (old) speed = TtsReader.WpmToSpeed(speed);
                string name = string.Join("|", p, 0, p.Length - 3);
                if (name.Length == 0) continue;
                Set(name, new VoicePrefs(speed, vol, pitch));
            }
        }

        public void Save(IniFile ini)
        {
            // Cleared first, so the V lines of a file written before 2026-08-23
            // go rather than sitting under the S lines for ever, where a later
            // reader could pick them up if a name were ever dropped.
            ini.DeleteSection(Section);
            ini.Write(Section, "Count", order.Count.ToString());
            for (int i = 0; i < order.Count; i++)
            {
                VoicePrefs p = byVoice[order[i]];
                ini.Write(Section, "S" + i,
                          order[i] + "|" + p.Speed + "|" + p.Volume + "|" + p.Pitch);
            }
        }
    }
}
