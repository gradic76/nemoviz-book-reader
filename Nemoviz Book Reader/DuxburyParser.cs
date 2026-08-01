using System;
using System.Collections.Generic;
using System.IO;

namespace Nemoviz_Book_Reader
{
    /// <summary>Duxbury (<c>.dxb</c>) — a binary envelope around ordinary
    /// braille ASCII.
    ///
    /// <para><b>What it turned out to be.</b> The container looked forbidding: a
    /// <c>FF D S I</c> signature, a binary block, and the names of the Duxbury
    /// translation tables the file was made with (<c>ENBRCP</c>, <c>ENBDXP</c>,
    /// <c>ENBRCB</c>, <c>ENGDXB</c>). Past that, 97.6% of the file is printable —
    /// and it is contracted English braille ASCII, exactly what a <c>.brf</c>
    /// holds. <c>f/ publi%$ #bjjb 0! o'bri5 press ltd1</c> is "first published
    /// 2002 by The O'Brien Press Ltd,".</para>
    ///
    /// <para>So this strips the envelope and hands the braille to
    /// <see cref="BrfParser"/> rather than translating any of it itself. Cell
    /// mapping, page splitting, table detection and box frames all live there,
    /// and a second copy of them is how two parsers come to disagree about the
    /// same book.</para>
    ///
    /// <para><b>The markup.</b> Runs of the form <c>0x1C name 0x1F</c> carry
    /// styles — <c>es~para.</c> opens a paragraph, <c>ee~para.</c> closes one —
    /// and they sit inline among the cells. They are removed, with the paragraph
    /// openers left behind as line breaks so the shape of the book survives; a
    /// tag left in place would be translated as though it were braille and come
    /// out as words.</para>
    ///
    /// <para>Measured on ten sample files; no specification was available.</para></summary>
    public class DuxburyParser : ITextFormatParser
    {
        private const byte TagStart = 0x1C, TagEnd = 0x1F;

        public bool Handles(string extension) { return extension == ".dxb"; }

        public static bool IsDuxbury(byte[] b)
        {
            return b != null && b.Length > 4
                && b[0] == 0xFF && b[1] == (byte)'D' && b[2] == (byte)'S' && b[3] == (byte)'I';
        }

        public TextDoc Parse(string filePath) { return Parse(filePath, null); }

        public TextDoc Parse(string filePath, string tableId)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(filePath);
                if (!IsDuxbury(raw)) return null;

                byte[] cells = Unwrap(raw);
                if (cells == null || cells.Length < 64) return null;

                // Contracted English unless the book says otherwise: the table
                // names in the header are Duxbury's own and do not map onto
                // liblouis, so the shared detector gets the final say.
                return new BrfParser().ParseBytes(cells, tableId);
            }
            catch { return null; }
        }

        /// <summary>Drops the header and the markup, keeping the cells.
        ///
        /// <para>The payload is found by looking for where the file settles into
        /// print rather than by a fixed offset — the header carries a variable
        /// number of table names, so its length is not the same twice.</para></summary>
        private static byte[] Unwrap(byte[] raw)
        {
            int start = FindPayload(raw);
            if (start < 0) return null;

            var outBuf = new List<byte>(raw.Length - start);
            bool inTag = false;
            var tag = new List<byte>();
            for (int i = start; i < raw.Length; i++)
            {
                byte b = raw[i];
                if (b == TagStart) { inTag = true; tag.Clear(); continue; }
                if (inTag)
                {
                    if (b != TagEnd) { if (tag.Count < 32) tag.Add(b); continue; }
                    inTag = false;
                    // A paragraph opener becomes the break it stands for; every
                    // other style is presentation this reader has no use for.
                    string name = System.Text.Encoding.ASCII.GetString(tag.ToArray());
                    if (name.StartsWith("es~", StringComparison.Ordinal) &&
                        name.IndexOf("para", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        outBuf.Add(13); outBuf.Add(10);
                    }
                    // Every OTHER tag leaves a space behind. A tag separates two
                    // records, and removing it without trace welded them into one
                    // word: "decemberthe", "plantierbymichael", "volumesvolume".
                    // The braille either side is complete; only the join was lost.
                    else if (outBuf.Count > 0 && outBuf[outBuf.Count - 1] != (byte)' '
                             && outBuf[outBuf.Count - 1] != 10)
                    {
                        outBuf.Add((byte)' ');
                    }
                    continue;
                }
                if (b == 0) continue;                   // padding between records
                outBuf.Add(b);
            }
            return outBuf.ToArray();
        }

        /// <summary>The first place the file runs to 200 bytes that are 95%
        /// printable — where the header stops and the braille starts.</summary>
        private static int FindPayload(byte[] b)
        {
            const int Window = 200;
            for (int i = 0; i + Window < b.Length && i < 65536; i++)
            {
                int ok = 0;
                for (int j = 0; j < Window; j++)
                {
                    byte x = b[i + j];
                    if ((x >= 32 && x <= 126) || x == 10 || x == 13 || x == 12
                        || x == TagStart || x == TagEnd) ok++;
                }
                if (ok * 100 / Window >= 95) return i;
            }
            return -1;
        }
    }
}
