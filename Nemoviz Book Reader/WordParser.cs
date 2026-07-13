using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>Zipped office documents: Word (.docx → word/document.xml) and
    /// OpenDocument text (.odt → content.xml). Flattened to text (editable group,
    /// no reliable structure).</summary>
    public class WordParser : ITextFormatParser
    {
        public bool Handles(string extension)
        {
            return extension == ".docx" || extension == ".odt";
        }

        public TextDoc Parse(string filePath)
        {
            try
            {
                string part = Path.GetExtension(filePath).ToLowerInvariant() == ".docx"
                    ? "word/document.xml" : "content.xml";
                return new TextDoc { Text = TextParsing.ZipXmlText(filePath, part) };
            }
            catch { return new TextDoc(); }
        }
    }
}
