using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Which service a translation goes to. Two shapes, not four — see
    /// <see cref="TranslationEngines"/>.</summary>
    internal enum EngineKind
    {
        /// <summary>OpenAI-compatible <c>/chat/completions</c>: DeepSeek, Kimi, and
        /// Gemini's own compatibility endpoint all speak it.</summary>
        OpenAiCompatible,
        /// <summary>Gemini's native <c>:generateContent</c>. We use this rather than
        /// its OpenAI-compatible door for one reason: the compatibility shape cannot
        /// carry <c>safetySettings</c>, and switching the content filters off is a
        /// decision this feature depends on.</summary>
        Gemini
    }

    /// <summary>One translation service, as far as the transport is concerned.</summary>
    internal sealed class TranslationEngine
    {
        public string Id;            // stable, used as the key-store key
        public string NameKey;       // en.lang key for the human name
        public EngineKind Kind;
        public string Endpoint;      // chat/completions, or the Gemini base
        public string Model;

        public string DisplayName { get { return Localization.T(NameKey); } }
        public bool HasKey { get { return TranslationKeys.Has(Id); } }
    }

    /// <summary>
    /// The services NBR will translate through, and why these.
    ///
    /// <para><b>Gemini is the choice, DeepSeek is the fallback</b> — settled by
    /// Gordan's ear on 2026-08-14 from a blind three-way comparison on a real
    /// novel. He picked Gemini on FLOW: individual phrases can be fixed with the
    /// pronunciation dictionary, the way sentences run into one another cannot.
    /// DeepSeek is there for the passages Gemini refuses to translate, "kad ovaj
    /// odluči štititi korisnike same od sebe" — the two filter different things, so
    /// they cover each other.</para>
    ///
    /// <para><b>Kimi was dropped</b> (dearer than DeepSeek, trained with the same
    /// English/Chinese weighting so its weakness is the same rather than
    /// complementary), and <b>Azure is deferred</b>: its one virtue is that it never
    /// refuses, and DeepSeek now holds that job. It comes in if a real book turns up
    /// that both LLMs refuse — which the feature measures for itself, by reporting
    /// how many passages did not pass.</para>
    ///
    /// <para><b>DeepL is not here at all and cannot be: it has no Croatian.</b>
    /// Nor Serbian, Bosnian or Montenegrin.</para>
    /// </summary>
    internal static class TranslationEngines
    {
        public const string Gemini = "gemini";
        public const string DeepSeek = "deepseek";

        /// <summary><b>Model names are settings in waiting, never constants to rely
        /// on.</b> Gemini 2.5 Flash-Lite retires 2026-10-16 and Moonshot's old
        /// series went on 2026-08-31; a name compiled in means the feature stops
        /// working on a date nobody is watching. These are the defaults until
        /// Settings offers the choice.</summary>
        public static readonly List<TranslationEngine> All = new List<TranslationEngine>
        {
            new TranslationEngine
            {
                Id = Gemini,
                NameKey = "Settings.Translate.Engine.Gemini",
                Kind = EngineKind.Gemini,
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/",
                Model = "gemini-3.1-flash-lite"
            },
            new TranslationEngine
            {
                Id = DeepSeek,
                NameKey = "Settings.Translate.Engine.DeepSeek",
                Kind = EngineKind.OpenAiCompatible,
                Endpoint = "https://api.deepseek.com/chat/completions",
                Model = "deepseek-v4-flash"
            }
        };

        public static TranslationEngine ById(string id)
        {
            foreach (var e in All)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Only the services that actually have a key. <b>Gordan's
        /// correction to me (2026-08-14): four services does not mean four keys.</b>
        /// The reader is offered what is configured and nothing else — the same rule
        /// by which only installed voices are offered, and by which the OCR language
        /// is asked only when there is more than one. One key means no question at
        /// all.</summary>
        public static List<TranslationEngine> Configured()
        {
            var list = new List<TranslationEngine>();
            foreach (var e in All) if (e.HasKey) list.Add(e);
            return list;
        }
    }

    /// <summary>What came back. <see cref="Error"/> is already fit to show a
    /// reader; <see cref="Detail"/> is the service's own words, for a diagnosis.</summary>
    internal sealed class TranslationResult
    {
        public bool Ok;
        public string Text;
        public string Error;
        public string Detail;
        public int Status;
    }

    /// <summary>
    /// The transport, and nothing else. It connects, authenticates and carries text
    /// both ways; it holds no opinion about chunking, prompts, glossaries or what to
    /// do with a refusal. Those rules come next and belong above this layer.
    ///
    /// <para><b>No package, no SDK, no new assembly.</b> All of it is
    /// <see cref="HttpWebRequest"/> plus <see cref="Json"/> — the same outcome as
    /// OCR and the OneCore voices, and zero bytes on the installer.</para>
    ///
    /// <para>Everything odd in here was measured on 2026-08-14 rather than guessed;
    /// each is commented where it sits.</para>
    /// </summary>
    internal static class Translator
    {
        /// <summary>Long, and it has to be. A whole chunk of a book can take a
        /// minute to generate, and .NET's default of 100 s would cut off perfectly
        /// good answers — measured: 48 s for 8 000 characters on the slower service,
        /// and a chunk will be larger than that.</summary>
        private const int TimeoutMs = 300000;

        /// <summary>A short call used to prove a key works. Kept tiny on purpose:
        /// it costs a fraction of a cent and answers in about a second.</summary>
        public static TranslationResult TestKey(TranslationEngine engine, string key)
        {
            if (engine == null) return Fail("Settings.Translate.Test.NoEngine");
            if (string.IsNullOrWhiteSpace(key)) return Fail("Settings.Translate.Test.NoKey");
            // Asking for a translation rather than "say OK" checks the one thing a
            // reader cares about: that this key can actually translate. A key that
            // authenticates but is barred from the model would otherwise pass.
            return Send(engine, key,
                        "Translate from English into Croatian. Output only the translation.",
                        "Good evening.", 64);
        }

        /// <summary>Sends one system instruction and one piece of text, and returns
        /// what came back. The whole surface the layers above need.</summary>
        public static TranslationResult Send(TranslationEngine engine, string key,
                                             string system, string user, int maxTokens)
        {
            if (engine == null) return Fail("Settings.Translate.Test.NoEngine");
            if (string.IsNullOrEmpty(key)) key = TranslationKeys.Get(engine.Id);
            if (string.IsNullOrEmpty(key)) return Fail("Settings.Translate.Test.NoKey");

            string url, body;
            var headers = new Dictionary<string, string>();

            if (engine.Kind == EngineKind.Gemini)
            {
                url = engine.Endpoint + engine.Model + ":generateContent";
                // The key goes in a HEADER, not ?key=, so it cannot come back inside
                // an error message or a redirect.
                headers["x-goog-api-key"] = key;
                body = GeminiBody(system, user, maxTokens);
            }
            else
            {
                url = engine.Endpoint;
                headers["Authorization"] = "Bearer " + key;
                body = OpenAiBody(engine.Model, system, user, maxTokens);
            }

            string raw;
            int status;
            string transport = Post(url, headers, body, out raw, out status);
            if (transport != null)
                return new TranslationResult { Ok = false, Status = status,
                                               Error = Localization.T("Settings.Translate.Test.NoNetwork"),
                                               Detail = transport };

            object json = Json.Parse(raw);

            if (status < 200 || status >= 300)
            {
                // Read the BODY, not just the code. Twice on 2026-08-14 the status
                // said only "403" or "429" while the body named the exact cause —
                // API_KEY_SERVICE_BLOCKED (the key was restricted to another API)
                // and "prepayment credits are depleted". Without the body both look
                // like "it does not work".
                string msg = Json.PathString(json, "error", "message")
                             ?? Json.PathString(json, "error", "0", "message")
                             ?? Truncate(raw, 400);
                return new TranslationResult { Ok = false, Status = status,
                                               Error = Localization.T("Settings.Translate.Test.Refused", status),
                                               Detail = msg };
            }

            string text = engine.Kind == EngineKind.Gemini
                ? Json.PathString(json, "candidates", "0", "content", "parts", "0", "text")
                : Json.PathString(json, "choices", "0", "message", "content");

            if (string.IsNullOrEmpty(text))
            {
                // An empty answer with a 200 is a real state and has two known
                // causes, both met on 2026-08-14. Gemini blocks at the prompt and
                // says so in promptFeedback; DeepSeek's V4 models are REASONING
                // models and will spend the entire output allowance thinking, then
                // return content of length zero with no error at all. The caller
                // gets told which, because the remedies are opposite.
                string blocked = Json.PathString(json, "promptFeedback", "blockReason");
                string finish = Json.PathString(json, "candidates", "0", "finishReason")
                                ?? Json.PathString(json, "choices", "0", "finish_reason");
                return new TranslationResult { Ok = false, Status = status,
                                               Error = Localization.T("Settings.Translate.Test.Empty"),
                                               Detail = blocked ?? finish ?? "empty" };
            }

            return new TranslationResult { Ok = true, Status = status, Text = text };
        }

        // ---- request bodies -----------------------------------------------------

        private static string GeminiBody(string system, string user, int maxTokens)
        {
            // ALL FOUR ADJUSTABLE CATEGORIES OFF, and that is a decision rather than
            // a default. Measured 2026-08-14: the setting is accepted on the FREE
            // tier, so it costs nothing. What it buys is that a refusal which still
            // gets through has hit Google's non-adjustable core — and a novel is
            // refused for perfectly ordinary reasons otherwise, which leaves a
            // reader with holes in a book and no idea why. What we cannot switch off
            // is stated in the hint instead of being silently suffered.
            //
            // Not proven: that the setting takes EFFECT. It is accepted; whether it
            // changes an outcome needs a book that is refused by default, and we do
            // not have one yet.
            string[] cats = { "HARM_CATEGORY_HARASSMENT", "HARM_CATEGORY_HATE_SPEECH",
                              "HARM_CATEGORY_SEXUALLY_EXPLICIT", "HARM_CATEGORY_DANGEROUS_CONTENT" };
            var safety = new StringBuilder();
            for (int i = 0; i < cats.Length; i++)
            {
                if (i > 0) safety.Append(',');
                safety.Append("{\"category\":").Append(Json.Str(cats[i]))
                      .Append(",\"threshold\":\"OFF\"}");
            }

            var sb = new StringBuilder();
            sb.Append("{\"systemInstruction\":{\"parts\":[{\"text\":").Append(Json.Str(system)).Append("}]},");
            sb.Append("\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":").Append(Json.Str(user)).Append("}]}],");
            sb.Append("\"safetySettings\":[").Append(safety).Append("],");
            sb.Append("\"generationConfig\":{\"temperature\":0.3,\"maxOutputTokens\":")
              .Append(maxTokens.ToString(CultureInfo.InvariantCulture)).Append("}}");
            return sb.ToString();
        }

        private static string OpenAiBody(string model, string system, string user, int maxTokens)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(Json.Str(model)).Append(',');
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":").Append(Json.Str(system)).Append("},");
            sb.Append("{\"role\":\"user\",\"content\":").Append(Json.Str(user)).Append("}],");
            sb.Append("\"temperature\":0.3,");
            // REASONING OFF, and this one is not an optimisation — without it the
            // service returns NOTHING. DeepSeek's V4 models think in a separate
            // `reasoning_content` field and bill that thinking as output: measured,
            // a 30-character sentence cost 124 thinking tokens against 12 of
            // translation, and a book-sized passage consumed the entire output
            // allowance before writing a word, returning an empty string and no
            // error. It is also the difference between paying once and paying
            // several times over.
            sb.Append("\"reasoning_effort\":\"none\",");
            sb.Append("\"max_tokens\":").Append(maxTokens.ToString(CultureInfo.InvariantCulture)).Append('}');
            return sb.ToString();
        }

        // ---- the wire -----------------------------------------------------------

        /// <summary>POSTs and hands back the body whatever the status. Returns null
        /// on success-or-HTTP-error, or a transport message when the request never
        /// reached a server at all — the two are different things to a reader: one
        /// is "the service said no", the other is "you are not online".</summary>
        private static string Post(string url, Dictionary<string, string> headers,
                                   string body, out string responseText, out int status)
        {
            responseText = "";
            status = 0;
            try
            {
                // Said explicitly rather than left to the framework's default. On an
                // older or oddly-configured Windows the default has been the reason
                // for handshakes that fail with nothing useful to show for it.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                // The system proxy is honoured rather than bypassed; a reader behind
                // one has no other way through.
                req.Proxy = WebRequest.DefaultWebProxy;
                foreach (var h in headers) req.Headers.Add(h.Key, h.Value);

                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.ContentLength = payload.Length;
                using (Stream s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    responseText = ReadAll(resp);
                }
                return null;
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    // An HTTP error still has a body, and the body is the diagnosis.
                    status = (int)resp.StatusCode;
                    try { responseText = ReadAll(resp); } catch { }
                    return null;
                }
                return ex.Message;      // no server was reached
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            {
                if (s == null) return "";
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        private static TranslationResult Fail(string key)
        {
            return new TranslationResult { Ok = false, Error = Localization.T(key) };
        }
    }
}
