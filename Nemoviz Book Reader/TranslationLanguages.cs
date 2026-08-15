using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The languages a book can be translated into.
    ///
    /// <para><b>Where this list comes from, and why it is not the library's.</b> The
    /// target languages used to be the ones the reader's own shelf held books in —
    /// borrowed from the voice picker, where the rule is right because a voice must
    /// be INSTALLED and offering three hundred would be offering things that cannot
    /// happen. Translation has no such constraint: a service translates into its
    /// languages whether or not you own a book in one. Gordan found it with two
    /// books in the library and three languages on offer (2026-08-15).</para>
    ///
    /// <para><b>It is Azure's published list, read off their own endpoint</b> — 138
    /// languages, and the only one of the four services that publishes a list at
    /// all. Gemini and DeepSeek document none, so an intersection cannot be looked
    /// up; it can only be measured, and a model never refuses, so the measurement
    /// has to be of the assumption rather than of the set.</para>
    ///
    /// <para><b>Measured on the hardest twenty</b> — Kashmiri, Divehi, Uyghur,
    /// Klingon, Inuktitut, Tigrinya, Sinhala, Khmer, Lao, Faroese and the rest —
    /// all three language models produced a real translation for every one of them,
    /// none refused and none echoed the English back. So Azure's list is a safe
    /// FLOOR: nothing on it comes back blank. <b>What that does NOT establish is
    /// quality at the rare end</b>, and nobody here can judge Kashmiri; the claim is
    /// coverage, not fluency.</para>
    ///
    /// <para><b>The variants are the point of using their list rather than a
    /// hand-written one.</b> These twelve carry a region or a script and change what
    /// comes out: es-MX beside es, pt-PT beside pt, and above all <b>sr-Cyrl beside
    /// sr-Latn</b>, which for this project's readers is a real choice and a true
    /// bijection. What no service distinguishes is British against American English
    /// — Azure has one plain <c>en</c> — and that is the right place for the reader's
    /// own notes on the book, which are prose and reach the model as prose.</para>
    ///
    /// <para>Compiled in rather than shipped as a data file: a missing file would
    /// empty the list silently, and this changes about once a year.</para>
    /// </summary>
    internal static class TranslationLanguages
    {
        internal sealed class Lang
        {
            public readonly string Code;
            /// <summary>The language's name in itself — the rule this project
            /// follows everywhere a language is named.</summary>
            public readonly string Native;
            public readonly string English;
            public Lang(string code, string native, string english)
            { Code = code; Native = native; English = english; }

            /// <summary>Both names when they differ, so a reader who does not read
            /// the script can still find the row.</summary>
            public string DisplayName
            {
                get { return string.Equals(Native, English, StringComparison.OrdinalIgnoreCase) ? Native : Native + " — " + English; }
            }
        }

        public static readonly List<Lang> All = new List<Lang>
        {            new Lang("af", "Afrikaans", "Afrikaans"),
            new Lang("am", "አማርኛ", "Amharic"),
            new Lang("ar", "العربية", "Arabic"),
            new Lang("as", "অসমীয়া", "Assamese"),
            new Lang("az", "Azərbaycan", "Azerbaijani"),
            new Lang("ba", "Bashkir", "Bashkir"),
            new Lang("be", "беларуская", "Belarusian"),
            new Lang("bg", "Български", "Bulgarian"),
            new Lang("bho", "भोजपुरी", "Bhojpuri"),
            new Lang("bn", "বাংলা", "Bangla"),
            new Lang("bo", "བོད་སྐད་", "Tibetan"),
            new Lang("brx", "बड़ो", "Bodo"),
            new Lang("bs", "Bosanski", "Bosnian"),
            new Lang("ca", "Català", "Catalan"),
            new Lang("cs", "Čeština", "Czech"),
            new Lang("cy", "Cymraeg", "Welsh"),
            new Lang("da", "Dansk", "Danish"),
            new Lang("de", "Deutsch", "German"),
            new Lang("doi", "डोगरी", "Dogri"),
            new Lang("dsb", "Dolnoserbšćina", "Lower Sorbian"),
            new Lang("dv", "ދިވެހިބަސް", "Divehi"),
            new Lang("el", "Ελληνικά", "Greek"),
            new Lang("en", "English", "English"),
            new Lang("es", "Español", "Spanish"),
            new Lang("es-MX", "Español (México)", "Spanish (Mexico)"),
            new Lang("et", "Eesti", "Estonian"),
            new Lang("eu", "Euskara", "Basque"),
            new Lang("fa", "فارسی", "Persian"),
            new Lang("fi", "Suomi", "Finnish"),
            new Lang("fil", "Filipino", "Filipino"),
            new Lang("fj", "Na Vosa Vakaviti", "Fijian"),
            new Lang("fo", "Føroyskt", "Faroese"),
            new Lang("fr", "Français", "French"),
            new Lang("fr-CA", "Français (Canada)", "French (Canada)"),
            new Lang("ga", "Gaeilge", "Irish"),
            new Lang("gl", "Galego", "Galician"),
            new Lang("gom", "कोंकणी", "Konkani"),
            new Lang("gu", "ગુજરાતી", "Gujarati"),
            new Lang("ha", "Hausa", "Hausa"),
            new Lang("he", "עברית", "Hebrew"),
            new Lang("hi", "हिन्दी", "Hindi"),
            new Lang("hne", "छत्तीसगढ़ी", "Chhattisgarhi"),
            new Lang("hr", "Hrvatski", "Croatian"),
            new Lang("hsb", "Hornjoserbšćina", "Upper Sorbian"),
            new Lang("ht", "Haitian Creole", "Haitian Creole"),
            new Lang("hu", "Magyar", "Hungarian"),
            new Lang("hy", "Հայերեն", "Armenian"),
            new Lang("id", "Indonesia", "Indonesian"),
            new Lang("ig", "Ásụ̀sụ́ Ìgbò", "Igbo"),
            new Lang("ikt", "Inuinnaqtun", "Inuinnaqtun"),
            new Lang("is", "Íslenska", "Icelandic"),
            new Lang("it", "Italiano", "Italian"),
            new Lang("iu", "ᐃᓄᒃᑎᑐᑦ", "Inuktitut"),
            new Lang("iu-Latn", "Inuktitut (Latin)", "Inuktitut (Latin)"),
            new Lang("ja", "日本語", "Japanese"),
            new Lang("ka", "ქართული", "Georgian"),
            new Lang("kk", "Қазақ Тілі", "Kazakh"),
            new Lang("km", "ខ្មែរ", "Khmer"),
            new Lang("kmr", "Kurdî (Bakur)", "Kurdish (Northern)"),
            new Lang("kn", "ಕನ್ನಡ", "Kannada"),
            new Lang("ko", "한국어", "Korean"),
            new Lang("ks", "کٲشُر", "Kashmiri"),
            new Lang("ku", "Kurdî (Navîn)", "Kurdish (Central)"),
            new Lang("ky", "Кыргызча", "Kyrgyz"),
            new Lang("lb", "Lëtzebuergesch", "Luxembourgish"),
            new Lang("ln", "Lingála", "Lingala"),
            new Lang("lo", "ລາວ", "Lao"),
            new Lang("lt", "Lietuvių", "Lithuanian"),
            new Lang("lug", "Ganda", "Ganda"),
            new Lang("lv", "Latviešu", "Latvian"),
            new Lang("lzh", "中文 (文言文)", "Chinese (Literary)"),
            new Lang("mai", "मैथिली", "Maithili"),
            new Lang("mg", "Malagasy", "Malagasy"),
            new Lang("mi", "Te Reo Māori", "Māori"),
            new Lang("mk", "Македонски", "Macedonian"),
            new Lang("ml", "മലയാളം", "Malayalam"),
            new Lang("mn-Cyrl", "Монгол", "Mongolian (Cyrillic)"),
            new Lang("mni", "ꯃꯩꯇꯩꯂꯣꯟ", "Manipuri"),
            new Lang("mn-Mong", "ᠮᠣᠩᠭᠣᠯ ᠬᠡᠯᠡ", "Mongolian (Traditional)"),
            new Lang("mr", "मराठी", "Marathi"),
            new Lang("ms", "Melayu", "Malay"),
            new Lang("mt", "Malti", "Maltese"),
            new Lang("mww", "Hmong Daw", "Hmong Daw"),
            new Lang("my", "မြန်မာ", "Myanmar (Burmese)"),
            new Lang("nb", "Norsk Bokmål", "Norwegian"),
            new Lang("ne", "नेपाली", "Nepali"),
            new Lang("nl", "Nederlands", "Dutch"),
            new Lang("nso", "Sesotho sa Leboa", "Sesotho sa Leboa"),
            new Lang("nya", "Nyanja", "Nyanja"),
            new Lang("or", "ଓଡ଼ିଆ", "Odia"),
            new Lang("otq", "Hñähñu", "Querétaro Otomi"),
            new Lang("pa", "ਪੰਜਾਬੀ", "Punjabi"),
            new Lang("pl", "Polski", "Polish"),
            new Lang("prs", "دری", "Dari"),
            new Lang("ps", "پښتو", "Pashto"),
            new Lang("pt", "Português (Brasil)", "Portuguese (Brazil)"),
            new Lang("pt-PT", "Português (Portugal)", "Portuguese (Portugal)"),
            new Lang("ro", "Română", "Romanian"),
            new Lang("ru", "Русский", "Russian"),
            new Lang("run", "Rundi", "Rundi"),
            new Lang("rw", "Kinyarwanda", "Kinyarwanda"),
            new Lang("sd", "سنڌي", "Sindhi"),
            new Lang("si", "සිංහල", "Sinhala"),
            new Lang("sk", "Slovenčina", "Slovak"),
            new Lang("sl", "Slovenščina", "Slovenian"),
            new Lang("sm", "Gagana Sāmoa", "Samoan"),
            new Lang("sn", "chiShona", "Shona"),
            new Lang("so", "Soomaali", "Somali"),
            new Lang("sq", "Shqip", "Albanian"),
            new Lang("sr-Cyrl", "Српски (ћирилица)", "Serbian (Cyrillic)"),
            new Lang("sr-Latn", "Srpski (latinica)", "Serbian (Latin)"),
            new Lang("st", "Sesotho", "Sesotho"),
            new Lang("sv", "Svenska", "Swedish"),
            new Lang("sw", "Kiswahili", "Swahili"),
            new Lang("ta", "தமிழ்", "Tamil"),
            new Lang("te", "తెలుగు", "Telugu"),
            new Lang("th", "ไทย", "Thai"),
            new Lang("ti", "ትግር", "Tigrinya"),
            new Lang("tk", "Türkmen Dili", "Turkmen"),
            new Lang("tlh-Latn", "Klingon (Latin)", "Klingon (Latin)"),
            new Lang("tlh-Piqd", "Klingon (pIqaD)", "Klingon (pIqaD)"),
            new Lang("tn", "Setswana", "Setswana"),
            new Lang("to", "Lea Fakatonga", "Tongan"),
            new Lang("tr", "Türkçe", "Turkish"),
            new Lang("tt", "Татар", "Tatar"),
            new Lang("ty", "Reo Tahiti", "Tahitian"),
            new Lang("ug", "ئۇيغۇرچە", "Uyghur"),
            new Lang("uk", "Українська", "Ukrainian"),
            new Lang("ur", "اردو", "Urdu"),
            new Lang("uz", "O‘Zbek", "Uzbek (Latin)"),
            new Lang("vi", "Tiếng Việt", "Vietnamese"),
            new Lang("xh", "isiXhosa", "Xhosa"),
            new Lang("yo", "Èdè Yorùbá", "Yoruba"),
            new Lang("yua", "Yucatec Maya", "Yucatec Maya"),
            new Lang("yue", "粵語 (繁體)", "Cantonese (Traditional)"),
            new Lang("zh-Hans", "中文 (简体)", "Chinese Simplified"),
            new Lang("zh-Hant", "繁體中文 (繁體)", "Chinese Traditional"),
            new Lang("zu", "Isi-Zulu", "Zulu"),
        };

        public static Lang ByCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            foreach (Lang l in All)
                if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
            // A bare code where the list carries a variant: "sr" finds Serbian in
            // whichever script comes first, which is better than finding nothing.
            foreach (Lang l in All)
            {
                int dash = l.Code.IndexOf('-');
                if (dash > 0 && string.Equals(l.Code.Substring(0, dash), code, StringComparison.OrdinalIgnoreCase))
                    return l;
            }
            return null;
        }

        /// <summary>Where the list should open for a book whose language was
        /// detected, or for a reader who has translated before.</summary>
        public static int IndexOf(string code)
        {
            Lang l = ByCode(code);
            return l == null ? -1 : All.IndexOf(l);
        }
    }
}