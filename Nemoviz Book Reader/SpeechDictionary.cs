using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>How a rule looks for its text.</summary>
    public enum DictMatch
    {
        /// <summary>The word on its own — "John" does not match "Johnson".</summary>
        WholeWord = 0,
        /// <summary>Anywhere, including inside a word.</summary>
        Anywhere = 1,
        /// <summary>A regular expression, exactly as the user wrote it.</summary>
        Regex = 2
    }

    /// <summary>One line of a user's speech dictionary.</summary>
    public class DictRule
    {
        public bool Enabled = true;
        public string Pattern = "";
        /// <summary>What is spoken instead. Ignored when <see cref="Skip"/>.</summary>
        public string Replacement = "";
        public DictMatch Match = DictMatch.WholeWord;
        public bool CaseSensitive;
        /// <summary>Say nothing at all in its place.</summary>
        public bool Skip;
        /// <summary>The user's own note — why this rule exists.</summary>
        public string Comment = "";

        private Regex compiled;
        private string compiledFor;

        /// <summary>The rule as a regular expression, built once and kept. A
        /// timeout is part of it: the pattern comes from the user, and a regex that
        /// backtracks forever would take the reader down with it.</summary>
        public Regex Compiled()
        {
            string key = Match + "|" + CaseSensitive + "|" + Pattern;
            if (compiled != null && compiledFor == key) return compiled;

            RegexOptions opts = RegexOptions.CultureInvariant;
            if (!CaseSensitive) opts |= RegexOptions.IgnoreCase;
            string expr;
            switch (Match)
            {
                case DictMatch.Regex: expr = Pattern; break;
                case DictMatch.Anywhere: expr = Regex.Escape(Pattern); break;
                default: expr = @"\b" + Regex.Escape(Pattern) + @"\b"; break;
            }
            compiled = new Regex(expr, opts, TimeSpan.FromMilliseconds(50));
            compiledFor = key;
            return compiled;
        }

        /// <summary>Why this rule cannot be used, or null when it is fine. Shown
        /// when the user saves it, rather than failing silently while reading.</summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(Pattern)) return Localization.T("Dict.Error.NoPattern");
            try { Compiled().IsMatch("test"); }
            catch (ArgumentException ex) { return ex.Message; }
            catch (RegexMatchTimeoutException) { return Localization.T("Dict.Error.TooSlow"); }
            return null;
        }

        public DictRule Copy()
        {
            return new DictRule
            {
                Enabled = Enabled, Pattern = Pattern, Replacement = Replacement,
                Match = Match, CaseSensitive = CaseSensitive, Skip = Skip, Comment = Comment
            };
        }
    }

    /// <summary>
    /// A user's speech dictionary: what NBR should say instead of what the book
    /// says. It ships <b>empty</b> and stays that way until someone types
    /// something into it — no built-in rules, no abbreviation lists, nothing
    /// clever behind the user's back. What one reader wants ("John" read as
    /// "Džon"), another does not, and a third fixes stress on a particular engine
    /// with a comma or an apostrophe. Only their own rules run.
    ///
    /// <para><b>It rewrites only the text handed to the speech engine</b>, in
    /// <see cref="TtsReader"/>, and nothing else. The book's own text is untouched,
    /// so every stored character offset — reading position, headings, pages,
    /// bookmarks — stays exactly valid, and braille (and the on-screen display
    /// later) still show what the author wrote. It also runs after the text has
    /// been split into sentences, so a replacement containing a full stop cannot
    /// break a sentence in two.</para>
    ///
    /// <para>A replacement is passed on <b>literally</b>: the spaces, commas and
    /// apostrophes people use to bend an engine's stress are the point of the
    /// exercise, so nothing tidies them afterwards.</para>
    /// </summary>
    public class SpeechDictionary
    {
        public readonly List<DictRule> Rules = new List<DictRule>();

        /// <summary>Where this dictionary is stored; also its identity.</summary>
        public string Path { get; private set; }

        public SpeechDictionary(string path) { Path = path; }

        /// <summary>Applies every enabled rule, in order. Each rule sees the text
        /// as the rules before it left it, and each runs once over it — a rule
        /// never re-reads its own output, so a replacement that happens to match
        /// its own pattern cannot loop.</summary>
        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (DictRule r in Rules)
            {
                if (!r.Enabled || string.IsNullOrEmpty(r.Pattern)) continue;
                try
                {
                    // "Skip" leaves a space rather than nothing, so the words on
                    // either side don't run together into one.
                    text = r.Compiled().Replace(text, r.Skip ? " " : (r.Replacement ?? ""));
                }
                catch (RegexMatchTimeoutException) { }   // a runaway pattern is skipped, not fatal
                catch (ArgumentException) { }            // a bad replacement reference, likewise
            }
            return text;
        }

        // ── Storage ───────────────────────────────────────────────────────────
        // One rule per line, tab-separated, with the tabs and newlines inside a
        // field escaped. A plain text file on purpose: a user can read it, back it
        // up, and send it to someone else.
        private const string Header = "# NBR speech dictionary — enabled\tmatch\tcase\tskip\tpattern\treplacement\tcomment";

        public void Load()
        {
            Rules.Clear();
            try
            {
                if (!File.Exists(Path)) return;
                foreach (string raw in File.ReadAllLines(Path, Encoding.UTF8))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 6) continue;
                    var r = new DictRule
                    {
                        Enabled = f[0] == "1",
                        Match = f[1] == "2" ? DictMatch.Regex : (f[1] == "1" ? DictMatch.Anywhere : DictMatch.WholeWord),
                        CaseSensitive = f[2] == "1",
                        Skip = f[3] == "1",
                        Pattern = Unescape(f[4]),
                        Replacement = Unescape(f[5]),
                        Comment = f.Length > 6 ? Unescape(f[6]) : ""
                    };
                    Rules.Add(r);
                }
            }
            catch { }
        }

        public bool Save()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine(Header);
                foreach (DictRule r in Rules)
                {
                    sb.Append(r.Enabled ? '1' : '0').Append('\t')
                      .Append((int)r.Match).Append('\t')
                      .Append(r.CaseSensitive ? '1' : '0').Append('\t')
                      .Append(r.Skip ? '1' : '0').Append('\t')
                      .Append(Escape(r.Pattern)).Append('\t')
                      .Append(Escape(r.Replacement)).Append('\t')
                      .Append(Escape(r.Comment)).AppendLine();
                }
                File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The dictionaries on this machine and which of them apply right now.
    ///
    /// <para>Three scopes, because a rule's reason differs: a **voice** rule fixes
    /// how one engine says something, a **language** rule belongs to the language
    /// whatever voice reads it, and a **global** rule is the user's own habit. They
    /// are applied most specific first — voice, then language, then global — so a
    /// rule written for one voice takes precedence over the general one.</para>
    /// </summary>
    public static class SpeechDictionaries
    {
        private static readonly Dictionary<string, SpeechDictionary> cache =
            new Dictionary<string, SpeechDictionary>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where the .dic files live — beside Lang, in the app folder.</summary>
        public static string Folder
        {
            get { return UserData.SubFolder("Dictionaries"); }
        }

        public static SpeechDictionary Global { get { return Get(System.IO.Path.Combine(Folder, "global.dic")); } }

        public static SpeechDictionary ForLanguage(string language)
        {
            string code = LanguageDetector.Primary(language);
            if (code.Length == 0) return null;
            return Get(System.IO.Path.Combine(Folder, "lang-" + code + ".dic"));
        }

        public static SpeechDictionary ForVoice(string voice)
        {
            if (string.IsNullOrEmpty(voice)) return null;
            return Get(System.IO.Path.Combine(Folder, "voice-" + SafeName(voice) + ".dic"));
        }

        /// <summary>The dictionaries in force for a voice reading a language, most
        /// specific first. Never null; may be empty.</summary>
        public static List<SpeechDictionary> Active(string voice, string language)
        {
            var list = new List<SpeechDictionary>();
            SpeechDictionary d = ForVoice(voice);
            if (d != null) list.Add(d);
            d = ForLanguage(language);
            if (d != null) list.Add(d);
            list.Add(Global);
            return list;
        }

        /// <summary>Runs the active dictionaries over a piece of text.</summary>
        public static string Apply(List<SpeechDictionary> active, string text)
        {
            if (active == null) return text;
            foreach (SpeechDictionary d in active) text = d.Apply(text);
            return text;
        }

        /// <summary>Loaded once per file and kept, so reading a book doesn't touch
        /// the disk for every sentence. <see cref="Reload"/> after editing.</summary>
        private static SpeechDictionary Get(string path)
        {
            SpeechDictionary d;
            if (cache.TryGetValue(path, out d)) return d;
            d = new SpeechDictionary(path);
            d.Load();
            cache[path] = d;
            return d;
        }

        /// <summary>Forgets what is loaded, so the next read picks up edits.</summary>
        public static void Reload() { cache.Clear(); }

        /// <summary>A voice name as a file name — voices are called things like
        /// "eSpeak-hr+michael".</summary>
        private static string SafeName(string voice)
        {
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in voice)
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }
    }
}
