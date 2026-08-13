using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// One kind of Windows language pack NBR can offer to install — recognition
    /// for reading pictures, or voices for speaking text.
    ///
    /// <para><b>Two lists and not one, by Gordan's call (2026-08-14).</b> A single
    /// list of languages with two ticks per row — recognition, voice — is more
    /// compact and probably closer to how someone thinks ("I want German, and
    /// then what with it"). His judgement was that it is <b>as likely to confuse
    /// as to help</b>, and a two-state row is an odd thing for a screen reader to
    /// read out. Voices with voices, recognition with OCR.</para>
    ///
    /// <para>Both are Features on Demand under the same servicing stack, so they
    /// share everything below the catalogue: one elevated helper, one consent
    /// prompt, the same wait on Windows Update, and the same rule that whether it
    /// ARRIVED is answered by asking Windows afterwards rather than by trusting
    /// an exit code.</para>
    /// </summary>
    internal class LanguagePackFamily
    {
        public string TitleKey;
        public string HintKey;
        /// <summary>Capability names, read off a real machine rather than from
        /// documentation — Gordan ran the elevated enumeration for both.</summary>
        public string[] Tags;
        public Func<string, string> Capability;
        public Func<string, bool> IsInstalled;
        public Action Rescan;

        /// <summary>Reading pictures. 35 packs on Windows 11 26200; the model
        /// itself is about a quarter of a megabyte.</summary>
        public static readonly LanguagePackFamily Ocr = new LanguagePackFamily
        {
            TitleKey = "Ocr.Add.Title",
            HintKey = "Ocr.Add.Hint",
            Tags = WindowsOcr.InstallableLanguages,
            Capability = WindowsOcr.CapabilityName,
            IsInstalled = WindowsOcr.IsInstalled,
            Rescan = WindowsOcr.Rescan
        };

        /// <summary>Speaking text — the OneCore voices, which are what
        /// <see cref="OneCoreBackend"/> reaches. 49 packs, and note the shape
        /// differs from OCR's: no script subtags, but far more regional variants
        /// (de-AT, en-IN, fr-CH, nl-BE), because a voice is tied to an accent in a
        /// way a recognizer is not.</summary>
        public static readonly LanguagePackFamily Voices = new LanguagePackFamily
        {
            TitleKey = "Voices.Add.Title",
            HintKey = "Voices.Add.Hint",
            Tags = new[]
            {
                "ar-EG", "ar-SA", "bg-BG", "ca-ES", "cs-CZ", "da-DK", "de-AT", "de-CH",
                "de-DE", "el-GR", "en-AU", "en-CA", "en-GB", "en-IE", "en-IN", "en-US",
                "es-ES", "es-MX", "fi-FI", "fr-CA", "fr-CH", "fr-FR", "he-IL", "hi-IN",
                "hr-HR", "hu-HU", "id-ID", "it-IT", "ja-JP", "ko-KR", "ms-MY", "nb-NO",
                "nl-BE", "nl-NL", "pl-PL", "pt-BR", "pt-PT", "ro-RO", "ru-RU", "sk-SK",
                "sl-SI", "sv-SE", "ta-IN", "th-TH", "tr-TR", "vi-VN", "zh-CN", "zh-HK",
                "zh-TW"
            },
            Capability = t => "Language.TextToSpeech~~~" + t + "~0.0.1.0",
            IsInstalled = SpeechPacks.HasVoiceFor,
            Rescan = SpeechPacks.Rescan
        };
    }

    /// <summary>Which languages this machine can already SPEAK.
    ///
    /// <para>Asked of the voices themselves rather than of the servicing stack,
    /// for the same reason the OCR side asks the engine: enumerating capabilities
    /// needs elevation, and "is there a voice for German" is answerable without
    /// it. It is also the honest question — a pack that installed but produced no
    /// usable voice is not installed as far as a reader is concerned.</para></summary>
    internal static class SpeechPacks
    {
        private static readonly object gate = new object();
        private static List<string> cache;

        public static void Rescan() { lock (gate) cache = null; }

        /// <summary>True when some installed voice speaks this language. Compared
        /// on the language alone, so a pack listed as <c>en-GB</c> is satisfied by
        /// a voice reporting <c>en-GB</c> and not by one reporting <c>en-US</c> —
        /// the region is the point with voices, unlike with recognizers.</summary>
        public static bool HasVoiceFor(string tag)
        {
            string want = (tag ?? "").Replace("-", "").ToLowerInvariant();
            if (want.Length == 0) return false;
            foreach (string have in Languages())
                if (have.Replace("-", "").ToLowerInvariant() == want) return true;
            return false;
        }

        private static List<string> Languages()
        {
            lock (gate)
            {
                if (cache != null) return cache;
                cache = new List<string>();
                try
                {
                    // The same late-bound WinRT route OneCoreBackend uses — no SDK
                    // and nothing shipped. AllVoices is static, so this costs
                    // nothing like building a synthesizer.
                    Type t = Type.GetType(
                        "Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows, ContentType=WindowsRuntime");
                    if (t == null) return cache;
                    object all = t.GetProperty("AllVoices", BindingFlags.Public | BindingFlags.Static)
                                  .GetValue(null);
                    foreach (object v in (IEnumerable)all)
                    {
                        string lang = v.GetType().GetProperty("Language").GetValue(v) as string;
                        if (!string.IsNullOrEmpty(lang)) cache.Add(lang);
                    }
                }
                catch { }
                return cache;
            }
        }
    }
}
