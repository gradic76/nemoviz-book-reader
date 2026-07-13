using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nemoviz_Book_Reader
{
    /// <summary>The result of extracting a document: the reading text, its
    /// heading structure (level, title, character offset), title/author
    /// metadata, and a DRM flag (content encrypted → can't read).</summary>
    public class TextDoc
    {
        public string Text = "";
        public List<(int Level, string Title, int Offset)> Headings = new List<(int, string, int)>();
        public string Title = "";
        public string Author = "";
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
        };

        /// <summary>True if the extension is one a parser handles (by extension
        /// alone; not the zip-wrapped-epub case — see <see cref="IsTextImport"/>).</summary>
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

        /// <summary>Extracts a document (or a zip-wrapped epub) to text. Never
        /// null; an unreadable/unsupported file yields an empty TextDoc.</summary>
        public static TextDoc Extract(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                foreach (ITextFormatParser p in Parsers)
                    if (p.Handles(ext)) return p.Parse(filePath) ?? new TextDoc();
                if (ext == ".zip" && EpubParser.WrapsEpub(filePath))
                    return new EpubParser().Parse(filePath) ?? new TextDoc();
                return new TextDoc();
            }
            catch { return new TextDoc(); }
        }
    }
}
