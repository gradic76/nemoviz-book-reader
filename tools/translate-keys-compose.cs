using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

// The twelve keys of the translation summary's CHECK sentence, re-translated as
// one set. Companion to translate-keys.cs, and it exists because that one
// cannot do this job:
//
//   csc -nologo -platform:x64 -r:System.Web.Extensions.dll \
//       -out:translate-keys-compose.exe translate-keys-compose.cs
//   translate-keys-compose.exe <Lang folder> cs da de ...
//
// Translate.Sum.Check.<kind> is not a sentence -- it is a fragment that stands
// INSIDE Translate.Sum.Check.One, after a preposition. English and Croatian were
// written by hand and agree ("zbog ponavljanja", genitive). The bulk pass
// translates each key ON ITS OWN, so every case language got its fragments in
// the NOMINATIVE and the sentence came out wrong in all of them: Slovene
// "zaradi ponavljanje", Russian "iz-za povtor", Slovak "kvoli opakovanie".
// Nothing a file-level check can see -- every key present, every placeholder
// right, and the grammar broken in nineteen languages at once.
//
// So the twelve are sent TOGETHER with the composition spelled out, and the
// model is asked to choose its sentence first and then put all eight reasons
// into the form that fits it. Measured 2026-10-30: it inflects correctly in
// Czech, Slovak, Polish, Slovene, Serbian, Russian, Ukrainian, German and
// Finnish, and picks a better preposition than the per-key pass did.
//
// TWO THINGS IT DOES THAT translate-keys.cs DOES NOT, and they are why this is
// a second file rather than a flag: it OVERWRITES rather than filling what is
// missing, which is what re-translating a changed string needs; and its prompt
// EXPLAINS how the strings compose, which cannot be written generically -- the
// explanation is the whole value.
//
// English and Croatian are never passed: they are the source and the approved
// wording. Pass sr and then re-derive sr-Cyrl with tools/sr-cyrillic.pl.
class TranslateKeysCompose
{
    const string Model = "gemini-3.1-flash-lite";
    const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";
    static string apiKey, LangDir;

    static readonly string[] Sentences = {
        "Translate.Sum.Check.One", "Translate.Sum.Check.Many",
        "Translate.Sum.Check.Next.One", "Translate.Sum.Check.Next.Many" };
    static readonly string[] Kinds = {
        "repetition", "paragraphs", "length", "language",
        "untranslated", "empty", "figures", "diacritics" };

    const string System1 =
        "You are translating the interface of Nemoviz Book Reader, a Windows audiobook and e-book "
      + "player for blind and partially sighted readers, from English into {LANG}.\n\n"
      + "These twelve strings form ONE sentence between them, and that is the whole point of the task.\n\n"
      + "The four SENTENCE strings carry {0} = a list of segment numbers, {1} = the reason, and in the "
      + "two Next ones {2} = the name of the service that translated the segment instead.\n"
      + "The eight REASON strings are what is substituted into {1}. They are never shown on their own.\n\n"
      + "So each reason must be in exactly the grammatical form your sentence requires at {1} -- the case "
      + "your preposition governs, the right number and gender. A nominative citation form is WRONG unless "
      + "your sentence happens to need the nominative there. Choose a natural sentence first, then put "
      + "every reason into the form that fits it, and keep all eight consistent with each other.\n\n"
      + "Input is lines of number, TAB, key, TAB, English. Reply with number, TAB, translation and NOTHING "
      + "else: same numbers, same count, one line each, in the same order. Do not repeat the key. Do not "
      + "add notes, quotes or explanation. Keep every placeholder such as {0}, {1} and {2} exactly. "
      + "Do not translate: Nemoviz Book Reader, NBR.";

    static void Main(string[] args)
    {
        LangDir = args[0];
        apiKey = Key("Gemini");
        var en = Load(Path.Combine(LangDir, "en.lang"));
        var keys = Sentences.Concat(Kinds.Select(k => "Translate.Sum.Check." + k)).ToList();

        foreach (string code in args.Skip(1))
        {
            string path = Path.Combine(LangDir, code + ".lang");
            var ask = new List<string>();
            for (int i = 0; i < keys.Count; i++)
                ask.Add((i + 1) + "\t" + keys[i] + "\t" + en[keys[i]]);

            var got = Parse(Ask(Name(code), ask), keys);
            Replace(path, got);
            Console.WriteLine(code + "  " + got.Count + " keys");
        }
    }

