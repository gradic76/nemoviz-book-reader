using System;
using System.Collections.Generic;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Simple localization system. Language files are plain UTF-8 text files
    /// with "Key=Value" lines, named "{code}.lang" (e.g. "en.lang", "hr.lang"),
    /// placed in a folder that is scanned automatically. No further structure
    /// or build step is required — dropping a new .lang file in the folder
    /// makes it available.
    /// </summary>
    public static class Localization
    {
        private const string FallbackCode = "en";

        private static Dictionary<string, string> currentStrings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> fallbackStrings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string CurrentLanguageCode { get; private set; } = FallbackCode;
        public static string CurrentLanguageName { get; private set; } = "English";
        public static List<(string Code, string Name)> AvailableLanguages { get; private set; }
            = new List<(string Code, string Name)>();

        private static string langFolder;

        /// <summary>
        /// Scans langFolderPath for *.lang files, builds the list of available
        /// languages, and activates preferredCode (falling back to English,
        /// and finally to raw keys, if something is missing).
        /// </summary>
        public static void Initialize(string langFolderPath, string preferredCode)
        {
            langFolder = langFolderPath;
            AvailableLanguages.Clear();
            fallbackStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!Directory.Exists(langFolder))
                    Directory.CreateDirectory(langFolder);
            }
            catch
            {
                // If the folder can't be created, we continue with the built-in
                // fallback keys (T() just returns the key itself if nothing is found).
            }

            if (Directory.Exists(langFolder))
            {
                foreach (string file in Directory.GetFiles(langFolder, "*.lang"))
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    Dictionary<string, string> strings = LoadLangFile(file);

                    string name = strings.ContainsKey("LanguageName") ? strings["LanguageName"] : code;
                    AvailableLanguages.Add((code, name));

                    if (string.Equals(code, FallbackCode, StringComparison.OrdinalIgnoreCase))
                        fallbackStrings = strings;
                }
            }

            LoadLanguage(string.IsNullOrEmpty(preferredCode) ? FallbackCode : preferredCode);
        }

        /// <summary>
        /// Switches the active language. If the requested code has no matching
        /// .lang file, falls back to English strings.
        /// </summary>
        public static void LoadLanguage(string code)
        {
            string filePath = Path.Combine(langFolder ?? "", code + ".lang");

            if (!string.IsNullOrEmpty(langFolder) && File.Exists(filePath))
            {
                currentStrings = LoadLangFile(filePath);
                CurrentLanguageCode = code;
                CurrentLanguageName = currentStrings.ContainsKey("LanguageName")
                    ? currentStrings["LanguageName"] : code;
            }
            else
            {
                currentStrings = fallbackStrings;
                CurrentLanguageCode = FallbackCode;
                CurrentLanguageName = fallbackStrings.ContainsKey("LanguageName")
                    ? fallbackStrings["LanguageName"] : "English";
            }
        }

        private static Dictionary<string, string> LoadLangFile(string filePath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string rawLine in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;

                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;

                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    value = value.Replace("\\r\\n", "\r\n").Replace("\\n", "\n");
                    result[key] = value;
                }
            }
            catch
            {
                // Unreadable/corrupted .lang file — simply skipped.
            }
            return result;
        }

        /// <summary>Looks up a key in the active language, then English, then returns the key itself.</summary>
        public static string T(string key)
        {
            if (currentStrings.TryGetValue(key, out string value))
                return value;
            if (fallbackStrings.TryGetValue(key, out string fallback))
                return fallback;
            return key;
        }

        /// <summary>Looks up a key and formats it with the given arguments (string.Format semantics).</summary>
        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }
    }
}
