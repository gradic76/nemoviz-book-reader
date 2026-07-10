using System;
using System.Collections.Generic;
using System.IO;

namespace Nemoviz_Book_Reader
{
    public class BookData
    {
        private IniFile ini;

        public string FolderPath { get; private set; }
        public string Title { get; set; }
        // Separate author, populated from metadata for produced formats (DAISY:
        // dc:creator). Empty for plain audiobooks, where a single merged Title
        // taken from the folder name is the convention (we can't reliably split
        // author from title there). Shown alongside Title only when non-empty.
        public string Author { get; set; }
        public string Format { get; set; }
        public string Duration { get; set; }
        public string LastPosition { get; set; }
        public int PercentListened { get; set; }
        public int Volume { get; set; }
        public int Speed { get; set; }
        // Index of the selected seek step in the player's dropdown
        // (0=15s, 1=30s, 2=1min, 3=5min, 4=Part, 5=Bookmark). Remembered
        // per book; the player clamps it on load in case the range shrank
        // (e.g. the book has no bookmarks so there is no Bookmark option).
        public int SeekStep { get; set; }
        public DateTime DateAdded { get; set; }

        // Virtual timeline
        public List<(string FileName, double Duration)> Chapters { get; private set; }
        public List<double> Offsets { get; private set; }
        public double TotalDuration { get; private set; }

        // Bookmarks: virtual-timeline positions only. Display names ("Bookmark
        // 01 (H:MM)") are computed live from sorted position, never stored.
        public List<double> Bookmarks { get; private set; }

        // DAISY navigation overlay — headings (with depth) and pages, each at
        // an absolute virtual-timeline position. Empty for non-DAISY books.
        // Recomputed at load from DaisyParser (parse is cheap, always correct),
        // mapped onto the audio timeline; never persisted.
        public bool IsDaisy { get; private set; }
        public List<(int Level, string Label, double Position)> DaisyHeadings { get; private set; }
        public List<(int Level, string Label, double Position)> DaisyPages { get; private set; }

        public BookData(string folderPath)
        {
            FolderPath = folderPath;
            string iniPath = Path.Combine(folderPath, "Book.ini");
            ini = new IniFile(iniPath);
            Chapters = new List<(string, double)>();
            Offsets = new List<double>();
            Bookmarks = new List<double>();
            DaisyHeadings = new List<(int, string, double)>();
            DaisyPages = new List<(int, string, double)>();
            Load();
        }

        private void Load()
        {
            // "Title" is the single merged name field ("Naziv") — it defaults
            // to the folder name. Standalone/orphan files are covered too,
            // because import always creates a folder named after the file.
            // The legacy "Author" key in old Book.ini files is simply ignored.
            Title = ini.Read("Book", "Title", Path.GetFileName(FolderPath));
            Author = ini.Read("Book", "Author", "");
            Format = ini.Read("Book", "Format", "Unknown");
            Duration = ini.Read("Book", "Duration", "00:00:00");
            LastPosition = ini.Read("Progress", "LastPosition", "00:00:00");
            PercentListened = int.Parse(ini.Read("Progress", "PercentListened", "0"));
            Volume = int.Parse(ini.Read("Settings", "Volume", "100"));
            Speed = int.Parse(ini.Read("Settings", "Speed", "100"));
            // -1 = never chosen for this book yet → the player defaults to the
            // first (largest) step. Once the user picks/plays, the real encoded
            // value is written back.
            int.TryParse(ini.Read("Settings", "SeekStep", "-1"), out int seekStep);
            SeekStep = seekStep;
            DateTime.TryParse(ini.Read("Book", "DateAdded", DateTime.Now.ToString()), out DateTime dt);
            DateAdded = dt;
            LoadChapters();
            LoadBookmarks();
            BuildDaisyNav();
        }

