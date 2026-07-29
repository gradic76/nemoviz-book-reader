using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>How the voice for a book was arrived at. The caller needs this,
    /// not just the name: <see cref="NoVoice"/> is the case the reader has to be
    /// TOLD about, and there is no way to spot it from a name alone.</summary>
    public enum VoiceSource
    {
        /// <summary>Nothing installed speaks the book's language. Nothing is
        /// substituted; the reader is told and chooses.</summary>
        NoVoice,
        /// <summary>The voice chosen in Settings for this very language.</summary>
        LanguageDefault,
        /// <summary>No choice was made, so the first installed voice that speaks
        /// the language.</summary>
        LanguageInstalled,
        /// <summary>The book's language is unknown, so there was nothing to match
        /// on and the global default is simply it.</summary>
        GlobalDefault,
    }

    /// <summary>
    /// Which voice reads a book, in one place. Both the player (opening a book)
    /// and Properties (showing what it will be read with) ask this, because two
    /// copies of the rule drift apart — they already had, before this existed.
    ///
    /// <para>The whole rule, and it is short: <b>the voice chosen for that
    /// language in Settings; failing that, the first installed voice that speaks
    /// it; failing that, nothing.</b></para>
    ///
    /// <para><b>A language is itself and nothing else.</b> Croatian is Croatian,
    /// Serbian is Serbian (Gordan, 2026-07-29) — matching is on the primary code,
    /// NOT through <see cref="LanguageDetector.SameLanguage"/>, which groups BCMS
    /// because that is the right answer to a different question. Nothing near
    /// enough is ever offered in place of the real thing, and nothing falls
    /// through to "the first voice on the machine": a voice that cannot speak the
    /// language does not read the book badly, it reads it as noise. When there is
    /// no voice the answer is empty and <see cref="VoiceSource.NoVoice"/>, so the
    /// caller can say so and let the reader pick for themselves.</para>
    /// </summary>
    public static class VoiceChooser
    {
        /// <summary>The voice a book in <paramref name="lang"/> should be read
        /// with. <paramref name="voices"/> is the installed catalog — element 1 is
        /// ignored, so both the player's (name, vendor, language) and the
        /// dialog's (name, engine, language) fit without being reshaped.</summary>
        public static string ForLanguage(AppSettings settings,
                                         IEnumerable<(string Name, string Group, string Language)> voices,
                                         string lang, out VoiceSource how)
        {
            string global = settings != null ? (settings.TtsVoice ?? "") : "";

            // Nothing was worked out about the book, so there is nothing to match
            // against and the global default is all there is to go on.
            if (string.IsNullOrEmpty(lang))
            {
                how = VoiceSource.GlobalDefault;
                return global;
            }

            string code = LanguageDetector.Primary(lang);
            var speaks = new List<string>();
            if (voices != null)
                foreach (var c in voices)
                    if (LanguageDetector.Primary(c.Language) == code) speaks.Add(c.Name);

            if (speaks.Count == 0)
            {
                how = VoiceSource.NoVoice;
                return "";
            }

            // The reader's own choice for this language, as long as it is still
            // installed and still speaks it.
            string own = settings != null ? settings.LanguageVoice(code) : "";
            foreach (string name in speaks)
                if (string.Equals(name, own, StringComparison.OrdinalIgnoreCase))
                {
                    how = VoiceSource.LanguageDefault;
                    return name;
                }

            // Otherwise the first that speaks it. The catalog is ordered
            // in-process first, so a 64-bit voice wins over the satellite.
            how = VoiceSource.LanguageInstalled;
            return speaks[0];
        }

        public static string ForLanguage(AppSettings settings,
                                         IEnumerable<(string Name, string Group, string Language)> voices,
                                         string lang)
        {
            VoiceSource how;
            return ForLanguage(settings, voices, lang, out how);
        }

        /// <summary>Every installed voice that speaks a language, in catalog
        /// order. The list Settings and Properties both offer.</summary>
        public static List<string> VoicesFor(IEnumerable<(string Name, string Group, string Language)> voices,
                                             string lang)
        {
            var list = new List<string>();
            string code = LanguageDetector.Primary(lang);
            if (voices == null) return list;
            foreach (var c in voices)
                if (code.Length == 0 || LanguageDetector.Primary(c.Language) == code)
                    list.Add(c.Name);
            return list;
        }
    }
}
