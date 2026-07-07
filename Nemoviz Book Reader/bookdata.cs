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
        public string Format { get; set; }
        public string Duration { get; set; }
        public string LastPosition { get; set; }
        public int PercentListened { get; set; }
        public int Volume { get; set; }
        public int Speed { get; set; }
        public DateTime DateAdded { get; set; }

        // Virtual timeline
        public List<(string FileName, double Duration)> Chapters { get; private set; }
        public List<double> Offsets { get; private set; }
        public double TotalDuration { get; private set; }

        // Bookmarks: virtual-timeline positions only. Display names ("Bookmark
        // 01 (H:MM)") are computed live from sorted position, never stored.
        public List<double> Bookmarks { get; private set; }

        public BookData(string folderPath)
        {
            FolderPath = folderPath;
            string iniPath = Path.Combine(folderPath, "Book.ini");
            ini = new IniFile(iniPath);
            Chapters = new List<(string, double)>();
            Offsets = new List<double>();
            Bookmarks = new List<double>();
            Load();
        }

        private void Load()
        {
            // "Title" is the single merged name field ("Naziv") — it defaults
            // to the folder name. Standalone/orphan files are covered too,
            // because import always creates a folder named after the file.
            // The legacy "Author" key in old Book.ini files is simply ignored.
            Title = ini.Read("Book", "Title", Path.GetFileName(FolderPath));
            Format = ini.Read("Book", "Format", "Unknown");
            Duration = ini.Read("Book", "Duration", "00:00:00");
            LastPosition = ini.Read("Progress", "LastPosition", "00:00:00");
            PercentListened = int.Parse(ini.Read("Progress", "PercentListened", "0"));
            Volume = int.Parse(ini.Read("Settings", "Volume", "100"));
            Speed = int.Parse(ini.Read("Settings", "Speed", "100"));
            DateTime.TryParse(ini.Read("Book", "DateAdded", DateTime.Now.ToString()), out DateTime dt);
            DateAdded = dt;
            LoadChapters();
            LoadBookmarks();
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
            ini.Write("Book", "Format", Format);
            ini.Write("Book", "Duration", Duration);
            ini.Write("Book", "DateAdded", DateAdded.ToString());
            ini.Write("Progress", "LastPosition", LastPosition);
            ini.Write("Progress", "PercentListened", PercentListened.ToString());
            ini.Write("Settings", "Volume", Volume.ToString());
            ini.Write("Settings", "Speed", Speed.ToString());
        }
    }
}
