using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

// Translates the manual, docs/help/hr.txt -> docs/help/<code>.txt, through the
// same Gemini model and the same stored key NBR itself uses.
//
//   translate-help.exe full <code> [<code> ...]
//   translate-help.exe sync <code> [<code> ...] [--sections 10,12,15]
//   translate-help.exe add <n> <code> [<code> ...]
//   translate-help.exe check
//
// WHY A TOOL AND NOT A SESSION SCRIPT. The ten interface languages of
// 2026-09-02 were translated by a harness that lived in a scratchpad and is
// gone, so the next batch would have started from nothing. Gordan asked for
// this one to be kept: "cuvaj alat za ubuduce, bit ce toga".
//
// THE SOURCE IS ALWAYS hr.txt, never en.txt. Croatian is Gordan's own text --
// make-help.pl says so, and it is the file his docx is derived into. English is
// a translation like any other, so translating out of it would be a translation
// of a translation.
//
// SHAPE IS THE INVARIANT. make-help.pl refuses to let a language differ in
// block structure from hr, because the page template is shared and a missing
// paragraph would silently shift every heading after it. So this tool works
// LINE BY LINE with the lines numbered, demands the same numbers back, and
// refuses a chunk whose count or whose leading markers changed. A translation
// that reads well and does not fit the shape is not usable here.
//
//   full   the whole manual. For a language that has no manual at all.
//   sync   only the sections whose block count differs from hr -- what you want
//          after Gordan edits the Croatian -- plus any named with --sections,
//          for a section he changed WITHOUT changing its shape (a sentence cut
//          from a bullet, say, which no counting can see).
//   check  says which languages differ and where, and translates nothing.
//
// Section numbers are 1-based and count both # and ## headings, in file order;
// `check` prints them.
//
// Human review is welcome and is not waited for (Gordan, 2026-09-02):
// "Strojni prijevod nam je primaran, ljudski uvid kad nam se netko smiluje."
// So a language a reviewer has been through is re-translated by `sync` like any
// other, and only the sections that had to change lose their review.
static class TranslateHelp
{
    const string Model = "gemini-3.1-flash-lite";
    const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";

    static string root;      // the repository root
    static string apiKey;

    // CultureInfo has no answer for three of the languages NBR ships, and a
    // wrong or empty language name in the prompt is the one thing that would
    // spoil a whole file quietly.
    static readonly Dictionary<string, string> Names = new Dictionary<string, string>
    {
        { "eo",      "Esperanto" },
        { "grc",     "Ancient Greek" },
        { "la",      "Latin" },
        { "sr",      "Serbian, written in the Latin alphabet" },
        { "sr-Cyrl", "Serbian, written in the Cyrillic alphabet" },
        { "nb",      "Norwegian Bokmal" },
    };

    static string LanguageName(string code)
    {
        if (Names.ContainsKey(code)) return Names[code];
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch { return code; }
    }

    // ── the file, as lines and as sections ────────────────────────────────
    //
    // A section is a heading and everything under it; everything before the
    // first heading (the comments, TITLE/LANG/TOC/BETA and the opening
    // paragraph) is section 0, which `sync` never touches and `full` does.
    class Doc
    {
        public List<string> Lines = new List<string>();
        public string Eol = "\r\n";
        public List<int> HeadingAt = new List<int>();   // line index of each heading

        public static Doc Read(string path)
        {
            var d = new Doc();
            string all = File.ReadAllText(path, new UTF8Encoding(false));
            d.Eol = all.Contains("\r\n") ? "\r\n" : "\n";
            d.Lines = all.Replace("\r\n", "\n").Split('\n').ToList();
            for (int i = 0; i < d.Lines.Count; i++)
                if (d.Lines[i].StartsWith("# ") || d.Lines[i].StartsWith("## "))
                    d.HeadingAt.Add(i);
            return d;
        }

        public void Write(string path)
        {
            File.WriteAllText(path, string.Join(Eol, Lines), new UTF8Encoding(false));
        }

        // Section n (1-based) as a line range [from, to).
        public void Range(int n, out int from, out int to)
        {
            from = HeadingAt[n - 1];
            to = n < HeadingAt.Count ? HeadingAt[n] : Lines.Count;
        }