        /// <summary>Detects a DAISY book and overlays its headings/pages onto
        /// the (already-loaded) audio timeline: each nav point's absolute
        /// position = the offset of its audio file + the clip-begin within it.
        /// Relies on Chapters being in DAISY reading order (import builds them
        /// via BuildChaptersFromDaisy). Never throws.</summary>
        private void BuildDaisyNav()
        {
            DaisyHeadings.Clear();
            DaisyPages.Clear();
            IsDaisy = false;
            try
            {
                if (!DaisyParser.IsDaisy(FolderPath)) return;
                DaisyBook db = DaisyParser.TryParse(FolderPath);
                if (db == null) return;
                IsDaisy = true;

                var offset = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Chapters.Count; i++) offset[Chapters[i].FileName] = Offsets[i];

                foreach (var h in db.Headings)
                    if (h.AudioFile != null && offset.TryGetValue(h.AudioFile, out double o))
                        DaisyHeadings.Add((h.Level, h.Label, o + h.ClipBegin));
                foreach (var p in db.Pages)
                    if (p.AudioFile != null && offset.TryGetValue(p.AudioFile, out double o))
                        DaisyPages.Add((0, p.Label, o + p.ClipBegin));
            }
            catch
            {
                // Malformed DAISY must never break loading a book.
            }
        }

