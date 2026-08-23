using System;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>Where a reader's own things live: <c>%APPDATA%\Nemoviz Book
    /// Reader</c>.
    ///
    /// <para><b>Why this exists at all</b> (Gordan, 2026-08-23): "Konvencija je
    /// još od Win 95 da aplikacije idu u Program Files… korisničke mape s
    /// individualnim postavkama idu u User folder." Until this file, everything
    /// NBR kept sat BESIDE THE EXE, which is portable and wrong — the moment the
    /// program is installed where a Windows program belongs, none of it can be
    /// written. The first installer sidestepped that by installing per user; this
    /// fixes it instead.</para>
    ///
    /// <para><b>The split is by whether NBR WRITES it, not by what it is.</b>
    /// Anything the program only ever READS stays beside the exe, where the
    /// installer put it and where it is shared by every account on the machine:
    /// the 480 liblouis tables, the language files, the manuals, the fonts, the
    /// 32-bit speech host. Anything written at runtime comes here.</para>
    ///
    /// <para><b>Roaming, not Local.</b> These are settings and credentials that
    /// belong to the PERSON — on a domain profile they should follow them from
    /// one machine to the next. That includes the cloud character counter, since
    /// a free allowance is reckoned per ACCOUNT and not per computer, so a reader
    /// working on two machines should see one running total rather than two that
    /// each look comfortable.</para>
    ///
    /// <para><b>The book's own things are NOT here and must not move.</b>
    /// <c>Book.ini</c>, <c>sync.map</c>, <c>content.txt</c>, the speech cache and
    /// <c>translation-glossary.txt</c> all live inside the book's own folder, so
    /// a library can be copied to another disk or another machine and arrive
    /// complete, with every reading position and bookmark still in it. That was
    /// already true and is the reason changing the library location needs no
    /// migration of anything but the books.</para></summary>
    internal static class UserData
    {
        private const string FolderName = "Nemoviz Book Reader";

        private static string cached;

        /// <summary>The folder, created on first use.
        ///
        /// <para>Falls back to the application folder if %APPDATA% cannot be
        /// reached or made — a portable NBR on a stick with no profile behind it
        /// still has to run, and the old behaviour is the safest thing to fall
        /// back TO, since it is what every earlier version did.</para></summary>
        public static string Folder
        {
            get
            {
                if (cached != null) return cached;
                try
                {
                    string appData = Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData);
                    if (!string.IsNullOrEmpty(appData))
                    {
                        string dir = Path.Combine(appData, FolderName);
                        Directory.CreateDirectory(dir);
                        return cached = dir;
                    }
                }
                catch { }
                return cached = AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>A file in the user's folder.</summary>
        public static string File(string name)
        {
            return Path.Combine(Folder, name);
        }

        /// <summary>A subfolder of the user's folder, created on first use.</summary>
        public static string SubFolder(string name)
        {
            string dir = Path.Combine(Folder, name);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>Where the same thing used to live: beside the exe.</summary>
        public static string OldPath(string name)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
        }

        /// <summary>Brings a reader's settings, dictionaries and keys over from
        /// the old place, once.
        ///
        /// <para><b>COPIES, never moves.</b> The old copy is left exactly where
        /// it is, for two reasons: the program may now be installed somewhere it
        /// cannot delete from, and if anything about this goes wrong the reader's
        /// only copy must not be the one that was in flight. It costs a few
        /// kilobytes of a folder nobody will look in again.</para>
        ///
        /// <para><b>Nothing is overwritten.</b> A file already in the new place
        /// wins, so running an old build and a new one alternately cannot make
        /// the old one's settings reach back and replace the new one's.</para>
        ///
        /// <para>Called once from <see cref="Program"/> before anything reads a
        /// setting — after that every path in the program points here anyway, so
        /// a second call finds everything present and does nothing.</para></summary>
        public static void MigrateFromAppFolder()
        {
            string here = Folder;
            string there = AppDomain.CurrentDomain.BaseDirectory;
            // Same folder: either the fallback above fired, or this is a build
            // running from its own output. Nothing to bring over.
            if (string.Equals(here.TrimEnd('\\'), there.TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase)) return;

            foreach (string name in new[]
                     {
                         "Settings.ini",
                         "nbr-services.dat",
                         "CloudUsage.ini",
                         "azure-voices.txt",
                         "google-voices.txt",
                     })
            {
                try
                {
                    string from = Path.Combine(there, name);
                    string to = Path.Combine(here, name);
                    if (System.IO.File.Exists(from) && !System.IO.File.Exists(to))
                        System.IO.File.Copy(from, to);
                }
                catch { }
            }

            try
            {
                string from = Path.Combine(there, "Dictionaries");
                string to = Path.Combine(here, "Dictionaries");
                if (Directory.Exists(from) && !Directory.Exists(to))
                {
                    Directory.CreateDirectory(to);
                    foreach (string f in Directory.GetFiles(from, "*.dic"))
                        System.IO.File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
                }
            }
            catch { }
        }
    }
}