        public int Blocks(int n)
        {
            int from, to; Range(n, out from, out to);
            int k = 0;
            for (int i = from + 1; i < to; i++) if (Lines[i].Trim().Length > 0) k++;
            return k;
        }
    }

    // ── the model ─────────────────────────────────────────────────────────
    const string System1 =
        "You are translating the user manual of Nemoviz Book Reader, an audiobook and e-book " +
        "player for blind and partially sighted readers, from Croatian into {LANG}.\n\n" +
        "Use the words that {LANG}'s own Windows, screen readers and media players use. This is " +
        "a manual read by people who navigate by keyboard and screen reader, so be plain and " +
        "concrete; do not make the prose more literary than the original.\n\n" +
        "You are given numbered lines. Return EXACTLY the same line numbers, in the same order, " +
        "one line each, in the form  N<TAB>text  and nothing else. No preamble, no commentary, " +
        "no blank lines, no markdown fences.\n\n" +
        "Each line keeps its leading marker exactly as it is:\n" +
        "  ; a comment       # a chapter        ## a section\n" +
        "  - a bullet        | a | table row |  TITLE:  LANG:  TOC:  BETA:\n" +
        "Translate the words after the marker; never move a line into another line.\n\n" +
        "Do NOT translate: the product name Nemoviz Book Reader; key names such as F1, F8, F11, " +
        "Ctrl, Shift, Alt, Enter, Tab, Escape, Insert; file extensions and file names; the " +
        "marker {beta}; the value on the TITLE: and LANG: lines. Keep *emphasis* markers where " +
        "they are. A key combination keeps its keys and translates only the words between them.";

    // ── the glossary, and why the manual needs one ────────────────────────
    //
    // THE MANUAL MUST CALL A CONTROL WHAT THE INTERFACE CALLS IT. Without this
    // the model renames them every time a section is re-translated, and it is
    // invisible: the prose reads perfectly and sends the reader looking for a
    // control that is not there. Measured 2026-09-11, re-translating one
    // section that had not moved since the names were set -- "Even out speech"
    // came back as "Equalize speech" in English, and its like in every other
    // language. Twenty manuals, one pass.
    //
    // It is built per SECTION and mechanically: every value in hr.lang that
    // actually occurs in the Croatian being translated, paired with the same
    // key read out of the target language own .lang. Nothing is curated by
    // hand, so a control added later is covered the day its key exists.
    static string glossary = "";

    static void BuildGlossary(string code, IEnumerable<string> sourceLines)
    {
        glossary = "";
        string langDir = Path.Combine(root, "Nemoviz Book Reader", "Lang");
        string hrPath = Path.Combine(langDir, "hr.lang");
        string toPath = Path.Combine(langDir, code + ".lang");
        if (!File.Exists(hrPath) || !File.Exists(toPath)) return;

        var from = ReadLang(hrPath);
        var to = ReadLang(toPath);
        string text = string.Join("\n", sourceLines);

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var kv in from)
        {
            string src = kv.Value;
            if (src.Length < 3 || src.Length > 48) continue;   // "Ton" is a control name
            if (src.IndexOf(Convert.ToChar(123)) >= 0) continue;   // a format placeholder
            if (!char.IsUpper(src[0])) continue;                   // control names are capitalised
            if (!WholeWord(text, src)) continue;
            string dst;
            if (!to.TryGetValue(kv.Key, out dst) || dst.Length == 0 || dst == src) continue;
            pairs.Add(new KeyValuePair<string, string>(src, dst));
        }
        if (pairs.Count == 0) return;

