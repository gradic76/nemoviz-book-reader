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
        // (-10..10); volume 0..100.
        public string TtsVoice { get; private set; }
        public int TtsWpm { get; private set; }
        public int TtsPitch { get; private set; }
        public int TtsVolume { get; private set; }

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
            AudioDevice = ini.Read("Audio", "Device", "");
        }

        public void SetAudioDevice(string device)
        {
            AudioDevice = device ?? "";
            ini.Write("Audio", "Device", AudioDevice);
        }

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