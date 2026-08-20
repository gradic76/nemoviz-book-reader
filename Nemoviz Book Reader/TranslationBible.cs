using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>What is true about THIS book, established once and sent with every
    /// piece of it.
    ///
    /// <para><b>Why it has to exist, and it was measured twice.</b> Chunking a
    /// novel means most pieces carry no trace of who is speaking, and with no
    /// instruction a model renders a first-person narrator MASCULINE — measured
    /// 2026-08-14, the same sentence coming back "Usao sam... Bio sam umoran" or
    /// "Usla sam... Bila sam umorna" depending on nothing but the note. Then
    /// Gordan read all three Richard Swan novels we had put through the pipe and
    /// found Helena Sedanka, the first-person narrator, treated as a man through
    /// much of them. The system prompt asked the model to "stay consistent"; it
    /// never said she was a woman, and the reader notes were empty. The engine did
    /// exactly what it was told.</para>
    ///
    /// <para><b>And it is not only the prompt that needed this.</b> The whole-book
    /// gender check takes the book OWN majority as the truth, so it measures
    /// consistency and not correctness: a translation that is uniformly wrong
    /// passes without a word. Given a detected fact to compare against, the same
    /// check lights up. One call, two uses.</para>
    ///
    /// <para><b>A plain line format, not JSON.</b> This file is meant to be opened
    /// and corrected by the reader after they have read the book, so it has to be
    /// legible; and a model asked for loose JSON is a model that will one day
    /// return prose around it. Unknown lines are kept exactly as read, so a newer
    /// field cannot destroy an older file and the other way round.</para>
    /// </summary>
    internal sealed class TranslationBible
    {
        /// <summary>"feminine", "masculine", or empty when the book has no
        /// first-person narrator or the model would not commit.</summary>
        public string NarratorGender = "";

        /// <summary>A name as the source writes it, and what the translation is to
        /// do with it. The order is the one the model gave, which puts the people a
        /// book names most often first.</summary>
        public readonly List<KeyValuePair<string, string>> Names =
            new List<KeyValuePair<string, string>>();

        /// <summary>Anything this version does not model, kept verbatim.</summary>
        public readonly List<string> Other = new List<string>();

        public bool IsEmpty { get { return NarratorGender.Length == 0 && Names.Count == 0; } }

        /// <summary>The book facts as the system prompt carries them.
        ///
        /// <para>It states the gender OUTRIGHT rather than asking for consistency,
        /// because consistency is what the model already had and it was not enough:
        /// there is nothing in a middle chunk to be consistent with.</para></summary>
        public string ToPrompt()
        {
            if (IsEmpty) return "";
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Facts about THIS book. They hold in every passage, including passages that show no sign of them:");
            if (NarratorGender.Length > 0)
                sb.AppendLine("- The narrator speaks in the first person and is " + NarratorGender
                              + ". Every first-person past-tense form must agree with that, in every passage.");
            if (Names.Count > 0)
            {
                sb.AppendLine("- Names and terms already decided for this book. Use these renderings and no others:");
                foreach (var n in Names) sb.AppendLine("    " + n.Key + " = " + n.Value);
            }
            return sb.ToString();
        }

        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; Nemoviz Book Reader - what was decided about this book before translating it.");
                sb.AppendLine("; Correct a line and translate the book again: the pieces already done come back");
                sb.AppendLine("; from the cache, so a fix costs seconds rather than the whole job.");
                sb.AppendLine();
                if (NarratorGender.Length > 0) sb.AppendLine("NARRATOR: " + NarratorGender);
                foreach (var n in Names) sb.AppendLine("NAME: " + n.Key + " = " + n.Value);
                foreach (string o in Other) sb.AppendLine(o);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static TranslationBible Load(string path)
        {
            var b = new TranslationBible();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return b;
            try { foreach (string line in File.ReadAllLines(path)) b.ReadLine(line); }
            catch { }
            return b;
        }

        /// <summary>Reads one stored or freshly answered line.</summary>
        public void ReadLine(string line)
        {
            if (line == null) return;
            string t = line.Trim();
            if (t.Length == 0 || t[0] == ';') return;

            if (t.StartsWith("NARRATOR:", StringComparison.OrdinalIgnoreCase))
            {
                string v = t.Substring(9).Trim().ToLowerInvariant();
                // ONLY THE TWO THE CHECK CAN ACT ON. "unclear", "varies", or a whole
                // sentence is not an answer, and treating it as one would put a
                // wrong fact in front of every passage in the book -- which is the
                // very fault this class exists to end.
                if (v.Contains("femin") || v.Contains("female") || v.Contains("woman"))
                    NarratorGender = "feminine";
                else if (v.Contains("mascul") || v.Contains("male") || v.Contains("man"))
                    NarratorGender = "masculine";
                return;
            }
            if (t.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase))
            {
                string v = t.Substring(5).Trim();
                int eq = v.IndexOf('=');
                if (eq <= 0) return;
                string k = v.Substring(0, eq).Trim();
                string val = v.Substring(eq + 1).Trim();
                if (k.Length > 0 && val.Length > 0)
                    Names.Add(new KeyValuePair<string, string>(k, val));
                return;
            }
            Other.Add(t);
        }

        /// <summary>Drops everything this book does not actually contain.
        ///
        /// <para><b>This is what makes inheriting a glossary safe.</b> Book two of a
        /// trilogy should render Vonvalt exactly as book one did — but book one list
        /// also holds people book two never mentions, and carrying them costs input
        /// tokens on every request and invites the model to reach for a name that is
        /// not in the passage. Intersecting with what the source really says keeps
        /// the decisions and drops the rest.</para></summary>
        public void KeepOnlyPresentIn(string sourceText)
        {
            if (string.IsNullOrEmpty(sourceText) || Names.Count == 0) return;
            var keep = new List<KeyValuePair<string, string>>();
            foreach (var n in Names)
            {
                // The stem, for the same reason the name check uses one: a name can
                // appear as a possessive or inside a compound.
                string stem = n.Key.Length <= 5 ? n.Key : n.Key.Substring(0, 5);
                if (sourceText.IndexOf(stem, StringComparison.Ordinal) >= 0) keep.Add(n);
            }
            Names.Clear();
            Names.AddRange(keep);
        }

        /// <summary>Adds what the inherited list does not already answer, so a
        /// decision taken for the first book of a series is never re-opened.</summary>
        public void FillGapsFrom(TranslationBible fresh)
        {
            if (fresh == null) return;
            if (NarratorGender.Length == 0) NarratorGender = fresh.NarratorGender;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in Names) have.Add(n.Key);
            foreach (var n in fresh.Names) if (!have.Contains(n.Key)) Names.Add(n);
        }
    }
}
