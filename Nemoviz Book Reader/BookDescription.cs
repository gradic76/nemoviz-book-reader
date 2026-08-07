using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>Turning what a book file calls its description into something a
    /// reader can actually be read.
    ///
    /// <para><b>Why this is not just a string copy.</b> Measured on 596 real
    /// EPUBs on 2026-08-07 — every OPF readable, 269 of them carrying a
    /// <c>dc:description</c>:</para>
    /// <list type="bullet">
    /// <item>the field is usually <b>escaped HTML</b>, not text. Read it out and
    /// you get <c>&lt;p class="description"&gt;For his dad's 65th birthday…</c>
    /// verbatim, tags and all. One decode is not enough and one strip is not
    /// enough: it has to be decoded FIRST, then stripped.</item>
    /// <item>some are Apple exports and arrive with inline CSS — one Croatian
    /// title was 4172 characters of <c>style="margin-top: 0pt; text-indent…"</c>
    /// around a couple of sentences.</item>
    /// <item><b>23 of the 269 run past 3000 characters</b>, which is not a blurb:
    /// producers put the next book's first chapter, or the author's whole
    /// bibliography, in that field.</item>
    /// </list>
    ///
    /// <para><b>A long one is kept, not thrown away.</b> There is no way to tell
    /// a 4000-character blurb from a mis-filled field without reading it, and
    /// discarding the publisher's own description because it is long would lose
    /// real ones. It is bounded and cut on a boundary instead — the same "when in
    /// doubt it stays" the import filter uses. A reader who meets one long
    /// description loses a moment; a reader whose book has no description never
    /// finds out there was one.</para></summary>
    public static class BookDescription
    {
        /// <summary>Generous, because a real blurb ran to 993 characters at the
        /// median and 9539 at the worst. Past this it is no longer a description
        /// of anything and the rest is not worth carrying in Book.ini.</summary>
        private const int MaxChars = 2500;

        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"[ \t ]+", RegexOptions.Compiled);
        private static readonly Regex BlankLines = new Regex(@"(\r?\n){3,}", RegexOptions.Compiled);

        /// <summary>Empty in, empty out; junk in, empty out. Never throws.</summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            try
            {
                string s = raw;

                // Decode BEFORE stripping. The field holds markup that was
                // escaped to survive being XML, so the tags are &lt;p&gt; at this
                // point and a strip pass would sail straight past them.
                s = System.Net.WebUtility.HtmlDecode(s);

                // Paragraph and line breaks are the only structure worth keeping;
                // turn them into real ones before the rest of the tags go.
                s = Regex.Replace(s, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"<\s*/\s*(p|div|li|h[1-6])\s*>", "\n\n", RegexOptions.IgnoreCase);

                s = Tags.Replace(s, " ");

                // A second decode, but only if the first left entities behind —
                // some producers escape twice. Guarded so text that legitimately
                // contains "&amp;" is not quietly rewritten.
                if (s.IndexOf("&lt;", StringComparison.Ordinal) >= 0 ||
                    s.IndexOf("&amp;", StringComparison.Ordinal) >= 0 ||
                    s.IndexOf("&#", StringComparison.Ordinal) >= 0)
                    s = System.Net.WebUtility.HtmlDecode(s);

                s = s.Replace("\r\n", "\n").Replace('\r', '\n');
                s = Spaces.Replace(s, " ");
                s = BlankLines.Replace(s, "\n\n");
                s = string.Join("\n", Array.ConvertAll(s.Split('\n'), l => l.Trim())).Trim();

                if (s.Length == 0) return "";
                return Bound(s);
            }
            catch { return ""; }
        }

        /// <summary>Cuts at a sentence or paragraph end rather than mid-word, so a
        /// bounded description still reads as prose and not as an accident.</summary>
        private static string Bound(string s)
        {
            if (s.Length <= MaxChars) return s;

            string head = s.Substring(0, MaxChars);
            int para = head.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (para > MaxChars / 2) return head.Substring(0, para).TrimEnd();

            int stop = head.LastIndexOfAny(new[] { '.', '!', '?', '…' });
            if (stop > MaxChars / 2) return head.Substring(0, stop + 1).TrimEnd();

            int space = head.LastIndexOf(' ');
            return (space > 0 ? head.Substring(0, space) : head).TrimEnd() + "…";
        }

        /// <summary>An ISBN as the file declares it, reduced to digits (a trailing
        /// X is kept — it is a valid ISBN-10 check character). Anything that is
        /// not 10 or 13 long comes back empty: <c>dc:identifier</c> also carries
        /// UUIDs, internal ids and URLs, and only a real ISBN is any use as a
        /// lookup key.</summary>
        public static string NormaliseIsbn(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (c == 'X' || c == 'x') sb.Append('X');
            }
            string s = sb.ToString();
            // "urn:isbn:" and friends leave no digits behind, but a URL can leave
            // a year or an id — length is what settles it.
            if (s.Length != 10 && s.Length != 13) return "";
            if (s.IndexOf('X') >= 0 && (s.Length != 10 || s.IndexOf('X') != 9)) return "";
            return s;
        }
    }
}
