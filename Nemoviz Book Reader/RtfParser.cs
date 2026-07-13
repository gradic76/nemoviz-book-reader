using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Rich Text Format (.rtf): let a WinForms RichTextBox parse the RTF
    /// and hand back the plain text — zero dependency. Read byte-preserving
    /// (1252) so any raw ANSI bytes survive to the RTF parser's own \ansicpg.</summary>
    public class RtfParser : ITextFormatParser
    {
        public bool Handles(string extension) { return extension == ".rtf"; }

        public TextDoc Parse(string filePath)
        {
            try
            {
                try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
                string rtf = File.ReadAllText(filePath, Encoding.GetEncoding(1252));
                using (RichTextBox rtb = new RichTextBox())
                {
                    rtb.Rtf = rtf;
                    return new TextDoc { Text = rtb.Text };
                }
            }
            catch { return new TextDoc(); }
        }
    }
}
