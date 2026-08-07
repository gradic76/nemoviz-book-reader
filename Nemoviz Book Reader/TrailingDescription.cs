using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>Finds the publisher's blurb where it is part of the TEXT rather
    /// than the metadata — a heading-shaped line near the very end, and the
    /// paragraphs after it.
    ///
    /// <para><b>What it is measured on, and what that does NOT mean.</b> 191
    /// DOCX books in <c>D:\Test naslovi\Test docx</c>: <b>167 carry it, 87 %</b>,
    /// median 929 characters — within a whisker of the 935 measured across 596
    /// EPUBs' <c>dc:description</c>, which is good evidence the two are the same
    /// kind of text. The 24 that miss have no blurb AT ALL — they end in
    /// acknowledgements, an epilogue, a translator's footnotes — so 87 % is the
    /// ceiling of the data, not of the rule.</para>
    ///
    /// <para><b>But that 87 % belongs to ONE PRODUCER (Gordan, 2026-08-07).</b>
    /// Those books come from a single source that collects from everywhere and
    /// converts the lot to DOCX and flat text weekly, with its own tool — the
    /// same tool several of NBR's other import rules were derived from. Books
    /// from there will behave this way in about 99 % of cases. A DOCX from
    /// another user, another country, another continent very likely will not.
    /// This is the DAISY story again (§8c): learn the producer you can measure,
    /// and structure it so the next producer is an ADDITION rather than a
    /// rewrite.</para>
    ///
    /// <para>Hence: the markers are a LIST, and finding none is the ordinary case
    /// rather than a failure. A book without one simply has no description, which
    /// is what it had before this existed.</para></summary>
    public static class TrailingDescription
    {
        /// <summary>The line that announces it. One per producer convention, and
        /// meant to grow — everything here is what the Croatian/Serbian source
        /// writes, plus the obvious English equivalents for when a book from
        /// somewhere else happens to follow the same habit. Whole-line matches
        /// only: a chapter that merely mentions "opis" is not a marker.</summary>
        private static readonly Regex Marker = new Regex(
            @"^\s*(opis\s+knjige|o\s+knjizi|kratak\s+opis|opis|sinopsis|
               about\s+the\s+book|book\s+description|description|synopsis|blurb)\s*[:.]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        /// <summary>Lines that END the description because something else starts:
        /// the author's biography is the common one, and it follows the blurb as
        /// often as it precedes it.</summary>
        private static readonly Regex NextSection = new Regex(
            @"^\s*(o\s+autoru|o\s+autorici|o\s+piscu|o\s+spisateljici|o\s+autorima|
               o\s+prevoditelju|about\s+the\s+author|about\s+the\s+translator)\s*[:.]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        /// <summary>A marker only counts this near the end. Measured: the earliest
        /// of the 167 sat at 97.5 % through its book and the median at 99.8 %, so
        /// 95 % is generous and still makes a false positive very hard — a chapter
        /// actually titled "Opis" in the middle of a book cannot reach it. This
        /// guard is what lets the marker list grow without the rule getting
        /// careless.</summary>
        private const double NearEnd = 0.95;

        /// <summary>Markers are headings, not sentences.</summary>
        private const int MaxMarkerChars = 30;

        /// <summary>Below this it is a stray line rather than a blurb; the
        /// shortest real one measured was 161 characters.</summary>
        private const int MinBodyChars = 80;

        /// <summary>Returns the description, or "" when the book carries none —
        /// which is the ordinary answer for most of the world's books.</summary>
        public static string Find(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2000) return "";
            try
            {
                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                // Where the tail begins, counted in CHARACTERS rather than lines:
                // a book of short dialogue lines and a book of long paragraphs
                // have wildly different line counts for the same length, and the
                // measurement above was about position in the TEXT.
                int cut = (int)(text.Length * NearEnd);

                int at = -1, seen = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    int start = seen;
                    seen += lines[i].Length + 1;
                    if (start < cut) continue;
                    string l = lines[i].Trim();
                    if (l.Length == 0 || l.Length > MaxMarkerChars) continue;
                    if (Marker.IsMatch(l)) at = i;      // the LAST one wins
                }
                if (at < 0) return "";

                var body = new StringBuilder();
                for (int j = at + 1; j < lines.Length; j++)
                {
                    string l = lines[j].Trim();
                    if (l.Length > 0 && l.Length <= MaxMarkerChars
                        && (Marker.IsMatch(l) || NextSection.IsMatch(l))) break;
                    body.Append(lines[j]).Append('\n');
                }

                string cleaned = BookDescription.Clean(body.ToString());
                return cleaned.Length >= MinBodyChars ? cleaned : "";
            }
            catch { return ""; }
        }
    }
}
