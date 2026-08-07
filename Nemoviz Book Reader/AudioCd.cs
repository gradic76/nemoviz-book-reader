using System;
using System.Collections.Generic;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>Turns a disc into something the player already knows how to play.
    ///
    /// <para><b>The design decision worth stating: a CD becomes a FOLDER OF
    /// FILES.</b> Once the tracks are on disk as WAVs in one folder, NBR's
    /// multi-file audio path takes over untouched — the playlist, Go To, the seek
    /// step by part, the title bar, the info box, bookmarks, sound processing.
    /// Not a line of the player knows a CD was involved. The alternative, a
    /// player mode of its own, would have meant teaching every one of those
    /// things about a medium that behaves like a folder anyway.</para>
    ///
    /// <para><b>It is NOT in the library</b> (Gordan): the folder lives in TEMP,
    /// not on the shelf, and goes when the book is closed. A CD is played, not
    /// collected — and the disc is not ours to keep.</para></summary>
    public static class AudioCd
    {
        private const string RipPrefix = "NBR-CD-";

        /// <summary>Where a disc's tracks are written. One folder per rip, named
        /// so a sweep can recognise its own leavings and nothing else's.</summary>
        public static string NewRipFolder()
        {
            string p = Path.Combine(Path.GetTempPath(), RipPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(p);
            return p;
        }

        public static bool IsRipFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            try
            {
                string name = new DirectoryInfo(folder).Name;
                return name.StartsWith(RipPrefix, StringComparison.Ordinal)
                       && Path.GetDirectoryName(folder.TrimEnd('\\'))
                          .Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Deletes a rip folder. Safe to call on anything: it refuses
        /// any path that is not one of ours, so a wrong argument cannot take a
        /// reader's books with it.</summary>
        public static void DeleteRip(string folder)
        {
            if (!IsRipFolder(folder)) return;
            try { Directory.Delete(folder, true); } catch { }
        }

        /// <summary>Clears rips a crash or a power cut left behind — 850 MB a
        /// disc is too much to leak quietly. Called at start-up, where the only
        /// rips in existence are by definition stale.</summary>
        public static void SweepOldRips()
        {
            try
            {
                foreach (string d in Directory.GetDirectories(Path.GetTempPath(), RipPrefix + "*"))
                    try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        /// <summary>The first drive holding an audio disc, or null. Most machines
        /// have one drive; on the rare machine with two, the one with a disc in it
        /// is the one the reader meant.</summary>
        public static string FindDiscDrive(out List<OpticalDrive.Track> tracks)
        {
            tracks = new List<OpticalDrive.Track>();
            foreach (string d in OpticalDrive.Drives())
            {
                if (!OpticalDrive.HasDisc(d)) continue;
                List<OpticalDrive.Track> toc = OpticalDrive.ReadToc(d);
                if (toc.Count == 0) continue;
                bool anyAudio = false;
                foreach (OpticalDrive.Track t in toc) if (t.IsAudio) { anyAudio = true; break; }
                if (!anyAudio) continue;
                tracks = AudioOnly(toc);
                return d;
            }
            return null;
        }

        /// <summary>A mixed-mode disc carries a data track; reading it as sound
        /// would produce a burst of noise, so it never reaches the playlist.</summary>
        private static List<OpticalDrive.Track> AudioOnly(List<OpticalDrive.Track> toc)
        {
            var audio = new List<OpticalDrive.Track>();
            foreach (OpticalDrive.Track t in toc) if (t.IsAudio) audio.Add(t);
            return audio;
        }

        /// <summary>True if a drive holds a disc that is NOT an audio CD — a data
        /// disc. Worth telling apart, because a data disc full of MP3s is a
        /// perfectly good book that "Open folder" already handles, and the reader
        /// should be told that rather than that their disc failed.</summary>
        public static bool HasDataDisc()
        {
            foreach (string d in OpticalDrive.Drives())
            {
                if (!OpticalDrive.HasDisc(d)) continue;
                List<OpticalDrive.Track> toc = OpticalDrive.ReadToc(d);
                if (toc.Count == 0) continue;
                bool anyAudio = false;
                foreach (OpticalDrive.Track t in toc) if (t.IsAudio) { anyAudio = true; break; }
                if (!anyAudio) return true;
            }
            return false;
        }

        /// <summary>Writes the Book.ini that makes the ripped folder a book, and
        /// hands back the BookData for it.
        ///
        /// <para>The title is the plainest thing that is true. A CD carries no
        /// title of its own — CD-Text is rare and CDDB is a network lookup NBR
        /// decided against — so inventing one would only produce a wrong name for
        /// a reader to correct. "Audio CD" plus the track count says exactly what
        /// is in front of them, and the shelf never sees it anyway.</para></summary>
        public static BookData BuildBook(string folder, List<OpticalDrive.Track> tracks)
        {
            double total = 0;
            foreach (OpticalDrive.Track t in tracks) total += t.Seconds;

            BookData book = new BookData(folder);
            book.Title = Localization.T("Cd.BookTitle", tracks.Count);
            book.Author = "";
            book.Format = Localization.T("Cd.Format");
            book.Duration = TimeSpan.FromSeconds(total).ToString(@"hh\:mm\:ss");
            book.Save();
            return book;
        }
    }
}
