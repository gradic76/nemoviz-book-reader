using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The import path for a document that is pictures of text rather than text:
    /// decide whether it is one, ask, read it, and keep the result.
    ///
    /// <para><b>The user is asked, and that is Gordan's call over mine.</b> I
    /// argued for running it silently in the background — the language choice
    /// barely matters (an English page through the Croatian engine measured 0.0 %
    /// character error), and most machines have exactly one recognizer installed,
    /// so a dialog offering one option is an obstacle rather than a choice. His
    /// argument won on the other half: faced with a single PDF or EPUB <b>the
    /// reader does not know what they have</b>, and recognition is not free —
    /// about half a second a page, so a 300-page book is minutes. Something that
    /// long does not get to start on its own. The language choice still does not
    /// belong here; it lives in Settings and in the book's own properties.</para>
    ///
    /// <para><b>The result is cached beside the book</b> with the language it was
    /// read in, so a re-import never repeats the work, and so a later Windows
    /// language install can be recognised as a reason to offer it again.</para>
    /// </summary>
    /// <summary>What a reading produced: the text, and where each page starts in
    /// it. The offsets travel with the text through <see cref="TextCleaner"/> so
    /// they still point at the right words after the clean.</summary>
    public class OcrText
    {
        public string Text = "";
        public System.Collections.Generic.List<(string Label, int Offset)> Pages =
            new System.Collections.Generic.List<(string, int)>();
        /// <summary>The recognizer that produced the text.
        ///
        /// <para><b>It is a CLAIM about the language, of exactly the kind a file's
        /// <c>dc:language</c> is</b> — weaker than a confident reading of the
        /// words, better than nothing — so it is handed to
        /// <see cref="LanguageDetector.Resolve"/> as the declaration and weighed
        /// there rather than being applied here.</para>
        ///
        /// <para>It exists because of a measured failure. Two nearly identical
        /// Croatian scans came out of import with different languages: one "hr",
        /// the other <b>nothing at all</b>. The detector is not at fault — it was
        /// calibrated on books, which are thousands of words of running prose, and
        /// these are 1200-character forms that are mostly names, numbers, headings
        /// and field labels. Measured on the six: two scored 0.200 confidence and
        /// three scored zero. On text that thin the answer is close to a coin
        /// toss, and the recognizer's own language is the better tiebreak we
        /// already have in hand.</para></summary>
        public string Language = "";
    }

    public static class OcrImport
    {
        /// <summary>Where the recognized text is kept inside the book's folder.</summary>
        public const string CacheName = "ocr.txt";
        /// <summary>Records which recognizer produced <see cref="CacheName"/>.</summary>
        public const string CacheStampName = "ocr.lang";
        /// <summary>Where the pictures came from, so the book can be read again
        /// with a different language.</summary>
        public const string SourceStampName = "ocr.src";

        /// <summary>The pictures themselves, kept inside the book.
        ///
        /// <para><b>Following the precedent braille already set:</b> a .brf is
        /// copied in beside its text so the reading can be redone with a
        /// different table when the automatic one was wrong. This is the same
        /// situation exactly — a scanned book read with the wrong recognizer is
        /// worth nothing, and without the pictures there is nothing to read
        /// again. It costs the size of the scan, and the scan IS the book.</para>
        ///
        /// <para><b>A folder of numbered images is copied too, and the first
        /// version of this was wrong not to.</b> I justified the exception by the
        /// NUMBER of files — "copying a hundred jpegs is a different bargain from
        /// copying one" — which is not a distinction a reader has: what costs them
        /// is bytes, and a folder of page scans is not systematically bigger than
        /// a PDF of the same scans. Gordan put the real objection: a remembered
        /// path breaks the moment the folder is moved or deleted, and then
        /// "Re-read" quietly disappears from the menu with nothing to explain it.
        /// A book that can be re-read only if the user happens not to have tidied
        /// up is not a feature.</para>
        ///
        /// <para>It is also the house rule rather than an exception to it: NBR
        /// copies audio books into the library wholesale and extracts archives
        /// into the book's folder. The scans are this book's material in exactly
        /// that sense.</para>
        ///
        /// <para><b>A SUBFOLDER, and that is not cosmetic.</b> Everything that
        /// inspects a book's own files — <see cref="BookData"/>'s format and
        /// content scans — reads the top level only. Loose page images beside
        /// <c>content.txt</c> would be in the way of all of it; one level down
        /// they are invisible to everything except the re-read.</para>
        ///
        /// <para><b>What it costs:</b> the scans, once. A page image at the size
        /// this reads at runs a few hundred kilobytes, so a long book is tens to a
        /// couple of hundred megabytes — the same order as the source the reader
        /// already has on disk. If that ever needs a ceiling it should apply to
        /// FILES and FOLDERS alike, since that was the flaw in the first rule.</para></summary>
        public const string SourceFolderName = "ocr-source";

        /// <summary>Below this share of pages carrying any text, the document is
        /// not a book — it is pictures. Deliberately low: a scanned book really
        /// does have blank leaves, and one measured here had a dozen in a row.</summary>
        public const double MinPagesWithText = 0.15;

        /// <summary>Whether a file is worth offering OCR for at all, on its name.</summary>
        public static bool CanOffer(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (Directory.Exists(path))
                return Directory.EnumerateFiles(path).Any(OcrPageSource.IsImageFile);
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".pdf" || OcrPageSource.IsImageFile(path);
        }

        /// <summary>A document that produced no usable text — so the caller knows
        /// to offer OCR rather than to report an empty book. The threshold is per
        /// page rather than absolute, because a long PDF with a stray page number
        /// on every page has "text" and still says nothing.</summary>
        public static bool LooksImageOnly(TextDoc doc)
        {
            if (doc == null) return true;
            string text = (doc.Text ?? "").Trim();
            if (text.Length == 0) return true;
            int pages = Math.Max(1, doc.Pages == null ? 1 : doc.Pages.Count);
            return text.Length / pages < 40;
        }

        /// <summary>Offers OCR for a document and runs it if the reader agrees.
        ///
        /// <para>Returns the recognized text, or null when there is nothing to
        /// report — refused, cancelled, or not offered. Everything the reader
        /// needs to hear has already been said by the time this returns.</para></summary>
        /// <param name="quiet">Bulk import: never ask, never explain, just skip.
        /// A hundred books must not become a hundred questions.</param>
        public static OcrText Offer(IWin32Window owner, string path, string bookFolder, bool quiet)
        {
            try
            {
                OcrText cached = ReadCache(bookFolder);
                if (cached != null)
                {
                    // A book read before we started keeping the pictures gets
                    // them now, from the very file being re-imported. Without
                    // this, re-importing such a book — the obvious way to repair
                    // it, and the one Gordan reached for — would hit the cache,
                    // return early and leave it exactly as it was.
                    if (SourceFor(bookFolder) == null) KeepSource(bookFolder, path);
                    return cached;
                }
                if (!CanOffer(path)) return null;

                // A BULK IMPORT STILL CANNOT ASK — a hundred books must not become
                // a hundred questions — but it must not throw the pictures away
                // either. Gordan imported a folder of scanned PDFs and got books
                // that were silently empty: pressing Enter on one started a
                // reading with nothing in it. Keeping the source turns each of
                // them into a book that can be read later, ONE AT A TIME, which is
                // also the right shape for his reason — a folder can hold several
                // languages, and one answer for all of them would be wrong for
                // most.
                if (quiet)
                {
                    KeepSource(bookFolder, path);
                    return null;
                }

                using (OcrPageSource source = OcrPageSource.Open(path))
                {
                    if (source.Refusal != OcrRefusal.None)
                    {
                        Explain(owner, source.Refusal);
                        return null;
                    }

                    string question = Localization.T("Ocr.Ask.Question",
                        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                        source.PageCount, Estimate(source.PageCount));
                    string language = AppSettings.Current != null ? (AppSettings.Current.OcrLanguage ?? "") : "";

                    // ONE recognizer installed → a plain yes/no, because a picker
                    // with one entry is an obstacle dressed as a choice. TWO or
                    // more → the choice belongs here, where the reader has the
                    // book in front of them and knows what language it is in.
                    // (Gordan, once he had installed a second one. My earlier
                    // "it goes in Settings" held only while there was one.)
                    if (WindowsOcr.Languages.Count > 1)
                    {
                        using (var ask = new OcrAskForm(question, language))
                        {
                            if (ask.ShowDialog(owner) != DialogResult.OK) return null;
                            language = ask.Language;
                        }
                    }
                    else if (!MessageForm.ShowConfirm(owner, question, Localization.T("Ocr.Ask.Title")))
                        return null;
                    var result = new OcrText();
                    using (var dlg = new OcrProgressForm(source, language))
                    {
                        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                        result.Text = dlg.Result;
                        result.Pages = dlg.Pages;

                        // Pages rendered and nothing on them. NOT an OCR failure,
                        // and it must not be reported as one — the commonest cause
                        // is a document that is pictures rather than text.
                        if (string.IsNullOrWhiteSpace(result.Text) ||
                            dlg.PagesWithText < source.PageCount * MinPagesWithText)
                        {
                            MessageForm.ShowInfo(owner, Localization.T("Ocr.Result.NoText"),
                                Localization.T("Ocr.Ask.Title"));
                            return null;
                        }
                    }

                    result.Language = WindowsOcr.ResolvedLanguage(language);
                    WriteCache(bookFolder, result.Text, result.Language);
                    KeepSource(bookFolder, path);
                    return result;
                }
            }
            catch { return null; }
        }

        /// <summary>Says why a document cannot be read, in terms of the document
        /// rather than of us.
        ///
        /// <para><b>The JBIG2 case is the one that matters.</b> Such a PDF renders
        /// as bare paper and OCR then finds nothing, which looks exactly like a
        /// hopeless scan — so it must be named for what it is, or the reader will
        /// go looking for a better copy of a file that is perfectly good.</para></summary>
        private static void Explain(IWin32Window owner, OcrRefusal why)
        {
            string key =
                why == OcrRefusal.UndrawablePdf ? "Ocr.Refusal.Undrawable" :
                why == OcrRefusal.NoEngine ? "Ocr.Refusal.NoEngine" :
                "Ocr.Refusal.NoPages";
            MessageForm.ShowInfo(owner, Localization.T(key), Localization.T("Ocr.Ask.Title"));
        }

        /// <summary>How long it will take, in the reader's words. Half a second a
        /// page was measured on this machine; it is a figure to set expectations
        /// with, and the progress dialog replaces it with a real one as soon as
        /// the first page has actually been read.</summary>
        private static string Estimate(int pages)
        {
            int seconds = (int)Math.Round(pages * 0.5);
            if (seconds < 45) return Localization.T("Ocr.Ask.UnderAMinute");
            return Localization.T("Ocr.Ask.Minutes", Math.Max(1, (int)Math.Round(seconds / 60.0)));
        }

        /// <summary>The cached reading, if this book has been read before. Page
        /// markers are rebuilt from the blank line between pages rather than
        /// stored — one file to keep in step instead of two, and the separator is
        /// written by us, not by the document.</summary>
        private static OcrText ReadCache(string bookFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(bookFolder)) return null;
                string p = Path.Combine(bookFolder, CacheName);
                if (!File.Exists(p)) return null;
                string text = File.ReadAllText(p, System.Text.Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return null;

                var result = new OcrText { Text = text };
                try
                {
                    string stamp = Path.Combine(bookFolder, CacheStampName);
                    if (File.Exists(stamp))
                        result.Language = File.ReadAllText(stamp, System.Text.Encoding.UTF8).Trim();
                }
                catch { }
                result.Pages = new System.Collections.Generic.List<(string, int)>();
                int at = 0, page = 1;
                while (at <= text.Length)
                {
                    result.Pages.Add((page.ToString(), at));
                    int next = text.IndexOf("\n\n", at, StringComparison.Ordinal);
                    if (next < 0) break;
                    at = next + 2;
                    page++;
                }
                return result;
            }
            catch { return null; }
        }

        /// <summary>Keeps the pictures with the book, or at least remembers where
        /// they were. See <see cref="SourceFolderName"/> for why.</summary>
        private static void KeepSource(string bookFolder, string path)
        {
            string keep = null;
            try
            {
                if (string.IsNullOrEmpty(bookFolder) || !Directory.Exists(bookFolder)) return;
                // The original location is recorded whatever happens, so a book
                // whose copy could not be made still knows where it came from.
                File.WriteAllText(Path.Combine(bookFolder, SourceStampName), path ?? "",
                    System.Text.Encoding.UTF8);

                keep = Path.Combine(bookFolder, SourceFolderName);
                Directory.CreateDirectory(keep);

                if (Directory.Exists(path))
                {
                    // Only the pages — whatever else the folder happened to hold
                    // is not part of the book.
                    foreach (string f in Directory.EnumerateFiles(path).Where(OcrPageSource.IsImageFile))
                    {
                        string to = Path.Combine(keep, Path.GetFileName(f));
                        if (!File.Exists(to)) File.Copy(f, to);
                    }
                }
                else
                {
                    string dest = Path.Combine(keep, Path.GetFileName(path));
                    if (!File.Exists(dest)) File.Copy(path, dest);
                }
            }
            catch
            {
                // A HALF COPY IS WORSE THAN NONE: re-reading it would produce a
                // book missing whatever pages the disk ran out on, and nothing
                // would say so. Throw the partial copy away and fall back to the
                // remembered path.
                try { if (keep != null && Directory.Exists(keep)) Directory.Delete(keep, true); }
                catch { }
            }
        }

        /// <summary>The pictures this book was read from, or null when they are
        /// not reachable any more. The copy inside the book wins — the original
        /// may have been on a stick that is long gone.</summary>
        public static string SourceFor(string bookFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(bookFolder)) return null;
                string keep = Path.Combine(bookFolder, SourceFolderName);
                if (Directory.Exists(keep))
                {
                    string[] files = Directory.GetFiles(keep);
                    // One file is a document — a PDF, a multi-page TIFF, a single
                    // image — and is handed over as itself. Several are the pages
                    // of a book, and the FOLDER is what reads them, in the natural
                    // name order OcrPageSource applies.
                    if (files.Length == 1) return files[0];
                    if (files.Length > 1) return keep;
                }
                string stamp = Path.Combine(bookFolder, SourceStampName);
                if (!File.Exists(stamp)) return null;
                string original = File.ReadAllText(stamp, System.Text.Encoding.UTF8).Trim();
                if (original.Length == 0) return null;
                return (File.Exists(original) || Directory.Exists(original)) ? original : null;
            }
            catch { return null; }
        }

        /// <summary>Whether this book was made by reading pictures.</summary>
        public static bool WasOcrRead(string bookFolder)
        {
            try
            {
                return !string.IsNullOrEmpty(bookFolder) &&
                       File.Exists(Path.Combine(bookFolder, CacheName));
            }
            catch { return false; }
        }

        /// <summary>Whether "read this again in another language" applies: the
        /// book was read from pictures, and there is another language to read it
        /// in.
        ///
        /// <para><b>It does NOT require the pictures to be reachable, and an
        /// earlier version did.</b> That hid the command on every book imported
        /// before the pictures were being kept — Gordan looked for it on his three
        /// and found nothing, with no way to tell whether the feature was missing
        /// or the books were. It is the same hole a moved folder would leave. The
        /// command is offered whenever the book is the right KIND, and asks where
        /// the pictures are if it cannot find them itself.</para></summary>
        public static bool CanReRead(string bookFolder)
        {
            return WindowsOcr.Languages.Count > 1 && WasOcrRead(bookFolder);
        }

        /// <summary>A book that IS pictures and has never been read — imported in
        /// bulk, where nothing could be asked.
        ///
        /// <para>Unlike <see cref="CanReRead"/> this does not want a second
        /// language: the point is to read the book at all, not to read it
        /// differently. One recognizer is enough for that.</para></summary>
        public static bool NeedsReading(string bookFolder)
        {
            return !WasOcrRead(bookFolder) && SourceFor(bookFolder) != null;
        }

        /// <summary>A text book with no text in it.
        ///
        /// <para><b>The books Gordan already has.</b> Before the bulk import kept
        /// the pictures, a folder of scanned PDFs produced exactly this: a book
        /// whose <c>content.txt</c> is empty and whose source is gone. Nothing
        /// marks them, so <see cref="NeedsReading"/> cannot see them — and opening
        /// one plays silence, which is indistinguishable from a book that has
        /// simply gone quiet. That is the worst kind of fault for someone who
        /// cannot look at the screen, so it is worth catching by the symptom even
        /// though the cause is no longer being created.</para>
        ///
        /// <para>The threshold is a few hundred characters rather than zero: a
        /// scanned book that yielded one stray page number is just as unreadable
        /// as one that yielded nothing.</para></summary>
        public static bool IsEmptyTextBook(string bookFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(bookFolder)) return false;
                string p = Path.Combine(bookFolder, "content.txt");
                if (!File.Exists(p)) return false;
                if (new FileInfo(p).Length > 4096) return false;   // cheap: no read
                return File.ReadAllText(p, System.Text.Encoding.UTF8).Trim().Length < 200;
            }
            catch { return false; }
        }

        /// <summary>A book whose pages are almost all EMPTY — a scan whose text
        /// layer is broken, as against one that has none at all.
        ///
        /// <para><b>Why the absolute threshold above cannot do this</b>
        /// (measured 2026-08-15). A mass-digitized PDF usually carries an
        /// invisible OCR text layer, and when that layer is broken it still
        /// yields a few thousand characters of fragments — comfortably past
        /// <see cref="IsEmptyTextBook"/>'s 200. Measured on a Google-scanned
        /// book: <b>2 970 characters over 227 pages</b>. So it imported as a
        /// real text book, OCR was never offered, and the reading stopped after
        /// twenty seconds with nothing said — the silent failure this whole
        /// area exists to prevent.</para>
        ///
        /// <para><b>The test is the SHARE OF PAGES that carry any text, not the
        /// average, and Gordan is why.</b> He objected to a chars-per-page
        /// threshold with the case that breaks it: a photo monograph whose
        /// every caption is a name, a date and a place is genuinely sparse and
        /// genuinely fine. An average cannot tell it from a broken layer.
        /// A share can — the monograph has its caption on MOST pages, a broken
        /// layer has nothing on nearly all of them.</para>
        ///
        /// <para><b>Measured, and the separation is not a fine judgement:</b>
        /// broken or absent layer 0,0–0,4 % of pages (a Google scan and all
        /// seven of Gordan's own image-only PDFs), real layer 83–98 %
        /// (archive.org's own scans). The per-page bar barely matters either —
        /// at 1, 20 and 100 characters the shares come out 89,5 / 85,5 / 83,2
        /// on the same book, because a page either has its text or it has
        /// none.</para>
        ///
        /// <para>It <b>offers</b>, it does not decide: the caller asks. A book
        /// whose layer covers only its first thirty pages would land near the
        /// threshold, and the reader knows what their book is where no rule
        /// does.</para></summary>
        public static bool IsSparseTextBook(BookData book, out int withText, out int pages)
        {
            withText = 0;
            pages = 0;
            try
            {
                if (book == null || !book.IsTextBook) return false;
                var marks = book.TextPages;
                // Too few pages to read a distribution off. A short document is
                // the absolute test's business, not this one's.
                if (marks == null || marks.Count < 5) return false;

                string p = Path.Combine(book.FolderPath, "content.txt");
                if (!File.Exists(p)) return false;

                // Cheap pre-filter, no read at all: a book carrying real text on
                // its pages is not in question, and slicing a big one costs.
                // It may only ever say "fine" — it must never say "sparse", or
                // it would be the average test again under another name.
                long bytes = new FileInfo(p).Length;
                if (bytes / Math.Max(1, marks.Count) > BytesPerPageNotInQuestion) return false;

                string text = File.ReadAllText(p, System.Text.Encoding.UTF8);
                pages = marks.Count;
                for (int i = 0; i < marks.Count; i++)
                {
                    int start = Clamp(marks[i].Offset, 0, text.Length);
                    int end = Clamp(i + 1 < marks.Count ? marks[i + 1].Offset : text.Length,
                                    start, text.Length);
                    if (HasWords(text, start, end)) withText++;
                }
                return withText * 100 < pages * SparsePagesPercent;
            }
            catch { return false; }
        }

        /// <summary>A page counts as carrying text if it has a single letter or
        /// digit. Deliberately the lowest bar there is — see the measurement in
        /// <see cref="IsSparseTextBook"/>: raising it changes almost nothing,
        /// and a monograph's caption must count.</summary>
        private static bool HasWords(string s, int start, int end)
        {
            for (int i = start; i < end; i++)
                if (char.IsLetterOrDigit(s[i])) return true;
            return false;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>Below this many bytes per page the share is worth computing.
        /// Generous on purpose: it only skips work, never reaches a verdict.</summary>
        private const int BytesPerPageNotInQuestion = 400;

        /// <summary>Fewer than this share of pages carrying text and the book is
        /// worth asking about. 0,4 % below it and 83 % above it, measured.</summary>
        private const int SparsePagesPercent = 20;

        /// <summary>Reads a book's pictures again, in a language the reader
        /// picks. Returns the new text, or null if they backed out.
        ///
        /// <para><b>Not a per-book setting, and deliberately not in Properties</b>
        /// (Gordan, 2026-08-11). Properties holds schemes that are tuned and
        /// remembered — the sound chain, the voice. A reading is not that: it
        /// happens once, from the first page to the last, and either it was right
        /// or it is done again. So it is an ACTION on the shelf, where the book
        /// is, and it takes up no room in a dialog that has real settings in
        /// it.</para></summary>
        public static OcrText ReRead(IWin32Window owner, string bookFolder)
        {
            try
            {
                string path = SourceFor(bookFolder) ?? Locate(owner);
                if (path == null) return null;

                using (OcrPageSource source = OcrPageSource.Open(path))
                {
                    if (source.Refusal != OcrRefusal.None) { Explain(owner, source.Refusal); return null; }

                    string was = "";
                    try
                    {
                        string stamp = Path.Combine(bookFolder, CacheStampName);
                        if (File.Exists(stamp)) was = File.ReadAllText(stamp, System.Text.Encoding.UTF8).Trim();
                    }
                    catch { }

                    // "Re-read" would be a lie to someone who has never heard a word
                    // of this book — a bulk import leaves it unread by design.
                    string question = Localization.T(
                        WasOcrRead(bookFolder) ? "Ocr.ReRead.Question" : "Ocr.Read.Question",
                        source.PageCount, Estimate(source.PageCount));
                    string language;
                    using (var ask = new OcrAskForm(question, was))
                    {
                        if (ask.ShowDialog(owner) != DialogResult.OK) return null;
                        language = ask.Language;
                    }

                    var result = new OcrText();
                    using (var dlg = new OcrProgressForm(source, language))
                    {
                        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                        result.Text = dlg.Result;
                        result.Pages = dlg.Pages;
                        if (string.IsNullOrWhiteSpace(result.Text))
                        {
                            MessageForm.ShowInfo(owner, Localization.T("Ocr.Result.NoText"),
                                Localization.T("Ocr.Ask.Title"));
                            return null;
                        }
                    }
                    result.Language = WindowsOcr.ResolvedLanguage(language);
                    WriteCache(bookFolder, result.Text, result.Language);
                    // Whatever it took to find them this time, keep them now — so
                    // it is asked once and never again.
                    if (SourceFor(bookFolder) == null) KeepSource(bookFolder, path);
                    return result;
                }
            }
            catch { return null; }
        }

        /// <summary>Asks the reader where the pictures are, for a book whose copy
        /// was never made or whose original has moved.
        ///
        /// <para>A file, because that is what one picks: a PDF, a multi-page TIFF,
        /// or — for a book that arrived as loose pages — any ONE of them, after
        /// which the rest of the folder is offered. Guessing the folder outright
        /// would be wrong as often as right; asking about it is one question with
        /// a number in it.</para></summary>
        private static string Locate(IWin32Window owner)
        {
            MessageForm.ShowInfo(owner, Localization.T("Ocr.ReRead.Locate"),
                Localization.T("Ocr.Ask.Title"));
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Localization.T("Ocr.ReRead.LocateTitle");
                dlg.Filter = Localization.T("Ocr.ReRead.Filter");
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;

                string picked = dlg.FileName;
                if (!OcrPageSource.IsImageFile(picked)) return picked;   // a document

                // Loose pages: the folder is the book, not the one page picked.
                string folder = Path.GetDirectoryName(picked);
                int images = 0;
                try { images = Directory.EnumerateFiles(folder).Count(OcrPageSource.IsImageFile); }
                catch { }
                if (images > 1 && MessageForm.ShowConfirm(owner,
                        Localization.T("Ocr.ReRead.WholeFolder", images),
                        Localization.T("Ocr.Ask.Title")))
                    return folder;
                return picked;
            }
        }

        private static void WriteCache(string bookFolder, string text, string language)
        {
            try
            {
                if (string.IsNullOrEmpty(bookFolder) || !Directory.Exists(bookFolder)) return;
                File.WriteAllText(Path.Combine(bookFolder, CacheName), text ?? "", System.Text.Encoding.UTF8);
                File.WriteAllText(Path.Combine(bookFolder, CacheStampName), language ?? "", System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
