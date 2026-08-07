using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>The blurb some people keep in a text file beside the book.
    ///
    /// <para><b>The same files the import filter throws away.</b> A small .txt
    /// next to the audio must not become a book — that is what the 5 KB rule and
    /// the info.txt/readme.txt list are for — but it is very often worth READING.
    /// The two rules are one mechanism seen from opposite ends.</para>
    ///
    /// <para><b>Measured over 161 book folders</b> in Test naslovi and four
    /// OneDrive collections, by running this class as it is compiled rather than
    /// a copy of its rules: <b>155 of them, 96 %, yield a description</b>, median
    /// about 1050 characters. That is a better rate than any embedded metadata
    /// except M4B's.</para>
    ///
    /// <para><b>Four shapes, and the common one was not the one I expected.</b>
    /// A handful carry an explicit <c>Book Description:</c> key; some are fields
    /// (Author, Title, Narrator, Duration…) followed by prose; one producer
    /// buries the blurb at the END under a heading of its own, below a torrent
    /// listing; but MOST are simply the blurb, on its own, with nothing around
    /// it. The first design was built for the keyed shape after looking at two
    /// samples, which would have covered about one file in fifty.</para>
    ///
    /// <para><b>And a few are not descriptions at all</b> — release dumps of the
    /// "ASIN: … | MP3 64 Kbps | 14:47:29 | 419.0 MB" kind, a stray "BBC Comedy
    /// Series", a torrent-tracker line, an advertisement for the uploader's
    /// file-host referral link. All six of the rejections in the sample are of
    /// that kind, and nothing genuine is refused except one blurb cut off at 99
    /// characters.</para>
    ///
    /// <para><b>The method, since every rule below came out of it and none of
    /// them out of a guess:</b> a build is kept as a baseline, the corpus is swept
    /// with both, and every single change is read. That is what caught the four
    /// times a rule fixed the shape it was written for and broke another —
    /// most instructively when "start at the first line that is not scaffolding"
    /// began INSIDE a header block and kept the rest of it.</para></summary>
    public static class SidecarDescription
    {
        /// <summary>An explicit heading ends the search: whatever follows it is
        /// the description, however the rest of the file is laid out.
        ///
        /// <para><b>It is a set of headings, not just "Description:", and the
        /// corpus is why.</b> One uploader's files carry the blurb at the very
        /// END, under "From back cover:", below a torrent listing and a thank-you
        /// — so line-by-line judgement returns the chatter and misses the one
        /// paragraph that is actually about the book. A heading says outright
        /// where the description is, and that outranks any amount of
        /// inference.</para>
        ///
        /// <para>Anchored to the start of a line and the colon is required: a
        /// sentence may contain the word "summary", a line that IS one does not.
        /// The same markers <see cref="TrailingDescription"/> looks for in a
        /// book's own text.</para></summary>
        private static readonly Regex KeyedBody = new Regex(
            @"^[ \t]*(?:book\s+description|description|from\s+(?:the\s+)?back\s+cover"
            + @"|back\s+cover|about\s+the\s+book|synopsis|summary|overview|blurb)[ \t]*:[ \t]*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline
            | RegexOptions.Compiled);

        /// <summary>A "Narrator: …" style header line. The value is captured so a
        /// long one can be kept — see <see cref="Keep"/>.
        ///
        /// <para>Dots and spaces are allowed between the label and the colon,
        /// because one producer in the sample pads the column by hand — "Read by
        /// . . :", "Publisher . :", "ISBN . . . .:". Those are the same field
        /// rows, and without this they read as prose.</para></summary>
        private static readonly Regex HeaderLine = new Regex(
            @"^\s*[A-Za-z][A-Za-z ]{2,24}[ .]*:[ \t]*(\S.*)$", RegexOptions.Compiled);

        /// <summary>A line that is nothing but a short heading — "From back
        /// cover:", "From Recorded Books:", "From Wiki:". The colon with nothing
        /// after it is what makes it a heading rather than a field.
        ///
        /// <para><b>An all-caps heading with no colon is deliberately NOT one</b>
        /// — "ABOUT THE BOOK", "GENERAL INFORMATION". It was tried, because two
        /// files in the sample signpost that way, and the sweep showed it losing
        /// more than it won: an omnibus of five novels has a capitalised line
        /// between the books, so the rule threw away four of the five
        /// descriptions. The colon is what makes a heading unambiguous, and
        /// without it the same shape is ordinary cover copy.</para></summary>
        private static readonly Regex Heading = new Regex(
            @"^[A-Za-z][A-Za-z0-9 .,'&()-]{2,40}:$", RegexOptions.Compiled);

        /// <summary>The same field written with leader dots — "Read by
        /// ................... Paul Shelley". Four, because an ellipsis is
        /// three.</summary>
        private static readonly Regex DottedLine = new Regex(@"\.{4,}", RegexOptions.Compiled);

        /// <summary>A drawn rule: dashes, equals signs, underscores.</summary>
        private static readonly Regex RuleLine = new Regex(@"^[-=_*#~]{3,}$", RegexOptions.Compiled);

        /// <summary>Release scaffolding rather than anything about the book. The
        /// bare pipe is in because these dumps are written as pipe-separated
        /// fields and a blurb almost never contains one.
        ///
        /// <para><b>Every entry has to be a word that prose does not use.</b>
        /// A first version also listed <c>unabridged</c>, <c>mono</c>,
        /// <c>stereo</c>, <c>mp3</c> and <c>Hz</c>, and the sweep caught it
        /// deleting a sentence out of the middle of a real blurb — "Penguin
        /// presents the unabridged, downloadable audiobook edition of The
        /// Pharaoh's Secret." None of them was earning anything either: the lines
        /// they were meant for are field rows, and are already gone as leader-dot
        /// or pipe rows or as fields with a short value.</para></summary>
        private static readonly Regex TechLine = new Regex(
            @"\bASIN\b|\bISBN(-1[03])?\b|k(b|bit)ps?\b|kb(it)?/s|\bMB\b|\bGB\b|\bhrs?\b|\bmins?\b"
            + @"|@\d|\||\[audiobook\]|\btorrent\b|https?://",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A title line with the author on it — "Spin by Robert Charles
        /// Wilson", "The Pharaoh's Secret: NUMA Files #13 by Clive Cussler". Not
        /// scaffolding exactly, but not the description either, and these files
        /// put one at the top.
        ///
        /// <para>It exists because length alone could not do the job: at a
        /// threshold of 60 the byline was skipped but so was a genuine opening of
        /// 55 characters, and at 45 the opening survived and so did a byline of
        /// 52. Asking the two questions separately settles both.</para></summary>
        private static readonly Regex Byline = new Regex(
            @"^.{0,70}\bby\s+[A-Z]", RegexOptions.Compiled);

        /// <summary>A line long enough to be a wrapped line of prose. Below this,
        /// a leading line only starts the blurb if it ends a sentence — which is
        /// what separates a real opening ("What lies beneath this planet of
        /// mystery?") from a bare title ("Spin by Robert Charles Wilson").</summary>
        private const int ProseLineChars = 45;

        /// <summary>Shorter than this is a fragment, not a blurb. The shortest
        /// real one in the sample was 156 characters; the rejected fragments were
        /// 17 and 99.</summary>
        private const int MinChars = 150;

        /// <summary>A release dump is mostly numbers — ASINs, bitrates, sizes,
        /// running times. Prose is not. This single test is what separates them
        /// when the scaffolding is all on one line and cannot be stripped by
        /// line.</summary>
        private const double MaxDigitShare = 0.06;

        private const long MaxFileBytes = 20 * 1024;

        /// <summary>Looks beside the book and returns a description, or "".
        /// Never throws.</summary>
        public static string FindIn(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "";
            try
            {
                if (!Directory.Exists(folder)) return "";
                var files = new List<string>(Directory.GetFiles(folder, "*.txt"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length == 0 || fi.Length > MaxFileBytes) continue;
                    }
                    catch { continue; }

                    // TtsReader.ReadFile, not File.ReadAllText: these files are
                    // written by whoever packed the book and are as often
                    // Windows-1250 as UTF-8 — the same problem the reading text
                    // already has, so the same answer.
                    string found = FromText(TtsReader.ReadFile(f));
                    if (found.Length > 0) return found;
                }
            }
            catch { }
            return "";
        }

        /// <summary>Exposed for the harness that measured this; the rules are
        /// worth being able to re-run when new samples arrive.</summary>
        public static string FromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            try
            {
                // The heading says where the description STARTS; it says nothing
                // about what follows it, and in the sample what follows includes
                // a drawn rule, a "From Wiki:" heading and a torrent listing. So
                // the body is filtered line by line exactly as an unheaded file
                // is — only without hunting for where to begin, which the heading
                // has already answered.
                Match m = KeyedBody.Match(text);
                string body = m.Success ? Prose(m.Groups[1].Value, false) : UnderHeading(text);
                if (body.Length == 0) body = Prose(text, true);
                if (body.Length < MinChars) return "";

                // A "must contain a full stop" test used to stand here. It was
                // measured when nothing filtered the file line by line, and it
                // was doing that filter's job — badly. It now rejects nothing
                // the line rules do not already reject, and it DOES reject two
                // real blurbs in the sample that were cut off before their first
                // full stop. Removed, and the corpus re-run to prove it changed
                // only those two.
                int digits = 0;
                foreach (char c in body) if (c >= '0' && c <= '9') digits++;
                if ((double)digits / body.Length >= MaxDigitShare) return "";

                return BookDescription.Clean(body);
            }
            catch { return ""; }
        }

        /// <summary>Everything from the first line that reads like prose, to the
        /// end.
        ///
        /// <para><b>"Not a header and not scaffolding" was not enough</b>, and one
        /// file in the sample proved it: a release dump whose FIRST line is the
        /// plain title, "Night Heron [Audiobook] by Adam Brookes". That line
        /// passes both tests, so the scan started there and swallowed the ASIN,
        /// the bitrate and the running time — which then hid inside a long enough
        /// blurb for the digit test to let the whole thing through. Scaffolding
        /// does not always announce itself on its first line.</para>
        ///
        /// <para>So the question asked is the positive one: does this line read
        /// like prose? A full wrapped line does; so does a short one that ends a
        /// sentence. A title, a byline and a field row do neither.</para>
        ///
        /// <para><b>Scaffolding is dropped wherever it sits, not only at the
        /// front.</b> A first attempt skipped a leading run and kept everything
        /// after it, and measuring that over the corpus showed the two ways it
        /// breaks: these files also carry a technical FOOTER ("Encoded at
        /// ......... 32 kbit/s"), and the header block is often interrupted by
        /// something that reads like prose, so the scan starts inside the header
        /// and keeps the rest of it. Judging each line on its own has neither
        /// problem.</para>
        ///
        /// <para><b>And it falls back</b>, because the leading test can be wrong
        /// in the other direction — a blurb wrapped narrow, with no sentence
        /// ending on its first line, would be skipped down to its second
        /// sentence. When the strict scan finds nothing at all, the lenient one
        /// runs instead: too much is better than nothing, and the tests in
        /// <see cref="FromText"/> still have to pass either way.</para></summary>
        /// <summary>The first section under a standalone heading that is long
        /// enough to be a description, or "".
        ///
        /// <para><b>Why a rule rather than a list of headings.</b> One uploader
        /// buries the blurb below a torrent listing and a thank-you, under
        /// headings like "From Recorded Books:" and "From back cover:" — and the
        /// publisher's name cannot be enumerated. What CAN be recognised is the
        /// shape: a short line that is only a heading. §10g settled the same
        /// argument for running heads — key on the structure, not on the
        /// words.</para>
        ///
        /// <para>The section has to earn it. That file also has "Links:" and
        /// "Originally posted:", both standalone headings, and both are passed
        /// over because what stands under them does not reach
        /// <see cref="MinChars"/> once the URLs are dropped. The first heading
        /// with a real paragraph beneath it is the one, and everything from
        /// there down is kept.</para></summary>
        private static string UnderHeading(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!StandsAlone(lines, i)) continue;

                int len = 0;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string l = lines[j].Trim();
                    if (StandsAlone(lines, j)) break;
                    len += Keep(l).Length;
                }
                if (len < MinChars) continue;

                var rest = new List<string>();
                for (int j = i + 1; j < lines.Length; j++) rest.Add(lines[j]);
                return Prose(string.Join("\n", rest.ToArray()), false);
            }
            return "";
        }

        /// <summary>A heading in a paragraph of its own — blank above and blank
        /// below.
        ///
        /// <para><b>The blank lines are the whole test</b>, and the corpus
        /// insisted on them. Without it the rule fired on lines that merely END
        /// in a colon inside the blurb itself — "AND FROM THE SUNKEN WORLD OF
        /// ATLANTIS:" runs straight on into the next line, "to help you learn:"
        /// follows one — and it threw away the paragraphs above them. A signpost
        /// is set apart by whoever wrote the file; a line of running text is
        /// not.</para></summary>
        private static bool StandsAlone(string[] lines, int i)
        {
            if (!Heading.IsMatch(lines[i].Trim())) return false;
            if (i > 0 && lines[i - 1].Trim().Length > 0) return false;
            // Blank below as well, and this is not belt-and-braces. Accepting a
            // heading written hard against its text was tried, and it fires on
            // the per-book headings inside an omnibus — one file of five novels
            // came back with only the fifth, the other four discarded as
            // whatever stood above the heading.
            return i + 1 >= lines.Length || lines[i + 1].Trim().Length == 0;
        }

        private static string Prose(string text, bool findStart)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var kept = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                // A signpost is dropped; a line of running text that happens to
                // end in a colon is not — which is why this is decided here,
                // where the neighbouring lines are in hand, and not in Keep.
                if (StandsAlone(lines, i)) continue;
                string l = Keep(lines[i].Trim());
                if (l.Length > 0) kept.Add(l);
            }
            if (!findStart) return Flatten(string.Join(" ", kept.ToArray()));
            string strict = From(kept, true);
            return strict.Length > 0 ? strict : From(kept, false);
        }

        /// <summary>What survives of one line: the line itself, the value of a
        /// field whose value is long enough to be prose in its own right, or
        /// nothing.
        ///
        /// <para>That middle case is not a nicety. "Overview: A lone astronaut
        /// must save the earth from disaster…" is a field by its shape and the
        /// opening sentence of the blurb by its content, and dropping the whole
        /// line started that book mid-thought.</para></summary>
        private static string Keep(string l)
        {
            if (l.Length == 0) return "";
            if (RuleLine.IsMatch(l)) return "";
            if (DottedLine.IsMatch(l)) return "";
            if (TechLine.IsMatch(l)) return "";
            Match m = HeaderLine.Match(l);
            if (m.Success)
            {
                string val = m.Groups[1].Value.Trim();
                return val.Length >= ProseLineChars ? val : "";
            }
            return l;
        }

        /// <summary>Everything from the first line that reads like prose.</summary>
        private static string From(List<string> lines, bool strict)
        {
            var kept = new List<string>();
            bool started = false;
            foreach (string l in lines)
            {
                if (!started)
                {
                    // A line only opens the blurb if it reads like prose: a full
                    // wrapped line, or a short one that ends a sentence — and in
                    // neither case a byline.
                    if (strict && !EndsSentence(l)
                        && (l.Length < ProseLineChars || Byline.IsMatch(l))) continue;
                    started = true;
                }
                kept.Add(l);
            }
            return Flatten(string.Join(" ", kept.ToArray()));
        }

        private static bool EndsSentence(string l)
        {
            char c = l[l.Length - 1];
            return c == '.' || c == '!' || c == '?';
        }

        private static string Flatten(string s)
        {
            return Regex.Replace(s ?? "", @"\s+", " ").Trim();
        }
    }
}
