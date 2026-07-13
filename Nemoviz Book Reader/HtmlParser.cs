namespace Nemoviz_Book_Reader
{
    /// <summary>HTML/XHTML (.htm/.html): strip tags to text, capturing h1–h6 as
    /// headings (read-only group — usually structured, falls back to flat).</summary>
    public class HtmlParser : ITextFormatParser
    {
        public bool Handles(string extension)
        {
            return extension == ".htm" || extension == ".html";
        }

        public TextDoc Parse(string filePath)
        {
            try
            {
                var blocks = TextParsing.HtmlBlocks(TtsReader.ReadFile(filePath));
                TextParsing.Assemble(blocks, out string text,
                    out var headings, out _);
                return new TextDoc { Text = text, Headings = headings };
            }
            catch { return new TextDoc(); }
        }
    }
}
