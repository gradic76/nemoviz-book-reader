using System;
using System.Globalization;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>How many characters this month have been SENT to each cloud voice
    /// service, and how that stands against its free allowance.
    ///
    /// <para><b>Counted here rather than asked of the service, and that is the
    /// whole point of it</b> (Gordan's idea, parked 2026-08-17, built
    /// 2026-08-23). Google offers a monitoring page and Azure a portal, but both
    /// answer AFTER the fact — a reader who has to open a web page to find out
    /// what a book will cost finds out once the book has cost it. NBR already
    /// knows every character it sends, so the number can be in front of the
    /// reader before the book starts.</para>
    ///
    /// <para><b>What is counted is what is SENT, not what is spoken.</b> The
    /// count is taken inside <see cref="CloudVoices.Synthesize"/>, which sits
    /// BEHIND the speech cache — so a second reading of a book, an export replayed
    /// from disk, or a sentence the look-ahead already fetched all cost nothing
    /// and are counted as nothing. That is also what makes the number match a
    /// bill rather than a listening habit.</para>
    ///
    /// <para><b>Per service and per calendar month</b>, because that is how both
    /// vendors reckon a free tier. A new month is not a rollover we perform: the
    /// stored month simply stops matching and the count reads zero, so a machine
    /// left off for six weeks needs no catching up.</para>
    /// </summary>
    internal static class CloudUsage
    {
        private const string Section = "CloudUsage";

        /// <summary>ITS OWN FILE, and Settings.ini would have LOST THE COUNT.
        ///
        /// <para><see cref="IniFile"/> reads a whole file into memory when it is
        /// constructed and writes the whole of it back on every Save.
        /// <see cref="AppSettings"/> holds one such object for the life of the
        /// program, so it carries a snapshot of Settings.ini taken at start-up —
        /// and the first thing it saved afterwards, a volume change or the last
        /// opened book, would have written that snapshot back over every
        /// character this class had counted in between. A reader listening to a
        /// long book and nudging the volume would have watched the number reset
        /// without anything appearing to go wrong.</para>
        ///
        /// <para>It is also the honest filing: this is a MEASUREMENT, not a
        /// setting. Nobody chose it, deleting the file loses nothing but a month's
        /// tally, and keeping it apart means the one place a reader might want to
        /// correct an allowance by hand has nothing else in it.</para></summary>
        private const string FileName = "CloudUsage.ini";

        /// <summary>The free characters a service gives per calendar month.
        ///
        /// <para><b>Both figures have a source and neither is invented, but they
        /// are not the same KIND of fact.</b> Azure's 0.5 M is Microsoft's own
        /// pricing page, read 2026-08-23: "0.5 million characters free per month"
        /// for neural voices. Google's 1 M for Chirp 3 HD is NOT from Google —
        /// its pricing page is rendered by JavaScript and gives up nothing to a
        /// fetch (recorded 2026-08-17 and still true) — it is what several
        /// third-party pricing summaries agree on, and it sits beside the $30 per
        /// million that Gordan read off Google's own page himself.</para>
        ///
        /// <para><b>AND A READER'S REAL ALLOWANCE MAY BE NEITHER.</b> A trial
        /// credit, an account that has already paid, a project with billing
        /// switched off — each moves the line, and NBR cannot see any of it. So
        /// the figure is overridable in CloudUsage.ini (<c>[CloudUsage]
        /// GoogleFreeChars</c> / <c>AzureFreeChars</c>) rather than compiled in,
        /// and the warning is written so that it is honest even when the number
        /// is a little wrong: it says what has been used and what this book will
        /// add, and only then that continuing may be charged.</para></summary>
        public static long FreeCharsPerMonth(string vendor)
        {
            long stored = Read(KeyFor(vendor) + "FreeChars", -1);
            if (stored >= 0) return stored;
            if (IsAzure(vendor)) return 500000;
            if (IsGoogle(vendor)) return 1000000;
            return -1;                       // a service we have no figure for
        }

        /// <summary>Characters sent to this service in the current calendar
        /// month.</summary>
        public static long UsedThisMonth(string vendor)
        {
            if (!Known(vendor)) return 0;
            if (Read(KeyFor(vendor) + "Month", -1) != ThisMonth) return 0;
            return Math.Max(0, Read(KeyFor(vendor) + "Chars", 0));
        }

        /// <summary>What is left of the free allowance, or -1 when the allowance
        /// for that service is not known.</summary>
        public static long RemainingThisMonth(string vendor)
        {
            long quota = FreeCharsPerMonth(vendor);
            if (quota < 0) return -1;
            return Math.Max(0, quota - UsedThisMonth(vendor));
        }

        /// <summary>Records characters actually sent. Called from the one place
        /// that sends them.</summary>
        public static void Note(string vendor, int chars)
        {
            if (!Known(vendor) || chars <= 0) return;
            try
            {
                lock (gate)
                {
                    IniFile ini = File();
                    if (ini == null) return;
                    string k = KeyFor(vendor);
                    long month = Read(k + "Month", -1);
                    long had = month == ThisMonth ? Math.Max(0, Read(k + "Chars", 0)) : 0;
                    ini.Write(Section, k + "Month", ThisMonth.ToString(CultureInfo.InvariantCulture));
                    ini.Write(Section, k + "Chars", (had + chars).ToString(CultureInfo.InvariantCulture));
                }
            }
            catch { }   // a counter is never a reason for a book to stop reading
        }

        /// <summary>Would reading this many more characters take this service past
        /// its free allowance? False when the allowance is unknown — a warning
        /// about a line nobody can locate is worse than none.</summary>
        public static bool WouldCrossFreeTier(string vendor, long chars)
        {
            long quota = FreeCharsPerMonth(vendor);
            if (quota < 0) return false;
            return UsedThisMonth(vendor) + Math.Max(0, chars) > quota;
        }

        /// <summary>Which service a voice belongs to, by the vendor tag its own
        /// catalogue puts on it — so this file names no vendor twice.</summary>
        public static string VendorOf(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            if (GoogleCloudVoices.IsOne(displayName)) return GoogleCloudVoices.Vendor;
            if (AzureVoices.IsOne(displayName)) return AzureVoices.Vendor;
            return null;
        }

        // ── the store ─────────────────────────────────────────────────────────

        private static readonly object gate = new object();

        /// <summary>The month as one comparable number, 202608 for August 2026.
        /// A number rather than a date string because it is only ever compared
        /// for equality, and a date written on one machine's culture and read on
        /// another's is the trap sync.map already paid for.</summary>
        private static long ThisMonth
        {
            get { return DateTime.Now.Year * 100 + DateTime.Now.Month; }
        }

        private static bool IsGoogle(string v)
        {
            return string.Equals(v, GoogleCloudVoices.Vendor, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAzure(string v)
        {
            return string.Equals(v, AzureVoices.Vendor, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Known(string v) { return IsGoogle(v) || IsAzure(v); }

        private static string KeyFor(string vendor) { return IsAzure(vendor) ? "Azure" : "Google"; }

        private static IniFile File()
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                return new IniFile(Path.Combine(dir, FileName));
            }
            catch { return null; }
        }

        private static long Read(string key, long fallback)
        {
            try
            {
                IniFile ini = File();
                if (ini == null) return fallback;
                long v;
                return long.TryParse(ini.Read(Section, key, ""), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out v) ? v : fallback;
            }
            catch { return fallback; }
        }
    }
}
