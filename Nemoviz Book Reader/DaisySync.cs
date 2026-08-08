using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>One point where the book's TEXT and its AUDIO are the same place:
    /// a character offset into the reading text, and the second on the virtual
    /// audio timeline that is being spoken there.</summary>
    public struct SyncPoint
    {
        public int CharOffset;   // into the cleaned reading text
        public double Seconds;   // on the concatenated virtual timeline
        public SyncPoint(int charOffset, double seconds) { CharOffset = charOffset; Seconds = seconds; }
    }

    /// <summary>
    /// The join between a DAISY book's text and its audio.
    ///
    /// <para>A text+audio DAISY already carries the answer: every SMIL
    /// <c>&lt;par&gt;</c> pairs a <c>&lt;text src="dtbook.xml#id"/&gt;</c> with an
    /// <c>&lt;audio src=… clipBegin=…/&gt;</c>. The producer did the alignment,
    /// sentence by sentence, and NBR only has to read it — there is nothing to
    /// infer, guess or measure. That is why a hybrid DAISY is the right FIRST
    /// multi-modal format: everything after it (EPUB media overlays, and the
    /// speech-driven pacing in §8l) is harder, and this one is free.</para>
    ///
    /// <para>Both halves of the join already existed and were simply never
    /// introduced: <see cref="TextParsing.Assemble"/> hands back
    /// <c>idOffsets</c> — element id → character offset — and
    /// <see cref="DaisyParser"/> already resolves a SMIL fragment to (audio file,
    /// clipBegin). What was missing is this: walking the SMIL for the
    /// <b>text src</b> of each par rather than its internal ids, which is what
    /// names the DTBook element the audio belongs to.</para>
    ///
    /// <para><b>Measured</b> on Kincaid, <i>Annie John</i> (DAISY 3, 163 audio
    /// files, 11 SMIL): 524 pars read, <b>524 joined to the text, none
    /// unmatched</b>, collapsing to 369 sync points over a 13 981 s timeline;
    /// character offsets and times both run strictly forwards. Walking the pairs
    /// in document order, the audio position never goes backwards either — the
    /// producer's alignment is internally sound, so nothing here has to repair
    /// it.</para>
    ///
    /// <para><b>The audio timeline must be built from real durations</b>, which
    /// sounds obvious and was not: a test harness that gave every file the
    /// sample's own 59.7 s average reported 59 of 368 steps going backwards, and
    /// they were all its own — a file longer than the placeholder pushes its
    /// <c>clipBegin</c> past the start of the next one. That measurement said
    /// nothing whatever about the parser. <see cref="Build"/>'s
    /// <c>audioStart</c> comes from <c>BookData.Offsets</c> for exactly this
    /// reason.</para>
    /// </summary>
    /// <summary>The finished join, held in BOTH orders.
    ///
    /// <para>One list cannot serve both directions, and assuming it can is a
    /// silent way to put the reading position in the wrong place. Measured over
    /// 22 real hybrid books: in most, character offset and time run forwards
    /// together and one sorted list would do — but four disagree. Three of them
    /// (worst: a Plato edition, 738 of 6099 pars) genuinely read their text in a
    /// different order from their audio; in the fourth the SMIL's
    /// <c>clipBegin</c> ran past where the audio file was measured to end, which
    /// is a duration problem, not an alignment one (TagLib under-reads a VBR MP3
    /// with no Xing header — the same reason <c>BuildChaptersFromFolder</c> keeps
    /// the <c>MpvDuration</c> fallback).</para>
    ///
    /// <para>Whatever the cause, a binary search over a list that is not sorted
    /// on the key being searched does not return a near miss — it returns
    /// nonsense. So each direction gets a list sorted on its own key, and neither
    /// point is thrown away: the producer's alignment is reported as it is.</para>
    /// </summary>
    public class SyncMap
    {
        /// <summary>Sorted by character offset, one point per offset — the text
        /// asking where the audio is.</summary>
        public List<SyncPoint> ByChar = new List<SyncPoint>();
        /// <summary>Sorted by second, one point per instant — the audio asking
        /// where the text is. This is the one that runs on every tick.</summary>
        public List<SyncPoint> ByTime = new List<SyncPoint>();
        public int Count { get { return ByChar.Count; } }
        public bool IsEmpty { get { return ByChar.Count == 0; } }
    }

    public static class DaisySync
    {
        private const RegexOptions RO = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        /// <summary>One SMIL pairing, before either side has been resolved to a
        /// number: which text element, and which audio clip speaks it.</summary>
        public struct Pair
        {
            public string TextId;      // the fragment from <text src="…#frag">
            public string AudioFile;   // the <audio src="…">
            public double ClipBegin;   // seconds into that file
        }

        /// <summary>Reads every <c>&lt;par&gt;</c> in the given SMIL files, in the
        /// order given, and returns the text-to-audio pairings.
        ///
        /// <para>Parsed by walking the tags in document order rather than as XML,
        /// for the same reason <see cref="DaisyParser"/> does: real DAISY files
        /// come with declared encodings that lie and doctypes pointing at URLs
        /// that no longer resolve, and a strict parser dies on both. The pairing
        /// rule is simply "the next audio at or after this text", which is what a
        /// par means and survives the variations producers put around it.</para>
        /// </summary>
        public static List<Pair> ReadPairs(string folder, IEnumerable<string> smilFilesInOrder)
        {
            var pairs = new List<Pair>();
            if (folder == null || smilFilesInOrder == null) return pairs;

            foreach (string name in smilFilesInOrder)
            {
                string path = FindFile(folder, name);
                if (path == null) continue;

                string smil;
                try { smil = File.ReadAllText(path); }
                catch { continue; }

                // Every <audio> with where it sits, so a <text> can find the one
                // that follows it.
                var audios = new List<(int At, string Src, double Begin)>();
                foreach (Match a in Regex.Matches(smil, @"<audio\b[^>]*>", RO))
                {
                    string src = Attr(a.Value, "src");
                    if (src == null) continue;
                    audios.Add((a.Index, Path.GetFileName(src), ParseClip(ClipBeginOf(a.Value))));
                }
                if (audios.Count == 0) continue;

                foreach (Match t in Regex.Matches(smil, @"<text\b[^>]*>", RO))
                {
                    string src = Attr(t.Value, "src");
                    if (src == null) continue;
                    int hash = src.IndexOf('#');
                    if (hash < 0) continue;                 // no fragment, nothing to anchor to
                    string frag = src.Substring(hash + 1);
                    if (frag.Length == 0) continue;

                    int idx = audios.FindIndex(x => x.At >= t.Index);
                    if (idx < 0) continue;                  // text after the last audio
                    pairs.Add(new Pair
                    {
                        TextId = frag,
                        AudioFile = audios[idx].Src,
                        ClipBegin = audios[idx].Begin
                    });
                }
            }
            return pairs;
        }

        /// <summary>Turns the pairings into the map the player wants: character
        /// offset ↔ second on the virtual timeline, sorted and de-duplicated.
        ///
        /// <para><paramref name="idOffsets"/> comes from the text extraction,
        /// <paramref name="audioStart"/> from the book's own chapter offsets — the
        /// same virtual timeline every seek already uses, so a sync point and a
        /// bookmark speak the same units.</para>
        ///
        /// <para>A pairing whose text id never made it into the reading text is
        /// dropped rather than guessed at: DTBook carries ids on things that are
        /// not read aloud (page numbers are lifted out of the flow, see §8c), and
        /// inventing an offset for one would put the highlight in the wrong
        /// place.</para></summary>
        public static SyncMap Build(List<Pair> pairs,
                                    Dictionary<string, int> idOffsets,
                                    Func<string, double> audioStart)
        {
            var map = new SyncMap();
            if (pairs == null || idOffsets == null || audioStart == null) return map;

            var points = new List<SyncPoint>();
            foreach (Pair p in pairs)
            {
                if (!idOffsets.TryGetValue(p.TextId, out int off)) continue;
                double baseSec = audioStart(p.AudioFile);
                if (baseSec < 0) continue;                  // audio file not on the timeline
                points.Add(new SyncPoint(off, baseSec + p.ClipBegin));
            }

            // Sorted by offset AND THEN BY TIME. Ties are everywhere — 524 pars
            // in the Annie John sample collapse onto 369 offsets — and List.Sort
            // is unstable, so without the second key which par survives the
            // de-duplication is arbitrary and can change between runs. Earliest
            // audio wins a tie: the first clip that speaks a place is where
            // following along should start, not whichever one sorted last.
            points.Sort((a, b) => a.CharOffset != b.CharOffset
                ? a.CharOffset.CompareTo(b.CharOffset)
                : a.Seconds.CompareTo(b.Seconds));
            // Several pars land on one offset whenever the blocks between them
            // cleaned down to nothing; the earliest keeps the place.
            foreach (SyncPoint p in points)
                if (map.ByChar.Count == 0 || p.CharOffset != map.ByChar[map.ByChar.Count - 1].CharOffset)
                    map.ByChar.Add(p);

            // And the same points again by time, on the same rule: where two
            // clips start at the same instant, the earlier place in the text is
            // the one to show.
            points.Sort((a, b) => a.Seconds != b.Seconds
                ? a.Seconds.CompareTo(b.Seconds)
                : a.CharOffset.CompareTo(b.CharOffset));
            foreach (SyncPoint p in points)
                if (map.ByTime.Count == 0 || p.Seconds != map.ByTime[map.ByTime.Count - 1].Seconds)
                    map.ByTime.Add(p);

            return map;
        }

        /// <summary>The second being spoken at a character offset — the text side
        /// asking the audio where it is.</summary>
        public static double SecondsAt(SyncMap map, int charOffset)
        {
            if (map == null || map.ByChar.Count == 0) return 0;
            var list = map.ByChar;
            int lo = 0, hi = list.Count - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].CharOffset <= charOffset) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return list[best].Seconds;
        }

        /// <summary>The character offset being spoken at a second — the audio side
        /// asking the text where it is. This is the direction that drives
        /// following along, so it runs on every position tick.</summary>
        public static int CharAt(SyncMap map, double seconds)
        {
            return CharAt(map, seconds, null);
        }

        /// <summary>The same, but moving SENTENCE BY SENTENCE between anchors
        /// when the book's own anchors are far apart.
        ///
        /// <para><b>Why this is needed, measured on the library.</b> Gordan tested
        /// braille on real hardware: plain text and EPUB follow beautifully,
        /// sentence by sentence, while a DAISY text+audio hybrid is
        /// <i>"ćudljiv, ne sinka baš, zna zaglaviti i ne micati se"</i>. It is not
        /// the surface. A hybrid is driven by this map, and one book in his
        /// library carries <b>1417 points across 21.9 hours — a median gap of
        /// 46 seconds, and up to 271</b>. The text CANNOT move more often than
        /// that; another hybrid with a 4.8 s median feels fine. The difference is
        /// how the producer authored the SMIL, per paragraph or per sentence, and
        /// no amount of care in the player changes what is in the file.</para>
        ///
        /// <para><b>So the position is estimated between anchors and exact at
        /// them.</b> Reading advances roughly evenly, so the text between two
        /// anchors is walked in step with the time between them.</para>
        ///
        /// <para><b>A SENTENCE at a time, never continuously</b>, and that is the
        /// whole design rather than a detail. Sentence-at-a-time is exactly what
        /// he reports working well on the formats that do work, it is the unit
        /// §8l requires all three outputs to share, and a continuously creeping
        /// caret would drive a braille display to refresh without end. So the
        /// estimate is snapped back to a sentence start — the same boundary the
        /// surface already highlights on.</para></summary>
        public static int CharAt(SyncMap map, double seconds, string text)
        {
            if (map == null || map.ByTime.Count == 0) return 0;
            var list = map.ByTime;
            int lo = 0, hi = list.Count - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Seconds <= seconds) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            SyncPoint prev = list[best];
            if (string.IsNullOrEmpty(text) || best + 1 >= list.Count) return prev.CharOffset;

            SyncPoint next = list[best + 1];
            double span = next.Seconds - prev.Seconds;
            // Below this the book's own anchors are already finer than a
            // sentence, and guessing between them could only be worse than the
            // truth it would be overwriting.
            if (span < MinGapToEstimate) return prev.CharOffset;

            int chars = next.CharOffset - prev.CharOffset;
            // Not every book reads in order — §8c found four whose text and audio
            // genuinely run backwards in places. There, estimate nothing.
            if (chars <= 0) return prev.CharOffset;

            double f = (seconds - prev.Seconds) / span;
            if (f < 0) f = 0; else if (f > 1) f = 1;
            int raw = prev.CharOffset + (int)(chars * f);
            if (raw >= text.Length) raw = text.Length - 1;
            if (raw < prev.CharOffset) return prev.CharOffset;

            int start = text.LastIndexOfAny(SentenceEnds, raw) + 1;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            // Never behind the anchor: an anchor is measured truth and an
            // estimate may not walk back over one.
            return start < prev.CharOffset ? prev.CharOffset : start;
        }

        /// <summary>The sentence boundary, the same set <c>SentenceAround</c> in
        /// the player highlights on. One convention, so an estimated position and
        /// the highlight it produces cannot land on different things.</summary>
        private static readonly char[] SentenceEnds = { '.', '!', '?', '\n' };

        /// <summary>Anchors closer together than this are left alone.</summary>
        public const double MinGapToEstimate = 8.0;

        private static string FindFile(string folder, string name)
        {
            try
            {
                string direct = Path.Combine(folder, name);
                if (File.Exists(direct)) return direct;
                return Directory.GetFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { return null; }
        }

        private static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, name + @"\s*=\s*""([^""]*)""", RO);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string ClipBeginOf(string audioTag)
        {
            var m = Regex.Match(audioTag, @"clip-begin\s*=\s*""([^""]*)""", RO);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(audioTag, @"clipBegin\s*=\s*""([^""]*)""", RO);
            return m.Success ? m.Groups[1].Value : null;
        }

        // npt=12.5s, 00:00:12.500, or bare seconds — the three spellings real
        // books use. Same rules as DaisyParser.ParseClip, kept here so this file
        // can be read on its own.
        private static double ParseClip(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            v = v.Trim();
            if (v.StartsWith("npt=", StringComparison.OrdinalIgnoreCase)) v = v.Substring(4).Trim();
            if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 1).Trim();

            if (v.Contains(":"))
            {
                string[] parts = v.Split(':');
                double total = 0;
                foreach (string part in parts)
                {
                    double n;
                    if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out n)) return 0;
                    total = total * 60 + n;
                }
                return total;
            }
            double secs;
            return double.TryParse(v, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out secs) ? secs : 0;
        }
    }
}
