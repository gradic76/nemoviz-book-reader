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
        /// <para><b>It is a supplement, never a replacement.</b> A message is
        /// transient and overwritten by the next thing NVDA has to show; there is
        /// no panning through the book and no routing keys, both of which focus
        /// tracking gives for free. So it is for when focus is NOT on the reading
        /// surface — with focus there, the reader is already doing a better job
        /// and pushing over it would only flicker.</para>
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
