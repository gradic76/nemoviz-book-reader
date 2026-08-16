using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Google Cloud Text-to-Speech: the credential, the token, the voice
    /// catalogue and one synthesis call. No SDK, no NuGet package, no bytes on
    /// the installer — the same zero-dependency shape as the translation
    /// engines.
    ///
    /// <para><b>The credential is NOT an API key, and that is measured rather
    /// than assumed.</b> Cloud TTS answers a key with <i>"API keys are not
    /// supported by this API. Expected OAuth2 access token or other
    /// authentication credentials that assert a principal"</i> — both as a
    /// header and as <c>?key=</c>. So NBR's whole paste-a-key story does not
    /// apply here: it takes a <b>service account</b>, a JSON file with a private
    /// key in it, and the token is minted from that.</para>
    ///
    /// <para><b>The file's CONTENTS are stored, never its path.</b> A reader
    /// downloads it once and it lands wherever their browser puts it; keeping a
    /// path means the voices stop working the day they tidy their Downloads
    /// folder. It goes into the same <c>nbr-services.dat</c> as every other
    /// credential — which already base64-encodes its values, so a multi-line
    /// JSON needs no new format.</para>
    ///
    /// <para><b>A voice here is a SPEAKER, not a voice-plus-language.</b>
    /// Measured on the live catalogue: 2066 entries, 1568 of them Chirp 3 HD,
    /// but only <b>30 distinct speakers</b> — and the same 30 in every language
    /// checked (hr, en, ja, ar). The language is a parameter of the request, not
    /// a property of the voice. That is the opposite of SAPI and OneCore, where
    /// Matej <i>is</i> Croatian, and it is why one voice is called the same
    /// thing for Croatian and for English.</para>
    ///
    /// <para><b>So the name carries the language</b> — "Achernar (hr-HR)" —
    /// because <see cref="CompositeSpeechBackend"/> keys on the voice NAME and
    /// drops a name it already owns. One entry per speaker would have kept
    /// whichever language happened to be merged first and hidden the speaker
    /// from every other one. The reader only ever sees this list filtered to
    /// their book's language, so the tail is quiet.</para>
    /// </summary>
    internal static class GoogleCloudVoices
    {
        /// <summary>Id in the shared credential store. Not "google" — Gemini and
        /// Google Cloud are different doors with different credentials, and one
        /// of them is a translation engine.</summary>
        public const string CredentialId = "google-cloud-tts";

        public const string Vendor = "Google Cloud";

        /// <summary>The most a single request may carry. Google's limit is 5000
        /// BYTES of input, not characters, so a Croatian sentence is measured in
        /// UTF-8 and not in <c>Length</c>. Left a little under it.</summary>
        public const int MaxRequestBytes = 4800;

        private const string TokenUrlFallback = "https://oauth2.googleapis.com/token";
        private const string VoicesUrl = "https://texttospeech.googleapis.com/v1/voices";
        private const string SynthesizeUrl = "https://texttospeech.googleapis.com/v1/text:synthesize";

        // ── The credential ────────────────────────────────────────────────────

        /// <summary>Is a service account stored?</summary>
        public static bool Have { get { return TranslationKeys.Has(CredentialId); } }

        /// <summary>Reads a downloaded service-account JSON, checks it is one,
        /// and stores its contents. Returns null on success, else a reason in
        /// plain language — the caller shows it, so it must not be jargon.</summary>
        public static string LoadFrom(string path)
        {
            string json;
            try { json = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { return ex.Message; }

            string why = Check(json);
            if (why != null) return why;

            TranslationKeys.Set(CredentialId, json);
            lock (tokenLock) { token = null; tokenUntil = DateTime.MinValue; }
            return null;
        }

        public static void Forget()
        {
            TranslationKeys.Set(CredentialId, null);
            lock (tokenLock) { token = null; tokenUntil = DateTime.MinValue; }
        }

        /// <summary>What a service account must have. Checked before storing, so
        /// a reader who picked the wrong file is told at once rather than at the
        /// first sentence of a book.</summary>
        private static string Check(string json)
        {
            object root;
            try { root = Json.Parse(json); }
            catch { return Localization.T("Google.Cred.NotJson"); }
            if (root == null) return Localization.T("Google.Cred.NotJson");

            string email = Json.PathString(root, "client_email");
            string key = Json.PathString(root, "private_key");
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(key))
                return Localization.T("Google.Cred.NotServiceAccount");
            if (key.IndexOf("PRIVATE KEY", StringComparison.Ordinal) < 0)
                return Localization.T("Google.Cred.NotServiceAccount");
            return null;
        }

        // ── The token ─────────────────────────────────────────────────────────

        private static readonly object tokenLock = new object();
        private static string token;
        private static DateTime tokenUntil = DateTime.MinValue;

        /// <summary>A live access token, minted from the service account and kept
        /// until shortly before it expires.
        ///
        /// <para><b>Refreshed early on purpose.</b> A token lasts an hour, and
        /// during this project's own testing it ran out mid-job twice — once
        /// between two measurements of the same book. Renewing with five minutes
        /// left costs one request and removes a class of failure that surfaces
        /// as a bare 401 in the middle of a chapter.</para></summary>
        public static string Token()
        {
            lock (tokenLock)
            {
                if (token != null && DateTime.UtcNow < tokenUntil) return token;
                token = null;

                string json = TranslationKeys.Get(CredentialId);
                if (string.IsNullOrEmpty(json)) return null;

                try
                {
                    object root = Json.Parse(json);
                    string email = Json.PathString(root, "client_email");
                    string pem = Json.PathString(root, "private_key");
                    string tokenUrl = Json.PathString(root, "token_uri");
                    if (string.IsNullOrEmpty(tokenUrl)) tokenUrl = TokenUrlFallback;
                    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pem)) return null;

                    // UTC, and this cost the only real delay when the chain was
                    // first built: PowerShell's `Get-Date -UFormat %s` returns
                    // LOCAL epoch seconds, so the JWT was issued two hours in the
                    // future and Google refused it saying only "400 Bad Request".
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string header = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
                    string claims =
                        "{\"iss\":" + Json.Str(email) +
                        ",\"scope\":\"https://www.googleapis.com/auth/cloud-platform\"" +
                        ",\"aud\":" + Json.Str(tokenUrl) +
                        ",\"exp\":" + (now + 3600) +
                        ",\"iat\":" + now + "}";

                    string signed = B64Url(Encoding.UTF8.GetBytes(header)) + "." +
                                    B64Url(Encoding.UTF8.GetBytes(claims));
                    string assertion = signed + "." + B64Url(Sign(pem, signed));

                    string body = "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer" +
                                  "&assertion=" + assertion;
                    string reply = Post(tokenUrl, "application/x-www-form-urlencoded",
                                        Encoding.UTF8.GetBytes(body), null);
                    if (reply == null) return null;

                    string t = Json.PathString(Json.Parse(reply), "access_token");
                    if (string.IsNullOrEmpty(t)) return null;

                    token = t;
                    tokenUntil = DateTime.UtcNow.AddMinutes(55);
                    return token;
                }
                catch { return null; }
            }
        }

        /// <summary>RS256 over the JWT's first two parts.
        ///
        /// <para><c>CngKey.Import</c> with <c>Pkcs8PrivateBlob</c> plus
        /// <c>RSACng</c> is what makes this possible on .NET Framework 4.8 with
        /// nothing vendored. The <c>ImportPkcs8PrivateKey</c> everyone reaches
        /// for first is .NET Core 3.0 and later, and is not here.</para></summary>
        private static byte[] Sign(string pem, string data)
        {
            string b64 = pem.Replace("-----BEGIN PRIVATE KEY-----", "")
                            .Replace("-----END PRIVATE KEY-----", "")
                            .Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();
            byte[] der = Convert.FromBase64String(b64);
            using (CngKey k = CngKey.Import(der, CngKeyBlobFormat.Pkcs8PrivateBlob))
            using (var rsa = new RSACng(k))
                return rsa.SignData(Encoding.UTF8.GetBytes(data),
                                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        private static string B64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ── The catalogue ─────────────────────────────────────────────────────

        private static readonly string CachePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "google-voices.txt");

        private static List<(string Name, string Language, string Gender)> cache;

        /// <summary>Every Chirp 3 HD speaker, once per language it reads.
        ///
        /// <para><b>Never asks the network twice, and never asks it at all once
        /// it has an answer.</b> Settings and Properties each build a catalogue
        /// every time they open; a round trip there would hang the dialog on a
        /// machine that is offline, which is exactly where a reader with no
        /// connection would go to find out why. The list is fetched once, written
        /// beside the credential store, and read from disk thereafter.</para>
        ///
        /// <para>Chirp 3 HD only. The older families exist for other languages
        /// but not for Croatian — measured, hr-HR has 30 voices and every one of
        /// them is Chirp 3 HD — and mixing tiers would put voices of visibly
        /// different quality in one list with nothing to tell them apart.</para></summary>
        public static List<(string Name, string Language, string Gender)> Voices()
        {
            if (cache != null) return cache;
            cache = ReadCache();
            if (cache.Count == 0 && Have)
            {
                cache = Fetch();
                if (cache.Count > 0) WriteCache(cache);
            }
            return cache;
        }

        /// <summary>Fetches the catalogue afresh — called when a credential is
        /// first stored, so the reader's very next visit to Properties already
        /// has the list.</summary>
        public static bool Refresh()
        {
            var got = Fetch();
            if (got.Count == 0) return false;
            WriteCache(got);
            cache = got;
            return true;
        }

        private static List<(string, string, string)> Fetch()
        {
            var list = new List<(string, string, string)>();
            try
            {
                string t = Token();
                if (t == null) return list;
                string reply = Get(VoicesUrl, t);
                if (reply == null) return list;

                var voices = Json.Path(Json.Parse(reply), "voices") as List<object>;
                if (voices == null) return list;
                foreach (object v in voices)
                {
                    string name = Json.PathString(v, "name");
                    if (name == null || name.IndexOf("Chirp3-HD", StringComparison.Ordinal) < 0) continue;
                    string gender = Json.PathString(v, "ssmlGender") ?? "";
                    var codes = Json.Path(v, "languageCodes") as List<object>;
                    if (codes == null) continue;
                    foreach (object c in codes)
                    {
                        string lang = c as string;
                        if (string.IsNullOrEmpty(lang)) continue;
                        list.Add((name, lang, gender));
                    }
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

        private static void WriteCache(List<(string Name, string Language, string Gender)> list)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var v in list)
                    sb.Append(v.Name).Append('\t').Append(v.Language).Append('\t')
                      .Append(v.Gender).Append('\n');
                File.WriteAllText(CachePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* a catalogue we cannot cache is re-fetched, never a crash */ }
        }

        // ── Names ─────────────────────────────────────────────────────────────

        /// <summary>The speaker's own name out of Google's full id
        /// ("hr-HR-Chirp3-HD-Achernar" → "Achernar").</summary>
        public static string Speaker(string googleName)
        {
            if (string.IsNullOrEmpty(googleName)) return "";
            int dash = googleName.LastIndexOf('-');
            return dash >= 0 && dash + 1 < googleName.Length
                ? googleName.Substring(dash + 1) : googleName;
        }

        /// <summary>What NBR calls this voice: the speaker plus the language it
        /// is being asked to read, because the composite keys on the name.</summary>
        public static string DisplayName(string googleName, string language)
        {
            return Speaker(googleName) + " (" + language + ")";
        }

        /// <summary>Google's own id and the language back out of a display name,
        /// or false if this is not one of ours.</summary>
        public static bool Split(string displayName, out string googleName, out string language)
        {
            googleName = null;
            language = null;
            if (string.IsNullOrEmpty(displayName)) return false;
            int open = displayName.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0 || !displayName.EndsWith(")", StringComparison.Ordinal)) return false;
            string speaker = displayName.Substring(0, open);
            language = displayName.Substring(open + 2, displayName.Length - open - 3);
            // IT HAS TO LOOK LIKE A LANGUAGE TAG, and that guard arrived with the
            // second cloud (2026-08-16). This used to accept whatever stood
            // between the brackets, so "Gabrijela (hr-HR, Azure)" parsed happily
            // and built the Google id "hr-HR, Azure-Chirp3-HD-Gabrijela" — an
            // Azure voice claimed as Google's, which would have sent its
            // synthesis to the wrong service and its cost to the wrong
            // allowance. A BCP-47 tag has letters, digits and hyphens and
            // nothing else.
            if (!LooksLikeLanguage(language)) { googleName = null; language = null; return false; }
            googleName = language + "-Chirp3-HD-" + speaker;
            return true;
        }

        /// <summary>Is this one of ours? Told by the NAME parsing back into a
        /// Google id — never by a vendor or engine LABEL, which is a display
        /// string and would quietly stop matching the day somebody reworded
        /// it.</summary>
        public static bool IsOne(string displayName)
        {
            string g, l;
            return Split(displayName, out g, out l);
        }

        private static bool LooksLikeLanguage(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 20) return false;
            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != '-') return false;
            return true;
        }

        /// <summary>The same catalogue with the cloud voices taken out, and how
        /// many were taken.
        ///
        /// <para>Wanted in three places at once, which is why it lives here and
        /// not in a dialog: Settings never offers them, Properties only when the
        /// reader has switched them on, and the speech inventory LOG must not
        /// drown in them — measured, a machine with a credential has 1568 of them
        /// against 7 installed voices, and that log exists precisely to find the
        /// installed one that is missing.</para></summary>
        public static List<(string Name, string Engine, string Language)> Exclude(
            IEnumerable<(string Name, string Engine, string Language)> all, out int removed)
        {
            var kept = new List<(string, string, string)>();
            removed = 0;
            if (all == null) return kept;
            foreach (var v in all)
            {
                if (IsOne(v.Name)) { removed++; continue; }
                kept.Add(v);
            }
            return kept;
        }

        // ── Synthesis ─────────────────────────────────────────────────────────

        /// <summary>One request: text in, a finished WAV out — which is exactly
        /// the shape <see cref="ISpeechBackend"/> already takes, since
        /// <see cref="OneCoreBackend"/> renders to a buffer and hands it to
        /// <see cref="SapiWavPlayer"/>.
        ///
        /// <para>LINEAR16 at the voice's own 24 kHz, so what comes back is a real
        /// WAV with a header and needs no decoding.</para>
        ///
        /// <para><paramref name="rate"/> is the ordinary speaking-rate multiplier
        /// (1.0 = normal). <b>Pitch is deliberately not sent:</b> Google's own
        /// documentation lists pitch control as unavailable for hr-HR, and
        /// sending a parameter the service ignores would make a control that does
        /// nothing look like a control that is broken.</para></summary>
        public static byte[] Synthesize(string text, string googleName, string language,
                                        double rate, double volumeDb)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                string t = Token();
                if (t == null) return null;

                if (rate < 0.25) rate = 0.25;
                if (rate > 4.0) rate = 4.0;
                if (volumeDb < -96) volumeDb = -96;
                if (volumeDb > 16) volumeDb = 16;

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string body =
                    "{\"input\":{\"text\":" + Json.Str(text) + "}," +
                    "\"voice\":{\"languageCode\":" + Json.Str(language) +
                    ",\"name\":" + Json.Str(googleName) + "}," +
                    "\"audioConfig\":{\"audioEncoding\":\"LINEAR16\",\"sampleRateHertz\":24000" +
                    ",\"speakingRate\":" + rate.ToString("0.###", inv) +
                    ",\"volumeGainDb\":" + volumeDb.ToString("0.##", inv) +
                    "}}";

                string reply = Post(SynthesizeUrl, "application/json; charset=utf-8",
                                    Encoding.UTF8.GetBytes(body), t);
                if (reply == null) return null;

                string audio = Json.PathString(Json.Parse(reply), "audioContent");
                if (string.IsNullOrEmpty(audio)) return null;
                return Convert.FromBase64String(audio);
            }
            catch { return null; }
        }

        // ── Transport ─────────────────────────────────────────────────────────

        private static string Post(string url, string contentType, byte[] body, string bearer)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = "POST";
            r.ContentType = contentType;
            r.Timeout = 120000;
            r.ReadWriteTimeout = 120000;
            if (bearer != null) r.Headers.Add("Authorization", "Bearer " + bearer);
            r.ContentLength = body.Length;
            using (Stream s = r.GetRequestStream()) s.Write(body, 0, body.Length);
            return Read(r);
        }

        private static string Get(string url, string bearer)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = "GET";
            r.Timeout = 60000;
            if (bearer != null) r.Headers.Add("Authorization", "Bearer " + bearer);
            return Read(r);
        }

        /// <summary>The reply, or null. A failure is never allowed to reach the
        /// reader as an exception mid-sentence: the backend treats null as "this
        /// utterance produced nothing" and reading carries on.</summary>
        private static string Read(HttpWebRequest r)
        {
            try
            {
                using (WebResponse resp = r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                // A 401 means the token died under us — throw it away so the next
                // call mints a fresh one instead of repeating the failure.
                var http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.Unauthorized)
                    lock (tokenLock) { token = null; tokenUntil = DateTime.MinValue; }
                return null;
            }
            catch { return null; }
        }
    }
}
