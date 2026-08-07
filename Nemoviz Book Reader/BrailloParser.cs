using System;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Braillo Text — an embosser page-layout file that contains
    /// ORDINARY TEXT, not braille cells.
    ///
    /// <para><b>Why it needs its own parser.</b> It uses the <c>.brl</c>
    /// extension, which is also used for plain braille ASCII, so it went to
    /// <see cref="BrfParser"/> and was read as though every byte were a cell.
    /// Measured, 90% of its bytes are not cells at all; the parser's habit of
    /// skipping what it does not recognise turned the surviving tenth into
    /// fluent-looking nonsense, which a reader cannot tell from a badly
    /// transcribed book. BrfParser refuses it now, and this reads it properly —
    /// the five sample books are Ukrainian, among them John M. Hull's account of
    /// going blind.</para>
    ///
    /// <para><b>The format, as measured on those five.</b> A <c>Braillo Text</c>
    /// header line, a title, a binary block, and then the body as
    /// <b>16-bit units</b>: the low byte is the character, the high byte an
    /// attribute (0x03 throughout the running text, with 0x0B, 0x02 and 0x33
    /// appearing in headings and rules). Lines are ended by an ordinary CR/LF in
    /// the character stream, and each page is drawn inside a frame of ASCII
    /// characters — a rule of <c>c</c>, rails of <c>l</c> and <c>|</c> — set 36
    /// columns apart.</para>
    ///
    /// <para>No specification was available, so every statement above is a
    /// measurement of the files themselves. Where a guess was unavoidable — the
    /// code page — it is decided per file by scoring rather than assumed.</para></summary>
    public class BrailloParser : ITextFormatParser
    {
        public bool Handles(string extension)
        {
            // Same extension as braille ASCII; the header is what tells them
            // apart, so both parsers claim it and the content decides.
            return extension == ".brl";
        }

        private static readonly byte[] Header = Encoding.ASCII.GetBytes("Braillo Text");

        public static bool IsBraillo(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Header.Length) return false;
            for (int i = 0; i < Header.Length; i++)
                if (bytes[i] != Header[i]) return false;
            return true;
        }

        public TextDoc Parse(string filePath)
        {
            var doc = new TextDoc();
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (!IsBraillo(bytes)) return null;

                int start = FindBody(bytes);
                if (start < 0) return null;

                // Every second byte is the character; the rest is attribute.
                int n = (bytes.Length - start) / 2;
                var raw = new byte[n];
                for (int i = 0; i < n; i++) raw[i] = bytes[start + i * 2];

                string text = Decode(raw);
                doc.Text = Clean(text);
                doc.Title = FirstRealLine(doc.Text);
                return doc.Text.Length == 0 ? null : doc;
            }
            catch { return null; }
        }

        /// <summary>Where the 16-bit body starts: the first place three units in
        /// a row carry the running-text attribute. Anchoring on the attribute
        /// rather than a fixed offset is what makes this survive the header and
        /// title being different lengths in different files.</summary>
        private static int FindBody(byte[] b)
        {
            for (int i = 1; i + 5 < b.Length && i < 8192; i++)
                if (b[i] == 0x03 && b[i + 2] == 0x03 && b[i + 4] == 0x03) return i - 1;
            return -1;
        }

        /// <summary>Picks the code page by trying them and counting letters.
        ///
        /// <para>The five samples are Cyrillic and decode under Windows-1251, but
        /// a Braillo installation elsewhere would use its own — and a file that
        /// says nothing about its encoding should not be assumed to share the one
        /// the first samples happened to have. Scoring costs nothing and is
        /// right more often than a default.</para></summary>
        private static string Decode(byte[] raw)
        {
            int[] pages = { 1251, 1250, 1252 };
            string best = null;
            int bestScore = -1;
            foreach (int cp in pages)
            {
                string s;
                try { s = Encoding.GetEncoding(cp).GetString(raw); }
                catch { continue; }
                int score = 0;
                foreach (char c in s) if (char.IsLetter(c)) score++;
                if (score > bestScore) { bestScore = score; best = s; }
            }
            return best ?? Encoding.ASCII.GetString(raw);
        }

        /// <summary>Drops the page frame and keeps the words.
        ///
        /// <para>The frame is drawn with ordinary letters, so it cannot be
        /// removed character by character without eating text. A LINE, though,
        /// gives it away: a rule is nothing but frame characters, and a framed
        /// line carries a rail at each end with the text between.</para></summary>
        private static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) { sb.Append('\n'); continue; }
                if (IsRule(line)) continue;
                line = StripRails(line);
                if (line.Length == 0) { sb.Append('\n'); continue; }
                sb.Append(line).Append('\n');
            }
            return sb.ToString().Trim();
        }

        /// <summary>A horizontal rule: one character repeated, with the corner
        /// pieces at the ends. Requiring a real run stops it eating a line of
        /// genuine text that happens to be short.</summary>
        private static bool IsRule(string line)
        {
            if (line.Length < 8) return false;
            int runs = 0;
            char prev = '\0';
            foreach (char c in line)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == prev) runs++;
                prev = c;
            }
            return runs * 100 / line.Length > 70;
        }

        private static string StripRails(string line)
        {
            const string Rails = "|l";
            if (line.Length > 0 && Rails.IndexOf(line[0]) >= 0) line = line.Substring(1);
            if (line.Length > 0 && Rails.IndexOf(line[line.Length - 1]) >= 0)
                line = line.Substring(0, line.Length - 1);
            return line.Trim();
        }

        private static string FirstRealLine(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length >= 3) return t.Length > 120 ? t.Substring(0, 120) : t;
            }
            return "";
        }
    }
}
