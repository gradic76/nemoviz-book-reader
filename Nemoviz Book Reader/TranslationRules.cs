using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>How a particular target language wants to be written — the layer
    /// between the rules that hold for every language and the facts that hold for
    /// one book.
    ///
    /// <para><b>Why it is its own layer.</b> The neutral rules must serve 138
    /// languages, so they cannot say anything about cases, aspect or the dropping
    /// of pronouns. The book facts change with every book. What sits between is
    /// stable for a whole language and reusable across every book translated into
    /// it — which is also what makes it worth putting SECOND in the prompt: prompt
    /// caching pays for a stable prefix, and these two layers are identical for
    /// every Croatian book NBR will ever translate.</para>
    ///
    /// <para><b>Compiled in, not shipped as a file</b>, for the reason
    /// <see cref="TranslationLanguages"/> is: a file can go missing from an
    /// install, and a translator that silently loses its rules produces work that
    /// looks finished.</para>
    ///
    /// <para><b>The Croatian text is a colleague of Gordan's</b>, used with her
    /// agreement and kept in her words, with two changes he approved:</para>
    ///
    /// <para><b>1. Names.</b> Hers said to keep foreign names in their original
    /// form, which is right and incomplete — it is the wording that failed here
    /// once already. A model obeying it literally will not DECLINE the name, and
    /// Croatian then pads around the hole: "u programu Tobi" where a translator
    /// writes "u Tobiju". Keeping a name and inflecting it are different things, so
    /// the rule now says both.</para>
    ///
    /// <para><b>2. One sentence per line is GONE.</b> It suits her workflow, which
    /// is a chat window she reads and edits. It would break ours: the paragraph
    /// count is one of the checks, measured catching a model that returned 63
    /// source lines as 51 by merging and another that returned 68 by splitting, and
    /// deliberately changing the line structure would retire that check and make
    /// reassembly guesswork. NBR splits into sentences itself for reading and
    /// braille, so nothing is lost.</para></summary>
    internal static class TranslationRules
    {
        /// <summary>The rules for a target language, or an empty string where none
        /// have been written. An empty block is not a failure — it is every
        /// language except the ones somebody has sat down and done.</summary>
        public static string For(string targetLang)
        {
            if (string.IsNullOrEmpty(targetLang)) return "";
            string code = targetLang.Trim().ToLowerInvariant();
            int dash = code.IndexOfAny(new[] { '-', '_' });
            if (dash > 0) code = code.Substring(0, dash);
            string rules;
            return byLanguage.TryGetValue(code, out rules) ? rules : "";
        }

        public static bool Has(string targetLang) { return For(targetLang).Length > 0; }

        private static readonly Dictionary<string, string> byLanguage =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "hr", Croatian },
            // One Serbian, in LATIN, and it serves sr-Cyrl too — because For()
            // strips the script off the tag. That is Gordan's call and the reason
            // is in the rules text below.
            { "sr", Serbian }
        };

        private const string Croatian =
@"
Pravila za književni prijevod na hrvatski:

