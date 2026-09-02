using System;
using System.Collections.Generic;
using System.IO;

namespace Nemoviz_Book_Reader
{
    public class AppSettings
    {
        private static readonly string AppFolder =
            AppDomain.CurrentDomain.BaseDirectory;
        // IN THE USER'S FOLDER, not beside the exe (Gordan, 2026-08-23). A
        // program installed where Windows programs belong cannot write to its own
        // folder; see UserData for the whole split and why the reading-only
        // material stays put.
        private static string SettingsPath { get { return UserData.File("Settings.ini"); } }
        private static readonly string DefaultLibraryPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NBR Library");
        private static readonly string DefaultLangPath =
            Path.Combine(AppFolder, "Lang");
        // IN THE READER'S FOLDER, and there is no second place (Gordan,
        // 2026-09-02). Rules beside the program could not be written to without
        // elevation and were overwritten by the next installer; a copy in each
        // place needed a rule about which one wins, and that rule is the
        // complication he threw out. One folder, and it survives an update and an
        // uninstall because it is the reader's rather than ours.
        private static string DefaultRulesPath { get { return UserData.File("Translation"); } }

        private IniFile ini;

        public string LibraryPath { get; private set; }
        public string LastOpenedBookPath { get; private set; }
        /// <summary>Folder last picked in the "Open folder" import dialog, so
        /// it reopens there instead of at some default each time.</summary>
        public string LastImportFolder { get; private set; }
        /// <summary>Where "Open file" last browsed. Windows usually remembers
        /// this by itself, but only usually — and "usually" is not a rule
        /// (Gordan, 2026-08-03), so both dialogs now reopen where they were.</summary>
        public string LastImportFileFolder { get; private set; }
        public string LangPath { get; private set; }
        public string RulesPath { get; private set; }
        public string LanguageCode { get; private set; }

        /// <summary>
        /// Global "start playing after jump" state of the Go To dialog's
        /// checkbox. Remembered across books and sessions — if it suits the
        /// user on one book, it'll suit them on the others.
        /// </summary>
        public bool GoToAutoPlay { get; private set; }

        // Whether NBR asks GitHub for the newest release once a day, and when it
        // last did. See UpdateCheck for what is and is not sent.
        public bool AutoCheckUpdates { get; private set; }
        public DateTime LastUpdateCheck { get; private set; }


        /// <summary>
        /// When true (default), a book's title/author come from embedded
        /// metadata when available — audio: Album = title, Artist = author;
        /// EPUB: dc:title / dc:creator. When false, the folder/file name is used
        /// instead. Plain text (docx/rtf/odt/txt) has no usable metadata, so the
        /// name is always used regardless of this setting.
        /// </summary>
        public bool UseMetadata { get; private set; }

        // Global text-to-speech defaults for text books (per-book overrides live
        // in Book.ini). Speed is a percentage of the voice's own natural pace,
        // 50..300, exactly as an audio book's is; pitch is SAPI-style (-10..10);
        // volume 0..100. These are the values of the DEFAULT voice below; every
        // voice the user has set up keeps its own in TtsVoicePrefs.
        public string TtsVoice { get; private set; }
        public int TtsSpeed { get; private set; }
        public int TtsPitch { get; private set; }
        public int TtsVolume { get; private set; }

        /// <summary>Speed / volume / pitch remembered per voice, so picking a
        /// voice restores how that voice was set up instead of inheriting the
        /// numbers of the one before it.</summary>
        public VoicePrefsTable TtsVoicePrefs { get; private set; }

        /// <summary>Whether the keyboard's media keys (Play/Pause, Next, Previous)
        /// drive NBR at all. On by default.</summary>
        public bool MediaKeys { get; private set; }

        /// <summary>Whether they drive it even when NBR is in the background. Off
        /// by default: registering them system-wide takes them away from every
        /// other player, which is a choice the user has to make deliberately.</summary>
        public bool MediaKeysGlobal { get; private set; }

        /// <summary>Which look the app builds: <c>classic</c> (what it has always
        /// looked like — the build regular testing runs on) or <c>new</c> (the
        /// redesign in progress). Temporary, until the new look replaces the old
        /// one for good. Switched in Settings → Misc.</summary>
        public string UiTheme { get; private set; }

