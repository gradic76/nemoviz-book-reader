using System;
using System.IO;
using System.Collections.Generic;
namespace Nemoviz_Book_Reader
{
    public class IniFile
    {
        private string filePath;
        private Dictionary<string, Dictionary<string, string>> data;
        public IniFile(string path)
        {
            filePath = path;
            data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
                Load();
        }
        private void Load()
        {
            string currentSection = "";
            foreach (string line in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed == "") continue;
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentSection = trimmed.Substring(1, trimmed.Length - 2);
                    if (!data.ContainsKey(currentSection))
                        data[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else if (trimmed.Contains("="))
                {
                    int idx = trimmed.IndexOf('=');
                    string key = trimmed.Substring(0, idx).Trim();
                    string value = trimmed.Substring(idx + 1).Trim();
                    if (currentSection != "" && data.ContainsKey(currentSection))
                        data[currentSection][key] = value;
                }
            }
        }
        public string Read(string section, string key, string defaultValue = "")
        {
            if (data.ContainsKey(section) && data[section].ContainsKey(key))
                return data[section][key];
            return defaultValue;
        }
        public void Write(string section, string key, string value)
        {
            if (!data.ContainsKey(section))
                data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            data[section][key] = value;
            Save();
        }

        // ── Writing a LIST ────────────────────────────────────────────────
        //
        // Every Write above puts the WHOLE file back on disk, which is right for
        // a setting changed one at a time and disastrous for a list: writing a
        // book's chapters one key at a time meant one full file write per
        // chapter, of a file that grows with every one of them. Measured on a
        // 387-part book: 1402 ms, and it was the largest single cost in the
        // library's duration build — larger than reading all 387 audio files.
        //
        // Between Begin and End the writes only reach the dictionary, and the
        // file is written once at the end. Nested pairs are counted, so a caller
        // that already batches can call another that does too.
        private int batching;
        private bool pending;

        public void BeginUpdate() { batching++; }

        public void EndUpdate()
        {
            if (batching > 0) batching--;
            if (batching == 0 && pending) { pending = false; Save(); }
        }
        /// <summary>Empties a section (e.g. before rewriting a list that may
        /// have shrunk) so stale keys don't linger past the new count.</summary>
        public void DeleteSection(string section)
        {
            if (data.ContainsKey(section))
            {
                data[section].Clear();
                Save();
            }
        }
        private void Save()
        {
            if (batching > 0) { pending = true; return; }
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    return; // folder was deleted in the meantime (e.g. active book removed from the library)

                using (StreamWriter sw = new StreamWriter(filePath, false, System.Text.Encoding.UTF8))
                {
                    foreach (var section in data)
                    {
                        sw.WriteLine("[" + section.Key + "]");
                        foreach (var kvp in section.Value)
                            sw.WriteLine(kvp.Key + "=" + kvp.Value);
                        sw.WriteLine();
                    }
                }
            }
            catch (Exception)
            {
                // Silently ignore — writing to disk must never crash the app
                // (e.g. folder deleted, or the file is currently locked).
            }
        }
    }
}
