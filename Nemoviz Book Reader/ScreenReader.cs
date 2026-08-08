using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Says something to whichever screen reader is running, without
    /// moving focus.
    ///
    /// <para>Both channels, because each is picked up by exactly one reader and
    /// ignored by the other, so calling both never double-speaks and each is a
    /// silent no-op when its reader is absent: a <b>UIA notification</b> for
    /// JAWS, and the <b>NVDA Controller Client</b> for NVDA. This is the same
    /// pairing <c>Form1.AnnounceToScreenReader</c> has used since §2, lifted out
    /// so a dialog can use it too.</para>
    ///
    /// <para><b>Why it had to be lifted.</b> The dialogs announce through
    /// <see cref="NvdaController"/> alone, which is NVDA-only — fine for the
    /// combo-box workaround it was written for, since JAWS reads combo changes
    /// correctly by itself. It is not fine for something a reader is WAITING on,
    /// like a measurement that takes a second and a half: under JAWS that would
    /// be a silent pause with nothing to explain it, and JAWS is the primary
    /// reader.</para>
    ///
    /// <para><b>Form1 has not been changed to use this</b>, deliberately — its
    /// copy works and is tested, and rewriting the announcement path of a 4600-
    /// line file in the middle of another feature buys nothing today. Worth
    /// folding in on the accessibility pass §11 already schedules.</para></summary>
    public static class ScreenReader
    {
        [DllImport("uiautomationcore.dll", CharSet = CharSet.Unicode)]
        private static extern int UiaRaiseNotificationEvent(
            IRawElementProviderSimple provider, NotificationKind kind,
            NotificationProcessing processing, string displayString, string activityId);

        [DllImport("uiautomationcore.dll")]
        private static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IRawElementProviderSimple provider);

        [ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IRawElementProviderSimple
        {
            // Never called from managed code — the provider is obtained from the
            // OS and handed straight back, so no members need declaring for the
            // pointer to marshal.
        }

        private enum NotificationKind { ItemAdded = 0, ItemRemoved = 1, ActionCompleted = 2, ActionAborted = 3, Other = 4 }

        private enum NotificationProcessing { ImportantAll = 0, ImportantMostRecent = 1, All = 2, MostRecent = 3, CurrentThenMostRecent = 4 }

        private static readonly Dictionary<IntPtr, IRawElementProviderSimple> providers =
            new Dictionary<IntPtr, IRawElementProviderSimple>();
        private static bool uiaUnavailable;

        /// <summary>Speaks <paramref name="text"/> without taking focus. Safe to
        /// call from any state; never throws.</summary>
        public static void Announce(Control owner, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            RaiseUia(owner, text);
            try { NvdaController.Speak(text); } catch { }
        }

        private static void RaiseUia(Control owner, string text)
        {
            if (uiaUnavailable || owner == null) return;
            try
            {
                // A disposed control is not a null one — the lesson the caret
                // watchdog cost. Touching Handle here would resurrect it.
                if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) return;

                IntPtr hwnd = owner.Handle;
                IRawElementProviderSimple provider;
                if (!providers.TryGetValue(hwnd, out provider))
                {
                    if (UiaHostProviderFromHwnd(hwnd, out provider) != 0 || provider == null)
                    {
                        uiaUnavailable = true;
                        return;
                    }
                    providers[hwnd] = provider;
                }

                // MostRecent so a reader drops stale queued values rather than
                // reading a backlog — the same choice §2 made for key repeat.
                UiaRaiseNotificationEvent(provider, NotificationKind.Other,
                    NotificationProcessing.MostRecent, text, string.Empty);
            }
            catch
            {
                // The export needs Windows 10 1709+. On anything older the first
                // call throws, so stop trying rather than throwing on every one.
                uiaUnavailable = true;
            }
        }

        /// <summary>Drops a window's cached provider. A dialog is opened and
        /// closed many times in a session and each gets a new HWND, so without
        /// this the table would grow for as long as NBR runs.</summary>
        public static void Forget(Control owner)
        {
            try
            {
                if (owner == null || !owner.IsHandleCreated) return;
                providers.Remove(owner.Handle);
            }
            catch { }
        }
    }
}
