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
        private static readonly string[] Supplied = { "hr", "sr" };

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
        public static string For(string targetLang)
        {
            string code = Normalize(targetLang);
            if (code.Length == 0) return "";
            string hit;
            if (cache.TryGetValue(code, out hit)) return hit;
            string text = Read(PathFor(code));
            cache[code] = text;
            return text;
        }

        public static bool Has(string targetLang) { return For(targetLang).Length > 0; }

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
                        codes.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch { }
            codes.Sort(StringComparer.OrdinalIgnoreCase);
            return codes;
        }

        /// <summary>Writes the supplied rulebooks into the reader's folder, for any
        /// that are not there. Call ONCE -- AppSettings.RulesSeeded remembers that
        /// it happened -- so that deleting one of them is a decision that stays
        /// made rather than being undone by the next launch.</summary>
        public static int SeedSupplied()
        {
            int written = 0;
            try
            {
                Directory.CreateDirectory(Folder);
                Assembly asm = Assembly.GetExecutingAssembly();
                string prefix = typeof(TranslationRules).Namespace + ".Translation.";
                foreach (string code in Supplied)
                {
                    string path = Path.Combine(Folder, code + ".rules");
                    if (File.Exists(path)) continue;
                    using (Stream s = asm.GetManifestResourceStream(prefix + code + ".rules"))
                    {
                        if (s == null) continue;
                        using (var r = new StreamReader(s, new UTF8Encoding(false)))
                            File.WriteAllText(path, r.ReadToEnd(), new UTF8Encoding(false));
                    }
                    written++;
                }
            }
            catch { }
            if (written > 0) cache.Clear();
            return written;
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
