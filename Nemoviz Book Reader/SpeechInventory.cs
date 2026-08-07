using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// What speech this machine actually has. NBR ships to other users, whose
    /// mixes will differ — several languages, other vendors, possibly engines we
    /// don't drive yet — so the app looks the machine over instead of assuming
    /// its own developer's setup.
    ///
    /// The three engines NBR speaks with report their own voices
    /// (<see cref="CompositeSpeechBackend"/>); this class exists for the whole
    /// picture, above all for the **source we cannot use yet, SAPI 4**. It is
    /// 32-bit-only and pre-dates the automation objects everything else here is
    /// built on: driving it needs direct COM interop against <c>ITTSEnumW</c> /
    /// <c>ITTSCentralW</c> in the 32-bit host. Nothing on this machine installs
    /// such a driver, so the interop is deliberately NOT written blind. What is
    /// written is the detection: the day a SAPI 4 engine appears, NBR sees it,
    /// says so, and the only work left is the speaking interface — no hunting for
    /// where it hides.
    /// </summary>
    public static class SpeechInventory
    {
        /// <summary>One speech source found on the machine.</summary>
        public class Source
        {
            public string Name;        // "SAPI 5 (64-bit)", "SAPI 4 (32-bit)", …
            public string Registry;    // where it was found
            public int VoiceCount;     // voices seen (0 = present but empty)
            public bool Usable;        // does NBR have a backend for it?
            public string Note;        // why not, when it isn't usable
        }

        // The SAPI 4 TTS enumerator, the class a SAPI 4 driver has to register.
        // Its presence in HKCR\CLSID (with a DLL that exists) is what "SAPI 4 is
        // installed" means in practice.
        private const string Sapi4EnumClsid = "{D67C0280-C743-11CD-80E5-00AA003E4B50}";

        // SAPI 4 lists its engines as GUID-named subkeys directly under Voices;
        // SAPI 5 puts its voices one level deeper, under Voices\Tokens.
        private const string Sapi4VoicesKey = @"SOFTWARE\Microsoft\Speech\Voices";
        private const string Sapi5VoicesKey = @"SOFTWARE\Microsoft\Speech\Voices\Tokens";
        private const string OneCoreVoicesKey = @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens";

        /// <summary>Everything found, usable or not.</summary>
        public static List<Source> Scan()
        {
            var list = new List<Source>();

            AddTokenSource(list, "SAPI 5 (64-bit)", RegistryView.Registry64, Sapi5VoicesKey, true, null);
            AddTokenSource(list, "SAPI 5 (32-bit)", RegistryView.Registry32, Sapi5VoicesKey, true, null);
            AddTokenSource(list, "OneCore (64-bit)", RegistryView.Registry64, OneCoreVoicesKey, true, null);

            Source sapi4 = ScanSapi4();
            if (sapi4 != null) list.Add(sapi4);
            return list;
        }

        private static void AddTokenSource(List<Source> list, string name, RegistryView view,
                                           string key, bool usable, string note)
        {
            int n = CountSubKeys(view, key);
            if (n < 0) return;                       // the key isn't there at all
            list.Add(new Source
            {
                Name = name,
                Registry = @"HKLM\" + key + (view == RegistryView.Registry32 ? "  (32-bit view)" : ""),
                VoiceCount = n,
                Usable = usable,
                Note = note
            });
        }

        /// <summary>Looks for SAPI 4: the enumerator class registered to a DLL that
        /// exists, and/or engine modes listed under Voices. Both are checked in the
        /// 32-bit view — SAPI 4 never was 64-bit.</summary>
        private static Source ScanSapi4()
        {
            string server = ClsidServer(Sapi4EnumClsid);
            int modes = CountSapi4Modes();
            if (server == null && modes <= 0) return null;

            return new Source
            {
                Name = "SAPI 4 (32-bit)",
                Registry = server != null
                    ? @"HKCR\CLSID\" + Sapi4EnumClsid + " → " + server
                    : @"HKLM\" + Sapi4VoicesKey + "  (32-bit view)",
                VoiceCount = modes > 0 ? modes : 0,
                Usable = false,
                Note = "detected but not driven yet: SAPI 4 needs direct COM interop "
                     + "(ITTSEnumW/ITTSCentralW) in the 32-bit host"
            };
        }

        /// <summary>Subkeys of a registry key, or -1 when the key doesn't exist.</summary>
        private static int CountSubKeys(RegistryView view, string key)
        {
            try
            {
                using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey k = b.OpenSubKey(key))
                    return k == null ? -1 : k.GetSubKeyNames().Length;
            }
            catch { return -1; }
        }

        // Engine modes are the GUID-named subkeys directly under Voices; "Tokens"
        // (SAPI 5) is not one of them.
        private static int CountSapi4Modes()
        {
            int found = 0;
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey k = b.OpenSubKey(Sapi4VoicesKey))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                            if (sub.StartsWith("{", StringComparison.Ordinal)) found++;
                    }
                }
                catch { }
            }
            return found;
        }

        /// <summary>The in-process server registered for a CLSID, if its file is
        /// really there. Null when the class isn't registered or the DLL is gone.</summary>
        private static string ClsidServer(string clsid)
        {
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                    using (RegistryKey k = b.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32"))
                    {
                        string path = k?.GetValue("") as string;
                        if (string.IsNullOrEmpty(path)) continue;
                        string file = path.Trim('"');
                        if (File.Exists(file) || File.Exists(Environment.ExpandEnvironmentVariables(file)))
                            return file;
                    }
                }
                catch { }
            }
            return null;
        }

        private static bool logged;

        /// <summary>Writes the inventory to <c>%TEMP%\NBR-speech-inventory.log</c>,
        /// once per run. On someone else's machine this is the first thing worth
        /// looking at when a voice they expect isn't in the list.</summary>
        public static void LogOnce(IEnumerable<(string Name, string Engine, string Language)> catalog)
        {
            if (logged) return;
            logged = true;
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "NBR-speech-inventory.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                    + Describe(catalog));
            }
            catch { }
        }

        /// <summary>A readable summary, for the diagnostic log and (later) Help /
        /// Settings. Lists what NBR found and what it can do with it.</summary>
        public static string Describe(IEnumerable<(string Name, string Engine, string Language)> catalog = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Speech sources found on this machine:");
            sb.AppendLine("  (registry counts are a floor, not the truth: an engine may register a");
            sb.AppendLine("   custom token enumerator instead of listing its voices — RHVoice does —");
            sb.AppendLine("   so the list below the sources is what NBR really enumerated.)");
            foreach (Source s in Scan())
            {
                sb.Append("  ").Append(s.Name)
                  .Append("  voices: ").Append(s.VoiceCount)
                  .Append(s.Usable ? "  [in use]" : "  [not usable]").AppendLine();
                sb.Append("      ").AppendLine(s.Registry);
                if (!string.IsNullOrEmpty(s.Note)) sb.Append("      ").AppendLine(s.Note);
            }
            if (catalog != null)
            {
                sb.AppendLine("Voices NBR can speak with:");
                foreach (var c in catalog)
                    sb.Append("  ").Append(c.Name).Append("  |  ").Append(c.Engine)
                      .Append("  |  ").AppendLine(c.Language);
            }
            return sb.ToString();
        }
    }
}