        /// <summary>Builds the virtual timeline for a DAISY book in reading
        /// order (from the parsed AudioPlayOrder) rather than the alphabetical
        /// file sort used for plain audiobooks — DAISY audio files are not
        /// always named in play order. Falls back to the folder's audio files
        /// if the order can't be resolved.</summary>
        public void BuildChaptersFromDaisy(DaisyBook db)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in Directory.GetFiles(FolderPath))
                byName[Path.GetFileName(p)] = p;

            var ordered = new List<string>();
            foreach (string name in db.AudioPlayOrder)
                if (byName.TryGetValue(name, out string full)) ordered.Add(full);

            if (ordered.Count == 0)
            {
                foreach (string p in byName.Values)
                    if (Array.IndexOf(LibraryScanner.AudioExtensions, Path.GetExtension(p).ToLower()) >= 0)
                        ordered.Add(p);
                ordered.Sort(StringComparer.OrdinalIgnoreCase);
            }

            BuildChaptersFromFolder(ordered.ToArray());

            // DAISY carries real metadata — surface it as-is (no folder-name
            // guessing): title and a separate author. Format becomes
            // "Daisy <version>, <sample rate>, <bitrate>, <channels>".
            Title = db.Title ?? "";
            Author = db.Author ?? "";
            ini.Write("Book", "Title", Title);
            ini.Write("Book", "Author", Author);

            string audioDetails = ordered.Count > 0 ? DetectAudioFormatString(ordered[0]) : null;
            // Drop the leading codec name ("MP3 Audio, ...") and prefix the
            // DAISY version instead.
            string tail = null;
            if (!string.IsNullOrEmpty(audioDetails))
            {
                int comma = audioDetails.IndexOf(',');
                tail = comma >= 0 ? audioDetails.Substring(comma + 1).Trim() : null;
            }
            Format = "Daisy " + db.Version + (string.IsNullOrEmpty(tail) ? "" : ", " + tail);
            ini.Write("Book", "Format", Format);

            BuildDaisyNav();
        }

        private void LoadChapters()
        {
            Chapters.Clear();
            Offsets.Clear();
            TotalDuration = 0;

            int i = 0;
            while (true)
            {
                string val = ini.Read("Chapters", "File" + i, null);
                if (val == null) break;

                string[] parts = val.Split('|');
                if (parts.Length == 2 && double.TryParse(parts[1],
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double dur))
                {
                    Chapters.Add((parts[0], dur));
                    Offsets.Add(TotalDuration);
                    TotalDuration += dur;
                }
                i++;
            }
        }

        public void SaveChapters()
        {
            for (int i = 0; i < Chapters.Count; i++)
            {
                string val = Chapters[i].FileName + "|" +
                    Chapters[i].Duration.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ini.Write("Chapters", "File" + i, val);
            }
        }

        public void BuildChaptersFromFolder(string[] audioFiles)
        {
            Chapters.Clear();
            Offsets.Clear();
            TotalDuration = 0;

            foreach (string filePath in audioFiles)
            {
                double dur = 0;
                try
                {
                    var tagFile = TagLib.File.Create(filePath);
                    dur = tagFile.Properties.Duration.TotalSeconds;
                    tagFile.Dispose();
                }
                catch { dur = 0; }

                string fileName = Path.GetFileName(filePath);
                Chapters.Add((fileName, dur));
                Offsets.Add(TotalDuration);
                TotalDuration += dur;
            }

            SaveChapters();

            Duration = FormatTime(TotalDuration);
            ini.Write("Book", "Duration", Duration);

            // While we're at it, store the detailed audio format string
            // (e.g. "MP3 Audio, 44.1 kHz, 128 kbps, stereo").
            if (audioFiles.Length > 0)
            {
                Format = DetectAudioFormatString(audioFiles[0]);
                ini.Write("Book", "Format", Format);
            }
        }

        /// <summary>Lazily builds the chapter list + total duration for a plain
        /// audio book that entered the library via a background scan (which
        /// skips this to keep scanning a big library fast). Mirrors
        /// EnsureFormatDetails: one-time — once [Chapters] is written to
        /// Book.ini this is a no-op — and called when the book is first shown
        /// in the library details, so the duration is there before playback,
        /// consistent with DAISY (whose timeline is built at import).</summary>
        public void EnsureDurationDetails()
        {
            if (IsDaisy) return;            // DAISY already built its timeline at import
            if (Chapters.Count > 0) return; // already built (import or a prior call)

            var audioFiles = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(FolderPath))
                    if (Array.IndexOf(LibraryScanner.AudioExtensions, Path.GetExtension(f).ToLower()) >= 0)
                        audioFiles.Add(f);
            }
            catch { return; }

            if (audioFiles.Count == 0) return;
            audioFiles.Sort(StringComparer.OrdinalIgnoreCase);
            BuildChaptersFromFolder(audioFiles.ToArray());
        }

        // ──────────────────────────────────────────────
        // Bookmarks
        // ──────────────────────────────────────────────

        private void LoadBookmarks()
        {
            Bookmarks.Clear();

            int i = 0;
            while (true)
            {
                string val = ini.Read("Bookmarks", "Bookmark" + i, null);
                if (val == null) break;

                if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double pos))
                    Bookmarks.Add(pos);
                i++;
            }
            Bookmarks.Sort();
        }

        /// <summary>Adds a bookmark at the given virtual-timeline position and
        /// saves immediately. Used by the simple "Set Bookmark" command.</summary>
        public void AddBookmark(double virtualPositionSeconds)
        {
            Bookmarks.Add(virtualPositionSeconds);
            Bookmarks.Sort();
            SaveBookmarks();
        }

        /// <summary>Replaces the whole bookmark list (e.g. after removals made
        /// in the Manage Bookmarks dialog) and saves.</summary>
        public void SetBookmarks(List<double> positions)
        {
            Bookmarks = new List<double>(positions);
            Bookmarks.Sort();
            SaveBookmarks();
        }

        public void SaveBookmarks()
        {
            ini.DeleteSection("Bookmarks");
            for (int i = 0; i < Bookmarks.Count; i++)
                ini.Write("Bookmarks", "Bookmark" + i,
                    Bookmarks[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // ──────────────────────────────────────────────
        // Audio format details
        // ──────────────────────────────────────────────

        /// <summary>
        /// Builds a human-readable format description from an audio file,
        /// e.g. "MP3 Audio, 44.1 kHz, 128 kbps, stereo". The decimal
        /// separator of the sample rate follows the current system culture
        /// (so "44,1 kHz" on Croatian Windows). Falls back to just the
        /// friendly format name if the file's properties can't be read.
        /// </summary>
        public static string DetectAudioFormatString(string filePath)
        {
            string baseName = FriendlyFormatName(Path.GetExtension(filePath));

            try
            {
                var tagFile = TagLib.File.Create(filePath);
                int sampleRate = tagFile.Properties.AudioSampleRate;
                int bitrate = tagFile.Properties.AudioBitrate;
                int channels = tagFile.Properties.AudioChannels;
                tagFile.Dispose();

                var parts = new List<string> { baseName };

                if (sampleRate > 0)
                    parts.Add((sampleRate / 1000.0).ToString("0.#") + " kHz");

                if (bitrate > 0)
                    parts.Add(bitrate + " kbps");

                if (channels == 1)
                    parts.Add("mono");
                else if (channels == 2)
                    parts.Add("stereo");
                else if (channels > 2)
                    parts.Add(channels + " ch");

                return string.Join(", ", parts);
            }
            catch
            {
                return baseName;
            }
        }

        /// <summary>
        /// Upgrades a plain audio format label (e.g. "MP3 Audio" written by
        /// older versions) to the detailed one, and persists it. Called
        /// lazily from the library details view; runs at most one TagLib
        /// probe per call and becomes a no-op once the details are stored.
        /// Returns true if the format was upgraded.
        /// </summary>
        public bool EnsureFormatDetails()
        {
            // Already detailed ("..., 44.1 kHz, ...") — nothing to do.
            if (!string.IsNullOrEmpty(Format) && Format.Contains(","))
                return false;

            string firstAudio = FindFirstAudioFile();
            if (firstAudio == null)
                return false; // text book or empty folder — leave as is

            string detailed = DetectAudioFormatString(firstAudio);
            if (detailed == Format)
                return false; // probe added nothing new — don't rewrite the ini

            Format = detailed;
            ini.Write("Book", "Format", Format);
            return true;
        }

        private string FindFirstAudioFile()
        {
            try
            {
                string[] files = Directory.GetFiles(FolderPath);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    if (Array.IndexOf(LibraryScanner.AudioExtensions,
                        Path.GetExtension(f).ToLower()) >= 0)
                        return f;
                }
            }
            catch
            {
                // Folder unreadable/deleted — treated as "no audio".
            }
            return null;
        }

        /// <summary>
        /// Maps a file extension to a friendly format name
        /// (single source of truth, also used by LibraryScanner).
        /// </summary>
        public static string FriendlyFormatName(string extension)
        {
            switch ((extension ?? "").ToLower())
            {
                case ".mp3": return "MP3 Audio";
                case ".ogg": return "OGG Audio";
                case ".flac": return "FLAC Audio";
                case ".m4a": return "M4A Audio";
                case ".m4b": return "M4B Audio";
                case ".wav": return "WAV Audio";
                case ".opus": return "Opus Audio";
                case ".aac": return "AAC Audio";
                case ".wma": return "WMA Audio";
                case ".ape": return "APE Audio";
                case ".mka": return "MKA Audio";
                case ".spx": return "Speex Audio";
                case ".oga": return "OGA Audio";
                case ".dsf": return "DSF Audio";
                case ".dff": return "DFF Audio";
                case ".caf": return "CAF Audio";
                case ".epub": return "EPUB";
                case ".txt": return "Text";
                case ".pdf": return "PDF";
                case ".djvu": return "DjVu";
                case ".fb2": return "FB2";
                case ".mobi": return "MOBI";
                case ".azw": return "AZW";
                case ".azw3": return "AZW3";
                case ".cbz": return "CBZ";
                case ".cbr": return "CBR";
            }
            return "Unknown";
        }

        private string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        public void Save()
        {
            ini.Write("Book", "Title", Title);
            ini.Write("Book", "Author", Author ?? "");
            ini.Write("Book", "Format", Format);
            ini.Write("Book", "Duration", Duration);
            ini.Write("Book", "DateAdded", DateAdded.ToString());
            ini.Write("Progress", "LastPosition", LastPosition);
            ini.Write("Progress", "PercentListened", PercentListened.ToString());
            ini.Write("Settings", "Volume", Volume.ToString());
            ini.Write("Settings", "Speed", Speed.ToString());
            ini.Write("Settings", "SeekStep", SeekStep.ToString());
        }
    }
}
