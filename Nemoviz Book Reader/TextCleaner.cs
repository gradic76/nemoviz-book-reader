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
        // letter-hyphen-newline-letter → glue the word back together.
        private static readonly Regex Dehyphenate = new Regex(@"(\p{L})-\n(\p{L})", RegexOptions.Compiled);
        // A spaced dash (hyphen / en / em) used as punctuation → comma.
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
            t = Dehyphenate.Replace(t, "$1$2");
            t = SpacedDash.Replace(t, ", ");
            t = TrailingSpace.Replace(t, "\n");
            t = MultiSpace.Replace(t, " ");
            t = BlankRuns.Replace(t, "\n\n"); // many blank lines → one
            return t.Trim();
        }
    }
}
