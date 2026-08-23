using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Asks GitHub what the newest published release of NBR is, and says
    /// whether it is the one running.
    ///
    /// <para><b>No library, no service, no account.</b> One GET against the public
    /// releases endpoint, parsed with the same hand-written <see cref="Json"/> the
    /// translation and cloud-voice code uses. Nothing is sent but the request
    /// itself — no identifier, no book, no library, and deliberately not the
    /// version we are on, which would let the number of people still running an
    /// old build be counted from the other end.</para>
    ///
    /// <para><b>It can never take the player down.</b> Every failure — no network,
    /// a repository that is not there yet, a rate limit, an HTML error page where
    /// JSON was expected — comes back as <see cref="Result.Failed"/>. A manual
    /// check says so; an automatic one says nothing at all, because a reader who
    /// did not ask has no use for the news that a check they did not know about
    /// did not work.</para></summary>
    internal static class UpdateCheck
    {
        /// <summary>THE RELEASE THIS BUILD IS, as a machine token — the git tag
        /// Gordan publishes it under, never shown to a reader.
        ///
        /// <para>It is separate from <c>Dialog.About.Release</c> on purpose, and
        /// the two are not duplicates of one fact. That one is PROSE the reader
        /// hears ("Alpha", "Beta 1", and from the first public release the date),
        /// it lives in the language files, and it is translated. This one is an
        /// identifier compared character for character against what GitHub
        /// reports, so it must not be translated and must not be prettied.</para>
        ///
        /// <para><b>Bump this and the About label together when a release goes
        /// out.</b> Getting it wrong is not silent: leave it behind and every
        /// reader is told there is an update when there is not.</para></summary>
        public const string Release = "alpha";

        /// <summary>The repository the releases are published from.
        ///
        /// <para><b>NOT PUBLISHED YET — this is the name it will have.</b> The NBR
        /// repository is local by design; only the mpv fork is on GitHub. Until
        /// the beta is pushed, the endpoint answers 404 and every check reports
        /// that it could not be made, which is the honest answer and not a
        /// failure of this code. Correct the name here if the repository is
        /// created under another one.</para></summary>
        public const string Repo = "gradic76/nemoviz-book-reader";

        /// <summary>Where a reader is sent to fetch it. The releases page rather
        /// than a file: what a release contains is Gordan's to decide, and a link
        /// straight to an installer would be a promise about its name.</summary>
        public static string ReleasesPage
        {
            get { return "https://github.com/" + Repo + "/releases/latest"; }
        }

        public enum Outcome
        {
            /// <summary>The check could not be made at all.</summary>
            Failed,
            /// <summary>The newest published release is the one running.</summary>
            UpToDate,
            /// <summary>Something newer has been published.</summary>
            Newer
        }

        public struct Result
        {
            public Outcome Outcome;
            /// <summary>The published release's own name, for showing — its title
            /// if it has one, otherwise its tag.</summary>
            public string Latest;

            public static Result Failed { get { return new Result { Outcome = Outcome.Failed }; } }
        }

        /// <summary>Asks GitHub, and blocks while it does — so call it off the UI
        /// thread. Ten seconds at the outside: a reader who chose Check for update
        /// is waiting in front of the menu, and a minute of nothing is worse news
        /// than a failure.</summary>
        public static Result Ask()
        {
            string body = Get("https://api.github.com/repos/" + Repo + "/releases/latest");
            if (body == null) return Result.Failed;

            object json = Json.Parse(body);
            string tag = Json.PathString(json, "tag_name");
            if (string.IsNullOrEmpty(tag)) return Result.Failed;

            // The title is what a reader recognises ("Beta 2"), the tag is what
            // the comparison is made on. A release published without a title
            // falls back to the tag rather than to an empty line.
            string name = Json.PathString(json, "name");
            if (string.IsNullOrEmpty(name)) name = tag;

            // COMPARED LOOSELY, because the two halves are written by different
            // hands: this constant by whoever edits the file, the tag by whoever
            // creates the release, and "v1.0" against "V1.0 " is the same release
            // by any reading. A leading v is dropped for the same reason — it is
            // a convention of git tags, not part of the version.
            return new Result
            {
                Outcome = Same(tag, Release) ? Outcome.UpToDate : Outcome.Newer,
                Latest = name
            };
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Trim(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]))
                s = s.Substring(1);
            return s;
        }

        // ── How often the automatic check runs ────────────────────────────────

        /// <summary>Whether the automatic check should run now: it is switched on,
        /// and it has not already run today.
        ///
        /// <para>Once a day and not once a launch, because NBR is a program people
        /// open and close all day — a check on every start would be a request per
        /// book, which is rude to a service giving this away and pointless besides,
        /// since releases do not appear by the hour.</para></summary>
        public static bool DueNow(AppSettings s)
        {
            if (s == null || !s.AutoCheckUpdates) return false;
            DateTime last = s.LastUpdateCheck;
            // A stored date in the future means the clock has been put back since;
            // treat it as due rather than as a reason never to check again.
            return last.Date != DateTime.Today || last > DateTime.Now;
        }

        public static string Today
        {
            get { return DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        }

        public static DateTime ParseDay(string s)
        {
            DateTime d;
            return DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out d) ? d : DateTime.MinValue;
        }

        // ── Transport ─────────────────────────────────────────────────────────

        private static string Get(string url)
        {
            try
            {
                // WITHOUT THIS THE CHECK CANNOT WORK AT ALL, and it fails in the
                // way that is hardest to read: .NET Framework's own default here
                // is still "Ssl3, Tls", GitHub has refused TLS 1.0 for years, and
                // what comes back is "Could not create SSL/TLS secure channel" --
                // which looks exactly like a machine with no network. Measured on
                // this machine, 2026-08-23: as shipped it threw, forced to 1.2 it
                // returned 20 402 bytes. Translator and AzureProvision each carry
                // the same line for the same reason.
                //
                // |= rather than =, so a protocol something else in the process
                // has already enabled is not taken away again.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "GET";
                r.Timeout = 10000;
                r.ReadWriteTimeout = 10000;
                // GITHUB REFUSES A REQUEST WITH NO USER AGENT — 403, with a page of
                // HTML explaining it, which would read here as a rate limit. The
                // same token AzureVoices sends; it is a machine identifier and not
                // user-visible text, so §3's naming rule does not reach it.
                r.UserAgent = "NemovizBookReader";
                r.Accept = "application/vnd.github+json";
                using (WebResponse resp = r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }
    }
}
