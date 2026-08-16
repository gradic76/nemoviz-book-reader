using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Azure AI Speech voices: the credential, the catalogue and one WAV.
    /// Deliberately the same shape as <see cref="GoogleCloudVoices"/>, so the two
    /// clouds read alike and neither becomes the special case.
    ///
    /// <para><b>NO REGION, and that took checking.</b> Gordan asked for the
    /// region to be hard-coded to "global" so the dialog needs only a key, on the
    /// strength of <c>AzureProvision</c> having done exactly that. That is true of
    /// <b>Translator</b> — a global Translator resource needs no region header —
    /// and it is NOT true of Speech, which is a different service: its synthesis
    /// endpoint is regional, <c>https://&lt;region&gt;.tts.speech.microsoft.com</c>,
    /// with a published table of some thirty-five of them and no global entry.
    /// Carrying the claim across would have shipped something that cannot
    /// work.</para>
    ///
    /// <para><b>What gives him what he actually wanted</b> is the other endpoint
    /// form Microsoft documents, which carries the RESOURCE NAME instead of a
    /// region — <c>https://&lt;resource&gt;.cognitiveservices.azure.com</c> — and
    /// which the docs say accepts <c>Ocp-Apim-Subscription-Key</c> with the plain
    /// key: *"which works with all endpoint formats"*. So there is no token
    /// dance, no region, and no drop-down of thirty-five entries. The second
    /// field is a name the reader copies off the same page as the key — and if
    /// <c>AzureProvision</c> is ever extended from Translator to Speech, NBR
    /// creates the resource and fills that field itself, with the code
    /// unchanged.</para>
    ///
    /// <para><b>Two things to know before offering these voices.</b> The free
    /// allowance is about half a million characters a month — roughly ONE book,
    /// against Google's two to nine — so this is a second opinion for a language
    /// Google reads poorly, not the workhorse. And Azure's <c>hr-HR</c> carries
    /// no custom pronunciations, so §8j's speech dictionary works less well there
    /// than with a local voice. Gordan knows both and asked for it anyway, for
    /// English and the other languages, where he rates it highly.</para>
    /// </summary>
    internal static class AzureVoices
    {
        /// <summary>Where the key lives, and separately the resource name. Two
        /// entries rather than one packed string: they are two different things,
        /// one is secret and one is not, and a reader who mistypes the name
        /// should not be told their key is wrong.</summary>
        public const string CredentialId = "azure-tts";
        public const string ResourceId = "azure-tts-resource";

        public const string Vendor = "Azure Speech";

        /// <summary>The tag that makes a display name unmistakably ours. See
        /// <see cref="DisplayName"/> for why it is a word and not a bracket.</summary>
        public const string Tag = "Azure";

        /// <summary>A conservative ceiling on one request's text. Azure caps the
        /// AUDIO at ten minutes rather than the input at a byte count, so this is
        /// not a documented limit being obeyed — it is a sentence-sized request
        /// staying sentence-sized, the same discipline Google's 4800 enforces.</summary>
        public const int MaxRequestBytes = 4800;

        // Nonstreaming RIFF: a real WAV with a header, which is what every
        // consumer here already takes -- SapiWavPlayer plays one and the speech
        // cache encodes one. 24 kHz because that is the rate Azure's standard
        // neural models are built at; asking for more only upsamples.
        private const string OutputFormat = "riff-24khz-16bit-mono-pcm";

        public static bool Have
        {
            get { return TranslationKeys.Has(CredentialId) && TranslationKeys.Has(ResourceId); }
        }

        public static string Resource
        {
            get { return (TranslationKeys.Get(ResourceId) ?? "").Trim(); }
        }

        private static string Key { get { return (TranslationKeys.Get(CredentialId) ?? "").Trim(); } }

        private static string Host { get { return Resource + ".cognitiveservices.azure.com"; } }

        /// <summary>Stores the pair and says what is wrong, or null if it works.
        /// Checking means ASKING the service for its voice list — a key is not a
        /// string we can validate by looking at it.</summary>
        public static string Save(string resource, string key)
        {
            resource = (resource ?? "").Trim();
            key = (key ?? "").Trim();
            // A reader who pastes the whole endpoint gets what they meant.
            if (resource.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                resource = resource.Substring(8);
            int dot = resource.IndexOf('.');
            if (dot > 0) resource = resource.Substring(0, dot);

            if (resource.Length == 0) return Localization.T("Settings.Azure.NoResource");
            if (key.Length == 0) return Localization.T("Settings.Azure.NoKey");

            string oldR = TranslationKeys.Get(ResourceId), oldK = TranslationKeys.Get(CredentialId);
            TranslationKeys.Set(ResourceId, resource);
            TranslationKeys.Set(CredentialId, key);
            cache = null;
            if (Refresh()) return null;

            TranslationKeys.Set(ResourceId, oldR);
            TranslationKeys.Set(CredentialId, oldK);
            cache = null;
            return Localization.T("Settings.Azure.Refused");
        }

        public static void Forget()
        {
            TranslationKeys.Set(ResourceId, null);
            TranslationKeys.Set(CredentialId, null);
            cache = null;
            try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        }

        // ── the catalogue ─────────────────────────────────────────────────────

        private static readonly string CachePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "azure-voices.txt");

        private static List<(string Short, string Display, string Language)> cache;

        public static List<(string Short, string Display, string Language)> Voices()
        {
            if (cache != null) return cache;
            cache = ReadCache();
            if (cache.Count == 0 && Have) { cache = Fetch(); if (cache.Count > 0) WriteCache(cache); }
            return cache;
        }

        public static bool Refresh()
        {
            var got = Fetch();
            if (got.Count == 0) return false;
            cache = got;
            WriteCache(got);
            return true;
        }

        private static List<(string, string, string)> Fetch()
        {
            var list = new List<(string, string, string)>();
            try
            {
                if (!Have) return list;
                string reply = Get("https://" + Host + "/tts/cognitiveservices/voices/list");
                if (reply == null) return list;

                var arr = Json.Parse(reply) as List<object>;
                if (arr == null) return list;
                foreach (object v in arr)
                {
                    // GA neural only. The preview voices come and go by region and
                    // a catalogue that offers one which then vanishes is worse
                    // than a shorter catalogue; the standard ones are what the
                    // free allowance is for anyway.
                    if (!string.Equals(Json.PathString(v, "VoiceType"), "Neural", StringComparison.Ordinal)) continue;
                    if (!string.Equals(Json.PathString(v, "Status"), "GA", StringComparison.Ordinal)) continue;

                    string shortName = Json.PathString(v, "ShortName");
                    string locale = Json.PathString(v, "Locale");
                    string display = Json.PathString(v, "DisplayName");
                    if (string.IsNullOrEmpty(shortName) || string.IsNullOrEmpty(locale)) continue;
                    if (string.IsNullOrEmpty(display)) display = shortName;
                    list.Add((shortName, display, locale));
                }
            }
            catch { }
            return list;
        }

        private static List<(string, string, string)> ReadCache()
        {
            var list = new List<(string, string, string)>();
            try
            {
                if (!File.Exists(CachePath)) return list;
                foreach (string line in File.ReadAllLines(CachePath, Encoding.UTF8))
                {
                    string[] p = line.Split('\t');
                    if (p.Length == 3 && p[0].Length > 0) list.Add((p[0], p[1], p[2]));
                }
            }
            catch { }
            return list;
        }

        private static void WriteCache(List<(string Short, string Display, string Language)> list)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var v in list)
                    sb.Append(v.Short).Append('\t').Append(v.Display).Append('\t')
                      .Append(v.Language).Append("\r\n");
                File.WriteAllText(CachePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ── naming ────────────────────────────────────────────────────────────

        /// <summary>What NBR calls this voice: <c>Gabrijela (hr-HR, Azure)</c>.
        ///
        /// <para><b>The vendor word is load-bearing, not decoration.</b> Google's
        /// names are <c>Speaker (language)</c>, and an Azure voice written the
        /// same way would be indistinguishable — worse, <see
        /// cref="GoogleCloudVoices.Split"/> would parse it and confidently build a
        /// Google id out of an Azure speaker. The tag makes each vendor's names
        /// its own, and it earns its place for the reader too: these are two
        /// different accounts with two different allowances, and which one a book
        /// is spending is worth hearing.</para></summary>
        public static string DisplayName(string display, string language)
        {
            return display + " (" + language + ", " + Tag + ")";
        }

        /// <summary>The locale and the speaker back out of a display name, or
        /// false if this is not one of ours.</summary>
        public static bool Split(string displayName, out string language, out string speaker)
        {
            language = null;
            speaker = null;
            if (string.IsNullOrEmpty(displayName)) return false;
            string ending = ", " + Tag + ")";
            if (!displayName.EndsWith(ending, StringComparison.Ordinal)) return false;
            int open = displayName.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0) return false;
            speaker = displayName.Substring(0, open);
            int start = open + 2;
            int len = displayName.Length - ending.Length - start;
            if (len <= 0) return false;
            language = displayName.Substring(start, len);
            return speaker.Length > 0;
        }

        public static bool IsOne(string displayName)
        {
            string l, s;
            return Split(displayName, out l, out s);
        }

        /// <summary>The Azure id for a display name, looked up in the catalogue
        /// rather than rebuilt from the parts.
        ///
        /// <para>Google can rebuild its id because Chirp 3 HD ids have one fixed
        /// shape. Azure's do not — <c>hr-HR-GabrijelaNeural</c> beside
        /// <c>en-US-JennyMultilingualNeural</c> — so guessing the suffix would be
        /// right most of the time, which is the worst kind of wrong.</para></summary>
        public static string ShortNameFor(string displayName)
        {
            string lang, speaker;
            if (!Split(displayName, out lang, out speaker)) return null;
            foreach (var v in Voices())
                if (v.Language == lang && v.Display == speaker) return v.Short;
            return null;
        }

        // ── synthesis ─────────────────────────────────────────────────────────

        /// <summary>One passage as a WAV, or null. Never throws at the reader:
        /// the backend treats null as "this utterance produced nothing" and
        /// reading carries on.</summary>
        public static byte[] Synthesize(string text, string shortName, string language,
                                        double speed, double volumeDb)
        {
            try
            {
                if (!Have || string.IsNullOrEmpty(shortName) || string.IsNullOrEmpty(text)) return null;

                var ssml = new StringBuilder();
                ssml.Append("<speak version='1.0' xml:lang='").Append(Esc(language)).Append("'>")
                    .Append("<voice name='").Append(Esc(shortName)).Append("'>");
                bool prosody = Math.Abs(speed - 1.0) > 0.001 || Math.Abs(volumeDb) > 0.001;
                if (prosody)
                    ssml.Append("<prosody rate='")
                        .Append(((speed - 1.0) * 100).ToString("0.#", CultureInfo.InvariantCulture))
                        .Append("%' volume='")
                        .Append(volumeDb.ToString("0.#", CultureInfo.InvariantCulture))
                        .Append("dB'>");
                ssml.Append(Esc(text));
                if (prosody) ssml.Append("</prosody>");
                ssml.Append("</voice></speak>");

                return PostAudio("https://" + Host + "/cognitiveservices/v1",
                                 Encoding.UTF8.GetBytes(ssml.ToString()));
            }
            catch { return null; }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // ── http ──────────────────────────────────────────────────────────────

        private static string Get(string url)
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "GET";
                r.Timeout = 60000;
                r.Headers.Add("Ocp-Apim-Subscription-Key", Key);
                using (WebResponse resp = r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private static byte[] PostAudio(string url, byte[] body)
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST";
                r.ContentType = "application/ssml+xml";
                r.Timeout = 120000;
                r.ReadWriteTimeout = 120000;
                r.Headers.Add("Ocp-Apim-Subscription-Key", Key);
                r.Headers.Add("X-Microsoft-OutputFormat", OutputFormat);
                // Required by the service, and refused if absent.
                r.UserAgent = "NemovizBookReader";
                r.ContentLength = body.Length;
                using (Stream s = r.GetRequestStream()) s.Write(body, 0, body.Length);
                using (WebResponse resp = r.GetResponse())
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    byte[] wav = ms.ToArray();
                    return wav.Length > 44 ? wav : null;
                }
            }
            catch { return null; }
        }
    }
}
