using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>How a particular target language wants to be written -- the layer
    /// between the rules that hold for every language and the facts that hold for
    /// one book.
    ///
    /// <para><b>Why it is its own layer.</b> The neutral rules must serve 138
    /// languages, so they cannot say anything about cases, aspect or the dropping
    /// of pronouns. The book facts change with every book. What sits between is
    /// stable for a whole language and reusable across every book translated into
    /// it -- which is also what makes it worth putting SECOND in the prompt: prompt
    /// caching pays for a stable prefix, and these two layers are identical for
    /// every Croatian book NBR will ever translate.</para>
    ///
    /// <para><b>ONE FOLDER, AND IT IS THE READER'S OWN</b> (Gordan, 2026-09-02).
    /// The first version shipped a copy beside the program and read the reader's
    /// copy in preference to it. He threw that out, and he is right: two files of
    /// the same name in two places, with a rule about which wins, is complication
    /// that buys nothing. *"Ako jezik postoji datoteka je tamo, ako ne postoji,
    /// nema je."* So rules live in %APPDATA%\Nemoviz Book Reader\Translation and
    /// nowhere else -- which is also where they survive an update and an
    /// uninstall, the thing the program's own folder cannot do.</para>
    ///
    /// <para><b>The two we supply are EMBEDDED, not shipped as files</b>, and
    /// written into that folder once. Embedded rather than copied so that there is
    /// never a second .rules on disk to wonder about; once rather than on every
    /// launch so that deleting one is a decision that sticks.</para>
    ///
    /// <para><b>The Croatian and Serbian text is Mila Kuran's</b>, used with her
    /// agreement and kept in her words, with two changes Gordan approved:</para>
    ///
    /// <para><b>1. Names.</b> Hers said to keep foreign names in their original
    /// form, which is right and incomplete -- it is the wording that failed here
    /// once already. A model obeying it literally will not DECLINE the name, and
    /// Croatian then pads around the hole: "u programu Tobi" where a translator
    /// writes "u Tobiju". Keeping a name and inflecting it are different things, so
    /// the rule now says both.</para>
    ///
    /// <para><b>2. One sentence per line is GONE.</b> It suits her workflow, which
    /// is a chat window she reads and edits. It would break ours: the paragraph
    /// count is one of the checks, measured catching a model that returned 63
    /// source lines as 51 by merging and another that returned 68 by splitting, and
    /// deliberately changing the line structure would retire that check and make
    /// reassembly guesswork. NBR splits into sentences itself for reading and
    /// braille, so nothing is lost.</para></summary>
    internal static class TranslationRules
    {
        /// <summary>The languages a rulebook is supplied for. Embedded under
        /// Translation\ and written into the reader's folder on first run.</summary>
        /// <summary>The file whose rules hold for EVERY language, sent ahead of
        /// the language one. Named with a leading underscore so it sorts to the top
        /// of the folder and can never collide with a language code.</summary>
        public const string CommonCode = "_common";

        private static readonly string[] Supplied = { CommonCode, "hr", "sr" };

        private static string folder;
        private static readonly Dictionary<string, string> cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize(string rulesFolder)
        {
            folder = rulesFolder;
            cache.Clear();
        }

        /// <summary>Where every .rules file lives. There is no second location and
        /// therefore no question about which one wins.</summary>
        public static string Folder
        {
            get
            {
                if (!string.IsNullOrEmpty(folder)) return folder;
                try { return UserData.File("Translation"); } catch { return "Translation"; }
            }
        }

        /// <summary>The file a language's rules are read from, whether or not it
        /// exists. Public so the dialog and the log can name it.</summary>
        public static string PathFor(string targetLang)
        {
            string code = Normalize(targetLang);
            if (code.Length == 0) return "";
            try { return Path.Combine(Folder, code + ".rules"); }
            catch { return ""; }
        }

        /// <summary>Whether a rules FILE exists for this language -- which is not
        /// the same question as whether it has any rules in it. A file holding only
        /// its header is a language somebody has started and not finished, and the
        /// dialog must be able to tell that from a language nobody has touched.
        /// </summary>
        public static bool FileExists(string targetLang)
        {
            try { string p = PathFor(targetLang); return p.Length > 0 && File.Exists(p); }
            catch { return false; }
        }

        /// <summary>The rules for a target language, or an empty string where none
        /// have been written. An empty block is not a failure -- it is every
        /// language except the ones somebody has sat down and done.</summary>
        /// <summary>What is sent for a target language: the rules that hold for
        /// every language, then that language's own, in that order.
        ///
        /// <para><b>Two files rather than one</b> (Gordan, 2026-09-02). Written as
        /// one, every language repeated the same ninety lines about fidelity and
        /// register, and writing rules for a new language meant writing a book. Now
        /// the neutral half is written once and a language file carries only what is
        /// true of that language -- Croatian went from 205 lines to 77.</para>
        ///
        /// <para>The order matters for the same reason the prompt is layered at all:
        /// caching pays for a stable prefix, and the common half is the most stable
        /// thing in the whole prompt -- identical for every book in every
        /// language.</para></summary>
        public static string For(string targetLang)
        {
            string code = Normalize(targetLang);
            if (code.Length == 0) return "";
            string hit;
            if (cache.TryGetValue(code, out hit)) return hit;
            string text = Read(PathFor(code));
            if (code != CommonCode)
            {
                string common = Read(PathFor(CommonCode));
                if (common.Length > 0)
                    text = text.Length > 0 ? common + Environment.NewLine + Environment.NewLine + text : common;
            }
            cache[code] = text;
            return text;
        }

        /// <summary>What ONE file holds, without the common rules joined to it.
        /// The dialog shows a file; For() builds a prompt. They are different
        /// questions and conflating them would make every language appear to
        /// carry the common text as its own.</summary>
        public static string OwnRules(string targetLang)
        {
            return Read(PathFor(targetLang));
        }

        /// <summary>Whether this LANGUAGE has rules of its own. Deliberately not
        /// "does For() return anything" -- the common file makes that true of all
        /// 138, and the dialog orders the list by this.</summary>
        public static bool Has(string targetLang)
        {
            string code = Normalize(targetLang);
            return code.Length > 0 && code != CommonCode && Read(PathFor(code)).Length > 0;
        }

        /// <summary>Forgets what has been read, so the next ask goes to disk.
        /// Called when a translation starts and whenever the dialog changes
        /// language: a reader who has just edited their rules expects the next book
        /// to use them, and telling them to restart NBR for it would be a poor
        /// answer to a file they were invited to edit.</summary>
        public static void Reload() { cache.Clear(); }

        /// <summary>Every language code that has a rules file, sorted. This is what
        /// the dialog's language list is ordered by -- what is actually on disk
        /// rather than a list kept in step by hand.</summary>
        public static List<string> AvailableLanguages()
        {
            List<string> codes = new List<string>();
            try
            {
                if (Directory.Exists(Folder))
                    foreach (string file in Directory.GetFiles(Folder, "*.rules"))
                    {
                        // A parked "<code> (new).rules" is a copy, not a language.
                        // Normalize refuses anything that is not letters, which is the
                        // same test the loader uses to keep a code inside the folder.
                        string code = Path.GetFileNameWithoutExtension(file);
                        if (Normalize(code) != code.ToLowerInvariant()) continue;
                        if (code == CommonCode) continue;
                        codes.Add(code);
                    }
            }
            catch { }
            codes.Sort(StringComparer.OrdinalIgnoreCase);
            return codes;
        }


        /// <summary>The file a newer supplied rulebook is parked in when the
        /// reader has edited their own. Never read by the translator -- it is a
        /// copy left where they will find it.</summary>
        public static string PendingPath(string targetLang)
        {
            string c = Normalize(targetLang);
            if (c.Length == 0) return "";
            try { return Path.Combine(Folder, c + " (new).rules"); }
            catch { return ""; }
        }

        public static bool HasPending(string targetLang)
        {
            try { string p = PendingPath(targetLang); return p.Length > 0 && File.Exists(p); }
            catch { return false; }
        }

        private static string Supply(string code)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string name = typeof(TranslationRules).Namespace + ".Translation." + code + ".rules";
                using (Stream s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (var rd = new StreamReader(s, new UTF8Encoding(false))) return rd.ReadToEnd();
                }
            }
            catch { return null; }
        }

        /// <summary>The version stamped in a rulebook header, or 0 for a file that
        /// carries none -- which is every file a reader wrote themselves, and is why
        /// an unstamped file is never replaced.</summary>
        private static int VersionOf(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            const string mark = "NBR-Rules-Version:";
            int i = text.IndexOf(mark, StringComparison.Ordinal);
            if (i < 0) return 0;
            int j = i + mark.Length;
            int n = 0; bool any = false;
            while (j < text.Length && text[j] == 32) j++;
            while (j < text.Length && text[j] >= 48 && text[j] <= 57) { n = n * 10 + (text[j] - 48); j++; any = true; }
            return any ? n : 0;
        }


        /// <summary>Whether two rulebooks are the same but for their version
        /// stamp. This is what recognises a file NBR wrote before the stamp
        /// existed -- Gordan's own hr.rules and sr.rules, seeded the day before --
        /// as ours rather than as something a reader had edited, and it also
        /// recovers when Settings.ini has been lost. The fingerprint stays the
        /// primary test, because it is the only one that can tell an UNEDITED old
        /// version from an edited one once the supplied text really changes.</summary>
        private static bool SameButForStamp(string a, string b)
        {
            return Destamp(a) == Destamp(b);
        }

        private static string Destamp(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            foreach (string line in text.Replace("\r\n", "\n").Split((char)10))
            {
                if (line.IndexOf("NBR-Rules-Version", StringComparison.Ordinal) >= 0) continue;
                sb.Append(line).Append((char)10);
            }
            return sb.ToString();
        }

        private static string Fingerprint(string text)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? ""));
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        /// <summary>Brings the supplied rulebooks up to date, and returns the
        /// languages where a newer one is waiting because the reader had changed
        /// theirs.
        ///
        /// <para><b>Three cases, and only the third needed deciding</b> (Gordan,
        /// 2026-09-02). No file: write ours. A file we wrote and nobody has touched:
        /// replace it, since nothing of theirs is lost, and that is the common case.
        /// A file they HAVE edited: never touch it -- park the new one beside it and
        /// say so in the dialog.</para>
        ///
        /// <para><b>Prose cannot be merged automatically, which is why there is no
        /// attempt at it.</b> These are instructions with a stated order of priority;
        /// two editions spliced together can contradict each other, and nothing
        /// reports it -- the book simply reads worse.</para>
        ///
        /// <para><b>The installer cannot do this either.</b> The folder is per USER
        /// and the installer runs once for the machine, elevated; on a machine with
        /// three accounts it would have to reach into three profiles. It also runs
        /// before anyone has launched NBR, and after somebody has deleted their file
        /// on purpose.</para>
        ///
        /// <para>Whether the reader edited it is a FACT here rather than a guess: the
        /// fingerprint of what we last wrote is kept in Settings.ini.</para></summary>
        public static List<string> UpdateSupplied(AppSettings settings)
        {
            var parked = new List<string>();
            try { Directory.CreateDirectory(Folder); } catch { }
            foreach (string code in Supplied)
            {
                string supplied = Supply(code);
                if (string.IsNullOrEmpty(supplied)) continue;
                string path = Path.Combine(Folder, code + ".rules");

                if (!File.Exists(path))
                {
                    // PER FILE, NOT PER INSTALL. A rulebook is missing for one of
                    // two reasons: we have never written it on this machine, or the
                    // reader deleted it. The fingerprint tells them apart -- we only
                    // have one for a file we wrote. A single "already seeded" flag
                    // got this wrong the moment a THIRD supplied file appeared:
                    // _common.rules would never have reached anybody who already had
                    // NBR, because their flag was set the day before it existed.
                    if (settings != null && settings.GetRulesStamp(code).Length > 0) continue;
                    try
                    {
                        File.WriteAllText(path, supplied, new UTF8Encoding(false));
                        if (settings != null) settings.SetRulesStamp(code, Fingerprint(supplied));
                    }
                    catch { }
                    continue;
                }

                string mine;
                try { mine = File.ReadAllText(path, Encoding.UTF8); } catch { continue; }
                if (VersionOf(mine) >= VersionOf(supplied)) continue;

                string wrote = settings == null ? "" : settings.GetRulesStamp(code);
                bool untouched = (wrote.Length > 0 && wrote == Fingerprint(mine))
                                 || SameButForStamp(mine, supplied);
                try
                {
                    if (untouched)
                    {
                        File.WriteAllText(path, supplied, new UTF8Encoding(false));
                        if (settings != null) settings.SetRulesStamp(code, Fingerprint(supplied));
                    }
                    else
                    {
                        // ONCE PER VERSION. Without this the copy would be written on
                        // every launch, so deleting it -- the reader saying they have
                        // dealt with it -- would be undone by the next start, and the
                        // dialog would go on announcing it for ever.
                        string mark = "parked-" + code;
                        string already = settings == null ? "" : settings.GetRulesStamp(mark);
                        if (already == VersionOf(supplied).ToString()) continue;
                        File.WriteAllText(PendingPath(code), supplied, new UTF8Encoding(false));
                        if (settings != null) settings.SetRulesStamp(mark, VersionOf(supplied).ToString());
                        parked.Add(code);
                    }
                }
                catch { }
            }
            cache.Clear();
            return parked;
        }

        /// <summary>Creates an empty rules file for a language, carrying nothing but
        /// its own instructions. The prose comes from the language files so it can
        /// be read by whoever is about to write the rules; the comment character and
        /// the layout are added here, because they are the FILE's business and not a
        /// translator's.</summary>
        public static bool CreateEmpty(string targetLang, string instructions, string languageName)
        {
            string path = PathFor(targetLang);
            if (path.Length == 0 || File.Exists(path)) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                string bar = ";" + new string('-', 76);
                var sb = new StringBuilder();
                sb.AppendLine(bar);
                sb.AppendLine("; Nemoviz Book Reader -- translation rules for: "
                              + Normalize(targetLang)
                              + (string.IsNullOrEmpty(languageName) ? "" : "  (" + languageName + ")"));
                sb.AppendLine(";");
                foreach (string line in (instructions ?? "").Replace("\r\n", "\n").Split('\n'))
                    sb.AppendLine(line.Length == 0 ? ";" : "; " + line);
                sb.AppendLine(bar);
                sb.AppendLine();
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                cache.Clear();
                return true;
            }
            catch { return false; }
        }

        // sr-Cyrl and sr-Latn both read sr.rules: ONE Serbian, in Latin, and that
        // is Gordan's call -- the reason is written into the file itself.
        private static string Normalize(string targetLang)
        {
            if (string.IsNullOrEmpty(targetLang)) return "";
            string code = targetLang.Trim().ToLowerInvariant();
            if (code == CommonCode) return code;   // not a language, but a file
            int dash = code.IndexOfAny(new[] { '-', '_' });
            if (dash > 0) code = code.Substring(0, dash);
            // A language code cannot contain a path separator; refusing one here
            // means a book's stored language can never reach outside the folder.
            foreach (char c in code)
                if (!char.IsLetter(c)) return "";
            return code;
        }

        /// <summary>Reads a rules file and drops its comment lines. Everything else
        /// goes to the service verbatim, blank lines included: the paragraph shape
        /// is part of the instruction.
        ///
        /// <para><b>TWO comment characters, and the difference is for the reader,
        /// not for us</b> -- both are dropped identically. ';' heads the file's own
        /// notes, as in a .lang. '#' marks a rule PARKED: text that was in force
        /// once, or came with the document and was taken out on purpose, kept where
        /// it can be found and put back by deleting one character. Gordan asked for
        /// exactly that (2026-09-01) so that nothing we remove is lost, and it costs
        /// the prompt nothing, since neither kind is ever sent.</para></summary>
        private static string Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                StringBuilder sb = new StringBuilder();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string head = line.TrimStart();
                    if (head.StartsWith(";") || head.StartsWith("#")) continue;
                    sb.AppendLine(line);
                }
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }
    }
}
