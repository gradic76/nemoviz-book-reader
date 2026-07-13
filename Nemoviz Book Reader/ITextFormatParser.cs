namespace Nemoviz_Book_Reader
{
    /// <summary>One document-format parser (txt, rtf, docx, epub, …). Each is a
    /// self-contained subsystem; <see cref="TextExtractor"/> dispatches to the
    /// one that handles a given file. A parser never throws — it returns an
    /// (possibly empty) TextDoc.</summary>
    public interface ITextFormatParser
    {
        /// <summary>True if this parser handles the given lower-case extension
        /// (with the leading dot, e.g. ".epub").</summary>
        bool Handles(string extension);

        /// <summary>Extracts the document to reading text (+ structure/metadata).</summary>
        TextDoc Parse(string filePath);
    }
}