- Prevedi tekst prirodnim, književnim standardnim hrvatskim jezikom. Ne prevodi doslovno. Prenesi značenje, ton, atmosferu, emociju i stil izvornika tako da tekst zvuči kao da je izvorno napisan na hrvatskom.
- Poštuj standardnu hrvatsku gramatiku, pravopis, sintaksu i prirodan red riječi. Izbjegavaj konstrukcije preslikane iz izvornog jezika.
- Ne ponavljaj nepotrebno osobne i posvojne zamjenice. Umjesto konstrukcija poput ""ja sam bio"", ""ja sam htio"", ""ja sam rekao"", kada kontekst to dopušta koristi prirodnije ""bio sam"", ""htio sam"", ""rekao sam"".
- Ne ponavljaj nepotrebno imena likova i subjekte ako je iz konteksta jasno na koga se rečenica odnosi.
- Izbjegavaj neprirodno ponavljanje istih riječi, glagola i konstrukcija. Kada smisao dopušta, koristi prirodnu hrvatsku sinonimiju i mijenjaj strukturu rečenice.
- Ne preslikavaj engleski ili drugi strani red riječi. Slobodno preuredi rečenicu kada je to potrebno da bi zvučala prirodno na hrvatskom.
- Pazi na prirodnu uporabu glagolskih vidova, padeža, roda i broja, glagolskih vremena, prijedloga i veznika. Pazi na prirodan položaj enklitika.
- Engleski Present Perfect, Past Perfect i progresivne oblike ne preslikavaj mehanički. Participne i infinitivne konstrukcije preoblikuj u prirodnu hrvatsku rečenicu. Pasiv preoblikuj u aktiv kada je to prirodnije i kada se značenje ne mijenja.
- Dijalog mora zvučati kao stvaran hrvatski govor, u skladu s karakterom, dobi, raspoloženjem i odnosom među likovima. Ne čini dijalog knjiški ukočenim ako izvornik nije takav.
- Psovke, humor, ironiju, sarkazam, nježnost, grubost i druge registre prenesi funkcionalnim hrvatskim ekvivalentom, a ne doslovnim prijevodom.
- Idiome, frazeme i ustaljene izraze prevodi njihovim prirodnim hrvatskim ekvivalentima kada postoje. Ne zadržavaj strani izraz ako postoji prirodan hrvatski izraz koji prenosi isto značenje. Ne kalkiraj strane kolokacije.
- Strane izraze prevodi kad god je to prirodno i moguće. Ne prevodi termine, nazive ili izraze koje je u hrvatskom prirodnije ostaviti u izvornom obliku.
- SLAVENSKA imena (ruska, poljska, češka, ukrajinska i slična) prenesi prema uobičajenoj hrvatskoj praksi: ""Pjotr"", ""Tatjana"", ""Dostojevski"" — ne u engleskoj transliteraciji ""Piotr"", ""Tatiana"", ""Dostoyevsky"". Jednom izabran oblik koristi dosljedno kroz cijeli tekst.
- Osobna i druga strana imena u pravilu zadrži u izvornom obliku i u latinici, ali ih OBAVEZNO sklanjaj po hrvatskim padežima: ""u Tobiju"", ""s Kristom"", ""Vonvaltova presuda"" — nikada ""u programu Tobi"". Ako za ime, povijesnu osobu ili zemljopisni naziv postoji uvriježen hrvatski oblik, koristi taj oblik. Jednom izabran oblik koristi dosljedno kroz cijeli tekst.
- Nazive mjesta, institucija, titula i drugih pojmova prevedi ili prilagodi prema prirodnoj i standardnoj hrvatskoj uporabi. Ne izmišljaj prijevod vlastitog imena ako se ono u hrvatskom uobičajeno ne prevodi.
- Čuvaj značenje izvornika. Ne dodaj informacije kojih nema. Ne izostavljaj sadržaj. Ne ublažavaj niti pojačavaj značenje bez razloga.
- Čuvaj karakterizaciju likova. Likovi trebaju zadržati svoj način govora i međusobne razlike.
- Čuvaj ritam i duljinu rečenica kada su stilski važni, ali ne po cijenu neprirodnog hrvatskog. Dugu rečenicu smiješ prirodno preoblikovati ako bi doslovna struktura bila teška ili nejasna.
- Dosljedno koristi ista imena, termine, nadimke, titule, način obraćanja i gramatičke izbore kroz cijelu knjigu.
- Sačuvaj odlomke i dijaloge smisleno. Ne spajaj različite replike. Ne mijenjaj raspored teksta bez potrebe.
- Prije nego što vratiš prijevod, pročitaj ga kao samostalan hrvatski tekst. Ako neka rečenica zvuči prevedeno, preoblikuj je tako da zvuči kao da je izvorno napisana na hrvatskom.
- Prijevod mora biti točan, tečan, prirodan i književan. Čitatelj ne smije osjećati strukturu stranog jezika iza hrvatskog teksta.
";

        // ── Serbian ────────────────────────────────────────────────────────
        //
        // Gordan's colleague again, and her Serbian rules are the same document
        // as the Croatian one rather than a translation of it — so two of the
        // three changes below are the ones he already approved for Croatian:
        //
        //   1. ONE SENTENCE PER LINE IS GONE. It suits a chat window she reads
        //      and edits; here it would retire the paragraph-count check, which
        //      has caught a model merging 63 source lines into 51 and another
        //      splitting 68.
        //   2. NAMES MUST BE DECLINED. A model obeying "keep the name" literally
        //      leaves it undeclined and the language pads around the hole.
        //
        //   3. AND ONE THAT IS SERBIAN'S OWN: FOREIGN NAMES STAY IN LATIN, and
        //      are NOT transcribed phonetically. Hers says to adapt them to
        //      Serbian, which is what Serbian normally does — and Gordan
        //      overruled it for this pipe with the case that settles it
        //      (2026-08-28): *"je li Jean Žan ili Džin?"*. Phonetic transcription
        //      needs a glossary, we have none, and a wrong one is silent — the
        //      reader has no way to know the name they are hearing was invented
        //      by a model. A Latin name inside a Serbian sentence is at worst
        //      unidiomatic; a mis-transcribed one is a different person.
        //
        //      AND HER DOCUMENT NOW ASKS FOR IT IN AS MANY WORDS (2026-08-28,
        //      the expanded rules): "Imena prevodi na srpski, npr: Dzejn umesto
        //      Jane, Majkl umesto Michael." Gordan read that and ruled it out a
        //      SECOND time, so this is a settled decision and not an oversight:
        //      it stays out of the hardcoded rules. What he asked for instead is
        //      that it be OVERRIDABLE, and it already is -- the reader notes are
        //      appended after every rule above and under the line "(these take
        //      precedence)", either per book in the translate dialog or standing
        //      for every book in Settings. Anyone who wants phonetic names types
        //      one sentence there; nobody gets them by default.
        //
        //      The same applies to her "soften violent scenes if you must, but do
        //      not retell or drastically shorten them". It would cut outright
        //      refusals, which cost a whole piece, but it licenses the model to
        //      change the text and so contradicts "Ne ublazavaj niti pojacavaj
        //      znacenje bez razloga" three bullets down. Out for the same reason,
        //      reachable the same way.
        //
        // THERE IS THEREFORE ONE SERBIAN AND IT IS IN LATIN, and it is what
        // sr-Cyrl gets as well, since For() strips the script. Writing a Cyrillic
        // variant would have meant either transcribing names — the thing just
        // ruled out — or Latin names embedded in Cyrillic text, which is a
        // decision nobody has taken. Left as it stands rather than guessed at.

        private const string Serbian =
