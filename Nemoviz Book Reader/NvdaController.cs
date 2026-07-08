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

        // Set once if the DLL can't be loaded (e.g. missing) so we don't throw
        // on every keystroke thereafter.
        private static bool dllUnavailable;

        /// <summary>Speaks the text through NVDA if NVDA is running; otherwise
        /// does nothing. Cancels any in-progress speech first so a fast key
        /// repeat announces only the latest value instead of queueing stale
        /// ones (the NVDA equivalent of the UIA MostRecent behaviour).</summary>
        public static void Speak(string text)
        {
            if (dllUnavailable || string.IsNullOrEmpty(text))
                return;

            try
            {
                if (nvdaController_testIfRunning() != 0)
                    return; // NVDA not running

                nvdaController_cancelSpeech();
                nvdaController_speakText(text);
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
        }
    }
}
