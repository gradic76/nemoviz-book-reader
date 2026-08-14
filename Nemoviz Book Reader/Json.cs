using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Just enough JSON for talking to a translation service, hand-written for the
    /// same reason everything else in this project is: it adds no assembly, no
    /// package and no licence, and what we need of JSON is genuinely small — escape
    /// one string on the way out, pull one string out of a nested object on the way
    /// back.
    ///
    /// <para><b>Why not <c>JavaScriptSerializer</c> or <c>DataContractJsonSerializer</c>:</b>
    /// both are in the framework and both would do, but each drags a reference in
    /// for a job of this size, and the second needs a contract class per message
    /// shape — which is exactly the thing that would have to change every time a
    /// provider adds a field.</para>
    ///
    /// <para><b>A warning worth keeping, from the probes that preceded this
    /// (2026-08-14):</b> PowerShell's own <c>ConvertTo-Json</c> serialised a long
    /// string as an OBJECT — <c>{"value": …}</c> — and inflated an 8 kB request to
    /// 46 MB, which the service rejected with a message about a scalar field. A
    /// serialiser that is clever about types is a liability here. This one is
    /// deliberately dumb.</para>
    /// </summary>
    internal static class Json
    {
        /// <summary>A .NET string as a JSON string literal, quotes included.
        ///
        /// <para>Non-ASCII is passed through as itself rather than escaped to
        /// <c>\uXXXX</c>: the body goes out as UTF-8 bytes, which every one of these
        /// services expects, and escaping would triple the size of Croatian text for
        /// no gain. Control characters have to go, because a raw newline inside a
        /// JSON string is not legal JSON — and book text is full of them.</para></summary>
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Parses a JSON document into nested
        /// <see cref="Dictionary{TKey,TValue}"/> / <see cref="List{T}"/> / string /
        /// double / bool / null. Returns null on anything malformed rather than
        /// throwing: a service that answers with an HTML error page is a case the
        /// caller has to handle anyway, and it is not worth a different code path
        /// from "the JSON made no sense".</summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            try
            {
                object v = ParseValue(text, ref i);
                return v;
            }
            catch { return null; }
        }

        /// <summary>Walks a path of object keys and array indices and returns what
        /// is there, or null if any step is missing.
        ///
        /// <para>Written because every answer we want is one leaf deep in a nest —
        /// <c>candidates.0.content.parts.0.text</c> for Gemini,
        /// <c>choices.0.message.content</c> for the OpenAI-shaped ones — and a
        /// chain of casts at every call site is where the null checks get
        /// forgotten. A step that is all digits is an array index.</para></summary>
        public static object Path(object root, params string[] steps)
        {
            object cur = root;
            foreach (string step in steps)
            {
                if (cur == null) return null;
                int idx;
                if (int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                {
                    List<object> list = cur as List<object>;
                    if (list == null || idx < 0 || idx >= list.Count) return null;
                    cur = list[idx];
                }
                else
                {
                    Dictionary<string, object> obj = cur as Dictionary<string, object>;
                    if (obj == null || !obj.TryGetValue(step, out cur)) return null;
                }
            }
            return cur;
        }

        /// <summary>The value at a path as a string, or null. Numbers come back in
        /// invariant form, because a decimal comma read back on another machine is
        /// a different number — the lesson <c>sync.map</c> already paid for.</summary>
        public static string PathString(object root, params string[] steps)
        {
            object v = Path(root, steps);
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            if (v is bool) return ((bool)v) ? "true" : "false";
            return v.ToString();
        }

        // ---- the parser itself -------------------------------------------------

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;                                   // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException();
                i++;
                o[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException();
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException();
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++;                                   // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException();
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException();
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException();
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException();
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                     CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException();
                }
            }
            throw new FormatException();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out d))
                throw new FormatException();
            return d;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException();
            i += word.Length;
        }
    }
}
