using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

// Fills the keys a language file is MISSING, through the same Gemini model and
// the same stored key NBR itself uses -- read via TranslationKeys, so this tool
// never holds a copy of it.
//
// The companion to tools/translate-help.cs: that one keeps the MANUAL in step,
// this one keeps the .lang files in step. Both exist because a new feature adds
// its strings to en and hr and the other nineteen languages then sit on the
// English fallback until somebody runs a pass.
//
//   check                what is missing, per language
//   <code> [<code>...]   fill those
//   all                  fill every language that is short
//
// Copy into bin\x64\Debug and build there -- it loads "Nemoviz Book Reader.exe"
// from beside itself to reach TranslationKeys:
//
//   csc -nologo -platform:x64 -r:System.Web.Extensions.dll -out:translate-keys.exe translate-keys.cs
//
// THE TEMPLATE IS hr.lang, NOT en.lang, and that is deliberate: English carries
// two keys a shipped file does not, so "everything in English" would push two
// strings into twenty files that have no business holding them. Croatian has
// exactly the shipped set. The VALUES still come from English, which is the
// source.
class TranslateKeys
{
    const string Model = "gemini-3.1-flash-lite";
    const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";

    static string LangDir;
    static string apiKey;

    // Names that are names. A model asked to translate an interface will happily
    // render "Play / Pause" AND "OpenAI", and the second is a mistake nobody
    // would catch in Finnish.
    const string System1 =
        "You are translating the interface of Nemoviz Book Reader, a Windows audiobook and e-book "
      + "player for blind and partially sighted readers, from English into {LANG}.\n\n"
      + "Use that language's own COMPUTING AND TECHNOLOGY vocabulary -- the words its Windows, its "
      + "screen readers and its media players use. These are button captions, menu entries and "
      + "messages, so keep them as short as the English.\n\n"
      + "Input is lines of number, TAB, key, TAB, English. Reply with number, TAB, translation and "
      + "NOTHING else: same numbers, same count, one line each, in the same order. Do not repeat "
      + "the key. Do not add notes, quotes or explanation.\n\n"
      + "RULES THAT ARE NOT STYLE:\n"
      + "- The two characters backslash and n are a LINE BREAK inside the string. Keep every one of "
      + "them exactly as it appears, as those two literal characters. Never turn one into a real "
      + "new line and never drop one.\n"
      + "- Keep every placeholder such as {0} or {1} exactly, in a place that reads naturally.\n"
      + "- Do NOT translate these: Nemoviz Book Reader, NBR, DAISY, EPUB, OpenAI, Google, Gemini, "
      + "GPT-6 Astra, Azure, DeepSeek, Applications, Shift, Ctrl, Alt, and the F-keys.\n"
      + "- A key whose English is a proper name plus a parenthetical, like "
      + "Gemini 3.8 Flash (Google, larger model), keeps the name and translates only the words "
      + "inside the brackets.";

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // THE SOURCE Lang, NOT THE ONE BESIDE THE EXE. There are two: the project's
        // own and the copy MSBuild puts in the output folder. Writing to the copy
        // looks like it worked and is thrown away by the next build -- which is
        // exactly what happened on the first run of this tool, and the give-away
        // was a translation of the PREVIOUS text, because the copy was made before
        // en.lang was edited. Same walk translate-help.cs uses for docs/help.
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (int up in new[] { 4, 5 })
        {
            var parts = new List<string> { dir };
            for (int i = 0; i < up; i++) parts.Add("..");
            string root = Path.GetFullPath(Path.Combine(parts.ToArray()));
            string cand = Path.Combine(root, "Nemoviz Book Reader", "Lang");
            if (File.Exists(Path.Combine(cand, "en.lang"))) { LangDir = cand; break; }
        }
        if (LangDir == null)
        { Console.WriteLine("ne nalazim izvorni Lang od " + dir); return; }
        Console.WriteLine("Lang: " + LangDir);

        var hr = Read("hr");
        var en = Read("en");

        var codes = Directory.GetFiles(LangDir, "*.lang")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(c => c != "hr" && c != "en")
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

        string mode = args.Length > 0 ? args[0] : "check";

        if (mode == "check")
        {
            List<string> sample = null;
            foreach (string c in codes)
            {
                var have = Read(c);
                var miss = hr.Keys.Where(k => !have.ContainsKey(k)).ToList();
                if (sample == null && miss.Count > 0) sample = miss;
                Console.WriteLine("{0,-9} {1}", c, miss.Count == 0 ? "potpun" : miss.Count + " nedostaje");
            }
            if (sample != null)
            {
                Console.WriteLine();
                Console.WriteLine("nedostaju:");
                foreach (string k in sample) Console.WriteLine("   " + k);
            }
            return;
        }

