using System;
using System.IO;

namespace Nemoviz_Book_Reader
{
    public class AppSettings
    {
        private static readonly string AppFolder =
            AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string SettingsPath =
            Path.Combine(AppFolder, "Settings.ini");
        private static readonly string DefaultLibraryPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NBR Library");
        private static readonly string DefaultLangPath =
            Path.Combine(AppFolder, "Lang");

        private IniFile ini;

        public string LibraryPath { get; private set; }
        public string LastOpenedBookPath { get; private set; }
        /// <summary>Folder last picked in the "Open folder" import dialog, so
        /// it reopens there instead of at some default each time.</summary>
        public string LastImportFolder { get; private set; }
        public string LangPath { get; private set; }
        public string LanguageCode { get; private set; }

        /// <summary>
        /// Global "start playing after jump" state of the Go To dialog's
        /// checkbox. Remembered across books and sessions — if it suits the
        /// user on one book, it'll suit them on the others.
        /// </summary>
        public bool GoToAutoPlay { get; private set; }

        /// <summary>
        /// When true (default), a book's title/author come from embedded
        /// metadata when available — audio: Album = title, Artist = author;
        /// EPUB: dc:title / dc:creator. When false, the folder/file name is used
        /// instead. Plain text (docx/rtf/odt/txt) has no usable metadata, so the
        /// name is always used regardless of this setting.
        /// </summary>
        public bool UseMetadata { get; private set; }

        // Global text-to-speech defaults for text books (per-book overrides live
        // in Book.ini). Speed is a nominal words-per-minute; pitch is SAPI-style
        // (-10..10); volume 0..100. These are the values of the DEFAULT voice
        // below; every voice the user has set up keeps its own in TtsVoicePrefs.
        public string TtsVoice { get; private set; }
        public int TtsWpm { get; private set; }
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

        /// <summary>Whether the explanatory hint lines are shown beside controls.
        /// On by default — they cost a first-time user nothing and can be switched
        /// off from any dialog that has the toggle.</summary>
        public bool ShowHints { get; private set; }

        /// <summary>The libmpv <c>audio-device</c> identifier for output (e.g.
        /// <c>wasapi/{…}</c>). Empty means <c>auto</c> — mpv picks the system
        /// default. Set from Settings → Device.</summary>
        public string AudioDevice { get; private set; }

        public AppSettings()
        {
            ini = new IniFile(SettingsPath);
            LibraryPath = ini.Read("Library", "Path", DefaultLibraryPath);
            LastOpenedBookPath = ini.Read("Library", "LastBook", "");
            LastImportFolder = ini.Read("Library", "LastImportFolder", "");
            LangPath = ini.Read("App", "LangPath", DefaultLangPath);
            LanguageCode = ini.Read("App", "Language", "en");
            GoToAutoPlay = ini.Read("Player", "GoToAutoPlay", "0") == "1";
            UseMetadata = ini.Read("Import", "UseMetadata", "1") == "1";
            TtsVoice = ini.Read("TextToSpeech", "Voice", "");
            int.TryParse(ini.Read("TextToSpeech", "Wpm", "175"), out int ttsWpm);
            TtsWpm = ttsWpm;
            int.TryParse(ini.Read("TextToSpeech", "Pitch", "0"), out int ttsPitch);
            TtsPitch = ttsPitch;
            int.TryParse(ini.Read("TextToSpeech", "Volume", "100"), out int ttsVol);
            TtsVolume = ttsVol;
            TtsVoicePrefs = new VoicePrefsTable();
            TtsVoicePrefs.Load(ini);
            // Settings written before voices were remembered individually hold one
            // set of numbers; they belong to the voice that was selected then.
            TtsVoicePrefs.SetIfAbsent(TtsVoice, new VoicePrefs(TtsWpm, TtsVolume, TtsPitch));
            AudioDevice = ini.Read("Audio", "Device", "");
            MediaKeys = ini.Read("Player", "MediaKeys", "1") == "1";
            MediaKeysGlobal = ini.Read("Player", "MediaKeysGlobal", "0") == "1";
            ShowHints = ini.Read("App", "ShowHints", "1") == "1";
        }

        public void SetMediaKeys(bool enabled, bool global)
        {
            MediaKeys = enabled;
            MediaKeysGlobal = global;
            ini.Write("Player", "MediaKeys", enabled ? "1" : "0");
            ini.Write("Player", "MediaKeysGlobal", global ? "1" : "0");
        }

        public void SetShowHints(bool value)
        {
            ShowHints = value;
            ini.Write("App", "ShowHints", value ? "1" : "0");
        }

        /// <summary>The remembered setup of a voice, or the neutral default when
        /// this machine has never set that voice up.</summary>
        public VoicePrefs PrefsFor(string voice)
        {
            return TtsVoicePrefs.Get(voice, VoicePrefs.Default);
        }

        public void SetAudioDevice(string device)
        {
            AudioDevice = device ?? "";
            ini.Write("Audio", "Device", AudioDevice);
        }

        /// <summary>Stores the default voice and how it is set up. The numbers are
        /// also filed under that voice, so returning to it later restores them
        /// even after other voices have been used in between.</summary>
        public void SetTtsDefaults(string voice, int wpm, int pitch, int volume)
        {
            TtsVoice = voice ?? "";
            TtsWpm = wpm;
            TtsPitch = pitch;
            TtsVolume = volume;
            ini.Write("TextToSpeech", "Voice", TtsVoice);
            ini.Write("TextToSpeech", "Wpm", TtsWpm.ToString());
            ini.Write("TextToSpeech", "Pitch", TtsPitch.ToString());
            ini.Write("TextToSpeech", "Volume", TtsVolume.ToString());
            SetVoicePrefs(TtsVoice, new VoicePrefs(wpm, volume, pitch));
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

        public void SetLanguage(string code)
        {
            LanguageCode = code;
            ini.Write("App", "Language", code);
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