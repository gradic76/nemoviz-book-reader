using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        // There is no "already asked" flag, and there does not need to be. A book
        // with no voice never becomes the last-opened book, so NBR never resumes
        // it by itself — every load of one is therefore a deliberate activation
        // (double-click, Enter, the button, Ctrl+O), and every deliberate
        // activation gets the question. An empty TextVoice IS "no voice assigned".
        public int TextVolume { get; set; }
        public int TextPitch { get; set; }
        /// <summary>Show this book's text on screen while it is read, and how —
        /// 0 two rows, 1 full screen instant, 2 full screen scrolling. Per book,
        /// like the voice: what suits a novel need not suit a textbook.
        ///
        /// <para>The Properties controls for these existed as scaffolding for a
        /// while and were never written anywhere, so every visit forgot what the
        /// last one chose.</para></summary>
        public bool TextVisual { get; set; }
        public int TextVisualMode { get; set; }

        /// <summary>What the reading window is painted in — indices into
        /// <see cref="ReadingColours"/>. Per book for the same reason the voice
        /// is: one reader's eyes want yellow on black for a long novel and
        /// something quieter for a reference book they dip into.
        ///
        /// <para>These four were the scaffolding that outlived the note above:
        /// the combos were built, announced and tabbable, and on OK only the
        /// display mode was written. Everything else went back to a hard-coded
        /// default at the next visit and reached no renderer at all.</para></summary>
        public int TextColour { get; set; }
        public int TextBackColour { get; set; }

        /// <summary>How the window marks where the reading has got to: 0 not at
        /// all, 1 the line it is on, 2 the whole sentence. The unit is the
        /// DISPLAY's, not the text's (Gordan, 2026-08-03) — which is also what
        /// makes it possible, since no speech backend NBR uses will say which
        /// word it is speaking.</summary>
        public int TextHighlight { get; set; }
        public int TextHighlightColour { get; set; }
        /// <summary>Read this book on a braille display.
        ///
        /// <para>Braille rides on the reading window rather than on a hidden
        /// control: a screen reader brailles what has FOCUS, so the text has to
        /// be somewhere the user can actually be. That makes this flag a second
        /// reason to open the window — see <c>OpensReadingWindow</c> — and it is
        /// also what <c>Form1.PushBrailleIfFocusLeft</c> tests before pushing a
        /// sentence to the display when focus has wandered off the text.</para>
        ///
        /// <para><b>There is no table beside it any more</b> (2026-08-04). One
        /// stood here, <c>TextBrailleTable</c>, describing how the text should
        /// become cells on the way out; it was written to the ini, read back by
        /// the two dialogs that offered it, and read by nothing else — because NBR
        /// has no text→braille translation for a table to govern, and does not
        /// need one: the screen reader translates, with the table set in its own
        /// braille settings. The table that IS real is <see cref="BrailleTable"/>,
        /// which back-translates a .brf on the way IN and is spent at import.</para></summary>
        public bool TextBraille { get; set; }
        /// <summary>Read this book without a voice, the position paced by
        /// <see cref="TextWpm"/> instead (Gordan, 2026-08-01).
        ///
        /// <para>Two ways in. The reader chooses it because they do not want
        /// speech over their braille or their screen; or the player falls back to
        /// it because nothing installed can speak the book's language — which
        /// used to leave a book that opened and then would not move.</para>
        ///
        /// <para>Kept apart from <see cref="TextVoice"/> rather than stored as a
        /// magic voice name, so that turning speech back on restores the voice
        /// the book was last read with instead of losing it.</para></summary>
        public bool TextNoSpeech { get; set; }
        /// <summary>True when the book asks for the reading window, whichever of
        /// the two reasons it is. Both callers used to test <c>TextVisual</c>
        /// alone, which left a braille reader with no window and so no braille.</summary>
        public bool OpensReadingWindow { get { return TextVisual || TextBraille; } }
        /// <summary>How each voice was set up while reading THIS book, so going
        /// back to a voice restores the speed/volume/pitch it was read at rather
        /// than inheriting the previous voice's.</summary>
        public VoicePrefsTable TextVoicePrefs { get; private set; }
        /// <summary>Whether <c>content.txt</c> has already been through
        /// <see cref="TextCleaner"/>, with the heading and page offsets moved to
        /// match. Books imported before that was done get it once, on load
        /// (<see cref="CleanTextFileOnce"/>); the reader never cleans again, so
        /// nothing shifts under the stored marks.</summary>
        public bool TextCleaned { get; set; }
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

        /// <summary>A book that is BOTH: narrated audio and the same words as
        /// text, joined point by point by its producer (a text+audio DAISY; EPUB
        /// media overlays later). It is <b>not</b> an <see cref="IsTextBook"/> —
        /// the narration is the reading, so the transport, the position and the
        /// seek steps all stay exactly what an audio book's are. The text is a
        /// second OUTPUT, driven by where the audio is, which is what §8l calls
        /// one position with several renderers windowing it.
        ///
        /// <para>Making a hybrid a text book instead would hand the transport to
        /// TTS and silence the narrator, which is the one thing the reader came
        /// for.</para></summary>
        public bool IsHybrid { get; private set; }

        /// <summary>Where the audio is ↔ where the text is, read from the SMIL at
        /// import and kept beside <c>content.txt</c>. Null until
        /// <see cref="LoadSyncMap"/> is called — it is bulk data (up to ~12 000
        /// points in the samples), so nothing pays for it unless it is used.</summary>
        public SyncMap Sync { get; private set; }

        /// <summary>The sync map's file, beside the book's text. Deliberately NOT
        /// in <c>Book.ini</c>: an INI is a settings file written key by key, and
        /// the biggest sample carries 11 953 points. This is bulk data and gets a
        /// file of its own.</summary>
        public string SyncFilePath { get { return Path.Combine(FolderPath, "sync.map"); } }

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
            TextVisual = ini.Read("Settings", "TextVisual",
                AppSettings.Current != null && AppSettings.Current.Visual ? "1" : "0") == "1";
            // The look falls back to the RULE in Settings, not to a constant: a
            // book that has never been given one of its own should open the way
            // this reader has said they want books to open. Once it is saved the
            // book owns its copy, so changing the rule later does not walk over a
            // book somebody has already set up by hand.
            AppSettings rule = AppSettings.Current;
            int.TryParse(ini.Read("Settings", "TextVisualMode",
                (rule != null ? rule.VisualMode : 0).ToString()), out int tvm);
            TextVisualMode = tvm >= 0 && tvm <= 2 ? tvm : 0;
            int.TryParse(ini.Read("Settings", "TextColour",
                (rule != null ? rule.TextColour : ReadingColours.DefaultText).ToString()), out int tfg);
            TextColour = ReadingColours.Clamp(tfg);
            int.TryParse(ini.Read("Settings", "TextBackColour",
                (rule != null ? rule.BackColour : ReadingColours.DefaultBack).ToString()), out int tbg);
            TextBackColour = ReadingColours.Clamp(tbg);
            int.TryParse(ini.Read("Settings", "TextHighlight",
                (rule != null ? rule.Highlight : 1).ToString()), out int thl);
            TextHighlight = thl >= 0 && thl <= 2 ? thl : 1;
            int.TryParse(ini.Read("Settings", "TextHighlightColour",
                (rule != null ? rule.HighlightColour : ReadingColours.DefaultHighlight).ToString()),
                out int thc);
            TextHighlightColour = ReadingColours.Clamp(thc);
            // Falls back to the RULE, exactly as TextVisual does above: a book
            // that has never been given one of its own opens the way this reader
            // has said books should open.
            TextBraille = ini.Read("Settings", "TextBraille",
                rule != null && rule.Braille ? "1" : "0") == "1";
            TextNoSpeech = ini.Read("Settings", "TextNoSpeech", "0") == "1";
            TextLanguage = ini.Read("Book", "Language", "");
            // Reading a book off the shelf registers its language too, not only
            // saving one. Without this an existing library stays invisible to
            // Settings until every book in it has been opened — and the Library
            // scan builds one of these for every book, so opening the shelf once
            // registers the lot.
            if (AppSettings.LanguageSeen != null) AppSettings.LanguageSeen(TextLanguage);
            TextCleaned = ini.Read("Book", "TextCleaned", "0") == "1";
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
            // A hybrid has text structure too — its headings and printed pages are
            // navigated in the text even though the transport is the audio.
            if (!IsTextBook && !IsHybrid) return;
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

        /// <summary>Writes the text↔audio join beside the book's text and makes it
        /// current. Called once, at import, from whatever read the producer's
        /// alignment; after that the book carries its own map and nothing re-reads
        /// hundreds of SMIL files on every load (one sample ships 385 of them).
        ///
        /// <para>One point per line, <c>charOffset seconds</c>, invariant culture
        /// — a decimal comma here would be read back as a different number on a
        /// machine set to another locale, which is how a book would open in sync
        /// on one computer and not on the next.</para></summary>
        public void SaveSyncMap(SyncMap map)
        {
            Sync = map;
            try
            {
                if (map == null || map.IsEmpty)
                {
                    if (File.Exists(SyncFilePath)) File.Delete(SyncFilePath);
                    return;
                }
                var sb = new StringBuilder(map.ByChar.Count * 16);
                foreach (SyncPoint p in map.ByChar)
                    sb.Append(p.CharOffset).Append(' ')
                      .Append(p.Seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                      .Append('\n');
                File.WriteAllText(SyncFilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }   // a book's folder can vanish under a background timer
        }

        /// <summary>Reads the join back, building both orders the way
        /// <see cref="DaisySync.Build"/> does. Safe to call more than once; a
        /// missing or unreadable file leaves <see cref="Sync"/> null, which every
        /// caller has to treat as "this book does not follow along".</summary>
        public SyncMap LoadSyncMap()
        {
            if (Sync != null) return Sync;
            try
            {
                if (!File.Exists(SyncFilePath)) return null;
                var map = new SyncMap();
                var points = new List<SyncPoint>();
                foreach (string line in File.ReadAllLines(SyncFilePath))
                {
                    int sp = line.IndexOf(' ');
                    if (sp <= 0) continue;
                    if (!int.TryParse(line.Substring(0, sp), out int off)) continue;
                    if (!double.TryParse(line.Substring(sp + 1),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double sec)) continue;
                    points.Add(new SyncPoint(off, sec));
                }
                if (points.Count == 0) return null;

                // The file is written in character order; that is one of the two
                // orders and can be taken as it stands. The other is sorted here
                // rather than stored twice — see SyncMap for why both are needed.
                map.ByChar.AddRange(points);
                points.Sort((a, b) => a.Seconds != b.Seconds
                    ? a.Seconds.CompareTo(b.Seconds)
                    : a.CharOffset.CompareTo(b.CharOffset));
                foreach (SyncPoint p in points)
                    if (map.ByTime.Count == 0 || p.Seconds != map.ByTime[map.ByTime.Count - 1].Seconds)
                        map.ByTime.Add(p);

                Sync = map;
                return Sync;
            }
            catch { return null; }
        }

        /// <summary>Sets the page-marker structure (from the import extractor) so
        /// the next Save persists it to [TextNav].</summary>
        public void SetTextPages(List<(string Label, int Offset)> pages)
        {
            TextPages = pages ?? new List<(string, int)>();
        }

        /// <summary>Brings a book imported before cleaning moved to import time up
        /// to date: rewrites <c>content.txt</c> cleaned and moves its stored
        /// heading and page offsets with it, so they keep pointing at the same
        /// words. One-time — <see cref="TextCleaned"/> then says so. Returns true
        /// when the file was rewritten (the caller re-reads it).</summary>
        public bool CleanTextFileOnce()
        {
            if (TextCleaned || !IsTextBook || string.IsNullOrEmpty(TextFilePath)) return false;
            try
            {
                string raw = TtsReader.ReadFile(TextFilePath);
                if (string.IsNullOrEmpty(raw)) return false;

                // Only the marks the PARSER recorded are moved. They were measured
                // on the raw text, which is what needs correcting.
                //
                // The reading position and the bookmarks are NOT: they were taken
                // from the reader, which had already cleaned the text, so they are
                // in the cleaned text's own coordinates — the very ones this file
                // is being brought into. Moving them again would push them off by
                // the drift a second time. (They can be a couple of characters out
                // where a cut fell inside a pattern the cleaning rules rewrite;
                // the reader snaps to the nearest sentence, so that is invisible.)
                var offsets = new List<int>();
                foreach (var h in TextHeadings) offsets.Add(h.Offset);
                foreach (var p in TextPages) offsets.Add(p.Offset);

                string cleaned = TextCleaner.CleanWithOffsets(raw, offsets);
                File.WriteAllText(TextFilePath, cleaned, new System.Text.UTF8Encoding(false));

                int at = 0;
                for (int i = 0; i < TextHeadings.Count; i++, at++)
                    TextHeadings[i] = (TextHeadings[i].Level, TextHeadings[i].Label, offsets[at]);
                for (int i = 0; i < TextPages.Count; i++, at++)
                    TextPages[i] = (TextPages[i].Label, offsets[at]);
                if (TextPosition > cleaned.Length) TextPosition = 0;

                TextChars = cleaned.Length;
                TextCleaned = true;
                Save();
                return true;
            }
            catch { return false; }
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
            IsHybrid = false;
            TextFilePath = null;
            // Audio AND text is a HYBRID, not a text book: the text is a second
            // output, the narration is still the reading. TextFilePath is set so
            // the text can be shown and navigated, but IsTextBook stays false so
            // nothing switches the transport over to TTS.
            if (IsDaisy || Chapters.Count > 0)
            {
                try
                {
                    string text = Path.Combine(FolderPath, "content.txt");
                    if (File.Exists(text) && File.Exists(SyncFilePath))
                    {
                        IsHybrid = true;
                        TextFilePath = text;
                    }
                }
                catch { }
                return;
            }
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
                // A HYBRID also has content.txt but must still get its DAISY nav —
                // it is played, not read aloud by TTS — and the sync map beside the
                // text is what tells the two apart without re-parsing anything.
                if (File.Exists(Path.Combine(FolderPath, "content.txt"))
                    && !File.Exists(SyncFilePath)) return;
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

            BuildHybridNavFromText();
        }

        /// <summary>Puts a hybrid's HEADINGS on the audio timeline, for a book
        /// whose navigation did not come with times.
        ///
        /// <para>A DAISY names its headings in the NCX with the audio file and
        /// offset beside each one. A narrated EPUB does not: its headings are in
        /// the text, and what ties the text to the clock is the sync map. So each
        /// heading's character offset is asked of the map and comes back as a
        /// position in seconds — the same list, arrived at from the other
        /// side.</para>
        ///
        /// <para>Without this, Go To offered the audio file names — "aud001",
        /// "aud002" — and the seek step could only say "Part". A reader cannot
        /// navigate a book by the producer's file numbering, and it is the one
        /// thing a narrated book is supposed to be good at.</para>
        ///
        /// <para>Computed at load rather than stored: the map and the headings are
        /// both on disk already, and a second copy of a thing derived from them is
        /// a second copy to go stale.</para></summary>
        private void BuildHybridNavFromText()
        {
            try
            {
                if (DaisyHeadings.Count > 0) return;         // it named its own
                if (!IsHybrid || TextHeadings == null || TextHeadings.Count == 0) return;
                SyncMap map = LoadSyncMap();
                if (map == null || map.IsEmpty) return;

                foreach (var h in TextHeadings)
                {
                    double at = DaisySync.SecondsAt(map, h.Offset);
                    if (at >= 0) DaisyHeadings.Add((h.Level, h.Label, at));
                }
                DaisyHeadings.Sort((x, y) => x.Position.CompareTo(y.Position));
            }
            catch { }
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

            // A CUE sheet beside one long file marks where each track begins —
            // the same thing an M4B carries inside itself, so it becomes the same
            // chapter list. Only when the book has no chapter marks already.
            if (M4bChapters.Count == 0)
            {
                CueSheet cue = CueParser.TryParseForFolder(FolderPath, audioFiles);
                // A sheet whose last mark lies beyond the end of the audio is not
                // describing this file — it was copied in from another rip. That
                // check is worth more than any name comparison.
                if (cue != null && (TotalDuration <= 0
                        || cue.Chapters[cue.Chapters.Count - 1].Position < TotalDuration))
                    SetM4bChapters(cue.Chapters);
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
            ini.Write("Settings", "TextVisual", TextVisual ? "1" : "0");
            ini.Write("Settings", "TextVisualMode", TextVisualMode.ToString());
            ini.Write("Settings", "TextColour", TextColour.ToString());
            ini.Write("Settings", "TextBackColour", TextBackColour.ToString());
            ini.Write("Settings", "TextHighlight", TextHighlight.ToString());
            ini.Write("Settings", "TextHighlightColour", TextHighlightColour.ToString());
            ini.Write("Settings", "TextBraille", TextBraille ? "1" : "0");
            ini.Write("Settings", "TextNoSpeech", TextNoSpeech ? "1" : "0");
            ini.Write("Book", "Language", TextLanguage ?? "");
            // Tell Settings this language exists in the library, so a voice can be
            // chosen for it even when nothing installed speaks it.
            if (AppSettings.LanguageSeen != null) AppSettings.LanguageSeen(TextLanguage ?? "");
            ini.Write("Book", "TextCleaned", TextCleaned ? "1" : "0");
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