        /// <summary>Whether the explanatory hint lines are shown beside controls.
        /// On by default — they cost a first-time user nothing and can be switched
        /// off from any dialog that has the toggle.</summary>

        /// <summary>How a book is shown on screen when nothing has been decided
        /// FOR that book: the same six choices Properties offers, standing as the
        /// rule the way the language→voice map does (§ Settings and Properties are
        /// the same two combos). A book takes these the first time it is opened
        /// and owns its copy from then on, so changing the rule later does not
        /// walk over a book someone has already set up by hand.
        ///
        /// <para>Until 2026-08-03 these six controls existed in Settings and in
        /// Properties and wrote nowhere at all.</para></summary>
        public bool Visual { get; private set; }
        public int VisualMode { get; private set; }
        public int Highlight { get; private set; }
        public int HighlightColour { get; private set; }
        public int TextColour { get; private set; }
        public int BackColour { get; private set; }

        /// <summary>What the reading window was left set to — the face and the
        /// size. Not per book (Gordan, 2026-08-03: "najbolje da pamti zadnje
        /// odabrano"): this is the reader's eyesight, which does not change from
        /// one book to the next, and it is chosen in the window itself rather
        /// than in Properties. Empty font means "whatever the window defaults
        /// to".</summary>
        public string ReadingFont { get; private set; }
        public int ReadingFontSize { get; private set; }

        /// <summary>How the shelf was left sorted: what by, and which way. A
        /// reader who arranges the library by status once means it, and coming
        /// back to alphabetical on the next run is the app forgetting something
        /// it was told (Gordan, 2026-08-03).</summary>
        public string ShelfSortKey { get; private set; }
        public bool ShelfSortAscending { get; private set; }

        /// <summary>Whether the card the player uses is held awake while a book
        /// plays, so it cannot power down between sentences and swallow the start
        /// of the next one (§10f — Gordan's HDMI output does exactly that).
        ///
        /// <para><b>On by default</b>, because the fault it prevents is one
        /// almost nobody would diagnose: words go missing and every measurement
        /// says the software is correct. It is a switch rather than a fact
        /// because it does keep an audio endpoint open, and on a machine that
        /// does not need it that is a cost with no return (Gordan,
        /// 2026-08-03).</para></summary>
        public bool KeepDeviceAlive { get; private set; }

        /// <summary>Whether NBR may use an optical drive to play an audio CD.
        ///
        /// <para><b>Off unless asked for (Gordan, 2026-08-07)</b>, even on a
        /// machine that has a drive. Reading a CD spins up hardware that most
        /// readers will never point at NBR, and a feature that touches the
        /// machine should be the reader's decision rather than ours. The Library's
        /// "Open audio CD" follows this switch, not the presence of a
        /// drive.</para></summary>
        public bool UseOpticalDrive { get; private set; }

        /// <summary>Which optical drive to read, as a letter with its colon
        /// ("F:"), or "" for whichever one has a disc in it.
        ///
        /// <para><b>More than one is not the museum piece it sounds like</b>
        /// (Gordan, 2026-08-07): he runs a physical drive and a virtual one side
        /// by side on another machine, and image-mounting software puts a second
        /// drive on plenty of systems. Guessing between them is exactly the sort
        /// of thing that looks fine on the machine it was written on.</para>
        ///
        /// <para>A letter that has since disappeared — the software uninstalled,
        /// the drive unplugged — falls back to the automatic search rather than
        /// failing, so a setting made on one machine cannot break NBR on
        /// another.</para></summary>
        public string OpticalDriveLetter { get; private set; }

        /// <summary>The libmpv <c>audio-device</c> identifier for output (e.g.
        /// <c>wasapi/{…}</c>). Empty means <c>auto</c> — mpv picks the system
        /// default. Set from Settings → Device.</summary>
        public string AudioDevice { get; private set; }

        /// <summary>Recognizer language for image documents; empty = automatic.
        /// See <see cref="SetOcrLanguage"/>.</summary>
        public string OcrLanguage { get; private set; }

        /// <summary>The language a book was last translated INTO. Remembered so the
        /// question is only really asked once: the first time the Windows display
        /// language is offered, and after that whatever the reader chose — which
        /// does not change from book to book the way the source language does.</summary>
        public string LastTranslationTarget { get; private set; }

