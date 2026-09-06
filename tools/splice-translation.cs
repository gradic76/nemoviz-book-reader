using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// Put a translated piece back into the output book, in place of the English a
// finished run left there.
//
// THE FILE HAS MIXED LINE ENDINGS and that is not a defect: the model returns
// "\n" inside a piece, and TranslationJob.Tidy appends Environment.NewLine
// twice at the end of each, so the CRLFs mark the piece boundaries and nothing
// else. Splitting on CRLF gives 297 blocks for a 600 kB book. So: split on
// '\n', leave every '\r' attached to the line it came with, compare with it
// trimmed, and rejoin with '\n' -- which puts back every byte that was there.
//
// The job's own Tidy is run over the new text, because the book uses straight
// quotes throughout and a model will hand back curly ones.
class Splice
{
    static Assembly app;
    static Type T(string n) { return app.GetTypes().First(x => x.Name == n); }
    static object Fld(object o, string n)
    { return o.GetType().GetField(n, BindingFlags.Public | BindingFlags.Instance).GetValue(o); }
    static string Tidy(string s)
    {
        return (string)T("TranslationJob").GetMethod("Tidy", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { s });
    }
    static string Key(string s) { return s.TrimEnd('\r').Trim(); }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        app = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Nemoviz Book Reader.exe"));
        string english = args[0], bookIni = args[1], target = args[2];

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

        string all = File.ReadAllText(target, Encoding.UTF8);
        if (!File.Exists(target + ".bak")) File.WriteAllText(target + ".bak", all, new UTF8Encoding(true));
        var lines = all.Split('\n').ToList();
        Console.WriteLine("output book: {0} characters, {1} lines", all.Length, lines.Count);

        foreach (string a in args.Skip(3))
        {
            int w = int.Parse(a);
            string src = (string)Fld(list[w - 1], "Text");
            var srcLines = Tidy(src).Split('\n').Select(Key).Where(x => x.Length > 0).ToList();
            string first = srcLines.First(), last = srcLines.Last();

            int a0 = lines.FindIndex(x => Key(x) == first);
            int a1 = a0 < 0 ? -1 : lines.FindIndex(a0, x => Key(x) == last);
            if (a0 < 0 || a1 < a0) { Console.WriteLine("piece {0}: NOT FOUND ({1}/{2})", w, a0, a1); continue; }

            // Everything between the two ends must be the English we are replacing
            // and nothing else -- check the count before touching the file.
            int span = a1 - a0 + 1;
            int nonEmpty = 0;
            for (int i = a0; i <= a1; i++) if (Key(lines[i]).Length > 0) nonEmpty++;
            Console.WriteLine("piece {0}: lines {1}..{2}  ({3} lines, {4} non-empty; source has {5})",
                w, a0 + 1, a1 + 1, span, nonEmpty, srcLines.Count);
            if (nonEmpty != srcLines.Count)
            { Console.WriteLine("   *** the span does not match the source piece, left alone"); continue; }

            string hr = Tidy(File.ReadAllText("piece" + w + ".hr.txt", Encoding.UTF8));
            var hrLines = hr.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
            while (hrLines.Count > 0 && hrLines[hrLines.Count - 1].Trim().Length == 0) hrLines.RemoveAt(hrLines.Count - 1);

            lines.RemoveRange(a0, span);
            lines.InsertRange(a0, hrLines);
            Console.WriteLine("   replaced with {0} lines of Croatian", hrLines.Count);
        }

        string outText = string.Join("\n", lines);
        File.WriteAllText(target, outText, new UTF8Encoding(true));
        Console.WriteLine();
        Console.WriteLine("written {0} characters (was {1}, delta {2})", outText.Length, all.Length, outText.Length - all.Length);
    }
}
