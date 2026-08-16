using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// "Is this voice made somewhere else, and what does that cost?" — asked
    /// without naming a vendor.
    ///
    /// <para><b>Why this exists.</b> Six places in the app ask whether a voice is
    /// a CLOUD voice, and every one of them spelled the question
    /// <c>GoogleCloudVoices.IsOne(...)</c> — because for a day Google was the
    /// only cloud there was. They are not Google questions. They are:</para>
    /// <list type="bullet">
    /// <item>should the look-ahead run at all, since it spends money
    /// (<c>Form1.SyncPrefillToPlayback</c>);</item>
    /// <item>how long will an export take, a round trip a passage or a local
    /// render (<c>LibraryForm</c>);</item>
    /// <item>is this book's speech worth keeping after an export, or was it free
    /// to make (<c>SpeechExportForm.DropLocalPieces</c>);</item>
    /// <item>may this voice be a per-language DEFAULT — no, because §8g says a
    /// cloud voice is chosen per book and never as a rule
    /// (<c>SettingsForm</c>, <c>GoogleCloudVoices.Exclude</c>);</item>
    /// <item>does Properties offer it (<c>PropertiesForm</c>);</item>
    /// <item>and what to say about it in the inventory log
    /// (<c>CompositeSpeechBackend</c>).</item>
    /// </list>
    ///
    /// <para>Each of those is true of ANY cloud voice, so each asks here now. A
    /// second vendor is then a change in one file rather than a hunt through
    /// six — and, more to the point, a vendor somebody forgets to add to one of
    /// the six cannot quietly get a per-language default or a free export
    /// estimate.</para>
    ///
    /// <para><b>Deliberately thin.</b> It does not wrap synthesis, voice lists or
    /// credentials: those differ per vendor in ways worth seeing at the call
    /// site, and <see cref="GoogleCloudBackend"/> owns Google's. This answers
    /// only the questions whose answer is the same for all of them.</para>
    /// </summary>
    internal static class CloudVoices
    {
        /// <summary>Is this display name a voice made by a service rather than on
        /// this machine?</summary>
        public static bool IsOne(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            return GoogleCloudVoices.IsOne(displayName);
        }

        /// <summary>Is any cloud voice service set up at all? Used to decide
        /// whether there is anything to offer, not whether to offer a
        /// particular voice.</summary>
        public static bool Any { get { return GoogleCloudVoices.Have; } }

        /// <summary>Roughly what one passage costs to make, in seconds — a round
        /// trip for a cloud voice against a local render.
        ///
        /// <para>Measured both ways and neither is a guess: a Google passage took
        /// 1.686 s cold, and Gordan's eSpeak export ran at about 0.06 s a
        /// passage. 0.15 rather than 0.06 for the local case because eSpeak is
        /// the fastest local engine and RHVoice and OneCore synthesise rather
        /// more slowly — there is one measurement, not a curve, and being early
        /// is the safe direction for a number somebody reads before committing
        /// to an export.</para></summary>
        public static double SecondsPerPassage(string displayName)
        {
            return IsOne(displayName) ? 1.0 : 0.15;
        }

        /// <summary>Everything except the cloud voices — what Settings may offer
        /// as a per-language DEFAULT (§8g: a cloud voice is chosen for one book
        /// in Properties, and never becomes a rule).</summary>
        public static List<(string Name, string Engine, string Language)> Exclude(
            IEnumerable<(string Name, string Engine, string Language)> all, out int removed)
        {
            return GoogleCloudVoices.Exclude(all, out removed);
        }
    }
}