        /// <summary>What the reader wants said to the translator about EVERY book.
        ///
        /// <para>Some of what one wants to tell a translator is not a property of
        /// the book but a habit of the reader — British spelling rather than
        /// American is the measured case, and it does not change from one book to
        /// the next. Same shape as the language-to-voice rule: the standing rule
        /// lives here, the exception beside the book, and the two are joined when a
        /// translation starts.</para>
        ///
        /// <para><b>Free text and deliberately not a set of choices.</b> NBR's own
        /// standing instructions were written from Croatian — inflect names, hold
        /// the level of address, hold the speaker's gender — and a fixed set of
        /// questions would harden that bias into the interface, offering our
        /// questions to someone translating into a language where they do not
        /// arise. It reaches the model as prose, so it may be written in whatever
        /// language the reader thinks in.</para></summary>
        public string TranslationNotes { get; private set; }

        public AppSettings()
        {
            ini = new IniFile(SettingsPath);
            LibraryPath = ini.Read("Library", "Path", DefaultLibraryPath);
            LastOpenedBookPath = ini.Read("Library", "LastBook", "");
            LastImportFolder = ini.Read("Library", "LastImportFolder", "");
            LastImportFileFolder = ini.Read("Library", "LastImportFileFolder", "");
            LangPath = ini.Read("App", "LangPath", DefaultLangPath);
            RulesPath = ini.Read("App", "RulesPath", DefaultRulesPath);
            // EMPTY, not "en" -- see Localization.Initialize. An empty setting means
            // nobody has chosen, which is what lets Windows decide on a first run; a
            // literal "en" here made that impossible to tell from a reader who chose
            // English on purpose.
            LanguageCode = ini.Read("App", "Language", "");
            GoToAutoPlay = ini.Read("Player", "GoToAutoPlay", "0") == "1";
            // ON BY DEFAULT, and for a beta that is the point: someone who does
            // not know a fix exists cannot ask for it. It is one request a day to
            // a public endpoint, carrying nothing about the reader or their books
            // (see UpdateCheck), and the switch beside it in Settings is there
            // for anyone who would rather NBR did not reach the network at all.
            AutoCheckUpdates = ini.Read("App", "AutoCheckUpdates", "1") == "1";
            LastUpdateCheck = UpdateCheck.ParseDay(ini.Read("App", "LastUpdateCheck", ""));
            UseMetadata = ini.Read("Import", "UseMetadata", "1") == "1";
            TtsVoice = ini.Read("TextToSpeech", "Voice", "");
            // Speed is the percentage, Wpm the words-per-minute scale it replaced
            // on 2026-08-23. An old file carries only Wpm, and it is converted
            // through the rate so the voice keeps sounding as it did.
            int ttsSpeed;
            if (int.TryParse(ini.Read("TextToSpeech", "Speed", ""), out ttsSpeed))
                TtsSpeed = ttsSpeed;
            else
            {
                int.TryParse(ini.Read("TextToSpeech", "Wpm", ""), out int ttsWpm);
                TtsSpeed = ttsWpm > 0 ? TtsReader.WpmToSpeed(ttsWpm) : 100;
            }
            int.TryParse(ini.Read("TextToSpeech", "Pitch", "0"), out int ttsPitch);
            TtsPitch = ttsPitch;
            int.TryParse(ini.Read("TextToSpeech", "Volume", "100"), out int ttsVol);
            TtsVolume = ttsVol;
            TtsVoicePrefs = new VoicePrefsTable();
            TtsVoicePrefs.Load(ini);
            // Settings written before voices were remembered individually hold one
            // set of numbers; they belong to the voice that was selected then.
            TtsVoicePrefs.SetIfAbsent(TtsVoice, new VoicePrefs(TtsSpeed, TtsVolume, TtsPitch));
            LoadLanguageVoices();
            LoadSeenLanguages();
            LanguageSeen = NoteLanguageSeen;
            AudioDevice = ini.Read("Audio", "Device", "");
            OcrLanguage = ini.Read("Ocr", "Language", "");
            LastTranslationTarget = ini.Read("Translate", "Target", "");
            TranslationNotes = ini.Read("Translate", "Notes", "");
            UseCloudVoices = ini.Read("TextToSpeech", "UseCloudVoices", "0") == "1";
            KeepDeviceAlive = ini.Read("Audio", "KeepAlive", "1") == "1";
            UseOpticalDrive = ini.Read("Audio", "UseOpticalDrive", "0") == "1";
            OpticalDriveLetter = ini.Read("Audio", "OpticalDrive", "");
            MediaKeys = ini.Read("Player", "MediaKeys", "1") == "1";
            MediaKeysGlobal = ini.Read("Player", "MediaKeysGlobal", "0") == "1";
            // EMPTY for anyone who has never chosen, and UiTheme.Select turns that
            // into the default: NBR design, or the system-colours look where the
            // reader runs high contrast. An install that already carries "classic"
            // or "new" keeps it — that WAS a choice, and it stands. One that
            // carries the retired "follow" is understood too, and resolves the
            // same way an unset value does.
            UiTheme = ini.Read("App", "Theme", "");   // unset = let UiTheme.Select apply the default rule

            Visual = ini.Read("Visual", "Use", "0") == "1";
            WarnBrailleReread = ini.Read("App", "WarnBrailleReread", "1") == "1";
            WarnSoundProcessing = ini.Read("App", "WarnSoundProcessing", "1") == "1";
            RulesSeeded = ini.Read("App", "RulesSeeded", "0") == "1";
            VisualMode = Clamp(ReadInt("Visual", "Mode", 0), 0, 2);
            Highlight = Clamp(ReadInt("Visual", "Highlight", 1), 0, 2);
            HighlightColour = ReadingColours.Clamp(
                ReadInt("Visual", "HighlightColour", ReadingColours.DefaultHighlight));
            TextColour = ReadingColours.Clamp(
                ReadInt("Visual", "TextColour", ReadingColours.DefaultText));
            BackColour = ReadingColours.Clamp(
                ReadInt("Visual", "BackColour", ReadingColours.DefaultBack));

            ReadingFont = ini.Read("Visual", "Font", "");
            ReadingFontSize = Clamp(ReadInt("Visual", "FontSize", 26), 10, 96);

            ShelfSortKey = ini.Read("Library", "SortKey", "alpha");
            ShelfSortAscending = ini.Read("Library", "SortAscending", "1") == "1";
        }

