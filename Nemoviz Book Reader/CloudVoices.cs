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
    /// site, and <see cref="CloudSpeechBackend"/> owns Google's. This answers
    /// only the questions whose answer is the same for all of them.</para>
    /// </summary>
    internal static class CloudVoices
    {
        /// <summary>Is this display name a voice made by a service rather than on
        /// this machine?</summary>
        public static bool IsOne(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            return GoogleCloudVoices.IsOne(displayName) || AzureVoices.IsOne(displayName);
        }

        /// <summary>One passage from whichever service owns this voice, or null.
        /// The look-ahead and the export both need to make speech without caring
        /// whose it is; everything else about a vendor stays in its own file.</summary>
        public static byte[] Synthesize(string displayName, string text)
        {
            string a, b;
            if (GoogleCloudVoices.Split(displayName, out a, out b))
                return Counted(GoogleCloudVoices.Vendor, text,
                               GoogleCloudVoices.Synthesize(text, a, b, 1.0, 0.0));
            if (AzureVoices.Split(displayName, out a, out b))
                return Counted(AzureVoices.Vendor, text,
                               AzureVoices.Synthesize(text, AzureVoices.ShortNameFor(displayName), a, 1.0, 0.0));
            return null;
        }

        /// <summary>Adds what was just sent to this month's running total, and
        /// hands the audio straight back.
        ///
        /// <para><b>Here and nowhere else, and the placement is the accuracy.</b>
        /// This method is BEHIND the speech cache — <c>CloudSpeechBackend.Render</c>
        /// looks in the cache first and only reaches Synthesize on a miss — so a
        /// second reading of a book, an export replayed from disk and a sentence
        /// the look-ahead already fetched are all counted as nothing, which is
        /// exactly how the service will bill them. Counting where speech is
        /// SPOKEN would have counted a re-read as a fresh cost.</para>
        ///
        /// <para><b>Only a reply that arrived is counted.</b> A refused or
        /// timed-out request returns null and is not charged, so it is not
        /// counted either; the alternative overstates a reader's usage on exactly
        /// the day their network is bad.</para></summary>
        private static byte[] Counted(string vendor, string text, byte[] audio)
        {
            if (audio != null && !string.IsNullOrEmpty(text))
                CloudUsage.Note(vendor, text.Length);
            return audio;
        }

        /// <summary>The largest request a vendor will take, for the chunker that
        /// splits an over-long passage. The smaller of the two is safe for
        /// either, and they are the same number today.</summary>
        public static int MaxRequestBytes(string displayName)
        {
            return AzureVoices.IsOne(displayName)
                ? AzureVoices.MaxRequestBytes : GoogleCloudVoices.MaxRequestBytes;
        }

        /// <summary>Is any cloud voice service set up at all? Used to decide
        /// whether there is anything to offer, not whether to offer a
        /// particular voice.</summary>
        public static bool Any { get { return GoogleCloudVoices.Have || AzureVoices.Have; } }

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
            // Filtered HERE against CloudVoices.IsOne, not delegated to one
            // vendor's own Exclude — that would have kept the other vendor's
            // voices in Settings, which is exactly the silent per-language
            // default §8g forbids, and nothing would have complained.
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
    }
}
