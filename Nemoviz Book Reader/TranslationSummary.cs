using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>What happened to a book, in sentences a reader can act on.
    ///
    /// <para><b>The log stays exactly as it was</b> (Gordan, 2026-10-29) — it is
    /// what the analysis is read out of, and a column of engines, asks and
    /// milliseconds is the right shape for that. This is the other audience: the
    /// person who pressed Translate and wants to know whether the book is usable.
    /// They are not the same text and should not be made to be.</para>
    ///
    /// <para><b>EVENTS, not outcomes.</b> One segment can appear on two lines —
    /// refused for its content, and then taken by the next translator — because
    /// that is what happened to it, and it is how Gordan wrote the example he
    /// asked for. Pairing every outcome with every reason would give eight
    /// sentences nobody asked for and bend the Croatian out of shape to fit a
    /// grammar of its own.</para>
    ///
    /// <para><b>A refusal names the service that RESCUED it, in the same
    /// sentence.</b> Gordan, 2026-10-30, of a run where Gemini refused one piece
    /// and DeepSeek took it: the box said only that it had been refused, and he
    /// had to work out from "116 of 116" and a zero at the foot of the log that
    /// it had been translated after all. The chain exists because it recovers;
    /// reporting the refusal alone describes the opposite of what happened. So
    /// every reason has two forms — with a successor and without — and the
    /// grouping is by (why, who refused, who took it), because "Gemini refused
    /// it and DeepSeek translated it" and "Gemini refused it and nobody did" are
    /// not the same news.</para>
    ///
    /// <para><b>Singular and plural are separate keys.</b> Croatian agrees the
    /// participle with the subject — "segment odbijen", "segmenti odbijeni" — so a
    /// template with one form is wrong half the time. Cheap to carry, impossible
    /// to compose.</para></summary>
    internal static class TranslationSummary
    {
        public static string For(TranslationReport report, int total, string took)
        {
            var sb = new StringBuilder();
            if (report == null) return "";

            int done = Math.Max(0, total - report.LeftInOriginal);
            sb.AppendLine(Localization.T("Translate.Sum.Done", done, total));
            if (took != null && took.Length > 0)
                sb.AppendLine(Localization.T("Translate.Took", took));
            sb.AppendLine();

            // Grouped by WHO as well as why, because "Gemini refused it" and
            // "OpenAI refused it" are two different pieces of news and a reader
            // acts on them differently (Gordan, 2026-10-29). One line per service,
            // and since 2026-10-30 per (service, rescuer) pair — see the note above.
            var content = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
            var quota = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
            var moved = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
            var movedAll = new SortedSet<int>();          // the same pieces, flat, for the subtractions
            var left = new SortedSet<int>();
            var byCheck = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);

            foreach (TranslationIssue i in report.Issues)
            {
                if (i == null || i.ChunkIndex < 0) continue;
                int n = i.ChunkIndex + 1;                 // segments are counted from one
                bool fellThrough = i.Kind == "later engine" || i.Kind == "last resort";
                bool abandoned = i.Kind == "left in the original";
                if (!fellThrough && !abandoned) continue;

                // A piece nobody took has no rescuer, and that is what tells the
                // two forms of every sentence apart.
                string rescuer = abandoned ? "" : (i.Took ?? "");
                if (abandoned) left.Add(n); else { Put(moved, rescuer, n); movedAll.Add(n); }

                string why = i.Reason ?? "";
                string who = i.Who ?? "";
                if (why == "content") Put(content, Pair(who, rescuer), n);
                else if (why == "quota") Put(quota, Pair(who, rescuer), n);
                else if (why.StartsWith("check:", StringComparison.Ordinal))
                    Put(byCheck, Pair(why.Substring(6), rescuer), n);
            }

            // TWO SUBTRACTIONS, AND BOTH ARE ABOUT NOT CONTRADICTING OURSELVES.
            //
            // The quota line says the segment was handed on, so a segment nobody
            // took must not appear on it — it belongs on the last line and only
            // there. Without this, one run said both "handed to the next
            // translator because the allowance was spent" and "left in the
            // original language" about the same number.
            //
            // And the plain "handed on" line is for segments with no better
            // explanation. A segment already named as refused by a service, or as
            // returned by a check, has had its reason given; repeating it under a
            // vaguer heading is noise. That leaves exactly the line Gordan wrote
            // in his example — one bare number, the piece that simply moved.
            foreach (var kv in quota) kv.Value.IntersectWith(movedAll);
            foreach (var kv in quota) Explained(moved, kv.Value);
            foreach (var kv in content) Explained(moved, kv.Value);
            foreach (var kv in byCheck) Explained(moved, kv.Value);

            foreach (var kv in content)
                Reasoned(sb, kv, "Translate.Sum.Content", ServiceName(First(kv.Key)));
            foreach (var kv in byCheck)
                Reasoned(sb, kv, "Translate.Sum.Check", CheckName(First(kv.Key)));
            foreach (var kv in moved)
            {
                if (kv.Key.Length > 0) Line(sb, kv.Value, "Translate.Sum.Moved.To", ServiceName(kv.Key));
                else Line(sb, kv.Value, "Translate.Sum.Moved");
            }
            foreach (var kv in quota)
                Reasoned(sb, kv, "Translate.Sum.Quota", ServiceName(First(kv.Key)));
            Line(sb, left, "Translate.Sum.Left");

            if (content.Count == 0 && quota.Count == 0 && moved.Count == 0
                && left.Count == 0 && byCheck.Count == 0)
                sb.AppendLine(Localization.T("Translate.Sum.Clean"));

            return sb.ToString().TrimEnd();
        }

        private static void Put(Dictionary<string, SortedSet<int>> d, string key, int n)
        {
            SortedSet<int> set;
            if (!d.TryGetValue(key, out set)) d[key] = set = new SortedSet<int>();
            set.Add(n);
        }

        /// <summary>A subject and the service that rescued the piece, as one key.
        /// A newline, because neither half can contain one and a service key could
        /// otherwise be mistaken for part of a check's name.</summary>
        private static string Pair(string subject, string took) { return subject + "\n" + took; }

        private static string First(string pair)
        {
            int i = pair.IndexOf('\n');
            return i < 0 ? pair : pair.Substring(0, i);
        }

        private static string Second(string pair)
        {
            int i = pair.IndexOf('\n');
            return i < 0 ? "" : pair.Substring(i + 1);
        }

        /// <summary>These pieces have had their reason given, so they come off the
        /// bare "handed on" line — see the two subtractions above.</summary>
        private static void Explained(Dictionary<string, SortedSet<int>> moved, SortedSet<int> named)
        {
            foreach (var kv in moved) kv.Value.ExceptWith(named);
        }

        /// <summary>One line for a group that has a reason, in whichever of the two
        /// forms fits: naming the service that took the piece on, or not, because
        /// nobody did.</summary>
        private static void Reasoned(StringBuilder sb, KeyValuePair<string, SortedSet<int>> group,
                                     string key, string subject)
        {
            string took = Second(group.Key);
            if (took.Length > 0)
                Line(sb, group.Value, key + ".Next", subject, ServiceName(took));
            else
                Line(sb, group.Value, key, subject);
        }

        /// <summary>The service in one word — Gemini, OpenAI, DeepSeek, Azure.
        /// Not the engine's label: "GPT-6 Luna (OpenAI, cheapest)" is the right
        /// thing in a combo box and the wrong thing in a sentence, and a reader
        /// out of credit needs to know WHICH ACCOUNT, which is the same for every
        /// model behind it. An unknown key says itself rather than nothing.</summary>
        private static string ServiceName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string k = "Translate.Sum.Service." + key;
            string name = Localization.T(k);
            return name == k ? key : name;
        }

        private static void Line(StringBuilder sb, SortedSet<int> set, string key, params string[] extra)
        {
            if (set.Count == 0) return;
            string k = key + (set.Count == 1 ? ".One" : ".Many");
            var args = new object[1 + (extra == null ? 0 : extra.Length)];
            args[0] = Numbers(set);
            for (int i = 1; i < args.Length; i++) args[i] = extra[i - 1];
            sb.AppendLine(Localization.T(k, args));
        }

        /// <summary>"45, 76 and 101" — the last joined by the target language's own
        /// word, which is why it is a key and not a comma.</summary>
        private static string Numbers(SortedSet<int> set)
        {
            var list = new List<int>(set);
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(i == list.Count - 1
                    ? " " + Localization.T("Translate.Sum.And") + " " : ", ");
                sb.Append(list[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>The check's own name in words. An unknown kind falls back to
        /// the kind itself rather than to nothing: a new check added later says
        /// something imperfect instead of leaving a hole in the sentence.</summary>
        private static string CheckName(string kind)
        {
            string key = "Translate.Sum.Check." + kind;
            string name = Localization.T(key);
            return name == key ? kind : name;
        }
    }
}
