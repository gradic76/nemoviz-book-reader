using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Nemoviz_Book_Reader
{
    /// <summary>FictionBook 2 (.fb2, or a zipped .fb2). Nested &lt;section&gt;
    /// give heading depth, &lt;title&gt; the heading text, &lt;p&gt; the
    /// paragraphs; metadata from &lt;title-info&gt;.</summary>
    public class Fb2Parser : ITextFormatParser
    {
        public bool Handles(string extension) { return extension == ".fb2"; }

        public TextDoc Parse(string filePath)
        {
            try
            {
                string xml = ReadFb2(filePath);
                if (string.IsNullOrEmpty(xml)) return new TextDoc();

                List<TextParsing.Block> blocks = new List<TextParsing.Block>();
                string title = "", first = "", last = "", lang = "";
                StringBuilder cur = new StringBuilder();
                StringBuilder headBuf = new StringBuilder(), metaBuf = new StringBuilder();
                int depth = 0;
                bool inTitle = false, inBody = false, inDesc = false;
                string metaField = null;

                System.Action flushPara = () =>
                {
                    if (cur.ToString().Trim().Length > 0)
                        blocks.Add(new TextParsing.Block { IsHeading = false, Level = 0, Text = cur.ToString() });
                    cur.Clear();
                };

                XmlReaderSettings settings = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
                using (XmlReader reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    while (reader.Read())
                    {
                        string ln = reader.LocalName;
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (ln == "body") inBody = true;
                            else if (ln == "title-info") inDesc = true;
                            else if (inBody)
                            {
                                if (ln == "section") { flushPara(); depth++; }
                                else if (ln == "title") { flushPara(); inTitle = true; headBuf.Clear(); }
                                else if (ln == "p" && !inTitle) flushPara();
                                else if (ln == "empty-line") flushPara();
                            }
                            else if (inDesc && (ln == "book-title" || ln == "first-name"
                                             || ln == "last-name" || ln == "lang"))
                            { metaField = ln; metaBuf.Clear(); }
                        }
                        else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                        {
                            if (inTitle) headBuf.Append(reader.Value);
                            else if (metaField != null) metaBuf.Append(reader.Value);
                            else if (inBody) cur.Append(reader.Value);
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            if (ln == "body") inBody = false;
                            else if (ln == "title-info") inDesc = false;
                            else if (inBody)
                            {
                                if (ln == "section") { flushPara(); if (depth > 0) depth--; }
                                else if (ln == "title")
                                {
                                    string ht = headBuf.ToString().Trim();
                                    if (ht.Length > 0)
                                        blocks.Add(new TextParsing.Block { IsHeading = true, Level = depth > 0 ? depth : 1, Text = ht });
                                    inTitle = false;
                                }
                                else if (ln == "p" && !inTitle) cur.Append('\n');
                            }
                            else if (metaField != null)
                            {
                                string val = metaBuf.ToString().Trim();
                                if (ln == "book-title") title = val;
                                else if (ln == "first-name") first = val;
                                else if (ln == "last-name") last = val;
                                else if (ln == "lang") lang = val;
                                metaField = null;
                            }
                        }
                    }
                }
                flushPara();

                TextParsing.Assemble(blocks, out string text, out var headings, out _);
                return new TextDoc { Text = text, Headings = headings, Title = title,
                                     Author = (first + " " + last).Trim(), Language = lang };
            }
            catch { return new TextDoc(); }
        }

        private static string ReadFb2(string path)
        {
            if (IsZip(path))
            {
                using (ZipArchive zip = ZipFile.OpenRead(path))
                {
                    ZipArchiveEntry e = zip.Entries.FirstOrDefault(x => x.Name.ToLowerInvariant().EndsWith(".fb2"));
                    if (e == null) return null;
                    using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8, true)) return r.ReadToEnd();
                }
            }
            return TtsReader.ReadFile(path);
        }

        private static bool IsZip(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                    return fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B;
            }
            catch { return false; }
        }
    }
}
