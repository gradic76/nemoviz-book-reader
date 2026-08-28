using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Gathers everything NBR already writes about itself into ONE file
    /// a tester can attach to a mail.
    ///
    /// <para><b>Nothing here is a new log.</b> The crash handler, the hang
    /// watchdog, the import, the speech host and the speech inventory have all
    /// been writing to <c>%TEMP%</c> since they were built — seven files, and on
    /// this machine one of them had reached 970 kB unread. What was missing was
    /// never the logging; it was that <c>%TEMP%</c> is the one folder a reader
    /// will never go to, and the one Windows empties. So this collects, and
    /// changes nothing about who writes what.</para>
    ///
    /// <para><b>Why not write the log somewhere findable in the first place.</b>
    /// That was the other option (Gordan, 2026-08-28) and it is worse in the
    /// ordinary case: it puts a file nobody asked for into somebody's Documents
    /// for ever, to be useful on the rare day something goes wrong. A report
    /// created only when it is asked for lands where the reader chose, once.
    /// <b>The root of C: was ruled out by measurement, not by doctrine</b> — a
    /// write there is refused even on the developer's own machine, and NBR's
    /// manifest disables the virtualisation that lets some programs seem to get
    /// away with it.</para>
    ///
    /// <para><b>What is deliberately NOT in it: <c>nbr-services.dat</c></b>,
    /// which holds the API keys and the Azure pair. A reader sending a fault
    /// report must not be sending their credentials with it, and the way to
    /// guarantee that is for the collector never to know the file's name.</para></summary>
    internal static class DiagnosticReport
    {
        /// <summary>The logs NBR writes, by the names they are written under.
        /// Listed rather than globbed, so a stray file somebody else left in
        /// %TEMP% cannot ride along in a report.</summary>
        private static readonly string[] TempLogs =
        {
            "NBR-crash.log",
            "NBR-hang.log",
            "NBR-import-diagnostic.log",
            "NBR-diagnostics.log",
            "NBR-timing.log",
            "NBR-reading-surface.log",
            "NBR-speech-inventory.log",
            "NBR-host32.log",
        };

        /// <summary>The tail kept from each log. The speech host's file grows
        /// without limit and was 970 kB when this was written; what matters
        /// after a fault is the END of a log, so the head is what gets dropped.</summary>
        private const int KeepBytesPerLog = 128 * 1024;

        /// <summary>Writes the report and returns its path, or null if it could
        /// not be written.</summary>
        public static string Write(string path)
        {
            try
            {
                var sb = new StringBuilder();
                Header(sb);
                Settings(sb);

                string temp = Path.GetTempPath();
                foreach (string name in TempLogs)
                {
                    string p = Path.Combine(temp, name);
                    if (!File.Exists(p)) continue;
                    var fi = new FileInfo(p);
                    sb.AppendLine();
                    sb.AppendLine("================================================================");
                    sb.AppendLine(name + "   (" + fi.Length + " bytes, last written " +
                                  fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + ")");
                    sb.AppendLine("================================================================");
                    sb.AppendLine(Tail(p, KeepBytesPerLog));
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return path;
            }
            catch { return null; }
        }

        /// <summary>A sensible name and place to offer: Documents, and a name
        /// carrying the date, so a second report does not overwrite the first
        /// and a mail with two of them can still be told apart.</summary>
        public static string SuggestedPath()
        {
            string docs;
            try { docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            catch { docs = ""; }
            return Path.Combine(docs, "NBR-report-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".txt");
        }

        private static void Header(StringBuilder sb)
        {
            sb.AppendLine("Nemoviz Book Reader — diagnostic report");
            sb.AppendLine("Written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("Release      : " + Safe(() => Localization.T("Dialog.About.Release")));
            sb.AppendLine("Build        : " + Safe(() => File.GetLastWriteTime(
                System.Reflection.Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm")));
            sb.AppendLine("Program at   : " + Safe(() => AppDomain.CurrentDomain.BaseDirectory));
            sb.AppendLine("User data at : " + Safe(() => UserData.Folder));
            sb.AppendLine("Language     : " + Safe(() => Localization.CurrentLanguageCode));
            sb.AppendLine("Windows      : " + Safe(() => Environment.OSVersion.VersionString) +
                          "  (" + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + ")");
            sb.AppendLine("Process      : " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") +
                          ", .NET " + Safe(() => Environment.Version.ToString()));
            sb.AppendLine("Culture      : " + Safe(() => System.Globalization.CultureInfo.InstalledUICulture.Name));
        }

        /// <summary>The reader's own settings, which is what most "it does not
        /// do X" reports turn on — and the cloud counter beside them.
        /// <c>nbr-services.dat</c> is not read; see the class note.</summary>
        private static void Settings(StringBuilder sb)
        {
            foreach (string name in new[] { "Settings.ini", "CloudUsage.ini" })
            {
                try
                {
                    string p = Path.Combine(UserData.Folder, name);
                    if (!File.Exists(p)) continue;
                    sb.AppendLine();
                    sb.AppendLine("================================================================");
                    sb.AppendLine(name);
                    sb.AppendLine("================================================================");
                    sb.AppendLine(File.ReadAllText(p));
                }
                catch { }
            }
        }

        private static string Tail(string path, int bytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bool trimmed = fs.Length > bytes;
                    if (trimmed) fs.Seek(-bytes, SeekOrigin.End);
                    using (var sr = new StreamReader(fs))
                    {
                        string s = sr.ReadToEnd();
                        return trimmed ? "[… earlier entries left out …]\r\n" + s : s;
                    }
                }
            }
            catch (Exception e) { return "[could not be read: " + e.GetType().Name + "]"; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; }
            catch { return "?"; }
        }
    }
}
