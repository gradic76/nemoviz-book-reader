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
        // Producer / publisher, from dc:publisher (DAISY + EPUB). Empty for audio
        // and editable text (no such tag). Placeholder values like "N/A" are
        // normalized to empty. Shown only when non-empty.
        public string Producer { get; set; }
        /// <summary>Braille books only: the liblouis table this book was read with
        /// (language + grade + national standard). A .brf declares none of that, so
        /// it is auto-detected at import and remembered here for later correction.</summary>
        public string BrailleTable { get; set; }
        // Print-edition publisher, from dc:publisher (DAISY + EPUB). Distinct
        // from Producer (the audio/accessible-edition producer, DAISY ncc:producer).
        public string Publisher { get; set; }
        public string Format { get; set; }
        public string Duration { get; set; }
        public string LastPosition { get; set; }
        public int PercentListened { get; set; }
        public bool Favorite { get; set; }
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

        // M4B (Apple audiobook) chapter overlay — a single audio file with
        // time-stamped chapter marks (title + position in seconds). Parsed once
        // at import and persisted in Book.ini's [M4bNav]. IsM4b is true only
        // when the book actually carries chapters.
        public bool IsM4b { get; private set; }
        public List<(string Title, double Position)> M4bChapters { get; private set; }

        // Per-book sound-processing settings (Properties dialog). Inert while
        // Sound.Enabled is false. Persisted in Book.ini's [Sound] section.
        public SoundSettings Sound { get; private set; }

        // Text book (read aloud by TTS): a folder with a text document and no
        // audio. TextPosition is the resume point as a character offset.
        public bool IsTextBook { get; private set; }
        public string TextFilePath { get; private set; }
        public int TextPosition { get; set; }
        // Per-book reading speed override (words per minute); -1 = use the
        // global default from Settings. Set from the text book's Properties.
        public int TextWpm { get; set; }
        /// <summary>Per-book speech overrides; empty/-1 means "use the Settings
        /// default". Settings holds the defaults, a book may differ. These are the
        /// values of the book's CURRENT voice — every voice this book has been
        /// read with keeps its own in <see cref="TextVoicePrefs"/>.</summary>
        public string TextVoice { get; set; }
        public int TextVolume { get; set; }
        public int TextPitch { get; set; }
        /// <summary>How each voice was set up while reading THIS book, so going
        /// back to a voice restores the speed/volume/pitch it was read at rather
        /// than inheriting the previous voice's.</summary>
        public VoicePrefsTable TextVoicePrefs { get; private set; }
        /// <summary>The language the book is written in (a culture tag like
        /// "hr-HR"), worked out at import from its own metadata and its actual
        /// words. Empty when it could not be told. It picks the default voice: a
        /// Croatian book should not be read out in English because that is what
        /// Settings happens to name.</summary>
        public string TextLanguage { get; set; }
        // Character count of the text, cached for the reading-time estimate.
        public int TextChars { get; set; }
        // Heading structure of a produced text book (epub/fb2/html): level +
        // title + character offset into content.txt. Empty for flat text.
        public List<(int Level, string Label, int Offset)> TextHeadings { get; private set; }
        // Print-page markers of a produced text book (EPUB page-list): label +
        // character offset into content.txt. Empty when the book has no pages.
        public List<(string Label, int Offset)> TextPages { get; private set; }

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
            M4bChapters = new List<(string, double)>();
            TextPages = new List<(string, int)>();
            TextHeadings = new List<(int, string, int)>();
            Sound = new SoundSettings();
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
            Producer = ini.Read("Book", "Producer", "");
            BrailleTable = ini.Read("Braille", "Table", "");
            Publisher = ini.Read("Book", "Publisher", "");
            Format = ini.Read("Book", "Format", "Unknown");
            Duration = ini.Read("Book", "Duration", "00:00:00");
            LastPosition = ini.Read("Progress", "LastPosition", "00:00:00");
            PercentListened = int.Parse(ini.Read("Progress", "PercentListened", "0"));
            Favorite = ini.Read("Book", "Favorite", "0") == "1";
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
            LoadM4bNav();
            Sound.Load(ini);
            DetectTextBook();
            int.TryParse(ini.Read("Progress", "TextPosition", "0"), out int tp);
            TextPosition = tp;
            int.TryParse(ini.Read("Settings", "TextWpm", "-1"), out int tw);
            TextWpm = tw;
            TextVoice = ini.Read("Settings", "TextVoice", "");
            int.TryParse(ini.Read("Settings", "TextVolume", "-1"), out int tvol);
            TextVolume = tvol;
            int.TryParse(ini.Read("Settings", "TextPitch", "-99"), out int tpit);
            TextPitch = tpit;
            TextLanguage = ini.Read("Book", "Language", "");
            TextVoicePrefs = new VoicePrefsTable();
            TextVoicePrefs.Load(ini);
            // A book saved before voices were remembered individually has one set
            // of numbers; they belong to the voice it was last read with.
            if (!string.IsNullOrEmpty(TextVoice) && TextWpm >= 0)
                TextVoicePrefs.SetIfAbsent(TextVoice,
                    new VoicePrefs(TextWpm, TextVolume >= 0 ? TextVolume : 100,
                                   TextPitch >= -10 && TextPitch <= 10 ? TextPitch : 0));
            int.TryParse(ini.Read("Book", "TextChars", "0"), out int tc);
            TextChars = tc;
            LoadTextNav();
        }

        private void LoadTextNav()
        {
            TextHeadings.Clear();
            TextPages.Clear();
            if (!IsTextBook) return;
            int.TryParse(ini.Read("TextNav", "Count", "0"), out int n);
            for (int i = 0; i < n; i++)
            {
                string[] p = ini.Read("TextNav", "H" + i, "").Split(new[] { '|' }, 3);
                if (p.Length == 3 && int.TryParse(p[0], out int lvl) && int.TryParse(p[1], out int off))
                    TextHeadings.Add((lvl, p[2], off));
            }
            int.TryParse(ini.Read("TextNav", "PageCount", "0"), out int pc);
            for (int i = 0; i < pc; i++)
            {
                string[] p = ini.Read("TextNav", "P" + i, "").Split(new[] { '|' }, 2);
                if (p.Length == 2 && int.TryParse(p[0], out int off))
                    TextPages.Add((p[1], off));
            }
        }

        /// <summary>Sets the heading structure (from the import extractor) so the
        /// next Save persists it to [TextNav].</summary>
        public void SetTextHeadings(List<(int Level, string Label, int Offset)> headings)
        {
            TextHeadings = headings ?? new List<(int, string, int)>();
        }

        /// <summary>Sets the page-marker structure (from the import extractor) so
        /// the next Save persists it to [TextNav].</summary>
        public void SetTextPages(List<(string Label, int Offset)> pages)
        {
            TextPages = pages ?? new List<(string, int)>();
        }

        // [M4bNav]: C<i>=<position seconds>|<title>. Positions are absolute
        // virtual-timeline seconds (a single-file book, so = time in the file).
        private void LoadM4bNav()
        {
            M4bChapters.Clear();
            int.TryParse(ini.Read("M4bNav", "Count", "0"), out int n);
            for (int i = 0; i < n; i++)
            {
                string[] p = ini.Read("M4bNav", "C" + i, "").Split(new[] { '|' }, 2);
                if (p.Length == 2 && double.TryParse(p[0],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double pos))
                    M4bChapters.Add((p[1], pos));
            }
            IsM4b = M4bChapters.Count > 0;
        }

        /// <summary>Sets the M4B chapter list (from M4bParser at import) so the
        /// next Save persists it to [M4bNav].</summary>
        public void SetM4bChapters(List<(string Title, double Position)> chapters)
        {
            M4bChapters = chapters ?? new List<(string, double)>();
            IsM4b = M4bChapters.Count > 0;
        }

        /// <summary>A text book is a folder that has a readable text document
        /// (Phase 1: a .txt) and no audio. The player then reads it via TTS
        /// instead of mpv.</summary>
        private void DetectTextBook()
        {
            IsTextBook = false;
            TextFilePath = null;
            if (IsDaisy || Chapters.Count > 0) return;
            try
            {
                // content.txt is the reading text written by import (text formats,
                // text DAISY) — prefer it over any stray .txt in the folder.
                string preferred = Path.Combine(FolderPath, "content.txt");
                if (File.Exists(preferred)) { IsTextBook = true; TextFilePath = preferred; return; }
                foreach (string f in Directory.GetFiles(FolderPath))
                    if (Path.GetExtension(f).ToLower() == ".txt")
                    {
                        IsTextBook = true;
                        TextFilePath = f;
                        return;
                    }
            }
            catch { }
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
                // A text DAISY was flattened to content.txt at import; it reads as a
                // text book, so skip the (potentially heavy) DAISY re-parse here.
                if (File.Exists(Path.Combine(FolderPath, "content.txt"))) return;
                if (!DaisyParser.IsDaisy(FolderPath)) return;
                DaisyBook db = DaisyParser.TryParse(FolderPath);
                if (db == null) return;
                // A text-only DAISY (no audio) is read by TTS, not played — leave
                // IsDaisy false so DetectTextBook claims it as a text book.
                if (db.AudioPlayOrder.Count == 0) return;
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
            Producer = NormalizeProducer(db.Producer);
            Publisher = NormalizeProducer(db.Publisher);
            ini.Write("Book", "Title", Title);
            ini.Write("Book", "Author", Author);
            ini.Write("Book", "Producer", Producer);
            ini.Write("Book", "Publisher", Publisher);

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

                // TagLib has no reader for some formats mpv plays perfectly
                // (.caf, .oga, .ac3, .amr, .weba, .spx, .dff) — ask mpv instead,
                // so the book doesn't end up with a 0:00 duration.
                if (dur <= 0) dur = MpvDuration.TryGet(filePath);

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

        /// <summary>Caches the text book's character count (read once) so the
        /// reading-time estimate can be computed without re-reading the file.</summary>
        public void EnsureTextInfo()
        {
            if (!IsTextBook || TextChars > 0) return;
            try
            {
                TextChars = TextCleaner.Clean(TtsReader.ReadFile(TextFilePath)).Length;
                ini.Write("Book", "TextChars", TextChars.ToString());
            }
            catch { }
        }

        /// <summary>Estimated reading time (as "H:MM:SS") for the given nominal
        /// words-per-minute. Empty for non-text books.</summary>
        public string EstimatedReadingTime(int wpm)
        {
            if (!IsTextBook) return Duration;
            EnsureTextInfo();
            int cpm = wpm * 6;
            if (TextChars <= 0 || cpm <= 0) return FormatTime(0);
            return FormatTime(TextChars * 60.0 / cpm);
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
        /// <summary>
        /// Cleans a raw dc:publisher value: trims it and drops placeholder
        /// non-values ("N/A", "Non disponible", "-", …) so they never show as a
        /// producer. Returns "" when there is no real publisher.
        /// </summary>
        public static string NormalizeProducer(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string p = System.Net.WebUtility.HtmlDecode(raw).Trim();
            string low = p.ToLowerInvariant();
            if (low == "n/a" || low == "na" || low == "-" || low == "unknown" ||
                low == "non disponible" || low == "non disponibile" || low == "none")
                return "";
            return p;
        }

        /// <summary>
        /// Maps a file extension to "TAG — Official Format Name" (e.g.
        /// "MP3 — MPEG-1 Audio Layer III"). The short tag comes first so it is
        /// recognised (and spoken) immediately, with the official name after it.
        /// This is the single source of truth for the format shown in the player
        /// and library info boxes; for audio the technical details (sample rate,
        /// bitrate, channels) are appended after a comma by
        /// DetectAudioFormatString.
        /// </summary>
        public static string FriendlyFormatName(string extension)
        {
            switch ((extension ?? "").ToLower())
            {
                // ── Audio ────────────────────────────────────────────────
                case ".mp3": return "MP3 — MPEG-1 Audio Layer III";
                case ".m4a": return "M4A — MPEG-4 Part 14 Audio";
                case ".m4b": return "M4B — MPEG-4 Audiobook";
                case ".wav": return "WAV — Waveform Audio File Format";
                case ".ogg": return "OGG — Ogg Vorbis Audio";
                case ".oga": return "OGA — Ogg Audio File";
                case ".opus": return "OPUS — Opus Interactive Audio Codec";
                case ".spx": return "SPX — Ogg Speex Audio";
                case ".flac": return "FLAC — Free Lossless Audio Codec";
                case ".aac": return "AAC — Advanced Audio Coding";
                case ".wma": return "WMA — Windows Media Audio";
                case ".ape": return "APE — Monkey's Audio";
                case ".mka": return "MKA — Matroska Audio";
                case ".dsf": return "DSF — DSD Stream File";
                case ".dff": return "DFF — Direct Stream Digital Interchange File Format";
                case ".caf": return "CAF — Core Audio Format";
                case ".aiff": return "AIFF — Audio Interchange File Format";
                case ".aif": return "AIF — Audio Interchange File";
                case ".ac3": return "AC3 — Dolby Digital Audio Codec 3";
                case ".amr": return "AMR — Adaptive Multi-Rate Audio Codec";
                case ".weba": return "WEBA — WebM Audio";
                case ".webm": return "WEBM — WebM Audio";
                case ".au": return "AU — Sun Microsystems Audio";
                case ".voc": return "VOC — Creative Voice File";

                // ── Text / documents ─────────────────────────────────────
                case ".txt": return "TXT — Plain Text";
                case ".rtf": return "RTF — Rich Text Format";
                case ".docx": return "DOCX — Microsoft Word Document";
                case ".doc": return "DOC — Microsoft Word Document";
                case ".brf": return "BRF — Braille Ready Format";
                case ".brl": return "BRL — Braille File";
                case ".bra": return "BRA — Braille File";
                case ".odt": return "ODT — OpenDocument Text";
                case ".epub": return "EPUB — Electronic Publication";
                case ".fb2": return "FB2 — FictionBook 2";
                case ".htm":
                case ".html": return "HTML — HyperText Markup Language";
                case ".pdf": return "PDF — Portable Document Format";
                case ".djvu": return "DJVU — DjVu Document";
                case ".mobi": return "MOBI — Mobipocket eBook";
                case ".azw": return "AZW — Amazon Kindle eBook";
                case ".azw3": return "AZW3 — Kindle Format 8";
                case ".cbz": return "CBZ — Comic Book Archive (ZIP)";
                case ".cbr": return "CBR — Comic Book Archive (RAR)";
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
            ini.Write("Book", "Producer", Producer ?? "");
            ini.Write("Book", "Publisher", Publisher ?? "");
            ini.Write("Book", "Format", Format);
            if (!string.IsNullOrEmpty(BrailleTable))
                ini.Write("Braille", "Table", BrailleTable);
            ini.Write("Book", "Duration", Duration);
            ini.Write("Book", "Favorite", Favorite ? "1" : "0");
            ini.Write("Book", "DateAdded", DateAdded.ToString());
            ini.Write("Progress", "LastPosition", LastPosition);
            ini.Write("Progress", "PercentListened", PercentListened.ToString());
            ini.Write("Settings", "Volume", Volume.ToString());
            ini.Write("Settings", "Speed", Speed.ToString());
            ini.Write("Settings", "SeekStep", SeekStep.ToString());
            ini.Write("Progress", "TextPosition", TextPosition.ToString());
            ini.Write("Settings", "TextWpm", TextWpm.ToString());
            ini.Write("Settings", "TextVoice", TextVoice ?? "");
            ini.Write("Settings", "TextVolume", TextVolume.ToString());
            ini.Write("Settings", "TextPitch", TextPitch.ToString());
            ini.Write("Book", "Language", TextLanguage ?? "");
            TextVoicePrefs.Save(ini);
            ini.Write("Book", "TextChars", TextChars.ToString());
            ini.Write("TextNav", "Count", TextHeadings.Count.ToString());
            for (int i = 0; i < TextHeadings.Count; i++)
                ini.Write("TextNav", "H" + i,
                    TextHeadings[i].Level + "|" + TextHeadings[i].Offset + "|" + TextHeadings[i].Label);
            ini.Write("TextNav", "PageCount", TextPages.Count.ToString());
            for (int i = 0; i < TextPages.Count; i++)
                ini.Write("TextNav", "P" + i, TextPages[i].Offset + "|" + TextPages[i].Label);
            ini.Write("M4bNav", "Count", M4bChapters.Count.ToString());
            for (int i = 0; i < M4bChapters.Count; i++)
                ini.Write("M4bNav", "C" + i,
                    M4bChapters[i].Position.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + M4bChapters[i].Title);
            Sound.Save(ini);
        }
    }
}
