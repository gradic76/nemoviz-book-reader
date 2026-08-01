using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nemoviz_Book_Reader
{
    /// <summary>The result of extracting a document: the reading text, its
    /// heading structure (level, title, character offset), title/author
    /// metadata, and a DRM flag (content encrypted â†’ can't read).</summary>
    public class TextDoc
    {
        public string Text = "";
        public List<(int Level, string Title, int Offset)> Headings = new List<(int, string, int)>();
        // Print-page markers (EPUB page-list / NCX pageList) â†’ character offsets.
        public List<(string Label, int Offset)> Pages = new List<(string, int)>();
        public string Title = "";
        public string Author = "";
        public string Producer = "";   // accessible-edition producer (empty for EPUB)
        // Braille only: id of the table the text was back-translated with, so the
        // book remembers it and the user can correct the choice later.
        public string BrailleTable = "";
        public string Publisher = "";  // dc:publisher (EPUB print/edition publisher)
        /// <summary>Element id â†’ character offset, for formats whose AUDIO is
        /// aligned to named points in the text: a DAISY SMIL par names a DTBook
        /// id, an EPUB media overlay names an XHTML id. Empty for everything
        /// else. <see cref="DaisySync.Build"/> is what turns it into a sync map.
        ///
        /// <para>Carried on the document rather than derived later because these
        /// offsets are in RAW-text coordinates and have to be moved by
        /// <see cref="TextCleaner.CleanDoc"/> along with the headings and pages â€”
        /// re-deriving them after the clean would put every one of them slightly
        /// too far into the book, which is the drift Â§8e already paid for
        /// once.</para></summary>
        public Dictionary<string, int> SyncIds = new Dictionary<string, int>();
        /// <summary>The language the FILE claims (dc:language, FB2 &lt;lang&gt;,
        /// MOBI EXTH 524, DAISY dc:language). Only a claim â€” producers get it
        /// wrong often enough that <see cref="LanguageDetector.Resolve"/> lets a
        /// confident reading of the actual text overrule it.</summary>
        public string Language = "";
        public bool DrmProtected = false;
    }

    /// <summary>Dispatches a document to the right format subsystem
    /// (<see cref="ITextFormatParser"/>). Each format is its own self-contained
    /// parser; adding a new one is just adding it to <see cref="Parsers"/>.</summary>
    public static class TextExtractor
    {
        private static readonly ITextFormatParser[] Parsers =
        {
            new PlainTextParser(),
            new RtfParser(),
            new WordParser(),
            new HtmlParser(),
            new Fb2Parser(),
            new EpubParser(),
            new PdfParser(),
            new MobiParser(),
            new DocParser(),
            new BrailloParser(),
            new BrfParser(),
        };

        /// <summary>True if the extension is one a parser handles (by extension
        /// alone; not the zip-wrapped-epub case â€” see <see cref="IsTextImport"/>).</summary>
        public static bool IsTextFormat(string extension)
        {
            string ext = (extension ?? "").ToLowerInvariant();
            return Parsers.Any(p => p.Handles(ext));
        }

        /// <summary>Whether importing this file should go through text extraction:
        /// a known text format, or a .zip that ultimately wraps an epub (as most
        /// libraries package them).</summary>
        public static bool IsTextImport(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (IsTextFormat(ext)) return true;
            if (ext == ".zip") return EpubParser.WrapsEpub(filePath);
            return false;
        }

        /// <summary>
        /// A produced format (epub/fb2/html/docxâ€¦) is only treated as STRUCTURED
        /// when it yields at least this many headings. Below it, the whole book
        /// is read as FLAT text (a handful of stray &lt;hN&gt; across an entire
        /// book isn't navigable structure â€” it just produces a near-useless Go To
        /// / Heading seek). This is the single global rule for every parser; the
        /// format LABEL is untouched (the user still sees "EPUB"/"DOCX"), only
        /// navigation goes flat. Tunable in one place.
        /// </summary>
        public const int MinStructureHeadings = 5;

        /// <summary>Extracts a document (or a zip-wrapped epub) to text. Never
        /// null; an unreadable/unsupported file yields an empty TextDoc.</summary>
        public static TextDoc Extract(string filePath)
        {
            try
            {
                TextDoc doc = null;
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                foreach (ITextFormatParser p in Parsers)
                    if (p.Handles(ext)) { doc = p.Parse(filePath); break; }
                if (doc == null && ext == ".zip" && EpubParser.WrapsEpub(filePath))
                    doc = new EpubParser().Parse(filePath);
                doc = doc ?? new TextDoc();

                // Global structure-vs-flat rule (see MinStructureHeadings).
                if (doc.Headings != null && doc.Headings.Count < MinStructureHeadings)
                    doc.Headings.Clear();

                return doc;
            }
            catch { return new TextDoc(); }
        }
    }
}

