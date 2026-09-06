using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// Translate the pieces a finished run left in the original, through the app's
// OWN prompt, glossary and rulebook -- so the repair reads like the rest of the
// book rather than like a second translator's work.
//
// The one thing that must be checked and not assumed is the RULEBOOK: it lives
// in %APPDATA%, which is shadowed for anything run out of the container, so a
// probe can silently read an empty file and send the passage with no rules at
// all. The run's own log says how many characters it used; if this does not
// match it, stop.
class Finish
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

        string english = args[0], bookIni = args[1], glossary = args[2], wantRules = args[3];

        // 1. The rulebook, and the check that it is the real one.
        T("TranslationRules").GetMethod("Reload", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        string rules = (string)T("TranslationRules")
            .GetMethod("For", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "hr" });
        string rulesPath = (string)T("TranslationRules")
            .GetMethod("PathFor", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "hr" });
        Console.WriteLine("rules {0} characters from {1}", rules.Length, rulesPath);
        if (rules.Length.ToString() != wantRules)
        {
            Console.WriteLine("*** STOP: the run used " + wantRules + " characters of rules and this reads "
                              + rules.Length + ". Almost certainly the shadowed %APPDATA%.");
            return;
        }

        // 2. The glossary the run itself saved.
        object bible = T("TranslationBible").GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                        .Invoke(null, new object[] { glossary });
        var names = (ICollection)Fld(bible, "Names");
        Console.WriteLine("glossary {0} names, narrator \"{1}\"", names.Count, Fld(bible, "NarratorGender"));

        string system = (string)T("TranslationJob")
            .GetMethod("BuildSystemPrompt", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { "en", "hr", "", bible });
        Console.WriteLine("system prompt {0} characters", system.Length);

        // 3. The same cut the run made.
        string book = File.ReadAllText(english, Encoding.UTF8).Replace("\r\n", "\n");
        object parts = T("BookMatter").GetMethod("Divide", BindingFlags.Public | BindingFlags.Static)
                        .Invoke(null, new object[] { book, true });
        string body = (string)Fld(parts, "Body");
        int bodyStart = (int)Fld(parts, "BodyStart");
        var chapters = new List<int>();
        foreach (string line in File.ReadAllLines(bookIni, Encoding.UTF8))
        {
            Match m = Regex.Match(line, @"^H\d+=\d+\|(\d+)\|");
            if (!m.Success) continue;
            int at = int.Parse(m.Groups[1].Value) - bodyStart;
            if (at > 0 && at < body.Length) chapters.Add(at);
        }
        var split = T("TextChunker").GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .First(m => m.Name == "Split" && m.GetParameters().Length == 3);
        var list = ((IEnumerable)split.Invoke(null, new object[] { body, 6000, chapters })).Cast<object>().ToList();

        object engine = T("TranslationEngines")
            .GetMethod("ById", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "gemini" });
        var buildUser = T("TranslationJob").GetMethod("BuildUserMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        var send = T("Translator").GetMethod("Send", BindingFlags.Public | BindingFlags.Static);

        foreach (string a in args.Skip(4))
        {
            int w = int.Parse(a);
            object chunk = list[w - 1];
            string src = (string)Fld(chunk, "Text");
            string user = (string)buildUser.Invoke(null, new object[] { chunk, engine });

            object r = send.Invoke(null, new object[] { engine, null, system, user, 8000, "en", "hr", null });
            bool ok = (bool)Fld(r, "Ok");
            string text = (string)Fld(r, "Text");
            Console.WriteLine();
            Console.WriteLine("=== piece {0} ===", w);
            if (!ok) { Console.WriteLine("   FAILED: {0} {1}", Fld(r, "Error"), Fld(r, "Detail")); continue; }
            Console.WriteLine("   source {0} chars, {1} paragraphs", src.Length, Paras(src));
            Console.WriteLine("   back   {0} chars, {1} paragraphs   ratio {2:0.00}x",
                text.Length, Paras(text), text.Length / (double)src.Length);
            File.WriteAllText("piece" + w + ".hr.txt", text, new UTF8Encoding(false));
            Console.WriteLine("   first line: {0}", text.Split('\n')[0]);
        }
    }
}