    static Dictionary<string, string> Parse(string reply, List<string> keys)
    {
        var d = new Dictionary<string, string>();
        foreach (string raw in reply.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int t = line.IndexOf('\t');
            if (t < 0) continue;
            int n;
            if (!int.TryParse(line.Substring(0, t).Trim(), out n)) continue;
            if (n < 1 || n > keys.Count) continue;
            // the value is what follows the LAST tab -- a model that echoes the
            // key puts it in between, and a first-tab split would keep it
            string v = line.Substring(line.LastIndexOf('\t') + 1).Trim();
            if (v.Length == 0 || v == keys[n - 1]) continue;
            d[keys[n - 1]] = v;
        }
        if (d.Count != keys.Count) throw new Exception("got " + d.Count + " of " + keys.Count);
        return d;
    }

    // Rewrites those keys in place, leaving every other line and the file's own
    // order exactly as they were.
    static void Replace(string path, Dictionary<string, string> v)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(";") || lines[i].StartsWith("#")) continue;
            int e = lines[i].IndexOf('=');
            if (e < 0) continue;
            string k = lines[i].Substring(0, e);
            if (v.ContainsKey(k)) lines[i] = k + "=" + v[k];
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    static Dictionary<string, string> Load(string path)
    {
        var d = new Dictionary<string, string>();
        foreach (string l in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (l.StartsWith(";") || l.StartsWith("#")) continue;
            int e = l.IndexOf('=');
            if (e > 0) d[l.Substring(0, e)] = l.Substring(e + 1);
        }
        return d;
    }

    static string Name(string code)
    {
        switch (code)
        {
            case "cs": return "Czech";
            case "da": return "Danish";
            case "de": return "German";
            case "eo": return "Esperanto";
            case "es": return "Spanish";
            case "fi": return "Finnish";
            case "fr": return "French";
            case "grc": return "Ancient Greek";
            case "it": return "Italian";
            case "la": return "Latin";
            case "nb": return "Norwegian Bokmal";
            case "pl": return "Polish";
            case "ru": return "Russian";
            case "sk": return "Slovak";
            case "sl": return "Slovenian";
            case "sr": return "Serbian (Latin script)";
            case "sv": return "Swedish";
            case "uk": return "Ukrainian";
            default: throw new Exception("unknown code " + code);
        }
    }

    static string Ask(string lang, List<string> numbered)
    {
        var sb = new StringBuilder();
        sb.Append("{\"systemInstruction\":{\"parts\":[{\"text\":")
          .Append(JsonStr(System1.Replace("{LANG}", lang))).Append("}]},");
        sb.Append("\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":")
          .Append(JsonStr(string.Join("\n", numbered))).Append("}]}],");
        sb.Append("\"generationConfig\":{\"temperature\":0.2,\"maxOutputTokens\":8192}}");

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        for (int attempt = 0; ; attempt++)
        {
            var req = (HttpWebRequest)WebRequest.Create(Endpoint + Model + ":generateContent");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["x-goog-api-key"] = apiKey;
            req.Timeout = 180000;
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            try
            {
                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = r.ReadToEnd();
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                var d = ser.DeserializeObject(json) as Dictionary<string, object>;
                var cands = d["candidates"] as object[];
                var c0 = cands[0] as Dictionary<string, object>;
                var content = c0["content"] as Dictionary<string, object>;
                var parts = content["parts"] as object[];
                return (string)((Dictionary<string, object>)parts[0])["text"];
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                int code = resp == null ? 0 : (int)resp.StatusCode;
                bool busy = code == 429 || code >= 500;
                if (!busy || attempt >= 2) throw;
                Console.WriteLine("   " + code + ", waiting " + (attempt + 1) * 5 + " s");
                System.Threading.Thread.Sleep((attempt + 1) * 5000);
            }
        }
    }

    static string JsonStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == (char)92) sb.Append((char)92).Append(ch);
            else if (ch == '\n') sb.Append((char)92).Append('n');
            else if (ch == '\r') sb.Append((char)92).Append('r');
            else if (ch == '\t') sb.Append((char)92).Append('t');
            else if (ch < 32) sb.Append((char)92).Append('u').Append(((int)ch).ToString("x4"));
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