        private int ReadInt(string section, string key, int def)
        {
            int v;
            return int.TryParse(ini.Read(section, key, def.ToString()), out v) ? v : def;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public void SetUiTheme(string id)
        {
            UiTheme = id ?? Nemoviz_Book_Reader.UiTheme.ClassicId;
            ini.Write("App", "Theme", UiTheme);
        }

        public void SetMediaKeys(bool enabled, bool global)
        {
            MediaKeys = enabled;
            MediaKeysGlobal = global;
            ini.Write("Player", "MediaKeys", enabled ? "1" : "0");
            ini.Write("Player", "MediaKeysGlobal", global ? "1" : "0");
        }

        /// <summary>Whether to warn before re-reading a braille book with another
        /// table. On until the reader ticks "Don't show this again" — Gordan,
        /// 2026-08-04: *"kroz par puta će se naučiti i isključiti"*. The warning is
        /// owed because the re-read throws away the reading position, the
        /// bookmarks and the percentage; the switch-off is owed because a reader
        /// hunting for the right table will meet it several times in a row.</summary>
        public bool WarnBrailleReread { get; private set; }

        /// <summary>Whether to say what sound processing can and cannot do the
        /// next time it is switched on. On until the reader ticks it away — the
        /// same shape as WarnBrailleReread.</summary>
        public bool WarnSoundProcessing { get; private set; }

        /// <summary>Whether the supplied rulebooks have been written into the
        /// reader's folder. Once, not on every launch: deleting one of them is a
        /// decision, and a launch that put it back would undo it silently.</summary>
        public bool RulesSeeded { get; private set; }

        public void MarkRulesSeeded()
        {
            if (RulesSeeded) return;
            RulesSeeded = true;
            ini.Write("App", "RulesSeeded", "1");
        }

        public void SetWarnSoundProcessing(bool value)
        {
            if (value == WarnSoundProcessing) return;
            WarnSoundProcessing = value;
            ini.Write("App", "WarnSoundProcessing", value ? "1" : "0");
        }

        public void SetWarnBrailleReread(bool value)
        {
            WarnBrailleReread = value;
            ini.Write("App", "WarnBrailleReread", value ? "1" : "0");
        }

        /// <summary>The visual rule, as Settings left it.</summary>
        public void SetVisualDefaults(bool use, int mode, int highlight,
                                      int highlightColour, int textColour, int backColour)
        {
            Visual = use;
            VisualMode = Clamp(mode, 0, 2);
            Highlight = Clamp(highlight, 0, 2);
            HighlightColour = ReadingColours.Clamp(highlightColour);
            TextColour = ReadingColours.Clamp(textColour);
            BackColour = ReadingColours.Clamp(backColour);

            ini.Write("Visual", "Use", Visual ? "1" : "0");
            ini.Write("Visual", "Mode", VisualMode.ToString());
            ini.Write("Visual", "Highlight", Highlight.ToString());
            ini.Write("Visual", "HighlightColour", HighlightColour.ToString());
            ini.Write("Visual", "TextColour", TextColour.ToString());
            ini.Write("Visual", "BackColour", BackColour.ToString());
        }

        /// <summary>What the reading window is left set to. Written as it happens
        /// rather than on a save button, because the window has none — the reader
        /// changes the face or the size and closes it.</summary>
        public void SetReadingFont(string family, int size)
        {
            string f = family ?? "";
            int s = Clamp(size, 10, 96);
            // Called every time the face is applied, which includes every layout
            // pass — so nothing is written unless something actually changed.
            if (f == ReadingFont && s == ReadingFontSize) return;
            ReadingFont = f;
            ReadingFontSize = s;
            ini.Write("Visual", "Font", ReadingFont);
            ini.Write("Visual", "FontSize", ReadingFontSize.ToString());
        }

        public void SetShelfSort(string key, bool ascending)
        {
            if (key == ShelfSortKey && ascending == ShelfSortAscending) return;
            ShelfSortKey = string.IsNullOrEmpty(key) ? "alpha" : key;
            ShelfSortAscending = ascending;
            ini.Write("Library", "SortKey", ShelfSortKey);
            ini.Write("Library", "SortAscending", ascending ? "1" : "0");
        }

        /// <summary>Set by the app at startup so a window with no AppSettings in
        /// its hands can still remember what the reader chose. The reading window
        /// is such a window: it is built from the player, which owns the settings,
        /// but it is the window itself that knows the font was changed.</summary>
        public static AppSettings Current;

        /// <summary>The remembered setup of a voice, or the neutral default when
        /// this machine has never set that voice up.</summary>
        public VoicePrefs PrefsFor(string voice)
        {
            return TtsVoicePrefs.Get(voice, VoicePrefs.Default);
        }

        // ── Languages this library has actually met ───────────────────────────
        // Settings can only offer a rule for a language it knows about, and the
        // languages with a voice installed are not that set: a French book on the
        // shelf with no French voice is exactly the case a rule is wanted for.
        // So the LIBRARY is the second source — a language goes on the list the
        // moment a book in it is imported.
        //
        // Reported through a static hook rather than plumbed through every import
        // path (there are four, one of them a static helper with no settings in
        // sight). BookData.Save is the one place they all pass through, and it
        // knows the language by then. Already-known languages return at once, so
        // the routine progress saves cost nothing.
        private const string SeenSection = "Languages";
        private readonly List<string> seenLanguages = new List<string>();
        public static Action<string> LanguageSeen;

        private void LoadSeenLanguages()
        {
            seenLanguages.Clear();
            foreach (string raw in ini.Read(SeenSection, "Seen", "").Split(','))
            {
                string c = LanguageDetector.Primary(raw);
                if (c.Length > 0 && !seenLanguages.Contains(c)) seenLanguages.Add(c);
            }
        }

        /// <summary>Every language a book in this library has been found to be in.</summary>
        public IEnumerable<string> SeenLanguages { get { return seenLanguages; } }

        public void NoteLanguageSeen(string tag)
        {
            string code = LanguageDetector.Primary(tag);
            if (code.Length == 0 || seenLanguages.Contains(code)) return;
            seenLanguages.Add(code);
            seenLanguages.Sort(StringComparer.OrdinalIgnoreCase);
            ini.Write(SeenSection, "Seen", string.Join(",", seenLanguages.ToArray()));
        }

        // ── One default voice per language ────────────────────────────────────
        // What finally makes language detection a feature rather than a fact:
        // opening a book becomes detect → look up that language's voice. Stored
        // by primary code, which is a safe INI key ("hr", "sr") unlike a voice
        // name; the Languages line keeps the section readable and enumerable.
        //
        //   [LanguageVoices]
        //   Languages=hr,sr
        //   hr=Microsoft Matej
        //
        private const string LangVoiceSection = "LanguageVoices";
        private readonly Dictionary<string, string> languageVoices =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void LoadLanguageVoices()
        {
            languageVoices.Clear();
            string list = ini.Read(LangVoiceSection, "Languages", "");
            foreach (string raw in list.Split(','))
            {
                string code = LanguageDetector.Primary(raw);
                if (code.Length == 0) continue;
                string voice = ini.Read(LangVoiceSection, code, "");
                if (voice.Length > 0) languageVoices[code] = voice;
            }
        }

        /// <summary>The voice chosen for this language and nothing else — empty
        /// when none has been. Kept apart from <see cref="DefaultVoiceForLanguage"/>
        /// so a caller can tell "this language has a voice" from "it is falling
        /// back to something else", which is the difference the user is told
        /// about.</summary>
        public string LanguageVoice(string tag)
        {
            string code = LanguageDetector.Primary(tag);
            string v;
            return code.Length > 0 && languageVoices.TryGetValue(code, out v) ? v : "";
        }

        /// <summary>Every language that has been given a voice.</summary>
        public IEnumerable<string> LanguagesWithVoice { get { return languageVoices.Keys; } }

        /// <summary>Sets (or, with an empty voice, clears) a language's voice and
        /// writes the section straight away.</summary>
        public void SetLanguageVoice(string tag, string voice)
        {
            string code = LanguageDetector.Primary(tag);
            if (code.Length == 0) return;
            if (string.IsNullOrEmpty(voice)) languageVoices.Remove(code);
            else languageVoices[code] = voice;

            var codes = new List<string>(languageVoices.Keys);
            codes.Sort(StringComparer.OrdinalIgnoreCase);
            ini.Write(LangVoiceSection, "Languages", string.Join(",", codes.ToArray()));
            // The cleared language keeps its key in the file with an empty value;
            // it is off the Languages line, so it is not read back.
            ini.Write(LangVoiceSection, code, languageVoices.ContainsKey(code) ? languageVoices[code] : "");
        }

        public void SetAudioDevice(string device)
        {
            AudioDevice = device ?? "";
            ini.Write("Audio", "Device", AudioDevice);
        }

        /// <summary>Which recognizer to read image documents with. Empty means
        /// the user's own Windows languages. It is the DEFAULT and not an
        /// answer — the language genuinely matters to the reading (see
        /// <see cref="WindowsOcr"/>), so the import asks whenever there is more
        /// than one recognizer to ask about, and this is what it offers first.
        /// A tag that is not installed
        /// falls back rather than failing, so a setting made on one machine
        /// cannot break NBR on another — the same rule
        /// <see cref="OpticalDriveLetter"/> follows.</summary>
        public void SetOcrLanguage(string tag)
        {
            OcrLanguage = tag ?? "";
            ini.Write("Ocr", "Language", OcrLanguage);
        }

        public void SetLastTranslationTarget(string tag)
        {
            LastTranslationTarget = tag ?? "";
            ini.Write("Translate", "Target", LastTranslationTarget);
        }

        public void SetTranslationNotes(string text)
        {
            TranslationNotes = (text ?? "").Trim();
            ini.Write("Translate", "Notes", TranslationNotes);
        }

        public void SetKeepDeviceAlive(bool on)
        {
            if (on == KeepDeviceAlive) return;
            KeepDeviceAlive = on;
            ini.Write("Audio", "KeepAlive", on ? "1" : "0");
        }

        public void SetUseOpticalDrive(bool on)
        {
            if (on == UseOpticalDrive) return;
            UseOpticalDrive = on;
            ini.Write("Audio", "UseOpticalDrive", on ? "1" : "0");
        }

        /// <summary>"" means "whichever drive has a disc in it".</summary>
        public void SetOpticalDriveLetter(string letter)
        {
            letter = letter ?? "";
            if (letter == OpticalDriveLetter) return;
            OpticalDriveLetter = letter;
            ini.Write("Audio", "OpticalDrive", letter);
        }

        /// <summary>Whether the cloud voices are offered in Properties.
        ///
        /// <para><b>Remembered rather than session-only, and Gordan reversed his
        /// own earlier call to get here</b> (2026-08-15): the switch lives on the
        /// Advanced tab, a page away from the picker it affects, and *"pošto je
        /// na neočekivanom mjestu ipak mora pamtiti"* — something a reader had to
        /// hunt for once must not have to be hunted for again at every
        /// launch.</para>
        ///
        /// <para>It governs what the PICKER OFFERS and nothing else. A book that
        /// already has a cloud voice keeps reading with it whatever this says —
        /// see <see cref="CloudSpeechBackend"/>, which reports its voices to the
        /// composite either way.</para></summary>
        public bool UseCloudVoices { get; private set; }

        public void SetUseCloudVoices(bool on)
        {
            UseCloudVoices = on;
            ini.Write("TextToSpeech", "UseCloudVoices", on ? "1" : "0");
        }

        /// <summary>Stores the default voice and how it is set up. The numbers are
        /// also filed under that voice, so returning to it later restores them
        /// even after other voices have been used in between.</summary>
        public void SetTtsDefaults(string voice, int speed, int pitch, int volume)
        {
            TtsVoice = voice ?? "";
            TtsSpeed = speed;
            TtsPitch = pitch;
            TtsVolume = volume;
            ini.Write("TextToSpeech", "Voice", TtsVoice);
            ini.Write("TextToSpeech", "Speed", TtsSpeed.ToString());
            ini.Write("TextToSpeech", "Pitch", TtsPitch.ToString());
            ini.Write("TextToSpeech", "Volume", TtsVolume.ToString());
            // The old key is BLANKED rather than left standing (IniFile can drop a
            // whole section but not one key), so a file cannot end up carrying two
            // answers to one question. The load above prefers Speed anyway; this
            // is for anything that ever reads the file without knowing that.
            ini.Write("TextToSpeech", "Wpm", "");
            SetVoicePrefs(TtsVoice, new VoicePrefs(speed, volume, pitch));
        }

        /// <summary>Remembers how one voice is set up (any voice, not just the
        /// default one) and saves it straight away.</summary>
        public void SetVoicePrefs(string voice, VoicePrefs prefs)
        {
            if (string.IsNullOrEmpty(voice)) return;
            TtsVoicePrefs.Set(voice, prefs);
            TtsVoicePrefs.Save(ini);
        }

        public void SetLibraryPath(string newPath)
        {
            LibraryPath = newPath;
            ini.Write("Library", "Path", newPath);
        }

        public void SetLastOpenedBook(string folderPath)
        {
            LastOpenedBookPath = folderPath;
            ini.Write("Library", "LastBook", folderPath);
        }

        public void SetLastImportFolder(string folderPath)
        {
            LastImportFolder = folderPath;
            ini.Write("Library", "LastImportFolder", folderPath);
        }

        public void SetLastImportFileFolder(string folderPath)
        {
            LastImportFileFolder = folderPath ?? "";
            ini.Write("Library", "LastImportFileFolder", LastImportFileFolder);
        }

        public void SetLanguage(string code)
        {
            LanguageCode = code;
            ini.Write("App", "Language", code);
        }

        public void SetAutoCheckUpdates(bool value)
        {
            AutoCheckUpdates = value;
            ini.Write("App", "AutoCheckUpdates", value ? "1" : "0");
        }

        /// <summary>Records that the automatic check has run today. Written the
        /// moment the check is STARTED rather than when it answers: a machine with
        /// no network would otherwise try again on every launch, which is the one
        /// case where retrying is most certain to be useless.</summary>
        public void NoteUpdateCheck()
        {
            LastUpdateCheck = DateTime.Now;
            ini.Write("App", "LastUpdateCheck", UpdateCheck.Today);
        }

        public void SetUseMetadata(bool value)
        {
            UseMetadata = value;
            ini.Write("Import", "UseMetadata", value ? "1" : "0");
        }


        public void SetGoToAutoPlay(bool value)
        {
            GoToAutoPlay = value;
            ini.Write("Player", "GoToAutoPlay", value ? "1" : "0");
        }

        public void EnsureLibraryExists()
        {
            if (!Directory.Exists(LibraryPath))
                Directory.CreateDirectory(LibraryPath);
        }

        public void EnsureLangFolderExists()
        {
            if (!Directory.Exists(LangPath))
                Directory.CreateDirectory(LangPath);
        }
    }
}