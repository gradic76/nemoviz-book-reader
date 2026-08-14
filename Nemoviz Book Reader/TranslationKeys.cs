using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Where a reader's translation-service keys live.
    ///
    /// <para><b>Not machine-bound, and that is Gordan's decision (2026-08-14):</b>
    /// he uses several computers and wants one key to work on all of them, so DPAPI
    /// is out. The honest consequence, which the hint says out loud rather than
    /// hiding: the key is in effect plain text. Encrypting it with a key baked into
    /// the binary would be protection that only LOOKS like protection, because that
    /// key ships with the program.</para>
    ///
    /// <para><b>Why a file of its own rather than Settings.ini</b> — and the reason
    /// is not obscurity. It is that a secret should not live in the file people
    /// SHARE. <c>Settings.ini</c> is what a user opens to check something, attaches
    /// to a bug report, screenshots, or sends for diagnosis; that is the commonest
    /// way keys leak, and it is not an attacker route. For the same reason nothing
    /// goes near a book: book folders get copied, zipped and passed on, and
    /// <c>Book.ini</c> travels with the book.</para>
    ///
    /// <para>The file is base64 and hidden. <b>Neither is security and neither is
    /// claimed to be</b> — base64 stops somebody who opens the file for an unrelated
    /// reason from reading a key off the screen, and the hidden attribute keeps it
    /// out of a casual listing. Anyone who wants the key has it.</para>
    ///
    /// <para><b>One hard rule: a key never reaches a log.</b> Not
    /// <see cref="ImportDiag"/>, not an exception message, not a URL. Every service
    /// here takes the key in a HEADER for exactly that reason — a key in a query
    /// string ends up echoed back inside error text.</para>
    /// </summary>
    internal static class TranslationKeys
    {
        // Deliberately not "keys.dat". The name says nothing about the contents.
        private const string FileName = "nbr-services.dat";

        private static readonly string StorePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        // engine id -> key, read once and kept, because Settings opens and closes
        // often and this is three short lines of text.
        private static Dictionary<string, string> cache;

        private static Dictionary<string, string> Load()
        {
            if (cache != null) return cache;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(StorePath))
                {
                    foreach (string line in File.ReadAllLines(StorePath, Encoding.UTF8))
                    {
                        string s = line.Trim();
                        if (s.Length == 0 || s[0] == ';') continue;
                        int eq = s.IndexOf('=');
                        if (eq <= 0) continue;
                        string id = s.Substring(0, eq).Trim();
                        string val = s.Substring(eq + 1).Trim();
                        if (val.Length == 0) continue;
                        try { d[id] = Encoding.UTF8.GetString(Convert.FromBase64String(val)); }
                        catch { /* a line somebody edited by hand; skip it, keep the rest */ }
                    }
                }
            }
            catch { /* unreadable store behaves as no keys, never as a crash */ }
            cache = d;
            return cache;
        }

        /// <summary>The key for an engine, or null. Never logged, never shown.</summary>
        public static string Get(string engineId)
        {
            string v;
            return Load().TryGetValue(engineId ?? "", out v) ? v : null;
        }

        /// <summary>Is there a key for this engine? This is what the Settings row
        /// says out loud, because a reader who cannot see the dialog otherwise has
        /// to open it to find out whether they already did this.</summary>
        public static bool Has(string engineId)
        {
            return !string.IsNullOrEmpty(Get(engineId));
        }

        /// <summary>Stores a key, or removes it when handed null or blank.</summary>
        public static void Set(string engineId, string key)
        {
            if (string.IsNullOrEmpty(engineId)) return;
            var d = Load();
            if (string.IsNullOrEmpty(key)) d.Remove(engineId);
            else d[engineId] = key.Trim();
            Save();
        }

        private static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; Nemoviz Book Reader - service credentials.");
                sb.AppendLine("; Base64 is not encryption. Treat this file as you would a password.");
                foreach (var kv in Load())
                    sb.Append(kv.Key).Append('=')
                      .AppendLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(kv.Value)));

                // The hidden attribute has to come off before a write and go back
                // on after: File.WriteAllText onto a hidden file throws
                // UnauthorizedAccessException, which would look like a permissions
                // problem and is nothing of the sort.
                if (File.Exists(StorePath))
                    File.SetAttributes(StorePath, FileAttributes.Normal);
                File.WriteAllText(StorePath, sb.ToString(), new UTF8Encoding(false));
                File.SetAttributes(StorePath, FileAttributes.Hidden);
            }
            catch { /* a read-only install folder must not take the dialog down */ }
        }
    }
}
