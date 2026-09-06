using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

// What models do the three services ACTUALLY offer this account today?
//
// Read from each service's own model list rather than from a documentation
// page: a docs page is somebody's summary and can lag, while /models is what
// the key can really call. Free, read-only, and it uses the reader's own stored
// credentials through NBR's TranslationKeys -- this probe never holds a copy.
//
// Copy it into bin\x64\Debug, build and run it there -- it loads "Nemoviz Book
// Reader.exe" from beside itself to reach TranslationKeys:
//
//   csc -nologo -platform:x64 -out:model-list.exe model-list.cs
//   model-list.exe
//
// A model on the LIST is not a model that answers: the same listing carries
// embeddings, video and music models that would refuse a translation request.
// Call a candidate for one real sentence before adding it to Translator.cs.
class ModelProbe
{
    static Assembly app;

    static string Key(string engineId)
    {
        Type tk = app.GetTypes().First(x => x.Name == "TranslationKeys");
        return (string)tk.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
                         .Invoke(null, new object[] { engineId });
    }

    static string Get(string url, string header, string value)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET";
        req.Timeout = 60000;
        req.Headers[header] = value;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }

    // Every id in the payload, however the service happens to shape its JSON.
    static List<string> Ids(string json, string field)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(json, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\""))
        {
            string id = m.Groups[1].Value;
            if (id.StartsWith("models/")) id = id.Substring(7);
            if (!found.Contains(id)) found.Add(id);
        }
        // A service that answers with nothing is a shape we have not met: show it.
        if (found.Count == 0)
            Console.WriteLine("   raw: " + (json.Length > 400 ? json.Substring(0, 400) : json));
        return found;
    }

    static void Show(string title, Func<List<string>> f)
    {
        Console.WriteLine("=== " + title + " ===");
        try
        {
            var ids = f();
            foreach (string id in ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine("   " + id);
            Console.WriteLine("   (" + ids.Count + ")");
        }
        catch (WebException e)
        {
            string body = "";
            try { using (var r = new StreamReader(e.Response.GetResponseStream())) body = r.ReadToEnd(); }
            catch { }
            Console.WriteLine("   FAILED: " + e.Message + " " + (body.Length > 300 ? body.Substring(0, 300) : body));
        }
        catch (Exception e) { Console.WriteLine("   FAILED: " + e.Message); }
        Console.WriteLine();
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        app = Assembly.LoadFrom(Path.Combine(dir, "Nemoviz Book Reader.exe"));

        Show("OpenAI", () => Ids(Get("https://api.openai.com/v1/models",
                                     "Authorization", "Bearer " + Key("openai")), "id"));

        Show("DeepSeek", () => Ids(Get("https://api.deepseek.com/models",
                                       "Authorization", "Bearer " + Key("deepseek")), "id"));

        // Gemini names its models "models/<id>"; the field is "name".
        Show("Gemini", () => Ids(Get("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200",
                                     "x-goog-api-key", Key("gemini")), "name"));
    }
}
