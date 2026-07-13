namespace Nemoviz_Book_Reader
{
    /// <summary>Plain text (.txt): read with encoding detection, no structure.</summary>
    public class PlainTextParser : ITextFormatParser
    {
        public bool Handles(string extension) { return extension == ".txt"; }

        public TextDoc Parse(string filePath)
        {
            try { return new TextDoc { Text = TtsReader.ReadFile(filePath) ?? "" }; }
            catch { return new TextDoc(); }
        }
    }
}
