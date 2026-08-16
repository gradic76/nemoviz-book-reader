using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Nemoviz_Book_Reader
{
    /// <summary>What the sign-in step hands back for the reader to act on.</summary>
    internal sealed class DeviceCodeRequest
    {
        public string DeviceCode;
        /// <summary>The short code the reader types on Microsoft's page.</summary>
        public string UserCode;
        /// <summary>Where they type it — microsoft.com/devicelogin.</summary>
        public string VerificationUri;
        public int IntervalSeconds;
        public int ExpiresInSeconds;
        public string Error;
    }

    internal sealed class AzureResult
    {
        public bool Ok;
        public string Value;        // a key, a subscription id, whatever was asked for
        public string Error;
        public string Detail;
    }

    /// <summary>
    /// Setting Azure up without opening the portal.
    ///
    /// <para><b>Why this exists.</b> Getting a key out of a cloud console is the
    /// hardest part of this whole feature for the reader it is built for — a
    /// morning of it, measured, with a screen reader on pages that fight back. For
    /// Google there is no way round that: automating its console would trip the
    /// very abuse detection that blocks people, and it refuses sign-in inside an
    /// embedded browser anyway, for good reasons. <b>Azure is different, and the
    /// difference is that it has a documented API for exactly this.</b> Creating
    /// the resource and reading its key are ordinary management calls. So this is
    /// not automating a user interface — it is using the interface Microsoft
    /// intends, and skipping the one built for eyes.</para>
    ///
    /// <para><b>Sign-in is the device code flow, and NBR never sees a password.</b>
    /// It shows a short code; the reader opens Microsoft's own page in their own
    /// browser and approves there. No embedded browser, nothing typed into our
    /// window, and nothing for us to store but the token that comes back.</para>
    ///
    /// <para><b>The one thing that cannot be automated is the account itself.</b>
    /// Opening an Azure subscription needs identity verification and the
    /// acceptance of an agreement — a human act, deliberately. That is the whole
    /// remaining manual step: the subscription once, and everything after it from
    /// here.</para>
    /// </summary>
    internal static class AzureProvision
    {
        /// <summary>NBR's own registration in Entra ID, made 2026-08-15.
        ///
        /// <para><b>This is not a secret and belongs in the source.</b> A device
        /// code client is a PUBLIC client: it has no secret by design, because
        /// nothing about it is trusted — the reader authorises it on Microsoft's
        /// page and the token belongs to them. Shipping the id is how the flow is
        /// meant to work.</para>
        ///
        /// <para>The registration needs two things set or the flow simply refuses:
        /// supported accounts on the widest option (any tenant plus personal
        /// Microsoft accounts), and <b>Allow public client flows = Enabled</b>,
        /// which lives under Authentication → Settings and is easy to miss.</para>
        ///
        /// <para><b>Never substitute somebody else's client id here.</b> It is a
        /// known trick to borrow the Azure CLI's, and it would work — it is also
        /// passing our requests off as another application's, which is not a thing
        /// to ship.</para></summary>
        public const string ClientId = "71525ffa-8165-4e6a-a739-39035de66904";

        /// <summary>Which directory the reader signs in through.
        ///
        /// <para><b>"common" does NOT work for this, and the reason is worth
        /// keeping.</b> Azure Resource Manager has no notion of a personal
        /// Microsoft account: when a scope of management.azure.com is asked for,
        /// Microsoft narrows the sign-in to work or school accounts, and a reader
        /// whose Azure was opened with a Gmail address is told flatly that his
        /// account will not do — even though it is the owner of the subscription.
        /// What happened when the subscription was created is that Microsoft made
        /// a DIRECTORY for it and put that account inside; the sign-in has to go
        /// through that directory rather than through the front door.</para>
        ///
        /// <para>So the tenant is a parameter. A shipped NBR cannot know it in
        /// advance — it is different for every reader — which leaves it as the one
        /// thing still to be worked out: either discovered before this step, or
        /// asked for once. It is printed on the app registration page as
        /// "Directory (tenant) ID".</para></summary>
        /// <summary>Where the directory is stored between runs, so the reader
        /// gives it once.</summary>
        public const string TenantId = "azure-tenant";

        /// <summary>Which directory to sign in against.
        ///
        /// <para><b>"common" does not work for a personal account, and this is the
        /// one thing about Azure that has caught us twice.</b> ARM has no notion
        /// of a personal Microsoft account: ask for a
        /// <c>management.azure.com</c> scope and Microsoft narrows sign-in to work
        /// or school accounts, so a Gmail-opened Azure is refused BY THE OWNER OF
        /// THE SUBSCRIPTION. Creating the subscription quietly made a DIRECTORY
        /// and put the account inside it, and sign-in has to go through that
        /// directory rather than the front door.</para>
        ///
        /// <para>NBR cannot know it in advance and nothing can derive it, so it is
        /// asked for once and remembered. It does NOT have to be the GUID:
        /// Microsoft documents the authority as taking "the tenant ID … or its
        /// tenant domain", and the domain is the readable half —
        /// <c>something.onmicrosoft.com</c>.</para></summary>
        public static string Tenant
        {
            get
            {
                string t = (TranslationKeys.Get(TenantId) ?? "").Trim();
                return t.Length > 0 ? t : "common";
            }
            set { TranslationKeys.Set(TenantId, (value ?? "").Trim()); }
        }

        private static string Authority { get { return "https://login.microsoftonline.com/" + Tenant + "/oauth2/v2.0"; } }
        private const string ArmScope = "https://management.azure.com/user_impersonation offline_access";
        private const string Arm = "https://management.azure.com";

        /// <summary>Step one: ask Microsoft for a code to show the reader.</summary>
        public static DeviceCodeRequest BeginSignIn()
        {
            string body = "client_id=" + Uri.EscapeDataString(ClientId) +
                          "&scope=" + Uri.EscapeDataString(ArmScope);
            string raw; int status;
            string transport = Post(Authority + "/devicecode", null, body,
                                    "application/x-www-form-urlencoded", out raw, out status);
            if (transport != null) return new DeviceCodeRequest { Error = transport };

            object j = Json.Parse(raw);
            string code = Json.PathString(j, "user_code");
            if (string.IsNullOrEmpty(code))
                return new DeviceCodeRequest { Error = Json.PathString(j, "error_description") ?? Truncate(raw, 300) };

            return new DeviceCodeRequest
            {
                DeviceCode = Json.PathString(j, "device_code"),
                UserCode = code,
                VerificationUri = Json.PathString(j, "verification_uri") ?? "https://microsoft.com/devicelogin",
                IntervalSeconds = ParseInt(Json.PathString(j, "interval"), 5),
                ExpiresInSeconds = ParseInt(Json.PathString(j, "expires_in"), 900)
            };
        }

        /// <summary>Step two: wait for the reader to approve, and come back with a
        /// token. <paramref name="cancelled"/> is checked between polls so a window
        /// can be closed without leaving this running.</summary>
        public static AzureResult CompleteSignIn(DeviceCodeRequest req, Func<bool> cancelled)
        {
            if (req == null || string.IsNullOrEmpty(req.DeviceCode))
                return Fail("no device code");

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, req.ExpiresInSeconds));
            int wait = Math.Max(3, req.IntervalSeconds);

            while (DateTime.UtcNow < deadline)
            {
                if (cancelled != null && cancelled()) return Fail("cancelled");
                Thread.Sleep(wait * 1000);

                string body = "grant_type=urn:ietf:params:oauth:grant-type:device_code" +
                              "&client_id=" + Uri.EscapeDataString(ClientId) +
                              "&device_code=" + Uri.EscapeDataString(req.DeviceCode);
                string raw; int status;
                string transport = Post(Authority + "/token", null, body,
                                        "application/x-www-form-urlencoded", out raw, out status);
                if (transport != null) return Fail(transport);

                object j = Json.Parse(raw);
                string token = Json.PathString(j, "access_token");
                if (!string.IsNullOrEmpty(token)) return new AzureResult { Ok = true, Value = token };

                string err = Json.PathString(j, "error") ?? "";
                // These two are the flow working as designed, not faults: the
                // reader has not finished yet, or we are asking too briskly.
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { wait += 5; continue; }
                return new AzureResult { Ok = false, Error = err, Detail = Json.PathString(j, "error_description") };
            }
            return Fail("the code expired before it was approved");
        }

        /// <summary>The subscriptions this account can use. Empty is the ordinary
        /// answer for somebody who has signed in but never opened one — which is
        /// the point at which the reader has to go and do the one manual step.</summary>
        public static List<KeyValuePair<string, string>> Subscriptions(string token, out string error)
        {
            error = null;
            var list = new List<KeyValuePair<string, string>>();
            string raw; int status;
            string transport = Get(Arm + "/subscriptions?api-version=2020-01-01", token, out raw, out status);
            if (transport != null) { error = transport; return list; }

            object j = Json.Parse(raw);
            if (status < 200 || status >= 300)
            {
                error = Json.PathString(j, "error", "message") ?? Truncate(raw, 300);
                return list;
            }
            var items = Json.Path(j, "value") as List<object>;
            if (items == null) return list;
            foreach (object it in items)
            {
                string id = Json.PathString(it, "subscriptionId");
                string name = Json.PathString(it, "displayName") ?? id;
                if (!string.IsNullOrEmpty(id)) list.Add(new KeyValuePair<string, string>(id, name));
            }
            return list;
        }

        /// <summary>Creates the resource group and the Translator resource, then
        /// reads the key out. <b>Global and F0</b> — global because a single-service
        /// global resource needs no region header, which is what lets the key dialog
        /// ask for one thing; F0 because it is the free tier, two million characters
        /// a month, and a reader should not be signed up to a bill by a program.
        ///
        /// <para><b>Untested end to end at the time of writing</b>, because there is
        /// no subscription here to run it against. The sign-in half above has been
        /// exercised; this half is written from Microsoft's own reference and should
        /// be treated as unproven until a real subscription has seen it.</para></summary>
        public static AzureResult CreateTranslator(string token, string subscriptionId,
                                                   string resourceGroup, string accountName)
        {
            // Global, because a single-service global Translator resource needs no
            // region header — which is what lets its key dialog have one field.
            return CreateAccount(token, subscriptionId, resourceGroup, accountName,
                                 "TextTranslation", "global", false);
        }

        /// <summary>The region a Speech resource is made in.
        ///
        /// <para><b>Speech has no "global", and that is the whole reason this is
        /// a constant rather than a question.</b> Translator does, which is why
        /// its account above says so; Speech publishes a table of some thirty-five
        /// regions and no global entry. Somebody has to choose, and asking a
        /// reader to pick a datacentre is asking a question they have no way to
        /// answer — so NBR picks the one its resource group already lives in, and
        /// the nearest large one to where this is being written.</para></summary>
        public const string SpeechRegion = "westeurope";

        /// <summary>A Speech resource on the free tier, its region known because
        /// we chose it. <see cref="AzureVoices"/> stores all three parts and the
        /// reader types none of them.</summary>
        public static AzureResult CreateSpeech(string token, string subscriptionId,
                                               string resourceGroup, string accountName)
        {
            // The custom subdomain is asked for as well as the region, because the
            // documentation disagrees with itself about which endpoint form Speech
            // accepts (see AzureVoices.RegionId) and this costs nothing: the name
            // is ours already, and having both means the fallback has something to
            // fall back TO.
            return CreateAccount(token, subscriptionId, resourceGroup, accountName,
                                 "SpeechServices", SpeechRegion, true);
        }

        private static AzureResult CreateAccount(string token, string subscriptionId,
                                                 string resourceGroup, string accountName,
                                                 string kind, string location, bool customSubdomain)
        {
            // THE SUBSCRIPTION HAS TO BE TOLD IT MAY HAVE THIS KIND OF RESOURCE AT
            // ALL, and this step exists because a live run found it: without it,
            // creating the account comes back
            //   409 "The subscription is not registered to use namespace
            //        'Microsoft.CognitiveServices'"
            // on a subscription that is perfectly healthy.
            //
            // The portal does this silently the first time somebody makes such a
            // resource, so nobody working by hand ever meets it — which is exactly
            // why writing this from the documentation was not enough. It is
            // idempotent and quick, so it simply runs every time rather than being
            // guarded by a check that could itself be wrong.
            // A SLOW STATUS FIELD IS NOT A FAILURE. Registration is asynchronous
            // and can take several minutes; the first version gave up after one
            // and reported defeat on a subscription that was very likely ready.
            // So a timeout here does not stop the job — it goes on and lets the
            // real operation answer, because if the provider truly is not ready
            // the create comes back with the same clear 409 and says so properly.
            RegisterProvider(token, subscriptionId, "Microsoft.CognitiveServices");

            string rgUrl = Arm + "/subscriptions/" + subscriptionId +
                           "/resourcegroups/" + Uri.EscapeDataString(resourceGroup) +
                           "?api-version=2021-04-01";
            string raw; int status;
            // The group has to live somewhere even though the account is global;
            // this is only where its metadata is kept.
            string t = Put(rgUrl, token, "{\"location\":\"westeurope\"}", out raw, out status);
            if (t != null) return Fail(t);
            if (status < 200 || status >= 300) return Refused(raw, status);

            string acctUrl = Arm + "/subscriptions/" + subscriptionId +
                             "/resourceGroups/" + Uri.EscapeDataString(resourceGroup) +
                             "/providers/Microsoft.CognitiveServices/accounts/" +
                             Uri.EscapeDataString(accountName) + "?api-version=2023-05-01";
            string props = customSubdomain
                ? "{\"customSubDomainName\":\"" + accountName + "\"}" : "{}";
            string acct = "{\"location\":\"" + location + "\",\"kind\":\"" + kind + "\"," +
                          "\"sku\":{\"name\":\"F0\"},\"properties\":" + props + "}";
            t = Put(acctUrl, token, acct, out raw, out status);
            if (t != null) return Fail(t);
            if (status < 200 || status >= 300) return Refused(raw, status);

            // Creation is asynchronous: the account exists before it is ready, and
            // asking for its keys too early answers with a state rather than a key.
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(3000);
                t = Get(acctUrl, token, out raw, out status);
                if (t == null && status >= 200 && status < 300)
                {
                    string state = Json.PathString(Json.Parse(raw), "properties", "provisioningState");
                    if (string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase)) break;
                    if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
                        return Fail("the resource could not be created");
                }
            }

            string keysUrl = Arm + "/subscriptions/" + subscriptionId +
                             "/resourceGroups/" + Uri.EscapeDataString(resourceGroup) +
                             "/providers/Microsoft.CognitiveServices/accounts/" +
                             Uri.EscapeDataString(accountName) + "/listKeys?api-version=2023-05-01";
            t = Post(keysUrl, token, "", "application/json", out raw, out status);
            if (t != null) return Fail(t);
            if (status < 200 || status >= 300) return Refused(raw, status);

            string key = Json.PathString(Json.Parse(raw), "key1");
            if (string.IsNullOrEmpty(key)) return Fail("no key came back");
            return new AzureResult { Ok = true, Value = key };
        }

        /// <summary>Registers a resource provider on the subscription and waits
        /// until it says it is registered. Both halves are needed: the call itself
        /// returns at once and the registration takes a little while, so creating
        /// the resource immediately afterwards can still be refused.</summary>
        private static AzureResult RegisterProvider(string token, string subscriptionId, string ns)
        {
            string baseUrl = Arm + "/subscriptions/" + subscriptionId + "/providers/" + ns;
            string raw; int status;

            string t = Post(baseUrl + "/register?api-version=2021-04-01", token, "",
                            "application/json", out raw, out status);
            if (t != null) return Fail(t);
            // A subscription that is already registered answers happily; only a
            // real refusal is worth stopping for.
            if (status < 200 || status >= 300) return Refused(raw, status);

            for (int i = 0; i < 40; i++)
            {
                t = Get(baseUrl + "?api-version=2021-04-01", token, out raw, out status);
                if (t == null && status >= 200 && status < 300)
                {
                    string state = Json.PathString(Json.Parse(raw), "registrationState");
                    if (string.Equals(state, "Registered", StringComparison.OrdinalIgnoreCase))
                        return new AzureResult { Ok = true };
                }
                Thread.Sleep(5000);
            }
            return Fail("the subscription did not finish registering " + ns);
        }

        // ---- the wire ----------------------------------------------------------

        private static string Get(string url, string token, out string raw, out int status)
        {
            return Send("GET", url, token, null, null, out raw, out status);
        }

        private static string Put(string url, string token, string body, out string raw, out int status)
        {
            return Send("PUT", url, token, body, "application/json", out raw, out status);
        }

        private static string Post(string url, string token, string body, string contentType,
                                   out string raw, out int status)
        {
            return Send("POST", url, token, body, contentType, out raw, out status);
        }

        private static string Send(string method, string url, string token, string body,
                                   string contentType, out string raw, out int status)
        {
            raw = ""; status = 0;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = 120000;
                req.ReadWriteTimeout = 120000;
                req.Proxy = WebRequest.DefaultWebProxy;
                if (!string.IsNullOrEmpty(token)) req.Headers.Add("Authorization", "Bearer " + token);
                if (body != null)
                {
                    req.ContentType = contentType ?? "application/json";
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = payload.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    raw = ReadAll(resp);
                }
                return null;
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    // The body carries the diagnosis; the code alone rarely does.
                    status = (int)resp.StatusCode;
                    try { raw = ReadAll(resp); } catch { }
                    return null;
                }
                return ex.Message;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            {
                if (s == null) return "";
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        private static AzureResult Refused(string raw, int status)
        {
            object j = Json.Parse(raw);
            return new AzureResult
            {
                Ok = false,
                Error = "Azure refused the request (" + status.ToString(CultureInfo.InvariantCulture) + ")",
                Detail = Json.PathString(j, "error", "message") ?? Truncate(raw, 300)
            };
        }

        private static AzureResult Fail(string why) { return new AzureResult { Ok = false, Error = why }; }

        private static int ParseInt(string s, int fallback)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (int)d : fallback;
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}
