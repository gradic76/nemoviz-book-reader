using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Nemoviz_Book_Reader
{
    public class LibraryScanner
    {
        // Public so that BookData can reuse the same lists
        // (single source of truth for supported extensions).
        public static readonly string[] AudioExtensions =
            { ".mp3", ".ogg", ".flac", ".m4a", ".m4b", ".wav", ".opus", ".aac",
              ".wma", ".ape", ".mka", ".spx", ".oga", ".dsf", ".dff", ".caf" };
        public static readonly string[] TextExtensions =
            { ".epub", ".txt", ".pdf", ".djvu", ".fb2", ".mobi", ".azw", ".azw3", ".cbz", ".cbr" };
        public static readonly string[] ArchiveExtensions =
            { ".zip", ".rar", ".7z" };

        private string libraryPath;
        private bool createBookIni;

        public LibraryScanner(string libraryPath, bool createBookIni = false)
        {
            this.libraryPath = libraryPath;
            this.createBookIni = createBookIni;
        }

        public List<BookData> Scan()
        {
            List<BookData> books = new List<BookData>();
            if (!Directory.Exists(libraryPath))
                return books;
            ScanFolder(libraryPath, books);
            return books;
        }

        private void ScanFolder(string folderPath, List<BookData> books)
        {
            foreach (string file in Directory.GetFiles(folderPath))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (IsArchive(ext))
                    ExtractAndScan(file, folderPath, books);
            }

            bool hasMediaFiles = false;
            foreach (string file in Directory.GetFiles(folderPath))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (IsAudio(ext) || IsText(ext))
                {
                    hasMediaFiles = true;
                    break;
                }
            }

            if (hasMediaFiles)
            {
                if (createBookIni)
                    EnsureBookIni(folderPath);
                books.Add(new BookData(folderPath));
            }
            else
            {
                foreach (string subFolder in Directory.GetDirectories(folderPath))
                    ScanFolder(subFolder, books);
            }
        }

        private void EnsureBookIni(string folderPath)
        {
            string iniPath = Path.Combine(folderPath, "Book.ini");
            if (!File.Exists(iniPath))
            {
                BookData book = new BookData(folderPath);
                book.Title = Path.GetFileName(folderPath);
                book.DateAdded = DateTime.Now;
                book.Format = DetectFormat(folderPath);
                book.Save();
            }
        }

        public static string DetectFormat(string folderPath)
        {
            // Quick, extension-only detection so that scanning a large
            // library stays fast. The detailed audio format string
            // ("MP3 Audio, 44.1 kHz, ...") is filled in lazily by
            // BookData.EnsureFormatDetails() when the book is first
            // shown in the library details view.
            foreach (string file in Directory.GetFiles(folderPath))
            {
                string name = BookData.FriendlyFormatName(Path.GetExtension(file));
                if (name != "Unknown")
                    return name;
            }
            return "Unknown";
        }

        /// <summary>Background-scan extraction: a loose archive found sitting
        /// inside a folder being scanned (e.g. dropped into the library via
        /// Explorer). Extracts next to itself, recurses into the result, then
        /// deletes the original — the archive is fully owned by the library
        /// at this point, so nothing is left to keep. Corrupt or
        /// password-protected archives are skipped silently so one bad file
        /// doesn't stop the whole scan.</summary>
        private void ExtractAndScan(string archivePath, string destinationFolder, List<BookData> books)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(archivePath);
                string extractPath = Path.Combine(destinationFolder, name);
                if (Directory.Exists(extractPath))
                    return;
                Directory.CreateDirectory(extractPath);
                ExtractArchive(archivePath, extractPath);
                ScanFolder(extractPath, books);
                File.Delete(archivePath);
            }
            catch
            {
                // Corrupt, password-protected, or otherwise unreadable — skip.
            }
        }

        public static bool IsArchive(string ext)
        {
            return Array.IndexOf(ArchiveExtensions, ext) >= 0;
        }

        /// <summary>Extracts every entry of a zip/rar/7z archive into
        /// destFolder (must already exist). Lets exceptions propagate —
        /// callers driven directly by a user action (Add File, Ctrl+O) should
        /// catch and report; the background scan above wraps its own call.</summary>
        public static void ExtractArchive(string archivePath, string destFolder)
        {
            ArchiveFactory.WriteToDirectory(archivePath, destFolder, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }

        /// <summary>If destFolder has no media directly in it but exactly one
        /// subfolder, moves that subfolder's contents up and removes it —
        /// handles the common archive layout where everything sits inside a
        /// single wrapper folder, so the book still lands exactly at
        /// destFolder regardless of how the archive was packed.</summary>
        public static void FlattenSingleWrapperFolder(string destFolder)
        {
            bool hasMediaDirectly = Directory.GetFiles(destFolder)
                .Any(f => IsAudioOrText(Path.GetExtension(f).ToLower()));
            if (hasMediaDirectly) return;

            string[] subDirs = Directory.GetDirectories(destFolder);
            if (subDirs.Length != 1) return;

            string wrapper = subDirs[0];
            foreach (string entry in Directory.GetFileSystemEntries(wrapper))
            {
                string name = Path.GetFileName(entry);
                string target = Path.Combine(destFolder, name);
                if (Directory.Exists(entry))
                    Directory.Move(entry, target);
                else
                    File.Move(entry, target);
            }
            Directory.Delete(wrapper);
        }

        private static bool IsAudioOrText(string ext)
        {
            return Array.IndexOf(AudioExtensions, ext) >= 0 || Array.IndexOf(TextExtensions, ext) >= 0;
        }

        private bool IsAudio(string ext)
        {
            return Array.IndexOf(AudioExtensions, ext) >= 0;
        }

        private bool IsText(string ext)
        {
            return Array.IndexOf(TextExtensions, ext) >= 0;
        }
    }
}
