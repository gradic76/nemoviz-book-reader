using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

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

        // An archive may contain further archives; cap how deep we auto-extract
        // so a maliciously or accidentally nested set (zip-in-zip-in-zip…) can't
        // recurse without bound.
        private const int MaxArchiveDepth = 3;

        private void ScanFolder(string folderPath, List<BookData> books, int archiveDepth = 0)
        {
            foreach (string file in Directory.GetFiles(folderPath))
            {
                string fn = Path.GetFileName(file);
                // A multi-volume set is extracted once, from its first part;
                // the continuation volumes (.r00, .z01, .002…) are pulled in by
                // GetFileParts, so don't treat them as archives of their own.
                if (IsVolumeContinuation(fn)) continue;
                if (IsExtractableArchive(fn))
                {
                    // Cap archive-in-archive recursion (see MaxArchiveDepth).
                    if (archiveDepth >= MaxArchiveDepth) continue;
                    ExtractAndScan(file, folderPath, books, archiveDepth);
                }
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
                    ScanFolder(subFolder, books, archiveDepth);
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
        private void ExtractAndScan(string archivePath, string destinationFolder, List<BookData> books, int archiveDepth = 0)
        {
            try
            {
                string extractPath = Path.Combine(destinationFolder, BaseArchiveName(archivePath));
                if (Directory.Exists(extractPath))
                    return;
                Directory.CreateDirectory(extractPath);
                // No password provider here: an encrypted archive dropped into
                // the library can't be prompted for mid-scan, so it's skipped
                // (ArchivePasswordRequiredException) like any other unreadable
                // one — the whole scan must not stall on a single bad file.
                ExtractArchive(archivePath, extractPath);
                ScanFolder(extractPath, books, archiveDepth + 1);
                // The set is fully owned by the library now — remove every
                // volume, not just the first part.
                foreach (FileInfo v in GetArchiveVolumes(archivePath))
                    try { v.Delete(); } catch { }
            }
            catch
            {
                // Corrupt, encrypted, unsupported split layout — skip silently.
            }
        }

        public static bool IsArchive(string ext)
        {
            return Array.IndexOf(ArchiveExtensions, ext) >= 0;
        }

        // ── Multi-volume archive recognition ──────────────────────────────
        // Split downloads arrive as either RAR volumes (name.part1.rar +
        // .part2.rar…, or old name.rar + name.r00 + name.r01…), or a numeric
        // split (name.7z.001/.002…, name.zip.001…), or spanned ZIP
        // (name.z01/.z02… + name.zip). We extract once from the entry-point
        // part and let SharpCompress.GetFileParts gather the rest.

        /// <summary>The entry point of an archive (a single archive, or the
        /// first volume of a multi-volume set) — the file the scan/import acts
        /// on. Higher volumes are recognized separately and skipped.</summary>
        public static bool IsExtractableArchive(string fileName)
        {
            string n = fileName.ToLowerInvariant();
            string ext = Path.GetExtension(n);
            if (ext == ".zip" || ext == ".7z") return true;    // single, or spanned-zip tail / old-rar-style handled by parts
            if (ext == ".rar") return !IsNonFirstRarPart(n);   // first .rar / .part1.rar only
            if (n.EndsWith(".001")) return true;               // first numeric split volume
            return false;
        }

        /// <summary>True if the folder directly holds any archive file (a single
        /// archive or any volume part). Used to steer the "Open folder" import
        /// away from archives, which belong in the reliable "Open file" path.</summary>
        public static bool ContainsArchiveFiles(string folderPath)
        {
            try
            {
                foreach (string f in Directory.GetFiles(folderPath))
                {
                    string fn = Path.GetFileName(f);
                    if (IsExtractableArchive(fn) || IsVolumeContinuation(fn)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>A continuation volume that must not be treated as its own
        /// archive (its content is pulled in with the first part).</summary>
        public static bool IsVolumeContinuation(string fileName)
        {
            string n = fileName.ToLowerInvariant();
            if (Regex.IsMatch(n, @"\.r\d{2}$")) return true;                       // old RAR .r00, .r01…
            if (Regex.IsMatch(n, @"\.z\d{2}$")) return true;                       // spanned ZIP .z01, .z02…
            if (Regex.IsMatch(n, @"\.\d{3}$") && !n.EndsWith(".001")) return true; // numeric split .002…
            if (IsNonFirstRarPart(n)) return true;                                 // .part2.rar…
            return false;
        }

        private static bool IsNonFirstRarPart(string lowerName)
        {
            Match m = Regex.Match(lowerName, @"\.part(\d+)\.rar$");
            return m.Success && int.Parse(m.Groups[1].Value) != 1;
        }

        /// <summary>Base book-folder name for an archive, with the volume and
        /// format suffixes stripped (so name.7z.001 → name, name.part1.rar →
        /// name).</summary>
        public static string BaseArchiveName(string archivePath)
        {
            string n = Path.GetFileName(archivePath);
            n = Regex.Replace(n, @"\.part\d+\.rar$", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\.\d{3}$", "");                       // .001 / .002…
            n = Regex.Replace(n, @"\.(zip|rar|7z)$", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\.(r|z)\d{2}$", "", RegexOptions.IgnoreCase);
            return n;
        }

        /// <summary>All files making up an archive, in volume order. Uses
        /// SharpCompress's own part detection; falls back to the single file.</summary>
        public static IReadOnlyList<FileInfo> GetArchiveVolumes(string archivePath)
        {
            FileInfo first = new FileInfo(archivePath);
            try
            {
                List<FileInfo> parts = ArchiveFactory.GetFileParts(first).ToList();
                if (parts.Count > 0) return parts;
            }
            catch { }
            return new List<FileInfo> { first };
        }

        // ── Extraction (single- or multi-volume, optional password) ───────

        private enum ExtractOutcome { Success, NeedPassword }

        /// <summary>Thrown when an archive is encrypted but no password could be
        /// supplied (e.g. the background scan, which can't prompt). Callers
        /// treat it like any other unreadable archive.</summary>
        public class ArchivePasswordRequiredException : Exception { }

        /// <summary>Extracts a zip/rar/7z archive (single- or multi-volume) into
        /// destFolder. If it's encrypted, <paramref name="passwordProvider"/> is
        /// called to obtain a password (return null/empty to cancel) — possibly
        /// again on a wrong password. A null provider throws
        /// ArchivePasswordRequiredException on an encrypted archive; a user
        /// cancel throws OperationCanceledException. Other failures (corrupt,
        /// unsupported split) propagate. The password is only ever held in
        /// memory for the duration of the call — never stored or logged.</summary>
        public static void ExtractArchive(string archivePath, string destFolder,
            Func<string> passwordProvider = null, Action<int, int> progress = null)
        {
            IReadOnlyList<FileInfo> volumes = GetArchiveVolumes(archivePath);

            // Common case: not encrypted — one pass, no password.
            if (TryExtract(volumes, destFolder, null, progress) == ExtractOutcome.Success)
                return;

            if (passwordProvider == null)
                throw new ArchivePasswordRequiredException();

            // Encrypted — prompt until the password works or the user cancels.
            while (true)
            {
                string password = passwordProvider();
                if (string.IsNullOrEmpty(password))
                    throw new OperationCanceledException();
                if (TryExtract(volumes, destFolder, password, progress) == ExtractOutcome.Success)
                    return;
            }
        }

        private static ExtractOutcome TryExtract(IReadOnlyList<FileInfo> volumes, string destFolder,
            string password, Action<int, int> progress = null)
        {
            ReaderOptions readerOptions = new ReaderOptions { Password = password };
            ExtractionOptions extractionOptions = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
            int n = 0;
            try
            {
                if (IsRar(volumes[0]))
                {
                    // RAR (single or multi-volume) must be streamed forward with
                    // the dedicated RarReader: a file spanning a volume boundary
                    // breaks the random-access per-entry path ("unpacked file
                    // size does not match header"), and archive.ExtractAllEntries
                    // refuses a non-solid RAR outright. RarReader reads across
                    // all volumes in order.
                    using (IReader reader = RarReader.OpenReader(volumes, readerOptions))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            if (reader.Entry.IsDirectory) continue;
                            if (IsUnsafeEntryPath(reader.Entry.Key)) continue;
                            n++;
                            reader.WriteEntryToDirectory(destFolder, extractionOptions);
                            // RAR streams forward without a known total up front →
                            // indeterminate progress (total = 0).
                            if (progress != null && (n <= 3 || n % 5 == 0))
                                progress(n, 0);
                        }
                    }
                }
                else
                {
                    // ZIP / 7z: extract through the archive's forward reader
                    // (ExtractAllEntries), NOT per-entry random access. A *solid*
                    // 7z shares one compression stream across all files, so
                    // random-access WriteToDirectory re-decompresses from the
                    // start of the solid block for every entry — O(N²), pegging a
                    // core and taking many minutes on a large audiobook. The
                    // forward reader decompresses the stream once → O(N). (7z has
                    // no standalone stream reader, but ExtractAllEntries on an
                    // already-opened archive reads in solid order and is fine.)
                    using (IArchive archive = volumes.Count == 1
                        ? ArchiveFactory.OpenArchive(volumes[0], readerOptions)
                        : ArchiveFactory.OpenArchive(volumes, readerOptions))
                    {
                        // File count for a determinate progress bar (header-only,
                        // no decompression — cheap even on a big solid archive).
                        int total = 0;
                        try { foreach (IArchiveEntry e in archive.Entries) if (!e.IsDirectory) total++; } catch { total = 0; }
                        int step = total > 100 ? total / 100 : 1;
                        using (IReader reader = archive.ExtractAllEntries())
                        {
                            while (reader.MoveToNextEntry())
                            {
                                if (reader.Entry.IsDirectory) continue;
                                if (IsUnsafeEntryPath(reader.Entry.Key)) continue;
                                n++;
                                reader.WriteEntryToDirectory(destFolder, extractionOptions);
                                if (progress != null && (n <= 3 || n == total || n % step == 0))
                                    progress(n, total);
                            }
                        }
                    }
                }
                return ExtractOutcome.Success;
            }
            catch (Exception ex) when (IsPasswordError(ex))
            {
                // Missing or wrong password — signal, so the caller can prompt.
                // Genuinely broken archives throw other exceptions, which
                // propagate instead of looping forever on the password prompt.
                return ExtractOutcome.NeedPassword;
            }
        }

        /// <summary>True if the archive (given its first volume) is RAR — by
        /// extension, or by sniffing the header for a numeric split (.001) whose
        /// name doesn't reveal the format.</summary>
        private static bool IsRar(FileInfo firstVolume)
        {
            if (firstVolume.Extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                return true;
            try
            {
                ArchiveType? type;
                if (ArchiveFactory.IsArchive(firstVolume.FullName, out type) && type == ArchiveType.Rar)
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>Rejects an archive entry whose name would escape the target
        /// folder — an absolute/rooted path, or one that climbs out with "..".
        /// Guards against a maliciously or carelessly packed archive writing
        /// files anywhere on disk (path traversal / "zip slip").</summary>
        private static bool IsUnsafeEntryPath(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string k = key.Replace('\\', '/');
            // Absolute ("/foo") or drive-rooted ("C:/foo", "C:foo").
            if (k.StartsWith("/")) return true;
            if (k.Length >= 2 && char.IsLetter(k[0]) && k[1] == ':') return true;
            // Any ".." segment that could climb above the destination.
            foreach (string seg in k.Split('/'))
                if (seg == "..") return true;
            return false;
        }

        private static bool IsPasswordError(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Security.Cryptography.CryptographicException) return true;
                string m = e.Message ?? "";
                if (m.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
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

        /// <summary>Picks the final book folder after an archive was extracted
        /// into <paramref name="extractFolder"/> (named from the archive). When
        /// the archive wrapped everything in a single (possibly nested)
        /// subfolder, the book takes that innermost folder's NAME — the folder
        /// closest to the files, e.g. "Author - Title" — rather than the
        /// archive's, and is moved to libraryPath/&lt;that name&gt;. When the
        /// content already sits directly in the extract folder, its archive name
        /// is kept. Returns the resulting book folder. (This mirrors what the
        /// background library scan already does by recursing to the media
        /// folder.)</summary>
        public static string ResolveBookFolder(string extractFolder, string libraryPath)
        {
            string contentDir = extractFolder;
            // Descend through pure wrapper folders (nothing but one subfolder).
            while (true)
            {
                if (Directory.GetFiles(contentDir).Length != 0) break;
                string[] subs = Directory.GetDirectories(contentDir);
                if (subs.Length != 1) break;
                contentDir = subs[0];
            }

            string content = Path.GetFullPath(contentDir).TrimEnd(Path.DirectorySeparatorChar);
            string extract = Path.GetFullPath(extractFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (content.Equals(extract, StringComparison.OrdinalIgnoreCase))
                return extractFolder; // content at the root — keep the archive name

            string target = Path.Combine(libraryPath, Path.GetFileName(content));

            // Don't clobber an existing book of that name (or the extract folder
            // itself, when the wrapper matched the archive name) — fall back to
            // flattening the wrapper into the archive-named folder.
            if (Directory.Exists(target))
            {
                FlattenSingleWrapperFolder(extractFolder);
                return extractFolder;
            }

            Directory.Move(contentDir, target);
            try { Directory.Delete(extractFolder, true); } catch { }
            return target;
        }

        /// <summary>Moves a DAISY book's content (nav + audio) up to the book
        /// root when the archive nested it deeper than a single wrapper (e.g.
        /// "Title/DAISY 2.02 export/…", or a producer id subfolder), so the
        /// rest of the app can treat the book as one flat folder. Given the
        /// parsed content root (the directory holding ncc.html/.ncx).</summary>
        public static void FlattenDaisyToRoot(string root, string contentRoot)
        {
            if (string.IsNullOrEmpty(contentRoot)) return;
            string a = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string b = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return;

            foreach (string entry in Directory.GetFileSystemEntries(contentRoot))
            {
                string target = Path.Combine(root, Path.GetFileName(entry));
                try
                {
                    if (Directory.Exists(entry)) { if (!Directory.Exists(target)) Directory.Move(entry, target); }
                    else { if (!File.Exists(target)) File.Move(entry, target); }
                }
                catch { }
            }
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
