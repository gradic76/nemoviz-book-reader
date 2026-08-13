using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Works out what language a book is in, so an imported text book is read by
    /// a voice that actually speaks it instead of whatever the Settings default
    /// happens to be.
    ///
    /// <para>Three layers, cheapest first:
    /// <b>1. Script</b> — Greek, Cyrillic, Arabic, Hebrew, Hangul, kana and Han
    /// settle the language (or narrow it to one family) from Unicode ranges alone.
    /// <b>2. Stopwords</b> — the share of all words that are one of a language's
    /// commonest. On a whole book this is decisive: the winner takes 27–58 % of
    /// the tokens where the runner-up takes 6–27 %.
    /// <b>3. Neighbour markers</b> — for the pair stopwords cannot separate,
    /// Croatian and Serbian, which share their common words almost exactly. The
    /// ijekavian/ekavian axis (vrijeme/vreme, dijete/dete, prije/pre) decides it,
    /// backed by everyday lexical pairs (tisuća/hiljada, kruh/hleb).</para>
    ///
    /// <para><b>Measured on ~85 real books</b> (txt, docx/odt/rtf, epub, braille,
    /// Kindle, DAISY, in en/hr/sr/fr/es/pt/el/ar/vi): every book with extractable
    /// text was placed correctly at the level that matters — which voice to read
    /// it with. The two "misses" were a script not in the table and image-only
    /// PDFs with no text at all, and both are reported as <i>unknown</i> rather
    /// than guessed, which is what the thresholds are for.</para>
    ///
    /// <para><b>Declared metadata does not win automatically.</b> Of 24 samples
    /// that declared a language, 4 (17 %) declared it wrongly — three Vietnamese
    /// DAISY books and a Greek one all said "en". So when the text is confident
    /// and disagrees with the file, the text wins; when it isn't, the declaration
    /// stands. See <see cref="Resolve"/>.</para>
    /// </summary>
    public static class LanguageDetector
    {
        /// <summary>Below this share of tokens nothing is claimed: the text is
        /// broken, or in a language/script the table doesn't cover.</summary>
        private const double MinScore = 0.10;
        /// <summary>And the winner has to beat the runner-up by this much,
        /// otherwise the two are too close to call.</summary>
        private const double MinMargin = 0.05;

        // The commonest words of each language. Kept deliberately short — this is
        // a language identifier, not a dictionary — and only for languages a
        // reader is realistically going to meet.
        private static readonly Dictionary<string, string[]> Stopwords = Build(new Dictionary<string, string>
        {
            ["en"] = "the of and to a in that is was he for it with as his on be at by i this had not are but from or have an they which one you were her all she there would their we him been has when who will more no if out so said what up its about into than them can only other new some could time these two may then do first any my now such like our over man me even most made after also did many before must through back years where much your way well down should because each just those people how too little state good very make world still own see men work long get here between both life being under never day same another know while last might us great old year off come since against go came right used take three",
            ["de"] = "der die und in den von zu das mit sich des auf für ist im dem nicht ein eine als auch es an werden aus er hat dass sie nach wird bei einer um am sind noch wie einem über einen so zum war haben nur oder aber vor zur bis mehr durch man sein wurde sei wir was wenn wieder kann schon vom ihre uns unter ihr diese alle doch dann seine bereits andere ihm mich mir ihn",
            ["fr"] = "de la le et les des en un une du dans il que pour qui sur ne pas ce se au plus par avec son sa mais ou ses on lui nous comme tout je elle été être avoir fait sont cette aux leur si tous même y deux quand faire vous dont là autre bien peu encore chez sans depuis toujours après avant moins toute très aussi sous entre cet dit alors jamais elles ces mon",
            ["it"] = "di che e il la per non un una in del al con si le da è più della sono anche come ma ha o suo se lo agli gli dei nel alla dal ci io questo tutto quando essere hanno cosa quello loro solo prima me era tra due mi te fatto senza dopo ancora molto sempre perché già dove tutti fra alle nella",
            ["es"] = "de la que el en y a los del se las por un para con no una su al lo como más pero sus le ya o este sí porque esta entre cuando muy sin sobre también me hasta hay donde quien desde todo nos durante todos uno les ni contra otros ese eso ante ellos esto mí antes algunos qué unos yo otro otras",
            ["pt"] = "de a o que e do da em um para com não uma os no se na por mais as dos como mas ao ele das à seu sua ou quando muito nos já eu também só pelo pela até isso ela entre depois sem mesmo aos seus quem nas me esse eles você essa num nem suas meu às minha numa pelos elas qual",
            ["nl"] = "de van het een en in is dat op te zijn met voor niet aan er die als maar om ook door uit nog dan bij ze had heeft over naar of deze werd tot toen hij zij wij haar hun wat ik je we me mijn geen wel meer nu al wordt kan worden onder tegen",
            ["hr"] = "i u je na se da za od su sa ne kao ali ili to bio bila će te po pa ga joj mu im ih koji koja koje kada nije samo tako više još uvijek nakon prije ovo ono ova jedan jedna dva tri kroz među pod nad bez oko iznad ispod prema zbog dok čak jer ako neka svoje svoju svoj njih nam vam nas vas mene tebe njega nju",
            ["sl"] = "in je na se za da od so pa ki ne bo tudi pri po kot ali ta te ti to bil bila kaj ker ko še vedno samo lahko brez med proti čez zaradi kjer nekaj svoje svojo jih jim nam vam mene tebe njega njo vse vsi zelo prav sem si smo ste bomo",
            ["cs"] = "a v na se že je do to jsem si ale za by ze jsou který jako po co nebo když už tak jen ještě podle před může být tam kde této jeho jejich však mezi která které jsme byl byla bylo tato tento",
            ["sk"] = "a v na sa že je do to som si ale za zo sú ktorý ako po čo alebo keď už tak len ešte podľa pred môže byť tam kde tejto jeho ich však medzi ktorá ktoré sme bol bola bolo tento táto",
            ["pl"] = "w i na z do nie się że to jest a o jak ale po co za od tak przez tym już tylko ich jego jej być może który która które gdy oraz przy pod nad bez dla lub czy sobie jeszcze bardzo wszystko można trzeba czym tego tej ten ta te",
            ["hu"] = "a az és hogy nem is meg egy de van volt el ki fel be már csak vagy mint ha nagy mi te ő mert még majd után előtt alatt között nélkül miatt szerint felé óta ezt azt ezek azok minden semmi ilyen olyan amikor ahol aki ami lehet kell",
            ["ro"] = "de a în și la cu pe un o care nu se este pentru din au sau ca mai dar când sunt fost lui său sa către despre între fără după până prin peste sub asupra deci însă totuși dacă unde cine ce cum atunci acum aici acolo",
            ["tr"] = "bir ve bu için ile de da ne olarak çok daha var olan gibi kadar sonra ama ya her ancak ise göre den dan tarafından üzere önce diye şey biz siz onlar ben sen o hem hiç yine böyle şimdi zaman",
            ["sv"] = "och att det som en av på är för med den till de i han inte har om men var jag så ett man när kan hon sig från vi eller vad hans sin nu där alla ska efter över bara mycket något andra vara blev sedan",
            ["da"] = "og at det en den til er af for som med de har på ikke der var han jeg men om så et man kan hun sig fra vi eller hvad hans sin nu hvor alle skal efter over kun meget noget andre være blev siden",
            ["no"] = "og at det en den til er av for som med de har på ikke der var han jeg men om så et man kan hun seg fra vi eller hva hans sin nå hvor alle skal etter over bare mye noe andre være ble siden",
            ["fi"] = "ja on ei että se hän oli mutta niin kuin myös kun jos vain sen ne tai koska sekä siitä joka ollut olen olisi vielä nyt sitten kaikki mitä missä kuka hyvin paljon jo",
            ["vi"] = "và của là có trong được một người những cho không với các đã này khi để như từ đến ra nhiều về sẽ nếu thì cũng còn nhưng vì nên tôi anh chị em họ mình rất đang phải làm",
        });

        // Cyrillic is one script but several languages, so it needs its own table.
        private static readonly Dictionary<string, string[]> CyrillicStopwords = Build(new Dictionary<string, string>
        {
            ["ru"] = "и в не на что с он как а то все она так его но да ты к у же вы за бы по только ее мне было вот от меня еще нет о из ему теперь когда даже ну вдруг ли если уже или ни быть был",
            ["sr"] = "и у је на се да за од су са не као али или то био била ће те по па га јој му им их који која које када није само тако више још након пре ово оно један два три кроз међу под над без око због док чак јер ако",
            ["bg"] = "и в на за да се от не с по като е са че този тази това които но или при към след през над под без около защото ако още само така много може има били беше",
            ["uk"] = "і в на з до не що за як так але або це той та які коли ще тільки після перед над під без через тому якщо може мають було буде його її їх ми ви вони",
            ["mk"] = "и во на за да се од со не како но или тоа беше ќе по па го му им ги кој која кое кога само така повеќе уште потоа пред ова она еден два три низ меѓу под над без околу затоа додека дури зашто ако",
        });

        // Croatian/Bosnian/Montenegrin ijekavian against Serbian ekavian. The
        // strongest signal there is, because it runs through every page of a book.
        private static readonly string[] Ijekavian =
        {
            "vrijeme", "dijete", "djeca", "lijepo", "lijep", "svijet", "svijeta", "prije", "poslije",
            "ovdje", "gdje", "mlijeko", "cvijet", "vjerojatno", "vjeruje", "tjedan", "sjeme", "brijeg",
            "riječ", "riječi", "mjesto", "mjesta", "mjesec", "djelo", "vidjeti", "željeti", "htjeti",
            "tijelo", "nedjelja", "srijeda", "uvijek", "dvije", "smiješak", "sjediti", "cijeli", "vijest",
        };
        private static readonly string[] Ekavian =
        {
            "vreme", "dete", "deca", "lepo", "lep", "svet", "sveta", "pre", "posle",
            "ovde", "gde", "mleko", "cvet", "verovatno", "veruje", "nedelja", "seme", "breg",
            "reč", "reči", "mesto", "mesta", "mesec", "delo", "videti", "želeti", "hteti",
            "telo", "sreda", "uvek", "dve", "osmeh", "sedeti", "ceo", "vest",
        };
        // Words that differ whatever the ijekavian/ekavian split does. NOTHING here
        // may appear in both lists — a shared word ("gospodin", "možda") scores for
        // both sides and turns a clear book into a tie.
        private static readonly string[] CroatianOnly =
        {
            "tisuća", "tisuće", "kruh", "obitelj", "točka", "točke", "uvjet", "uvjeti", "sveučilište",
            "kazalište", "glazba", "vlak", "zrak", "tvrtka", "siječnja", "veljače", "ožujka", "travnja",
            "svibnja", "lipnja", "srpnja", "kolovoza", "rujna", "listopada", "studenoga", "prosinca",
            "unatoč", "općenito", "povijest", "znanost", "juha", "otok", "tijekom", "također", "nazočan",
            "tvrtke", "zrakoplov", "nogomet", "kolodvor",
        };
        private static readonly string[] SerbianOnly =
        {
            "hiljada", "hiljade", "hleb", "porodica", "tačka", "tačke", "uslov", "uslovi", "univerzitet",
            "pozorište", "muzika", "voz", "vazduh", "firma", "januara", "februara", "marta", "aprila",
            "maja", "juna", "jula", "avgusta", "septembra", "oktobra", "novembra", "decembra",
            "uprkos", "uopšte", "istorija", "nauka", "supa", "ostrvo", "tokom", "takođe", "prisutan",
            "firme", "avion", "fudbal", "stanica",
        };

        /// <summary>The result: a two-letter code ("hr", "en") or empty when
        /// nothing could be said, plus how sure we are (0..1).</summary>
        public struct Result
        {
            public string Code;
            public double Confidence;
            public bool Known { get { return !string.IsNullOrEmpty(Code); } }
        }

        /// <summary>
        /// The language to use for a book: the text decides when it is sure of
        /// itself, the file's own declaration fills in when it isn't. Producers
        /// get this wrong often enough (17 % of the declared samples) that the
        /// declaration cannot simply be believed — but a confident reading of the
        /// actual words has been right every time it was checked.
        /// </summary>
        public static string Resolve(string declared, string text)
        {
            Result r = Detect(text);
            string decl = Normalize(declared);
            if (!r.Known) return decl;                       // unreadable/unknown → trust the file
            if (string.IsNullOrEmpty(decl)) return r.Code;   // nothing declared → the text
            if (SameLanguage(decl, r.Code)) return decl;     // agree → keep the declared form
            return r.Code;                                   // disagree, and the text is sure
        }

        /// <summary>Detects the language of a body of text. Samples several places
        /// in it rather than the opening, because front matter, a foreword or a
        /// copyright page is regularly in another language.</summary>
        public static Result Detect(string full)
        {
            var none = new Result { Code = "", Confidence = 0 };
            if (string.IsNullOrEmpty(full) || full.Length < 200) return none;
            string text = Sample(full, 12, 4000);

            // 1. Script.
            Dictionary<string, int> script = ScriptCounts(text);
            double letters = 0;
            foreach (int n in script.Values) letters += n;
            if (letters < 100) return none;

            double greek = Share(script, "greek", letters);
            double cyr = Share(script, "cyrillic", letters);
            double hangul = Share(script, "hangul", letters);
            double kana = Share(script, "kana", letters);
            double han = Share(script, "han", letters);
            double arabic = Share(script, "arabic", letters);
            double hebrew = Share(script, "hebrew", letters);

            if (greek > 0.30) return new Result { Code = "el", Confidence = greek };
            if (hangul > 0.20) return new Result { Code = "ko", Confidence = hangul };
            if (kana > 0.05) return new Result { Code = "ja", Confidence = kana };
            if (han > 0.20) return new Result { Code = "zh", Confidence = han };
            if (arabic > 0.30) return new Result { Code = "ar", Confidence = arabic };
            if (hebrew > 0.30) return new Result { Code = "he", Confidence = hebrew };

            // 2. Stopwords.
            Dictionary<string, int> freq = WordFrequencies(text, out int total);
            if (total < 50) return none;

            var table = cyr > 0.30 ? CyrillicStopwords : Stopwords;
            string best = null, second = null;
            double bestScore = 0, secondScore = 0;
            foreach (var kv in table)
            {
                int hits = 0;
                foreach (string w in kv.Value) hits += Count(freq, w);
                double score = (double)hits / total;
                if (score > bestScore)
                {
                    second = best; secondScore = bestScore;
                    best = kv.Key; bestScore = score;
                }
                else if (score > secondScore) { second = kv.Key; secondScore = score; }
            }

            if (best == null || bestScore < MinScore) return none;
            if (bestScore - secondScore < MinMargin) return none;   // too close to call

            // 3. Croatian vs Serbian, which the common words cannot separate.
            if (cyr <= 0.30 && (best == "hr" || best == "sr"
                || (best == "sl" && second == "hr")))
                return SplitBcms(freq, bestScore - secondScore);

            return new Result { Code = best, Confidence = bestScore - secondScore };
        }

        private static Result SplitBcms(Dictionary<string, int> freq, double margin)
        {
            int ije = 0, eka = 0, hrLex = 0, srLex = 0;
            foreach (string w in Ijekavian) ije += Count(freq, w);
            foreach (string w in Ekavian) eka += Count(freq, w);
            foreach (string w in CroatianOnly) hrLex += Count(freq, w);
            foreach (string w in SerbianOnly) srLex += Count(freq, w);

            // The lexical pairs count double: they are rarer but decide outright,
            // while a stray ijekavian form can be a quotation or a name.
            double hr = ije + hrLex * 2.0;
            double sr = eka + srLex * 2.0;
            if (hr + sr < 5) return new Result { Code = "hr", Confidence = 0.2 };   // too little to tell; hr is the house default
            return new Result
            {
                Code = hr >= sr ? "hr" : "sr",
                Confidence = Math.Min(1.0, margin + Math.Abs(hr - sr) / Math.Max(1.0, hr + sr))
            };
        }

        /// <summary>Two language tags naming the same language ("hr" vs "hr-HR"),
        /// and the Balkan tags that share a voice ("bs"/"sh"/"hbs" → the Croatian
        /// family) count as agreement.</summary>
        public static bool SameLanguage(string a, string b)
        {
            string x = Primary(a), y = Primary(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;
            return IsBcms(x) && IsBcms(y);
        }

        private static bool IsBcms(string code)
        {
            return code == "hr" || code == "sr" || code == "bs" || code == "sh" || code == "hbs" || code == "cnr";
        }

        // There is deliberately NO table of languages that may stand in for one
        // another (Gordan, 2026-07-29). A first version had one — Serbian and
        // Croatian reading each other, Czech and Slovak reading both — and it was
        // rejected outright: Croatian is Croatian, Serbian is Serbian, Czech is
        // Czech. A book gets a voice in ITS OWN language or it gets told there
        // isn't one; NBR does not decide for the reader that a near-enough accent
        // will do. If they want a Mandarin voice for a Russian book that is their
        // choice to make by hand, but it is never one NBR offers by guessing that
        // two languages are close.
        //
        // Note that SameLanguage is a different question and still groups BCMS —
        // it answers "is this the same language", which matters to DETECTION.
        // Choosing a voice asks the narrower question and matches on the primary
        // code alone.

        /// <summary>"hr-HR" → "hr"; empty stays empty.</summary>
        public static string Primary(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            string t = tag.Trim().ToLowerInvariant();
            int cut = t.IndexOfAny(new[] { '-', '_' });
            return cut > 0 ? t.Substring(0, cut) : t;
        }

        /// <summary>Cleans a declared tag to the form we store ("hr-hr" → "hr-HR",
        /// "eng" → "en"); empty when it isn't a language tag at all.</summary>
        public static string Normalize(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            string t = tag.Trim().Replace('_', '-');
            try { return CultureInfo.GetCultureInfo(t).Name; }
            catch { }
            // A tag that names no known language is junk, not a language: DAISY
            // producers write things like "VN" (a country, not a language) there.
            string p = Primary(t);
            try { return CultureInfo.GetCultureInfo(p).Name; }
            catch { return ""; }
        }

        /// <summary>A language's name <b>in that language</b> — "hr-HR" →
        /// "Hrvatski (Hrvatska)", "es-ES" → "Español (España)", "el-GR" →
        /// "Ελληνικά (Ελλάδα)". Falls back to the tag itself.
        ///
        /// <para><b>The convention for every language list in NBR</b> (Gordan,
        /// 2026-08-14): voices, OCR recognizers, the interface language, a book's
        /// own language — all of them. His reasoning is better than mine would
        /// have been: it means the names <b>never need translating</b>. A list
        /// written in the reader's own interface language only helps someone who
        /// already reads that language; written in each language's own words it
        /// helps the native speaker AND anyone who knows the language, in every
        /// localisation NBR will ever have, with no work per localisation.</para>
        ///
        /// <para>It replaces <c>EnglishName</c>, which gave "Croatian (Croatia)"
        /// to a Croatian reader. <c>DisplayName</c> was no better: it follows the
        /// APPLICATION's UI culture, so the same call returned "Croatian" here and
        /// "hrvatski" inside the player — the one thing a convention must not
        /// do.</para>
        ///
        /// <para>The first letter is raised because these are list entries, and a
        /// lower-case row reads as a fault to anyone who can see it. Windows
        /// itself leaves them as the culture data has them — <c>hrvatski</c>,
        /// <c>español</c> — which is correct orthography and looks like a
        /// mistake in a list box.</para></summary>
        public static string DisplayName(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            try
            {
                string n = CultureInfo.GetCultureInfo(tag).NativeName;
                if (string.IsNullOrEmpty(n)) return tag;
                return char.ToUpper(n[0], CultureInfo.InvariantCulture) + n.Substring(1);
            }
            catch { return tag; }
        }

        private static Dictionary<string, string[]> Build(Dictionary<string, string> src)
        {
            var d = new Dictionary<string, string[]>();
            foreach (var kv in src) d[kv.Key] = kv.Value.Split(' ');
            return d;
        }

        private static double Share(Dictionary<string, int> counts, string key, double total)
        {
            int n;
            return counts.TryGetValue(key, out n) ? n / total : 0;
        }

        private static int Count(Dictionary<string, int> freq, string word)
        {
            int n;
            return freq.TryGetValue(word, out n) ? n : 0;
        }

        private static Dictionary<string, int> WordFrequencies(string text, out int total)
        {
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            total = 0;
            foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"\p{L}+"))
            {
                total++;
                string w = m.Value;
                int n;
                freq[w] = freq.TryGetValue(w, out n) ? n + 1 : 1;
            }
            return freq;
        }

        private static string Sample(string text, int slices, int sliceLen)
        {
            if (text.Length <= slices * sliceLen) return text;
            var sb = new StringBuilder(slices * sliceLen + slices);
            for (int i = 0; i < slices; i++)
            {
                int start = (int)((double)text.Length / (slices + 1) * (i + 1));
                if (start > text.Length - sliceLen) start = text.Length - sliceLen;
                sb.Append(text, start, sliceLen).Append(' ');
            }
            return sb.ToString();
        }

        private static Dictionary<string, int> ScriptCounts(string text)
        {
            var d = new Dictionary<string, int>();
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) continue;
                string s;
                if (c >= 0x0370 && c <= 0x03FF) s = "greek";
                else if (c >= 0x0400 && c <= 0x04FF) s = "cyrillic";
                else if (c >= 0x0590 && c <= 0x05FF) s = "hebrew";
                else if (c >= 0x0600 && c <= 0x06FF) s = "arabic";
                else if (c >= 0x3040 && c <= 0x30FF) s = "kana";
                else if (c >= 0x4E00 && c <= 0x9FFF) s = "han";
                else if (c >= 0xAC00 && c <= 0xD7AF) s = "hangul";
                else s = "latin";
                int n;
                d[s] = d.TryGetValue(s, out n) ? n + 1 : 1;
            }
            return d;
        }
    }
}
