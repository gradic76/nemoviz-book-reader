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
                if (cached != null) return cached;
                if (quiet || !CanOffer(path)) return null;

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
                    if (!MessageForm.ShowConfirm(owner, question, Localization.T("Ocr.Ask.Title")))
                        return null;

                    string language = AppSettings.Current != null ? (AppSettings.Current.OcrLanguage ?? "") : "";
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
