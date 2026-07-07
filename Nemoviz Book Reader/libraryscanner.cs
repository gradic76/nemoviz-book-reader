using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;

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
                if (ext == ".zip")
                    ExtractZip(file, folderPath, books);
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

        private string DetectFormat(string folderPath)
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

        private void ExtractZip(string zipPath, string destinationFolder, List<BookData> books)
        {
            try
            {
                string zipName = Path.GetFileNameWithoutExtension(zipPath);
                string extractPath = Path.Combine(destinationFolder, zipName);
                if (Directory.Exists(extractPath))
                    return;
                ZipFile.ExtractToDirectory(zipPath, extractPath);
                ScanFolder(extractPath, books);
                File.Delete(zipPath);
            }
            catch
            {
                // If extraction fails, silently skip
            }
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