        // Longest first, so a name that contains a shorter one is listed before it.
        pairs.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        foreach (var p in pairs)
        {
            if (!seen.Add(p.Key)) continue;
            sb.Append("  ").Append(p.Key).Append("  ->  ").Append(p.Value).Append("\n");
            if (seen.Count >= 60) break;
        }
        glossary = sb.ToString();
    }

    /// <summary>Does the name stand as a word of its own in the text? A plain
    /// substring test is not enough once short names are allowed in: "Ton" is a
    /// control here, and it is also the first three letters of a dozen ordinary
    /// Croatian words.</summary>
    static bool WholeWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = at == 0 || !char.IsLetter(text[at - 1]);
            int end = at + word.Length;
            bool rightOk = end >= text.Length || !char.IsLetter(text[end]);
            if (leftOk && rightOk) return true;
            at = end;
        }
        return false;
    }

    static Dictionary<string, string> ReadLang(string path)
    {
        var d = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(path, new UTF8Encoding(false)))
        {
            if (line.Length == 0 || line[0] == Convert.ToChar(35) || line[0] == Convert.ToChar(59)) continue;
            int eq = line.IndexOf(Convert.ToChar(61));
            if (eq <= 0) continue;
            d[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        return d;
    }

    static string Translate(string lang, List<string> numbered)
    {
        string system = System1.Replace("{LANG}", lang);
        if (glossary.Length > 0)
            system += "\n\nTHE INTERFACE ALREADY HAS NAMES FOR ITS CONTROLS in " + lang
                    + ", and the manual has to use them exactly or the reader is sent "
                    + "looking for a control that does not exist. Where the Croatian uses "
                    + "a name on the left, your translation MUST use the name on the "
                    + "right, verbatim, including its capitalisation:\n\n" + glossary;
        string user = string.Join("\n", numbered);

        var sb = new StringBuilder();
        sb.Append("{\"systemInstruction\":{\"parts\":[{\"text\":").Append(JsonStr(system)).Append("}]},");
        sb.Append("\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":").Append(JsonStr(user)).Append("}]}],");
        sb.Append("\"generationConfig\":{\"temperature\":0.2,\"maxOutputTokens\":16384}}");

        // .NET Framework 4.8 still defaults to Ssl3|Tls here, and every modern
        // service refuses TLS 1.0 -- the bug that sat in two shipped NBR files
        // until 2026-08-23. It is process-wide, so it is set once, but it is set.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var req = (HttpWebRequest)WebRequest.Create(Endpoint + Model + ":generateContent");
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Headers["x-goog-api-key"] = apiKey;   // a header, so it cannot come back in an error URL
        req.Timeout = 180000;
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        req.ContentLength = body.Length;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

        string json;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            json = r.ReadToEnd();

        var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
        ser.MaxJsonLength = int.MaxValue;
        var d = ser.DeserializeObject(json) as Dictionary<string, object>;
        var cands = d != null && d.ContainsKey("candidates") ? d["candidates"] as object[] : null;
        if (cands == null || cands.Length == 0)
            throw new Exception("nema odgovora: " + json.Substring(0, Math.Min(400, json.Length)));
        var c0 = cands[0] as Dictionary<string, object>;
        var content = c0["content"] as Dictionary<string, object>;
        var parts = content["parts"] as object[];
        return (string)((Dictionary<string, object>)parts[0])["text"];
    }

    static string JsonStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    // Translates a set of source lines, keyed by their index, and hands back the
    // same keys. Retried once, because a model that miscounts usually gets it
    // right the second time and a whole language should not fail on one chunk.
    static Dictionary<int, string> Chunk(string lang, Dictionary<int, string> src)
    {
        var numbered = src.OrderBy(k => k.Key)
                          .Select(k => k.Key + "\t" + k.Value).ToList();
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string answer;
            try { answer = Translate(lang, numbered); }
            catch (Exception e)
            {
                if (attempt == 2) throw;
                Console.WriteLine("      (" + e.Message.Split('\n')[0] + ", pokusavam ponovo)");
                System.Threading.Thread.Sleep(3000);
                continue;
            }

            var got = new Dictionary<int, string>();
            foreach (string raw in answer.Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) tab = line.IndexOf(' ');
                if (tab < 0) continue;
                int n;
                if (!int.TryParse(line.Substring(0, tab).Trim(), out n)) continue;
                got[n] = line.Substring(tab + 1).Trim();
            }

            string bad = Check(src, got);
            if (bad == null) return got;
            if (attempt == 2) throw new Exception("prijevod ne odgovara izvorniku: " + bad);
            Console.WriteLine("      (" + bad + ", pokusavam ponovo)");
        }
        throw new Exception("nedostizno");
    }

    // The shape check, and it is the whole reason this tool can be trusted: same
    // line numbers, same leading marker, and a table row keeps its cell count.
    static string Check(Dictionary<int, string> src, Dictionary<int, string> got)
    {
        foreach (var k in src.Keys)
        {
            if (!got.ContainsKey(k)) return "nedostaje redak " + k;
            string a = src[k], b = got[k];
            foreach (string m in new[] { "## ", "# ", "- ", "; ", "TITLE:", "LANG:", "TOC:", "BETA:" })
                if (a.StartsWith(m) != b.StartsWith(m)) return "redak " + k + " izgubio oznaku " + m.Trim();
            if (a.StartsWith("|") != b.StartsWith("|")) return "redak " + k + " nije tablicni";
            if (a.StartsWith("|") && a.Count(c => c == '|') != b.Count(c => c == '|'))
                return "redak " + k + " ima drukciji broj celija";
            if (b.Length == 0) return "redak " + k + " je prazan";
        }
        foreach (var k in got.Keys) if (!src.ContainsKey(k)) return "visak retka " + k;
        return null;
    }

    // ── the two jobs ──────────────────────────────────────────────────────
    static void Full(string code, Doc hr)
    {
        string lang = LanguageName(code);
        Console.WriteLine(code + "  (" + lang + ")  cijeli prirucnik");
        // THE CROATIAN HEADER IS NOT TRANSLATED, IT IS REPLACED. hr.txt opens
        // with a comment block saying where it came from and that the docx is
        // Gordan's original -- none of which is true of a translation, and the
        // languages translated by hand carry two lines instead (see it.txt).
        // Translating it faithfully produced a Czech file announcing itself as
        // "the manual, Croatian" and claiming the docx as its source. The
        // stand-in goes in BEFORE the chunking, so the model renders the right
        // sentence rather than a good translation of the wrong one. Plain ASCII
        // because it is only ever INPUT -- what comes back carries the target
        // language's own spelling.
        var srcLines = new List<string>(hr.Lines);
        int firstReal = 0;
        while (firstReal < srcLines.Count &&
               (srcLines[firstReal].StartsWith(";") || srcLines[firstReal].Trim().Length == 0))
            firstReal++;
        srcLines.RemoveRange(0, firstReal);
        srcLines.InsertRange(0, new[] {
            "; Prirucnik za Nemoviz Book Reader.",
            "; Strojno preveden iz docs/help/hr.txt, koji je Gordanov izvornik.",
            "" });
        var outLines = new List<string>(srcLines);

        // CHUNKED BY SIZE, NOT BY SECTION, and the change is measured. A section
        // is a handful of short lines, and at about 27 s a request the manual's
        // twenty-five sections cost twenty minutes a language -- nearly all of it
        // round trips rather than generation, since the sections are small and the
        // latency is not. Grouping to ~5000 characters, the size the book
        // translator settled on, makes it seven requests instead of twenty-five.
        // Section boundaries are not respected here because the numbering protocol
        // does not care where a line came from; `sync` still works section by
        // section, where the unit is the thing being replaced.
        const int MaxChars = 5000;
        var all = new List<int>();
        for (int i = 0; i < srcLines.Count; i++)
            if (srcLines[i].Trim().Length > 0) all.Add(i);

        int done = 0;
        for (int i = 0; i < all.Count; )
        {
            var src = new Dictionary<int, string>();
            int chars = 0;
            while (i < all.Count && (src.Count == 0 || chars + srcLines[all[i]].Length <= MaxChars))
            {
                src[all[i]] = srcLines[all[i]];
                chars += srcLines[all[i]].Length;
                i++;
            }
            BuildGlossary(code, src.Values);
            var got = Chunk(lang, src);
            foreach (var k in got.Keys) outLines[k] = got[k];
            done += src.Count;
            Console.Write("\r      " + done + " / " + all.Count + " redaka");
        }
        Console.WriteLine();
        // LANG: names the file, so it is set here rather than trusted to the model.
        for (int i = 0; i < outLines.Count; i++)
            if (outLines[i].StartsWith("LANG:")) outLines[i] = "LANG: " + code;

        var doc = new Doc { Lines = outLines, Eol = hr.Eol };
        doc.Write(Path.Combine(root, "docs", "help", code + ".txt"));
        Console.WriteLine("      -> docs/help/" + code + ".txt");
    }

    static void Sync(string code, Doc hr, List<int> also)
    {
        string path = Path.Combine(root, "docs", "help", code + ".txt");
        if (!File.Exists(path)) { Console.WriteLine(code + ": nema datoteke, treba 'full'"); return; }
        Doc t = Doc.Read(path);
        string lang = LanguageName(code);

        if (t.HeadingAt.Count != hr.HeadingAt.Count)
        {
            Console.WriteLine(code + ": drukciji broj naslova (" + t.HeadingAt.Count
                              + " prema " + hr.HeadingAt.Count + ") -- treba 'full'");
            return;
        }

        var todo = new List<int>();
        for (int n = 1; n <= hr.HeadingAt.Count; n++)
            if (hr.Blocks(n) != t.Blocks(n)) todo.Add(n);
        foreach (int n in also) if (!todo.Contains(n)) todo.Add(n);
        todo.Sort();

        if (todo.Count == 0) { Console.WriteLine(code + ": nista za uskladiti"); return; }
        Console.WriteLine(code + "  (" + lang + ")  odjeljci: " + string.Join(", ", todo));

        // Back to front, so replacing one section cannot move the next one's lines.
        for (int i = todo.Count - 1; i >= 0; i--)
        {
            int n = todo[i];
            int hf, ht; hr.Range(n, out hf, out ht);
            var src = new Dictionary<int, string>();
            for (int j = hf; j < ht; j++) if (hr.Lines[j].Trim().Length > 0) src[j] = hr.Lines[j];

            BuildGlossary(code, src.Values);
            var got = Chunk(lang, src);

            var block = new List<string>();
            for (int j = hf; j < ht; j++)
                block.Add(hr.Lines[j].Trim().Length == 0 ? "" : got[j]);

            int tf, tt; t.Range(n, out tf, out tt);
            t.Lines.RemoveRange(tf, tt - tf);
            t.Lines.InsertRange(tf, block);
            Console.WriteLine("      odjeljak " + n + ": " + src.Count + " redaka");
        }

        t.Write(path);
        Console.WriteLine("      -> docs/help/" + code + ".txt");
    }

    /// <summary>Translates ONE new section of hr and INSERTS it, rather than
    /// replacing what is there.
    ///
    /// <para><b>`sync` cannot do this and that is not a defect in it.</b> Sync
    /// aligns the two documents by section INDEX, so it refuses outright when the
    /// counts differ -- and a section added in the middle shifts every index after
    /// it, which is precisely when "translate the ones whose block count differs"
    /// would rewrite the wrong half of the manual.</para>
    ///
    /// <para>The alternative was `full` on nineteen languages, about twenty-two
    /// minutes and the whole manual re-generated to add six paragraphs. The manual
    /// will keep growing until release, so the cheap path is worth having: this
    /// costs one section's worth of translation per language.</para>
    ///
    /// <para><b>It refuses unless the target is EXACTLY one section short</b>, so
    /// it cannot be pointed at a file that has drifted some other way -- there the
    /// honest answer really is `full`.</para></summary>
    static void Add(int n, string code, Doc hr)
    {
        string path = Path.Combine(root, "docs", "help", code + ".txt");
        if (!File.Exists(path)) { Console.WriteLine(code + ": nema datoteke, treba 'full'"); return; }
        Doc t = Doc.Read(path);
        string lang = LanguageName(code);

        if (t.HeadingAt.Count != hr.HeadingAt.Count - 1)
        {
            Console.WriteLine(code + ": nije tocno jedan odjeljak kraci ("
                              + t.HeadingAt.Count + " prema " + hr.HeadingAt.Count + ") -- treba 'full'");
            return;
        }
        if (n < 1 || n > hr.HeadingAt.Count)
        { Console.WriteLine(code + ": odjeljak " + n + " ne postoji u hr"); return; }

        int hf, ht; hr.Range(n, out hf, out ht);
        var src = new Dictionary<int, string>();
        for (int j = hf; j < ht; j++) if (hr.Lines[j].Trim().Length > 0) src[j] = hr.Lines[j];

        BuildGlossary(code, src.Values);
        var got = Chunk(lang, src);

        var block = new List<string>();
        for (int j = hf; j < ht; j++)
            block.Add(hr.Lines[j].Trim().Length == 0 ? "" : got[j]);

        // Where hr's section n begins is where the target's must begin too: the
        // target still holds hr's n+1 at that index, so inserting pushes it down.
        int put = n <= t.HeadingAt.Count ? t.HeadingAt[n - 1] : t.Lines.Count;
        t.Lines.InsertRange(put, block);
        t.Write(path);
        Console.WriteLine("{0,-8} ({1}) odjeljak {2}: {3} redaka -> docs/help/{0}.txt",
                          code, lang, n, src.Count);
    }

    static void Check(Doc hr)
    {
        Console.WriteLine("odjeljci u hr: " + hr.HeadingAt.Count);
        foreach (string path in Directory.GetFiles(Path.Combine(root, "docs", "help"), "*.txt").OrderBy(x => x))
        {
            string code = Path.GetFileNameWithoutExtension(path);
            if (code == "hr") continue;
            Doc t = Doc.Read(path);
            if (t.HeadingAt.Count != hr.HeadingAt.Count)
            {
                Console.WriteLine(string.Format("  {0,-8} naslova {1} prema {2}", code, t.HeadingAt.Count, hr.HeadingAt.Count));
                continue;
            }
            var diff = new List<string>();
            for (int n = 1; n <= hr.HeadingAt.Count; n++)
                if (hr.Blocks(n) != t.Blocks(n))
                    diff.Add(n + " (" + t.Blocks(n) + "/" + hr.Blocks(n) + ")");
            Console.WriteLine(string.Format("  {0,-8} {1}", code,
                              diff.Count == 0 ? "isti oblik" : "razlike u odjeljcima: " + string.Join(", ", diff)));
        }
        Console.WriteLine();
        Console.WriteLine("Jezici bez prirucnika:");
        var have = Directory.GetFiles(Path.Combine(root, "docs", "help"), "*.txt")
                            .Select(Path.GetFileNameWithoutExtension).ToList();
        foreach (string lp in Directory.GetFiles(Path.Combine(root, "Nemoviz Book Reader", "Lang"), "*.lang").OrderBy(x => x))
        {
            string code = Path.GetFileNameWithoutExtension(lp);
            if (!have.Contains(code)) Console.Write(code + " ");
        }
        Console.WriteLine();
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine("translate-help full <code> [...]   |   sync <code> [...] [--sections 1,2]   |   check");
            return;
        }

        // The tool lives in tools/ and is compiled into bin/x64/Debug beside the
        // player, whose assembly holds the stored key. Both paths are derived
        // from where this exe stands, so it works from any working directory.
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        root = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        if (!Directory.Exists(Path.Combine(root, "docs", "help")))
            root = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", ".."));
        if (!Directory.Exists(Path.Combine(root, "docs", "help")))
        { Console.WriteLine("ne nalazim docs/help od " + dir); return; }

        Doc hr = Doc.Read(Path.Combine(root, "docs", "help", "hr.txt"));
        string mode = args[0].ToLowerInvariant();

        if (mode == "check") { Check(hr); return; }

        // The key is the reader's own, read through the player's own store, so
        // this tool never holds a copy of it and never asks for one.
        var app = Assembly.LoadFrom(Path.Combine(dir, "Nemoviz Book Reader.exe"));
        Type tk = app.GetTypes().First(x => x.Name == "TranslationKeys");
        apiKey = (string)tk.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
                           .Invoke(null, new object[] { "gemini" });
        if (string.IsNullOrEmpty(apiKey)) { Console.WriteLine("nema pohranjenog Gemini kljuca"); return; }

        var also = new List<int>();
        var codes = new List<string>();
        int addAt = 0;
        int firstArg = 1;
        if (mode == "add")
        {
            if (args.Length < 3 || !int.TryParse(args[1], out addAt))
            { Console.WriteLine("add trazi broj odjeljka: add <n> <code> [<code>...]"); return; }
            firstArg = 2;
        }
        for (int i = firstArg; i < args.Length; i++)
        {
            if (args[i] == "--sections" && i + 1 < args.Length)
            {
                foreach (string s in args[++i].Split(','))
                { int n; if (int.TryParse(s.Trim(), out n)) also.Add(n); }
            }
            else codes.Add(args[i]);
        }

        foreach (string code in codes)
        {
            try
            {
                if (mode == "full") Full(code, hr);
                else if (mode == "add") Add(addAt, code, hr);
                else Sync(code, hr, also);
            }
            catch (Exception e)
            {
                Console.WriteLine("  " + code + " NIJE gotov: " + e.Message);
            }
        }
    }
}
