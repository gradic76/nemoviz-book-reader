using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>Tidies unstructured text before it is read aloud, so TTS doesn't
    /// stumble on layout noise: huge gaps between lines (long silences), words
    /// split by an end-of-line hyphen ("co-\nmma"), stray tabs/bullets, and
    /// standalone dashes. Distilled from Gordan's Word "cleanup" macro but
    /// adapted for our needs — notably, runs of blank lines collapse to a
    /// *single* blank line (not zero), preserving paragraph boundaries that the
    /// reader navigates by. Conservative on symbols: quotes (« » „ "), &amp;,
    /// brackets and the like are left intact. Deterministic, so a book's saved
    /// character-offset position stays valid across reloads.</summary>
    public static class TextCleaner
    {
        // Clear layout/list noise + the invisible soft hyphen. Left alone:
        // guillemets/quotes, ampersand, angle/brackets, backslash, tilde.
        private static readonly Regex Noise = new Regex("[•·◦▪‣⁃¶­]", RegexOptions.Compiled);
        // Private Use Area: not text at all, but a glyph from some symbol font —
        // a Word/Wingdings list bullet (U+F0B7) above all — that survived the
        // conversion. It has no meaning to read out, and a speech engine either
        // stumbles on it or invents a name for it. Zero-width marks and a stray
        // mid-file BOM go the same way. Replaced with a space rather than deleted:
        // "•Nema" must not become "Nema" glued to the word before it.
        private static readonly Regex Invisible =
            new Regex(@"[\uE000-\uF8FF\u200B-\u200F\uFEFF]", RegexOptions.Compiled);
        // letter-hyphen-newline-letter → glue the word back together.
        private static readonly Regex Dehyphenate = new Regex(@"(\p{L})-\n(\p{L})", RegexOptions.Compiled);
        // A spaced dash (hyphen / en / em) used as punctuation → comma.
        // A hard line break in the middle of a sentence is just the source's
        // wrapping — braille wraps at ~40 columns, PDF at the page width, plain text
        // at 70-odd. Speech engines treat every newline as a prosodic boundary, so
        // those breaks make a voice stutter mid-sentence (very audible on Microsoft
        // voices, less so on eSpeak). A line that continues in lowercase is a
        // continuation, so the break becomes a space. It is a REPLACEMENT, not a
        // deletion: the text keeps its length, so every heading/page offset already
        // stored for a book stays exactly valid. Blank-line paragraph breaks are
        // untouched (the next line doesn't start with a lowercase letter there).
        private static readonly Regex WrappedLine =
            new Regex(@"(?<=\S)\n(?=\p{Ll})", RegexOptions.Compiled);

        private static readonly Regex SpacedDash = new Regex(@" [-–—] ", RegexOptions.Compiled);
        private static readonly Regex TrailingSpace = new Regex(@"[ \t]+\n", RegexOptions.Compiled);
        private static readonly Regex MultiSpace = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
        private static readonly Regex BlankRuns = new Regex(@"\n{3,}", RegexOptions.Compiled);

        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string t = text.Replace("\r\n", "\n").Replace("\r", "\n");
            t = t.Replace('\t', ' ');
            t = Noise.Replace(t, "");
            t = Invisible.Replace(t, " ");
            t = Dehyphenate.Replace(t, "$1$2");
            t = WrappedLine.Replace(t, " ");   // unwrap mid-sentence line breaks
            t = SpacedDash.Replace(t, ", ");
            t = TrailingSpace.Replace(t, "\n");
            t = MultiSpace.Replace(t, " ");
            t = BlankRuns.Replace(t, "\n\n"); // many blank lines → one
            return t.Trim();
        }
    }
}
