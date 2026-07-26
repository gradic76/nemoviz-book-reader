using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>How one voice is set up: reading speed (nominal words per
    /// minute), volume (0..100) and pitch (-10..10, SAPI-style).</summary>
    public struct VoicePrefs
    {
        public int Wpm;
        public int Volume;
        public int Pitch;

        public VoicePrefs(int wpm, int volume, int pitch) { Wpm = wpm; Volume = volume; Pitch = pitch; }

        /// <summary>What a voice starts at when nothing is remembered for it —
        /// deliberately NOT the settings of whatever voice was selected before.
        /// Voices differ enormously in how fast they sound at the same nominal
        /// speed, so carrying numbers across from another voice is worse than
        /// starting from the neutral middle.</summary>
        public static VoicePrefs Default { get { return new VoicePrefs(175, 100, 0); } }

        public VoicePrefs Clamped()
        {
            return new VoicePrefs(Clamp(Wpm, 80, 400), Clamp(Volume, 0, 100), Clamp(Pitch, -10, 10));
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
    /// V0=Microsoft Matej|200|90|0
    /// </code>
    /// The three numbers are read from the END of the line, so a name containing
    /// the separator still round-trips.
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
                string line = ini.Read(Section, "V" + i, "");
                if (string.IsNullOrEmpty(line)) continue;
                string[] p = line.Split('|');
                if (p.Length < 4) continue;
                int wpm, vol, pitch;
                if (!int.TryParse(p[p.Length - 3], out wpm)) continue;
                if (!int.TryParse(p[p.Length - 2], out vol)) continue;
                if (!int.TryParse(p[p.Length - 1], out pitch)) continue;
                string name = string.Join("|", p, 0, p.Length - 3);
                if (name.Length == 0) continue;
                Set(name, new VoicePrefs(wpm, vol, pitch));
            }
        }

        public void Save(IniFile ini)
        {
            ini.Write(Section, "Count", order.Count.ToString());
            for (int i = 0; i < order.Count; i++)
            {
                VoicePrefs p = byVoice[order[i]];
                ini.Write(Section, "V" + i,
                          order[i] + "|" + p.Wpm + "|" + p.Volume + "|" + p.Pitch);
            }
        }
    }
}