        apiKey = Key("gemini");
        if (string.IsNullOrEmpty(apiKey)) { Console.WriteLine("nema Gemini kljuca"); return; }

        var todo = mode == "all" ? codes : args.ToList();
        foreach (string code in todo)
        {
            if (!File.Exists(Path.Combine(LangDir, code + ".lang")))
            { Console.WriteLine(code + ": nema datoteke"); continue; }
            try { Fill(code, hr, en); }
            catch (Exception ex) { Console.WriteLine(code + ": NEUSPJEH -- " + ex.Message); }
        }
    }

    static void Fill(string code, Dictionary<string, string> hr, Dictionary<string, string> en)
    {
        var have = Read(code);
        var missing = hr.Keys.Where(k => !have.ContainsKey(k)).ToList();
        if (missing.Count == 0) { Console.WriteLine(code + ": potpun"); return; }

        // AN EMPTY ENGLISH VALUE IS NOTHING TO TRANSLATE, and this is not
        // hypothetical: Hint.Settings.Cloud is empty ON PURPOSE (that is how "no
        // question-mark button at all" is spelt), and a harness that demanded a
        // value back for it lost 25 keys per language on 2026-09-02.
        var send = new Dictionary<int, string>();
        var order = new List<string>();
        foreach (string k in missing)
        {
            string v = en.ContainsKey(k) ? en[k] : hr[k];
            if (v.Length == 0) continue;
            send[order.Count] = k + "\t" + v;
            order.Add(k);
        }

        var t0 = DateTime.UtcNow;
        var back = Chunk(LanguageName(code), send);
        var made = new Dictionary<string, string>();
        for (int i = 0; i < order.Count; i++)
        {
            string k = order[i], src = en.ContainsKey(k) ? en[k] : hr[k];
            if (!back.ContainsKey(i)) throw new Exception("nedostaje redak " + i + " za " + k);
            string got = back[i].Trim();
            Check(k, src, got);
            made[k] = got;
        }
        // An empty source keeps its empty value rather than being left out.
        foreach (string k in missing) if (!made.ContainsKey(k)) made[k] = "";

        Insert(code, hr, made);
        Console.WriteLine("{0,-9} {1} kljuceva, {2:0.0} s", code, made.Count,
                          (DateTime.UtcNow - t0).TotalSeconds);
    }

    // What must survive a translation, checked rather than hoped for. A
    // placeholder that goes missing throws at runtime; a lost backslash-n runs
    // two paragraphs of What's new together.
    static void Check(string key, string src, string got)
    {
        if (got.Length == 0) throw new Exception(key + ": prazan prijevod");
        int wantN = Count(src, "\\n"), gotN = Count(got, "\\n");
        if (wantN != gotN) throw new Exception(key + ": prijeloma " + wantN + " -> " + gotN);
        for (int i = 0; i < 5; i++)
        {
            string ph = "{" + i + "}";
            if (src.Contains(ph) && !got.Contains(ph))
                throw new Exception(key + ": izgubljen placeholder " + ph);
        }
        if (got.IndexOf('\t') >= 0)
            throw new Exception(key + ": tabulator u vrijednosti");
        if (got.IndexOf('\n') >= 0 || got.IndexOf('\r') >= 0)
            throw new Exception(key + ": pravi prijelom retka u vrijednosti");
        // A value that still IS its own key means the echo repair above took the
        // wrong half, and shipping it would put "Menu.Book" on a menu.
        if (got == key)
            throw new Exception(key + ": vrijednost je sam kljuc");
    }

    static int Count(string s, string what)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
        return n;
    }

    // Puts each new key where hr keeps it -- after the same neighbour -- so the
    // twenty files stay parallel and a later diff means something. The file's own
    // line endings are preserved; they are CRLF here, and a mixed file is a diff
    // nobody can read.
    static void Insert(string code, Dictionary<string, string> hr, Dictionary<string, string> made)
    {
        string path = Path.Combine(LangDir, code + ".lang");
        string all = File.ReadAllText(path, Encoding.UTF8);
        string nl = all.Contains("\r\n") ? "\r\n" : "\n";
        var lines = all.Split('\n').Select(x => x.TrimEnd('\r')).ToList();

        var hrOrder = hr.Keys.ToList();
        foreach (var kv in made)
        {
            string key = kv.Key;
            int at = hrOrder.IndexOf(key);
            int put = -1;
            for (int j = at - 1; j >= 0 && put < 0; j--)          // the nearest neighbour ABOVE
            {
                string prev = hrOrder[j];
                put = lines.FindIndex(l => l.StartsWith(prev + "=", StringComparison.Ordinal));
            }
            string line = key + "=" + kv.Value;
            if (put >= 0) lines.Insert(put + 1, line); else lines.Add(line);
        }
        File.WriteAllText(path, string.Join(nl, lines), new UTF8Encoding(false));
    }

    static Dictionary<string, string> Read(string code)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        string p = Path.Combine(LangDir, code + ".lang");
        if (!File.Exists(p)) return d;
        foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq);
            if (!d.ContainsKey(k)) d[k] = line.Substring(eq + 1);
        }
        return d;
    }

    static string LanguageName(string code)
    {
        var d = Read(code);
        string n;
        if (d.TryGetValue("LanguageName", out n) && n.Length > 0) return n + " (" + code + ")";
        return code;
    }

    static Dictionary<int, string> Chunk(string lang, Dictionary<int, string> src)
    {
        var numbered = src.OrderBy(k => k.Key).Select(k => k.Key + "\t" + k.Value).ToList();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string text = Ask(lang, numbered);
            var got = new Dictionary<int, string>();
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                int n;
                if (!int.TryParse(line.Substring(0, tab).Trim(), out n)) continue;
                string v = line.Substring(tab + 1);

                // THE MODEL SOMETIMES ECHOES THE KEY, and it is asked not to. Two
                // of twenty-one languages did on the first real run: Swedish sent
                // back "Menu.Book<TAB>Bok" and Latin went further and TRANSLATED
                // the key as well -- "Repertio.PlayPause<TAB>Lude / Pausa". Both
                // parsed as a value, both passed every file-level check (the key
                // count was right, the \n count was right), and both were caught
                // only by asking the shipped Localization what a reader gets.
                //
                // The translation is whatever follows the LAST tab, since the key
                // never contains one. Keeping the repair here rather than only
                // refusing means one language does not fail on the model's habit.
                int last = v.LastIndexOf('\t');
                if (last >= 0) v = v.Substring(last + 1);
                got[n] = v;
            }
            if (got.Count == src.Count && src.Keys.All(got.ContainsKey)) return got;
            Console.WriteLine("   ponavljam: vratio " + got.Count + " od " + src.Count);
        }
        throw new Exception("oblik odgovora ne odgovara ni iz drugog pokusaja");
    }

    static string Ask(string lang, List<string> numbered)
    {
        string system = System1.Replace("{LANG}", lang);
        string user = string.Join("\n", numbered);

        var sb = new StringBuilder();
        sb.Append("{\"systemInstruction\":{\"parts\":[{\"text\":").Append(JsonStr(system)).Append("}]},");
        sb.Append("\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":").Append(JsonStr(user)).Append("}]}],");
        sb.Append("\"generationConfig\":{\"temperature\":0.2,\"maxOutputTokens\":16384}}");

        // .NET Framework 4.8 still defaults to Ssl3 and Tls here, and every modern
        // service refuses TLS 1.0 -- the bug that sat in two shipped NBR files
        // until 2026-08-23. It is process-wide, so it is set once, but it is set.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        var req = (HttpWebRequest)WebRequest.Create(Endpoint + Model + ":generateContent");
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Headers["x-goog-api-key"] = apiKey;   // a header, so it cannot come back in an error URL
        req.Timeout = 180000;
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        pendingBody = body;             // kept so a retry can build the same request
        req.ContentLength = body.Length;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

        string json = Fetch(req);

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

    // A 429 or a 503 is the service being busy, not an answer about the text --
    // and a whole language failed on one of each during the run of 2026-09-09
    // (Slovenian on a 503, Serbian Cyrillic on a 429), which then has to be
    // noticed and re-run by hand. Three tries, backing off; anything else, and a
    // third failure, is reported as it always was.
    static string Fetch(HttpWebRequest req)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                int code = resp == null ? 0 : (int)resp.StatusCode;
                bool busy = code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
                if (!busy || attempt >= 2) throw;
                Console.WriteLine("   " + code + ", cekam " + (attempt + 1) * 5 + " s");
                System.Threading.Thread.Sleep((attempt + 1) * 5000);
                req = Clone(req);   // a WebRequest cannot be sent twice
            }
        }
    }

    // The body has already been written, so a retry needs a fresh request built
    // the same way. Kept beside Fetch so the two cannot drift.
    static byte[] pendingBody;
    static HttpWebRequest Clone(HttpWebRequest old)
    {
        var req = (HttpWebRequest)WebRequest.Create(old.RequestUri);
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Headers["x-goog-api-key"] = apiKey;
        req.Timeout = old.Timeout;
        req.ContentLength = pendingBody.Length;
        using (var s = req.GetRequestStream()) s.Write(pendingBody, 0, pendingBody.Length);
        return req;
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

    static string Key(string engineId)
    {
        var app = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                                 "Nemoviz Book Reader.exe"));
        Type tk = app.GetTypes().First(x => x.Name == "TranslationKeys");
        return (string)tk.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
                         .Invoke(null, new object[] { engineId });
    }
}
