using System;
using System.Runtime.InteropServices;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Thin wrapper over NV Access's NVDA Controller Client
    /// (nvdaControllerClient.dll, x64, LGPL 2.1 — see
    /// nvdaControllerClient-license.txt). Speaks text straight through NVDA
    /// without moving focus — the NVDA counterpart to the UIA notification
    /// used for JAWS, because NVDA drives WinForms via MSAA and ignores our
    /// UIA notification event.
    ///
    /// Every call degrades to a silent no-op when NVDA isn't running or the
    /// DLL is missing, so it is always safe to call alongside the UIA
    /// notification: whichever reader is active picks up its own channel and
    /// the other path does nothing (no double-speak — JAWS ignores the NVDA
    /// client, NVDA ignores the UIA notification).
    /// </summary>
    internal static class NvdaController
    {
        // error_status_t (unsigned long) — 0 means success / NVDA running.
        [DllImport("nvdaControllerClient.dll")]
        private static extern int nvdaController_testIfRunning();

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_speakText([MarshalAs(UnmanagedType.LPWStr)] string text);

        [DllImport("nvdaControllerClient.dll")]
        private static extern int nvdaController_cancelSpeech();

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_brailleMessage([MarshalAs(UnmanagedType.LPWStr)] string text);

        // Set once if the DLL can't be loaded (e.g. missing) so we don't throw
        // on every keystroke thereafter.
        private static bool dllUnavailable;

        /// <summary>Speaks the text through NVDA if NVDA is running; otherwise
        /// does nothing. Cancels any in-progress speech first so a fast key
        /// repeat announces only the latest value instead of queueing stale
        /// ones (the NVDA equivalent of the UIA MostRecent behaviour).</summary>
        /// <summary>Speaks without cancelling what is already being said, so
        /// consecutive utterances QUEUE instead of cutting each other off.
        ///
        /// <para><see cref="Speak"/> cancels first, which is right for a value
        /// that replaces the last one — volume stepped twice should say the
        /// second number, not both. It is wrong for consecutive sentences of a
        /// book: each new one guillotines the one still being spoken, and what
        /// comes out is chopped and missing words.</para></summary>
        public static bool SpeakQueued(string text)
        {
            if (dllUnavailable || string.IsNullOrEmpty(text)) return false;
            try
            {
                if (nvdaController_testIfRunning() != 0) return false;
                nvdaController_speakText(text);      // deliberately no cancel
                return true;
            }
            catch (DllNotFoundException) { dllUnavailable = true; }
            catch (EntryPointNotFoundException) { dllUnavailable = true; }
            catch { }
            return false;
        }

        /// <returns>True if the text was actually handed to a running NVDA.
        /// Callers that ignore it are unaffected; it exists because "nothing was
        /// spoken" and "NVDA was not there to speak it" look identical from the
        /// outside, and telling them apart is the difference between fixing the
        /// right thing and fixing three wrong ones.</returns>
        public static bool Speak(string text)
        {
            if (dllUnavailable || string.IsNullOrEmpty(text))
                return false;

            try
            {
                if (nvdaController_testIfRunning() != 0)
                    return false; // NVDA not running

                nvdaController_cancelSpeech();
                nvdaController_speakText(text);
                return true;
            }
            catch (DllNotFoundException)
            {
                dllUnavailable = true;
            }
            catch (EntryPointNotFoundException)
            {
                dllUnavailable = true;
            }
            catch
            {
                // Transient RPC hiccup (e.g. NVDA closing mid-call) — ignore.
            }
            return false;
        }

        /// <summary>Puts a line on the braille display without caring what has
        /// focus — the braille counterpart of <see cref="Speak"/>.
        ///
        /// <para><b>Why this exists.</b> Braille normally follows FOCUS: the
        /// reader tracks whatever control holds it, which is how NBR's reading
        /// surface reaches the display at all (§8l), and it is how every Windows
        /// application does it — no app "sends text to a braille display". But it
        /// means a stray Tab onto a player key leaves speech reading on while the
        /// display shows "Forward, Shift+Right" (Gordan, 2026-08-01). This channel
        /// keeps the book coming in that case.</para>
        ///
        /// <para><b>It turned out to be the whole story, not a supplement
        /// (2026-08-07).</b> The paragraph here used to say this was for when
        /// focus is NOT on the reading surface, because with focus there "the
        /// reader is already doing a better job". It is not: a screen reader
        /// follows a caret the USER moves and ignores one the PROGRAM moves, so
        /// with focus on the surface both NVDA and JAWS sat on the same sentence
        /// while the caret walked whole paragraphs. Measured — see
        /// Form1.PushBrailleIfSurfaceFocused. The gate is now the other way round.</para>
        ///
        /// <para><b>What it still cannot do.</b> A message is transient and
        /// overwritten by the next thing NVDA has to show, and it gives no panning
        /// through the book and no routing keys — those come from focus tracking,
        /// which is exactly what does not work here. So the display shows the line
        /// being read and the reader cannot wander off it by hand.</para>
        ///
        /// <para><b>NVDA only.</b> JAWS has no public equivalent; braille there is
        /// written from a JAWS script, which would mean shipping and installing
        /// one into the user's JAWS folder — not something a portable app should
        /// do. On JAWS, focus tracking remains the whole story.</para>
        ///
        /// <para><b>Do not call this while another APPLICATION has the
        /// foreground.</b> Winning the display back from a Windows Update prompt
        /// would stop the reader reading the prompt, which is what they need at
        /// that moment. The caller decides; see Form1.</para></summary>
        /// <summary>Makes a drop-down speak its new value when the arrows change
        /// it while it is closed.
        ///
        /// <para><b>NVDA does not announce a collapsed DropDownList on arrow.</b>
        /// Open it with Alt+Down and arrowing speaks every entry; closed, the
        /// value changes in silence. Measured across the whole app with Gordan
        /// (2026-08-11): the seven Sound-processing combos spoke and every other
        /// combo did not, and the reason turned out to be that
        /// <see cref="PropertiesForm"/> had already solved it by hand, for its own
        /// combos, with this exact call.</para>
        ///
        /// <para>Three of them were compared through the accessibility layer
        /// first — name, role, value, state and the whole parent chain — and they
        /// came back <b>identical</b>. That is the useful part: the fault was
        /// never in what the control exposes, so no amount of fixing roles or
        /// names would have touched it. NVDA simply does not speak this, and an
        /// application that wants it spoken has to say it.</para>
        ///
        /// <para>Attached centrally so a combo added next year gets it without
        /// anyone remembering. Silent under JAWS, which announces these by
        /// itself, and silent with no reader running.</para></summary>
        public static void SpeakOnChange(System.Windows.Forms.ComboBox combo)
        {
            if (combo == null) return;
            combo.SelectedIndexChanged += (s, e) =>
            {
                // Only when the reader is standing on it. A combo repopulated in
                // the background, or set from code while the focus is elsewhere,
                // is not something anybody asked to hear.
                if (combo.Focused && !combo.DroppedDown) Speak(combo.Text);
            };
        }

        public static void Braille(string text)
        {
            if (dllUnavailable || string.IsNullOrEmpty(text)) return;
            try
            {
                if (nvdaController_testIfRunning() != 0) return;
                nvdaController_brailleMessage(text);
            }
            catch (DllNotFoundException) { dllUnavailable = true; }
            catch (EntryPointNotFoundException) { dllUnavailable = true; }
            catch { }
        }
    }
}