@"
Pravila za književni prevod na srpski:

- Prevedi tekst prirodnim, književnim srpskim jezikom. Ne prevodi bukvalno. Prenesi značenje, ton, atmosferu, emociju i stil originala tako da tekst zvuči kao da je izvorno napisan na srpskom.
- Poštuj standardnu srpsku gramatiku, pravopis, sintaksu i prirodan red reči. Izbegavaj konstrukcije preslikane iz izvornog jezika.
- Ne ponavljaj nepotrebno lične zamenice. Umesto konstrukcija poput ""ja sam bio"", ""ja sam hteo"", ""ja sam rekao"", kada kontekst to dopušta koristi prirodnije ""bio sam"", ""hteo sam"", ""rekao sam"".
- Ne ponavljaj nepotrebno imena likova i subjekte ako je iz konteksta jasno na koga se rečenica odnosi.
- Izbegavaj neprirodno ponavljanje istih reči, glagola i konstrukcija. Kada smisao dopušta, koristi prirodnu srpsku sinonimiju i menjaj strukturu rečenice.
- Ne preslikavaj engleski red reči. Slobodno preuredi rečenicu kada je to potrebno da bi zvučala prirodno na srpskom.
- Pazi na prirodnu upotrebu svršenih i nesvršenih glagola, na padeže, slaganje roda i broja, glagolska vremena, predloge i veznike. Pazi na prirodan položaj enklitika.
- Engleski Present Perfect, Past Perfect i progresivne oblike ne preslikavaj mehanički. Participne i infinitivne konstrukcije preoblikuj u prirodnu srpsku rečenicu. Pasiv preoblikuj u aktiv kada je to prirodnije i kada se značenje ne menja.
- Dijalog mora zvučati kao stvaran govor na srpskom, u skladu sa karakterom, uzrastom, raspoloženjem i odnosom među likovima. Ne pravi dijalog knjiški ukočenim ako original nije takav.
- Psovke, humor, ironiju, sarkazam, nežnost, grubost i druge registre prenesi funkcionalnim srpskim ekvivalentom, a ne doslovnim prevodom.
- Idiome, frazeme i ustaljene izraze prevodi njihovim prirodnim srpskim ekvivalentima kad postoje. Ne zadržavaj strani izraz ako postoji prirodan srpski izraz koji prenosi isto značenje. Ne kalkiraj strane kolokacije.
- Strane izraze prevodi kad god je to prirodno i moguće. Ne prevodi termine, nazive ili izraze koje je u srpskom prirodnije ostaviti u izvornom obliku.
- SLOVENSKA imena (ruska, poljska, češka, ukrajinska i slična) prenesi prema uobičajenoj srpskoj praksi: ""Pjotr"", ""Tatjana"", ""Dostojevski"" — ne u engleskoj transliteraciji ""Piotr"", ""Tatiana"", ""Dostoyevsky"". Jednom izabran oblik koristi dosledno kroz ceo tekst.
- Lična i druga strana imena zadrži u izvornom obliku i u latinici, nemoj ih fonetski transkribovati, ali ih OBAVEZNO menjaj po padežima: ""sa Tobijem"", ""Vonvaltova presuda"", ""kod Jeana"" — nikada nepromenjen oblik uz opisnu konstrukciju. Ako za ime, istorijsku ličnost ili geografski naziv postoji odomaćen srpski oblik, koristi taj oblik. Jednom izabran oblik imena koristi dosledno kroz ceo tekst.
- Nazive mesta, institucija, titula i drugih pojmova prevedi ili prilagodi prema prirodnoj i standardnoj srpskoj upotrebi. Ne izmišljaj prevod vlastitog imena ako se ono u srpskom uobičajeno ne prevodi.
- Čuvaj značenje originala. Ne dodaj informacije kojih nema. Ne izostavljaj sadržaj. Ne ublažavaj niti pojačavaj značenje bez razloga.
- Čuvaj karakterizaciju likova. Likovi treba da zadrže svoj način govora i međusobne razlike.
- Čuvaj ritam i dužinu rečenica kada su stilski važni, ali ne po cenu neprirodnog srpskog. Dugačku rečenicu smeš prirodno preoblikovati ako bi doslovna struktura bila teška ili nejasna.
- Dosledno koristi ista imena, termine, nadimke, titule, način obraćanja i gramatičke izbore kroz celu knjigu. Ako prethodni deo teksta pokazuje rod lika, odnos među likovima ili već usvojen prevod termina, poštuj ga.
- Sačuvaj pasuse i dijaloge smisleno. Ne spajaj različite replike. Ne menjaj raspored teksta bez potrebe.
- Pre nego što vratiš prevod, pročitaj ga kao samostalan srpski tekst. Ako neka rečenica zvuči prevedeno, preoblikuj je tako da zvuči kao da je prvobitno napisana na srpskom.
- Piši srpskom latinicom, sa slovima č, ć, dž, đ, š i ž.
- Prevod mora biti tačan, tečan, prirodan i književan. Čitalac ne treba da oseća strukturu stranog jezika iza srpskog teksta.
";
    }
}
