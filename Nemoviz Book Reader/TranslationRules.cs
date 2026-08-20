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
            { "hr", Croatian }
        };

        private const string Croatian =
@"
Pravila za književni prijevod na hrvatski:

- Prevedi tekst prirodnim, književnim standardnim hrvatskim jezikom. Ne prevodi doslovno. Prenesi značenje, ton, atmosferu, emociju i stil izvornika tako da tekst zvuči kao da je izvorno napisan na hrvatskom.
- Poštuj standardnu hrvatsku gramatiku, pravopis, sintaksu i prirodan red riječi. Izbjegavaj konstrukcije preslikane iz izvornog jezika.
- Ne ponavljaj nepotrebno osobne i posvojne zamjenice. Kada kontekst jasno pokazuje osobu, koristi prirodnu hrvatsku rečeničnu strukturu bez suvišnog ponavljanja zamjenice.
- Ne ponavljaj nepotrebno imena likova i subjekte ako je iz konteksta jasno na koga se rečenica odnosi.
- Izbjegavaj neprirodno ponavljanje istih riječi, glagola i konstrukcija. Kada smisao dopušta, koristi prirodnu hrvatsku sinonimiju i mijenjaj strukturu rečenice.
- Ne preslikavaj engleski ili drugi strani red riječi. Slobodno preuredi rečenicu kada je to potrebno da bi zvučala prirodno na hrvatskom.
- Pazi na prirodnu uporabu glagolskih vidova, padeža, roda i broja, glagolskih vremena, prijedloga i veznika.
- Dijalog mora zvučati kao stvaran hrvatski govor, u skladu s karakterom, dobi, raspoloženjem i odnosom među likovima. Ne čini dijalog knjiški ukočenim ako izvornik nije takav.
- Psovke, humor, ironiju, sarkazam, nježnost, grubost i druge registre prenesi funkcionalnim hrvatskim ekvivalentom, a ne doslovnim prijevodom.
- Idiome, frazeme i ustaljene izraze prevodi njihovim prirodnim hrvatskim ekvivalentima kada postoje. Ne zadržavaj strani izraz ako postoji prirodan hrvatski izraz koji prenosi isto značenje.
- Strane izraze prevodi kad god je to prirodno i moguće. Ne prevodi termine, nazive ili izraze koje je u hrvatskom prirodnije ostaviti u izvornom obliku.
- Osobna i druga strana imena u pravilu zadrži u izvornom obliku i u latinici, ali ih OBAVEZNO sklanjaj po hrvatskim padežima: ""u Tobiju"", ""s Kristom"", ""Vonvaltova presuda"" — nikada ""u programu Tobi"". Ako za ime, povijesnu osobu ili zemljopisni naziv postoji uvriježen hrvatski oblik, koristi taj oblik. Jednom izabran oblik koristi dosljedno kroz cijeli tekst.
- Nazive mjesta, institucija, titula i drugih pojmova prevedi ili prilagodi prema prirodnoj i standardnoj hrvatskoj uporabi. Ne izmišljaj prijevod vlastitog imena ako se ono u hrvatskom uobičajeno ne prevodi.
- Čuvaj značenje izvornika. Ne dodaj informacije kojih nema. Ne izostavljaj sadržaj. Ne ublažavaj niti pojačavaj značenje bez razloga.
- Čuvaj karakterizaciju likova. Likovi trebaju zadržati svoj način govora i međusobne razlike.
- Čuvaj ritam i duljinu rečenica kada su stilski važni, ali ne po cijenu neprirodnog hrvatskog. Dugu rečenicu smiješ prirodno preoblikovati ako bi doslovna struktura bila teška ili nejasna.
- Dosljedno koristi ista imena, termine, nadimke, titule, način obraćanja i gramatičke izbore kroz cijelu knjigu.
- Prijevod mora biti točan, tečan, prirodan i književan. Čitatelj ne smije osjećati strukturu stranog jezika iza hrvatskog teksta.
";
    }
}
