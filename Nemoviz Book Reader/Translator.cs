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
        Gemini,
        /// <summary>Azure Translator v3 — not a model you talk to, but a
        /// translator. Text in, text out, no prompt and no model name.
        ///
        /// <para><b>That is its whole value and its whole limitation, and the two
        /// are the same fact.</b> It never refuses, because there is nobody in
        /// there to refuse — which is why it is the engine of last resort for a
        /// passage the language models decline. But for the same reason it cannot
        /// be TOLD anything: the standing prompt, the reader's notes, the narrator's
        /// gender, the level of address between two characters — none of it
        /// reaches it. A passage rescued here has those decided by nothing at
        /// all.</para></summary>
        AzureTranslator
    }

    /// <summary>One translation service, as far as the transport is concerned.</summary>
    internal sealed class TranslationEngine
    {
        public string Id;            // stable, identifies this stop in the chain
        public string NameKey;       // en.lang key for the human name
        public EngineKind Kind;
        public string Endpoint;      // chat/completions, or the Gemini base
        public string Model;

        /// <summary>Which stored key this engine authenticates with, when that is
        /// not its own id. <b>Two stops can share one account</b>: DeepSeek's cheap
        /// and dear models are one subscription and one key, so asking the reader
        /// for a second would be asking for the same string twice.</summary>
        public string KeyId;

        /// <summary>How many times this stop is asked before the chain moves on.
        ///
        /// <para>Three for a language model, because a refusal is a throw of the
        /// dice — measured on seven passages a novel had been refused over, four of
        /// the seven went through within four asks, and what does not clear in four
        /// never clears, being systematic rather than moody. <b>One for Azure</b>,
        /// which has nobody in it to refuse: a failure there is the network, and the
        /// transport already retries that itself.</para></summary>
        public int Attempts = 3;

        /// <summary>True for the stop that translates without being able to be told
        /// anything — see <see cref="EngineKind.AzureTranslator"/>. A passage
        /// rescued here is MARKED IN THE BOOK, because the two faults it makes are
        /// invisible to every check we have.</summary>
        public bool LastResort;

        public string DisplayName { get { return Localization.T(NameKey); } }
        public string KeyName { get { return string.IsNullOrEmpty(KeyId) ? Id : KeyId; } }
        public bool HasKey { get { return TranslationKeys.Has(KeyName); } }
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
    /// <para><b>Kimi was dropped</b> — dearer than DeepSeek and trained with the
    /// same English/Chinese weighting, so its weakness is the same rather than
    /// complementary.</para>
    ///
    /// <para><b>Azure was deferred and then a measurement brought it back.</b> It
    /// was to enter "if a real book turns up that both language models refuse —
    /// which the feature measures for itself". The feature measured it: Gemini
    /// declines roughly a sixth of an ordinary published novel, and not for its
    /// content — the first passage refused is the copyright page, the second is a
    /// widow talking about a boy who wants to go to school. It looks like a model
    /// declining to reproduce a book it recognises, and NO setting prevents it
    /// (measured four ways, from no safetySettings through to everything OFF, all
    /// identical). DeepSeek covered those passages this time, but it has filters of
    /// its own, so the case for an engine that cannot refuse anything is no longer
    /// hypothetical.</para>
    ///
    /// <para><b>DeepL is not here at all and cannot be: it has no Croatian.</b>
    /// Nor Serbian, Bosnian or Montenegrin.</para>
    /// </summary>
    internal static class TranslationEngines
    {
        public const string Gemini = "gemini";
        public const string DeepSeek = "deepseek";
        public const string DeepSeekPro = "deepseek-pro";
        public const string Azure = "azure";

        /// <summary>Azure keeps a second value beside its key: the region a
        /// regional resource was made in. A single-service GLOBAL resource needs
        /// none, which is why the setup guidance says to choose Global — it makes
        /// Azure look like every other service, one field and done.</summary>
        public const string AzureRegion = "azure-region";

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
            },
            // THE DEARER MODEL IS ITS OWN STOP, not a different service. It shares
            // DeepSeek's account and key, so it costs the reader no second signup
            // and appears in no key dialog of its own.
            //
            // Why it earns a place: measured on a French chapter (2026-08-15) it
            // held the narrator's gender and the formal register as cleanly as
            // Gemini, where the cheap model slipped once on gender. It is also
            // several times the price, which is exactly why it stands AFTER the
            // cheap one — it is asked only for the passages that have already
            // defeated two attempts at a third of the cost.
            new TranslationEngine
            {
                Id = DeepSeekPro,
                NameKey = "Settings.Translate.Engine.DeepSeekPro",
                Kind = EngineKind.OpenAiCompatible,
                Endpoint = "https://api.deepseek.com/chat/completions",
                Model = "deepseek-v4-pro",
                KeyId = DeepSeek
            },
            // Deferred once, and then a measurement brought it back: Gemini
            // refuses roughly a sixth of an ordinary published novel — not for
            // its content but, by every sign, because it recognises the book —
            // and no setting prevents that. DeepSeek covered those passages, but
            // it has filters of its own. Azure is the only one of the three that
            // cannot refuse anything, so it belongs at the end of the chain.
            new TranslationEngine
            {
                Id = Azure,
                NameKey = "Settings.Translate.Engine.Azure",
                Kind = EngineKind.AzureTranslator,
                Endpoint = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0",
                Model = "",      // there is no model to choose
                Attempts = 1,
                LastResort = true
            }
        };

        /// <summary>The order the stops are tried in, starting from whichever the
        /// reader chose. Only services with a key appear.
        ///
        /// <para><b>The reader picks ONE engine and gets a chain</b>, which is what
        /// the help text on this control has to explain. Offering four separate
        /// choices would be asking someone to design a retry policy; what they have
        /// an opinion about is which translation they would rather read.</para>
        ///
        /// <para><b>Azure is always last and never anywhere else</b>, and that was
        /// measured rather than assumed. On a French chapter it narrated a girl's
        /// first-person story in the masculine eight times out of nine, and rendered
        /// a formal confrontation between two adults as if they were friends —
        /// nine <i>vous</i> in the source, no polite form in the output. Both faults
        /// are set wrongly for a whole passage rather than clumsily in a phrase, and
        /// the second cannot be checked for at all, since a real book carries both
        /// registers between different pairs of characters.</para>
        ///
        /// <para><b>But it belongs in the chain, and the comparison that settles
        /// that is not against Gemini.</b> At the last stop the choice is Azure
        /// against leaving the passage in the source language — and an English
        /// paragraph inside a Croatian book is read aloud by a Croatian voice under
        /// Croatian rules, which is noise. A readable sentence with the narrator's
        /// sex wrong still carries the plot; the other carries nothing.</para></summary>
        public static List<TranslationEngine> Chain(TranslationEngine primary)
        {
            // The preference order among the rest: cheapest capable first, the
            // dearer model of the same family after it, and the one that cannot
            // refuse at the very end.
            string[] order = { Gemini, DeepSeek, DeepSeekPro, Azure };
            var chain = new List<TranslationEngine>();
            if (primary != null && primary.HasKey) chain.Add(primary);
            foreach (string id in order)
            {
                var e = ById(id);
                if (e == null || !e.HasKey) continue;
                if (primary != null && string.Equals(e.Id, primary.Id, StringComparison.OrdinalIgnoreCase)) continue;
                chain.Add(e);
            }
            return chain;
        }

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
        public static TranslationResult TestKey(TranslationEngine engine, string key, string azureRegion = null)
        {
            if (engine == null) return Fail("Settings.Translate.Test.NoEngine");
            if (string.IsNullOrWhiteSpace(key)) return Fail("Settings.Translate.Test.NoKey");
            // Asking for a translation rather than "say OK" checks the one thing a
            // reader cares about: that this key can actually translate. A key that
            // authenticates but is barred from the model would otherwise pass.
            return Send(engine, key,
                        "Translate from English into Croatian. Output only the translation.",
                        "Good evening.", 64, "en", "hr", azureRegion);
        }

        /// <summary>Sends one system instruction and one piece of text, and returns
        /// what came back. The whole surface the layers above need.</summary>
        public static TranslationResult Send(TranslationEngine engine, string key,
                                             string system, string user, int maxTokens,
                                             string sourceLang = null, string targetLang = null,
                                             string azureRegion = null)
        {
            if (engine == null) return Fail("Settings.Translate.Test.NoEngine");
            // KeyName, not Id: two stops can share one account, so the dearer
            // DeepSeek model authenticates with the key stored for the cheap one.
            if (string.IsNullOrEmpty(key)) key = TranslationKeys.Get(engine.KeyName);
            if (string.IsNullOrEmpty(key)) return Fail("Settings.Translate.Test.NoKey");

            string url, body;
            var headers = new Dictionary<string, string>();

            if (engine.Kind == EngineKind.AzureTranslator)
            {
                // The languages are in the URL because there is no prompt to put
                // them in. The caller's system instruction is DROPPED here, and
                // that is not an oversight — see EngineKind.AzureTranslator.
                url = engine.Endpoint
                      + (string.IsNullOrEmpty(sourceLang) ? "" : "&from=" + Uri.EscapeDataString(sourceLang))
                      + "&to=" + Uri.EscapeDataString(targetLang ?? "");
                headers["Ocp-Apim-Subscription-Key"] = key;
                // The CALLER's region wins over the stored one. Without this the
                // Check button would test a freshly pasted key against whatever
                // region was saved before it — the same shape of fault as a dialog
                // rebuilding its settings from controls and losing the one value
                // that has no control.
                string region = azureRegion ?? TranslationKeys.Get(TranslationEngines.AzureRegion);
                if (!string.IsNullOrEmpty(region)) headers["Ocp-Apim-Subscription-Region"] = region;
                body = "[{\"Text\":" + Json.Str(user) + "}]";
            }
            else if (engine.Kind == EngineKind.Gemini)
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

            // A RATE LIMIT AND A HICCUP ARE NORMAL STATES OVER A BOOK, NOT FAULTS.
            // A hundred-odd requests in a row will meet one; the free tiers are
            // measured per minute, and a single 429 that ends a translation would
            // be absurd. Backoff is generous rather than eager: these limits are
            // per minute, so waiting is what actually clears them.
            //
            // The service often says how long to wait (Gemini returns retryDelay);
            // that is believed over our own schedule when it is there.
            string raw = "";
            int status = 0;
            string transport = null;
            int[] waits = { 2000, 6000, 20000, 45000 };
            for (int attempt = 0; ; attempt++)
            {
                transport = Post(url, headers, body, out raw, out status);
                bool worthRetrying = transport != null || status == 429 || status == 408 || status >= 500;
                if (!worthRetrying || attempt >= waits.Length) break;

                int wait = waits[attempt];
                object err = Json.Parse(raw);
                string told = Json.PathString(err, "error", "details", "2", "retryDelay")
                              ?? Json.PathString(err, "error", "details", "0", "retryDelay");
                if (!string.IsNullOrEmpty(told) && told.EndsWith("s", StringComparison.Ordinal))
                {
                    double secs;
                    if (double.TryParse(told.Substring(0, told.Length - 1),
                                        NumberStyles.Float, CultureInfo.InvariantCulture, out secs))
                        wait = Math.Max(wait, (int)(secs * 1000) + 500);
                }
                System.Threading.Thread.Sleep(wait);
            }

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

            string text;
            if (engine.Kind == EngineKind.AzureTranslator)
                text = Json.PathString(json, "0", "translations", "0", "text");
            else if (engine.Kind == EngineKind.Gemini)
                text = Json.PathString(json, "candidates", "0", "content", "parts", "0", "text");
            else
                text = Json.PathString(json, "choices", "0", "message", "content");

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
            // All four adjustable categories off. Accepted on the free tier, so it
            // costs nothing — but do NOT expect it to solve refusals, and the
            // measurement that settles this is worth keeping.
            //
            // MEASURED 2026-08-15 on passages a real novel was actually refused
            // over: the same three pieces were sent four ways — no safetySettings
            // at all, BLOCK_ONLY_HIGH, BLOCK_NONE and OFF — and every one came back
            // identically as finishReason PROHIBITED_CONTENT with an empty answer.
            // THE SETTING CHANGES NOTHING FOR THIS KIND OF REFUSAL, because
            // PROHIBITED_CONTENT is Google's non-adjustable layer and not one of
            // the four categories these switches reach.
            //
            // What that leaves unproven, precisely: whether the switches do
            // anything for the four categories they DO cover. We never saw a block
            // of that kind (which would arrive as finishReason SAFETY with
            // safetyRatings attached), so there was nothing to compare.
            //
            // Which is why the fallback engine is load-bearing rather than a
            // nicety: 17 % of a mainstream novel came back refused, and there is no
            // setting that would have prevented it.
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
