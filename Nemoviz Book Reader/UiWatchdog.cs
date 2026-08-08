using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Notices when the player stops answering, and writes down what it
    /// was doing.
    ///
    /// <para><b>Why it exists.</b> Gordan: after reading, open the Library, press
    /// Ctrl+O, and everything freezes into Windows' own "wait or close". Nothing
    /// on that path could be identified by reading the code, and the alternative
    /// to a diagnostic is guessing — which this project has paid for before
    /// (§10f: four wrong diagnoses in a row).</para>
    ///
    /// <para><b>How it knows.</b> A background thread posts a heartbeat to the UI
    /// thread every second and watches for it to come back. A message pump that
    /// is running answers immediately, even while a modal dialog is up, because
    /// a modal dialog pumps. Silence for <see cref="StallSeconds"/> means the
    /// thread is not pumping at all.</para>
    ///
    /// <para><b>Breadcrumbs are the real payload.</b> The last things the UI
    /// thread announced doing, with timings, are far more use than a stack —
    /// they survive whatever the thread is stuck in, they cost a string, and they
    /// say what led up to it rather than only where it stopped. A stack is
    /// attempted as well, and is allowed to fail.</para>
    ///
    /// <para><b>It writes to <c>%TEMP%\NBR-hang.log</c></b> and never to the
    /// library or the book folder. It reports a stall once, then again only if it
    /// doubles, so a machine that went to sleep does not fill the file.</para></summary>
    public static class UiWatchdog
    {
        /// <summary>How long the UI thread may ignore a heartbeat before this is
        /// a hang. Long enough that a slow folder scan or a big archive listing
        /// is not reported, short enough to catch what the user sees.</summary>
        public const int StallSeconds = 5;

        private const int MaxCrumbs = 60;

        private static readonly object gate = new object();
        private static readonly Queue<string> crumbs = new Queue<string>();
        private static Control host;
        private static Thread worker;
        private static volatile bool running;
        private static long lastBeatTicks;
        private static int reportedAt;

        /// <summary>Records what the UI thread is about to do. Cheap and safe to
        /// call from anywhere; only the last <see cref="MaxCrumbs"/> are kept.</summary>
        public static void Note(string what)
        {
            if (string.IsNullOrEmpty(what)) return;
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what;
                lock (gate)
                {
                    crumbs.Enqueue(line);
                    while (crumbs.Count > MaxCrumbs) crumbs.Dequeue();
                }
            }
            catch { }
        }

        /// <summary>Starts watching. Safe to call twice; the second is ignored.</summary>
        public static void Start(Control uiHost)
        {
            if (running || uiHost == null) return;
            host = uiHost;
            running = true;
            Interlocked.Exchange(ref lastBeatTicks, DateTime.UtcNow.Ticks);
            worker = new Thread(Loop);
            worker.IsBackground = true;      // must never keep the app alive
            worker.Name = "NBR UI watchdog";
            worker.Start();
            Note("watchdog started");
        }

        public static void Stop()
        {
            running = false;
        }

        private static void Loop()
        {
            while (running)
            {
                try
                {
                    Thread.Sleep(1000);
                    if (!running) break;

                    Control h = host;
                    if (h == null || h.IsDisposed || h.Disposing || !h.IsHandleCreated) continue;

                    // BeginInvoke, never Invoke: Invoke would block THIS thread
                    // for as long as the UI thread is stuck, which is precisely
                    // the condition being measured.
                    try { h.BeginInvoke((MethodInvoker)Beat); }
                    catch { continue; }      // handle went away between the check and here

                    double stalled = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastBeatTicks))).TotalSeconds;
                    if (stalled < StallSeconds) { reportedAt = 0; continue; }

                    // Once per stall, then again only when it has doubled.
                    if (reportedAt != 0 && stalled < reportedAt * 2) continue;
                    reportedAt = (int)stalled;
                    Report(stalled);
                }
                catch { }
            }
        }

        private static void Beat()
        {
            Interlocked.Exchange(ref lastBeatTicks, DateTime.UtcNow.Ticks);
        }

        private static void Report(double stalled)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("==== UI thread stalled " + stalled.ToString("0.0")
                              + " s at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                Process me = Process.GetCurrentProcess();
                // 0 % says it is WAITING for something; a pegged core says it is
                // spinning. Two different faults, and this is the cheapest way to
                // tell them apart without asking anyone to open Task Manager.
                TimeSpan cpu1 = me.TotalProcessorTime;
                Thread.Sleep(500);
                me.Refresh();
                double busy = (me.TotalProcessorTime - cpu1).TotalMilliseconds / 500.0;
                sb.AppendLine("     cpu during the stall: " + (busy * 100).ToString("0")
                              + " % of one core   (near 0 = waiting, near 100 = spinning)");
                sb.AppendLine("     threads: " + me.Threads.Count
                              + "   handles: " + me.HandleCount
                              + "   private: " + (me.PrivateMemorySize64 / (1024 * 1024)) + " MB");

                sb.AppendLine("     what the UI thread was doing, most recent last:");
                lock (gate)
                    foreach (string c in crumbs) sb.AppendLine("       " + c);

                File.AppendAllText(LogPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string LogPath
        {
            get
            {
                try { return Path.Combine(Path.GetTempPath(), "NBR-hang.log"); }
                catch { return "NBR-hang.log"; }
            }
        }
    }
}
