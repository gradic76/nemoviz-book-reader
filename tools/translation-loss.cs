using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// How many paragraphs did the ACCEPTED translations lose? The rejections are in
// the log, but the accepted answers are the ones that say where the check's
// threshold really sits: if most of what passed lost one or two, then "three is
// serious" is standing on the noise floor rather than above it.
class LossProbe
{
    static Assembly app;
    static Type T(string n) { return app.GetTypes().First(x => x.Name == n); }
    static object Fld(object o, string n)
    { return o.GetType().GetField(n, BindingFlags.Public | BindingFlags.Instance).GetValue(o); }
    static int Paras(string s)
    {
        return (int)T("TranslationChecks").GetMethod("CountParagraphs",
            BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { s });
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        app = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Nemoviz Book Reader.exe"));

        string book = File.ReadAllText(args[0], Encoding.UTF8).Replace("\r\n", "\n");
        object parts = T("BookMatter").GetMethod("Divide", BindingFlags.Public | BindingFlags.Static)
                        .Invoke(null, new object[] { book, true });
        string body = (string)Fld(parts, "Body");
        int bodyStart = (int)Fld(parts, "BodyStart");

        var chapters = new List<int>();
        foreach (string line in File.ReadAllLines(args[1], Encoding.UTF8))
        {
            Match m = Regex.Match(line, @"^H\d+=\d+\|(\d+)\|");
            if (!m.Success) continue;
            int at = int.Parse(m.Groups[1].Value) - bodyStart;
            if (at > 0 && at < body.Length) chapters.Add(at);
        }
        var split = T("TextChunker").GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .First(m => m.Name == "Split" && m.GetParameters().Length == 3);
        var list = ((IEnumerable)split.Invoke(null, new object[] { body, 6000, chapters })).Cast<object>().ToList();

        // The cache, keyed "<start>-<hash>"; we only need the start.
        var cache = new Dictionary<int, string>();
        string all = File.ReadAllText(args[2], Encoding.UTF8);
        foreach (string rec in all.Split(new[] { "␞\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int nl = rec.IndexOf('\n');
            if (nl <= 0) continue;
            string key = rec.Substring(0, nl).Trim();
            int dash = key.IndexOf('-');
            int start;
            if (dash <= 0 || !int.TryParse(key.Substring(0, dash), out start)) continue;
            cache[start] = rec.Substring(nl + 1);
        }
        Console.WriteLine("cache holds {0} pieces of {1}", cache.Count, list.Count);

        var loss = new List<int>();
        int matched = 0;
        var hist = new SortedDictionary<int, int>();
        foreach (object ch in list)
        {
            int start = (int)Fld(ch, "Start");
            string src = (string)Fld(ch, "Text");
            string tr;
            if (!cache.TryGetValue(start, out tr)) continue;
            matched++;
            int d = Paras(src) - Paras(tr);
            loss.Add(d);
            if (!hist.ContainsKey(d)) hist[d] = 0;
            hist[d]++;
        }
        Console.WriteLine("matched {0} accepted translations", matched);
        Console.WriteLine();
        Console.WriteLine("paragraphs lost by an ACCEPTED translation:");
        foreach (var kv in hist)
            Console.WriteLine("   {0,3} lost : {1,3} piece(s)  {2}", kv.Key, kv.Value, new string('#', kv.Value));
        Console.WriteLine();
        Console.WriteLine("   none lost      : {0} of {1}  ({2:P0})",
            loss.Count(x => x == 0), loss.Count, loss.Count(x => x == 0) / (double)loss.Count);
        Console.WriteLine("   lost 1 or 2    : {0}  -- allowed by the rule, and the rule is right about them",
            loss.Count(x => x == 1 || x == 2));
        Console.WriteLine("   lost 3 or more : {0}  -- would be called SERIOUS if it happened again", loss.Count(x => x >= 3));
    }
}
