using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>How a particular target language wants to be written — the layer
    /// between the rules that hold for every language and the facts that hold for
    /// one book.
    ///
    /// <para><b>Why it is its own layer.</b> The neutral rules must serve 138
    /// languages, so they cannot say anything about cases, aspect or the dropping
    /// of pronouns. The book facts change with every book. What sits between is
    /// stable for a whole language and reusable across every book translated into
    /// it — which is also what makes it worth putting SECOND in the prompt: prompt
    /// caching pays for a stable prefix, and these two layers are identical for
    /// every Croatian book NBR will ever translate.</para>
    ///
    /// <para><b>Shipped as a file, one per language, in the Translation
    /// folder</b> -- Gordan's call, 2026-09-01: rules a reader cannot see are
    /// rules a reader cannot judge, and he wanted them editable without a
    /// rebuild, by him and by whoever writes the next language.
    ///
    /// <para>The objection this replaces was real and is answered rather than
    /// dropped. A file CAN go missing from an install, and a translator that
    /// silently loses its rules produces work that looks finished. So nothing
    /// here is silent: the file actually used, and its size, are written into
    /// translation.log for every book, and the same thing is on show in the
    /// rules dialog. An absent file is now a visible fact rather than an
    /// invisible one -- which is more than the compiled-in version offered,
    /// since that one could not be inspected at all.</para>
    ///
    /// <para><b>The Croatian text is Mila Kuran's</b>, used with her
    /// agreement and kept in her words, with two changes he approved:</para>
    ///
    /// <para><b>1. Names.</b> Hers said to keep foreign names in their original
    /// form, which is right and incomplete — it is the wording that failed here
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
        // Set once at startup, exactly as Localization is; the fallback keeps a
        // probe or a test harness working without one.
        private static string folder;
        private static readonly Dictionary<string, string> cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize(string rulesFolder)
        {
            folder = rulesFolder;
            cache.Clear();
        }

        public static string Folder
        {
            get
            {
                if (!string.IsNullOrEmpty(folder)) return folder;
                try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Translation"); }
                catch { return "Translation"; }
            }
        }

        /// <summary>Where a reader's OWN rules live, which is the whole point of
        /// the rules being files.
        ///
        /// <para>The shipped folder sits beside the program, and a program
        /// installed where Windows programs belong cannot be written to -- the
        /// same wall that moved the settings and the service keys out in August.
        /// A reader editing the rules there would need elevation to save, and the
        /// next installer would overwrite the result. So their copy goes in their
        /// own folder and WINS, and the shipped file stays untouched underneath as
        /// the reference and the way back: delete your copy and the default
        /// returns.</para></summary>
        public static string UserFolder
        {
            get { try { return UserData.File("Translation"); } catch { return ""; } }
        }

        /// <summary>The file a language's rules are actually read from -- the
        /// reader's own if they have made one, otherwise the shipped one. Public
        /// so the dialog and the log can name the file that was really used, which
        /// is the only honest way to show it.</summary>
        public static string PathFor(string targetLang)
        {
            string code = Normalize(targetLang);
            if (code.Length == 0) return "";
            try
            {
                string mine = Path.Combine(UserFolder, code + ".rules");
                if (File.Exists(mine)) return mine;
                return Path.Combine(Folder, code + ".rules");
            }
            catch { return ""; }
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
        /// Called when a translation starts: a reader who has just edited their
        /// rules expects the next book to use them, and telling them to restart
        /// NBR for it would be a poor answer to a file they were invited to
        /// edit.</summary>
        public static void Reload() { cache.Clear(); }

        /// <summary>Every language code that has a rules file, sorted. This is
        /// what the dialog's language list is built from -- it lists what is
        /// actually on disk rather than a list kept in step by hand.</summary>
        public static List<string> AvailableLanguages()
        {
            List<string> codes = new List<string>();
            foreach (string where in new[] { Folder, UserFolder })
            {
                try
                {
                    if (string.IsNullOrEmpty(where) || !Directory.Exists(where)) continue;
                    foreach (string file in Directory.GetFiles(where, "*.rules"))
                    {
                        string code = Path.GetFileNameWithoutExtension(file);
                        // A reader's own file for a language we ship is the SAME
                        // language, not a second entry in the list.
                        if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) codes.Add(code);
                    }
                }
                catch { }
            }
            codes.Sort(StringComparer.OrdinalIgnoreCase);
            return codes;
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
