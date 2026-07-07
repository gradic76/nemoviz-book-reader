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
        public string LangPath { get; private set; }
        public string LanguageCode { get; private set; }

        /// <summary>
        /// Global "start playing after jump" state of the Go To dialog's
        /// checkbox. Remembered across books and sessions — if it suits the
        /// user on one book, it'll suit them on the others.
        /// </summary>
        public bool GoToAutoPlay { get; private set; }

        public AppSettings()
        {
            ini = new IniFile(SettingsPath);
            LibraryPath = ini.Read("Library", "Path", DefaultLibraryPath);
            LastOpenedBookPath = ini.Read("Library", "LastBook", "");
            LangPath = ini.Read("App", "LangPath", DefaultLangPath);
            LanguageCode = ini.Read("App", "Language", "en");
            GoToAutoPlay = ini.Read("Player", "GoToAutoPlay", "0") == "1";
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

        public void SetLanguage(string code)
        {
            LanguageCode = code;
            ini.Write("App", "Language", code);
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