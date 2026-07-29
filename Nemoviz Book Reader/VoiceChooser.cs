using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>How the voice for a book was arrived at. The caller needs this,
    /// not just the name: the difference between "you chose this for Serbian" and
    /// "nothing here speaks Serbian, so it is reading in whatever the default is"
    /// is the difference between saying nothing and saying something.</summary>
    public enum VoiceSource
    {
        /// <summary>Nothing installed, nothing chosen — there is no voice.</summary>
        None,
        /// <summary>The voice chosen in Settings for this very language.</summary>
        LanguageDefault,
        /// <summary>The voice chosen for a language that reads this one.</summary>
        RelatedDefault,
        /// <summary>No choice was made, but something installed speaks it.</summary>
        LanguageInstalled,
        /// <summary>No choice was made; something installed reads it without
        /// speaking it.</summary>
        RelatedInstalled,
        /// <summary>The global default, and it does speak the language.</summary>
        GlobalDefault,
        /// <summary>The global default, which does NOT speak the language. The
        /// book will be read in the wrong accent, or worse — the one case the
        /// user has to be told about.</summary>
        GlobalMismatch,
    }

    /// <summary>
    /// Which voice reads a book, in one place. Both the player (opening a book)
    /// and Properties (showing what it will be read with) ask this, because two
    /// copies of a rule this fiddly drift apart — they already had, before this
    /// existed.
    ///
    /// <para>The order is: <b>the language's own chosen voice → a related
    /// language's chosen voice → any installed voice that speaks it → any
    /// installed voice that reads it → the global default</b>. A choice the user
    /// made always outranks a coincidence of what is installed.</para>
    ///
    /// <para>It deliberately never falls through to "the first voice on the
    /// machine". A voice that cannot speak the language does not read the book
    /// badly, it reads it as noise, and a silent wrong choice is worse than an
    /// empty box and a message (§10c). The last step, the global default, is
    /// reported as <see cref="VoiceSource.GlobalMismatch"/> when it cannot speak
    /// the language, precisely so the caller can say so.</para>
    /// </summary>
    public static class VoiceChooser
    {
        /// <summary>The voice a book in <paramref name="lang"/> should be read
        /// with. <paramref name="voices"/> is the installed catalog — element 1 is
        /// ignored, so both the player's (name, vendor, language) and the
        /// dialog's (name, engine, language) fit without being reshaped.</summary>
        public static string ForLanguage(AppSettings settings,
                                         IEnumerable<(string Name, string Group, string Language)> voices,
                                         string lang, out VoiceSource how, out string via)
        {
            how = VoiceSource.None;
            via = "";
            string global = settings != null ? (settings.TtsVoice ?? "") : "";

            var installed = new List<(string Name, string Group, string Language)>();
            if (voices != null) installed.AddRange(voices);

            // With no idea what the book is in there is nothing to match against,
            // so the global default is simply it.
            if (string.IsNullOrEmpty(lang))
            {
                how = global.Length > 0 ? VoiceSource.GlobalDefault : VoiceSource.None;
                return global;
            }

            // 1 — chosen for this language.
            string own = settings != null ? settings.LanguageVoice(lang) : "";
            if (own.Length > 0 && IsInstalled(installed, own))
            {
                how = VoiceSource.LanguageDefault;
                return own;
            }

            // 2 — chosen for a language that reads this one.
            foreach (string neighbour in LanguageDetector.StandInsFor(lang))
            {
                string v = settings != null ? settings.LanguageVoice(neighbour) : "";
                if (v.Length > 0 && IsInstalled(installed, v))
                {
                    how = VoiceSource.RelatedDefault;
                    via = neighbour;
                    return v;
                }
            }

            // 3 — nothing chosen, but something speaks it. In-process voices come
            // first in the catalog, so a 64-bit voice wins over the satellite.
            foreach (var c in installed)
                if (LanguageDetector.SameLanguage(c.Language, lang))
                {
                    how = VoiceSource.LanguageInstalled;
                    return c.Name;
                }

            // 4 — nothing speaks it, but something reads it.
            foreach (string neighbour in LanguageDetector.StandInsFor(lang))
                foreach (var c in installed)
                    if (LanguageDetector.SameLanguage(c.Language, neighbour))
                    {
                        how = VoiceSource.RelatedInstalled;
                        via = neighbour;
                        return c.Name;
                    }

            // 5 — the global default, and whether it is any good for this book.
            if (global.Length == 0)
            {
                how = VoiceSource.None;
                return "";
            }
            how = SpeaksOrReads(installed, global, lang) ? VoiceSource.GlobalDefault
                                                        : VoiceSource.GlobalMismatch;
            return global;
        }

        public static string ForLanguage(AppSettings settings,
                                         IEnumerable<(string Name, string Group, string Language)> voices,
                                         string lang)
        {
            VoiceSource how;
            string via;
            return ForLanguage(settings, voices, lang, out how, out via);
        }

        private static bool IsInstalled(List<(string Name, string Group, string Language)> voices, string name)
        {
            foreach (var c in voices)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool SpeaksOrReads(List<(string Name, string Group, string Language)> voices,
                                          string name, string lang)
        {
            foreach (var c in voices)
            {
                if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (LanguageDetector.SameLanguage(c.Language, lang)) return true;
                foreach (string neighbour in LanguageDetector.StandInsFor(lang))
                    if (LanguageDetector.SameLanguage(c.Language, neighbour)) return true;
                return false;
            }
            return false;   // not installed at all
        }
    }
}
