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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static readonly object gate = new object();
        private static readonly Queue<string> crumbs = new Queue<string>();
        private static Control host;
        private static Thread worker;
        private static volatile bool running;
        private static long lastBeatTicks;
        private static int reportedAt;
        private static uint uiOsThreadId;
        private static Thread uiThread;

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

        /// <summary>Leaves a breadcrumb only if the message loop is still turning
        /// a moment from now.
        ///
        /// <para>It is the difference between "the dialog never opened" and "the
        /// dialog opened and then froze" — a modal dialog pumps, so this fires
        /// from inside one. Nothing else in the log can tell those two apart, and
        /// they are looked for in completely different places.</para></summary>

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);

        /// <summary>The window state a common dialog is about to inherit, written
        /// into the crumb trail so one reproduction can settle what six theories
        /// could not.
        ///
        /// <para>Gordan bisected it on 2026-08-20 and the cut is clean: with no
        /// reading window, Open file works every time from both the player and the
        /// Library; open the reading window on F9, close it on Escape, and the next
        /// Ctrl+O blocks. So the reading window LEAVES SOMETHING BEHIND, and this
        /// records the candidates rather than guessing between them — whether a
        /// window is left disabled (which is what makes an invisible modal dialog
        /// and the DING he has always described), whether the active window is one
        /// that is going away, and whether the owner chain still makes sense.</para>
        ///
        /// <para>Every value is asked of WINDOWS, not of WinForms: a stale HWND is
        /// exactly the thing under suspicion, and WinForms would answer about the
        /// object it thinks it has.</para></summary>
        public static void NoteWindows(string tag, Form self)
        {
            try
            {
                IntPtr active = GetActiveWindow();
                IntPtr fore = GetForegroundWindow();
                var sb = new StringBuilder();
                sb.Append(tag).Append("  active=").Append(Describe(active))
                  .Append("  foreground=").Append(Describe(fore));
                if (self != null && self.IsHandleCreated)
                {
                    sb.Append("  self=").Append(Describe(self.Handle));
                    IntPtr owner = GetWindow(self.Handle, 4 /* GW_OWNER */);
                    sb.Append("  owner=").Append(Describe(owner));
                }
                foreach (Form f in Application.OpenForms)
                {
                    if (f == null || !f.IsHandleCreated) continue;
                    sb.Append("  [").Append(f.GetType().Name).Append(' ')
                      .Append(Describe(f.Handle)).Append(']');
                }
                // WRITTEN AT ONCE, not only into the crumb ring (2026-08-20).
                // Note() keeps its lines in memory and the file is written ONLY
                // when something stalls — so a HEALTHY run leaves no trace, and a
                // healthy run is exactly the control this needs. Gordan's clean
                // pass produced an empty log and told us nothing; now the same
                // pass records what "working" looks like, and the broken state can
                // be diffed against it instead of read on its own.
                Note(sb.ToString());
                try { ReadingDiagnostics.Always(sb.ToString()); } catch { }
            }
            catch { }
        }

        /// <summary>hwnd, enabled and visible as Windows itself reports them.</summary>
        private static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero) return "none";
            string name = "?";
            try { Control c = Control.FromHandle(h); if (c != null) name = c.GetType().Name; } catch { }
            return name + "/0x" + h.ToInt64().ToString("X")
                 + (IsWindowEnabled(h) ? "/enabled" : "/DISABLED")
                 + (IsWindowVisible(h) ? "/visible" : "/hidden");
        }
        public static void NoteWhenPumping(string what, int afterMs = 400)
        {
            try
            {
                // The WinForms one on purpose: it ticks from the message loop, so
                // it fires only while that loop is turning. A threading timer
                // would fire regardless and prove nothing.
                var t = new System.Windows.Forms.Timer { Interval = afterMs };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); Note(what); };
                t.Start();
            }
            catch { }
        }

        /// <summary>Starts watching. Safe to call twice; the second is ignored.</summary>
        public static void Start(Control uiHost)
        {
            if (running || uiHost == null) return;
            host = uiHost;
            // Called from the UI thread, so this IS the thread to watch. Both
            // identities are kept: the OS id finds its ProcessThread and its
            // wait reason, the managed one is what a stack can be read from.
            try { uiOsThreadId = GetCurrentThreadId(); } catch { }
            uiThread = Thread.CurrentThread;
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

        /// <summary>Every OTHER thread's wait reason, grouped.
        ///
        /// <para><b>Added 2026-08-10, and the capture it is meant for is the
        /// Ctrl+O freeze.</b> That one has the UI thread waiting inside
        /// <c>IFileDialog::Show</c> on <c>UserRequest</c> — an ordinary handle,
        /// NOT <c>LpcReceive</c>/<c>LpcReply</c> — so it is not blocked on a call
        /// out of the process. It is waiting for something INSIDE the process to
        /// finish, and the shell dialog does its start-up work on threads of its
        /// own. Those threads are the missing half of the picture: one of them
        /// sitting on <c>LpcReply</c> would name a provider outside, one on
        /// <c>Executive</c> a disk that is not answering.</para></summary>
        private static void DumpOtherThreads(StringBuilder sb, Process me)
        {
            try
            {
                var byReason = new System.Collections.Generic.SortedDictionary<string, int>();
                foreach (ProcessThread pt in me.Threads)
                {
                    if (pt.Id == uiOsThreadId) continue;
                    string k;
                    try
                    {
                        k = pt.ThreadState == System.Diagnostics.ThreadState.Wait
                            ? "Wait/" + pt.WaitReason : pt.ThreadState.ToString();
                    }
                    catch { k = "(gone)"; }   // a thread can exit while being read
                    byReason[k] = byReason.ContainsKey(k) ? byReason[k] + 1 : 1;
                }
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in byReason) parts.Add(kv.Value + " " + kv.Key);
                sb.AppendLine("     other threads: " + string.Join(", ", parts.ToArray()));
            }
            catch (Exception ex) { sb.AppendLine("     other threads unavailable: " + ex.Message); }
        }

        /// <summary>Modules loaded from outside the app folder, Windows and the
        /// .NET framework — i.e. <b>what has been injected into this process</b>:
        /// shell extensions, cloud-storage providers, anti-virus hooks, screen
        /// readers.
        ///
        /// <para>Worth capturing because the shell file dialog is the one place
        /// NBR hands control to code nobody here wrote. If a hang always carries
        /// the same foreign DLL, that names the cause; if the list is empty, the
        /// cause is ours after all. Either answer is progress, and neither can be
        /// had from the managed stack.</para></summary>
        private static void DumpForeignModules(StringBuilder sb, Process me)
        {
            try
            {
                string appDir = "";
                try { appDir = System.IO.Path.GetDirectoryName(
                          System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ""; }
                catch { }
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                var foreign = new System.Collections.Generic.List<string>();
                foreach (ProcessModule m in me.Modules)
                {
                    string p;
                    try { p = m.FileName ?? ""; } catch { continue; }
                    if (p.Length == 0) continue;
                    if (appDir.Length > 0 && p.StartsWith(appDir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.StartsWith(win, StringComparison.OrdinalIgnoreCase)) continue;
                    foreign.Add(System.IO.Path.GetFileName(p));
                }
                sb.AppendLine(foreign.Count == 0
                    ? "     injected modules: none"
                    : "     injected modules: " + string.Join(", ", foreign.ToArray()));
            }
            catch (Exception ex) { sb.AppendLine("     injected modules unavailable: " + ex.Message); }
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

                // WHAT it is waiting on. A blocked thread's wait reason is the
                // single most useful fact there is and costs nothing to read —
                // no suspending, no debugger. LpcReceive or LpcReply means it is
                // stuck in a cross-process call (COM, the shell, a screen
                // reader); UserRequest means an ordinary wait handle.
                try
                {
                    foreach (ProcessThread pt in me.Threads)
                    {
                        if (pt.Id != uiOsThreadId) continue;
                        sb.Append("     UI thread state: " + pt.ThreadState);
                        if (pt.ThreadState == System.Diagnostics.ThreadState.Wait)
                            sb.Append("   waiting on: " + pt.WaitReason);
                        sb.AppendLine("   user time " + pt.UserProcessorTime.TotalSeconds.ToString("0.0") + " s");
                        break;
                    }
                }
                catch (Exception ex) { sb.AppendLine("     UI thread state unavailable: " + ex.Message); }

                DumpOtherThreads(sb, me);
                DumpForeignModules(sb, me);

                sb.AppendLine("     what the UI thread was doing, most recent last:");
                lock (gate)
                    foreach (string c in crumbs) sb.AppendLine("       " + c);

                sb.AppendLine("     where it stopped:");
                sb.AppendLine(Stack());

                File.AppendAllText(LogPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>The UI thread's managed stack, best effort.
        ///
        /// <para><b>Suspending a thread to read it is deprecated and unsafe</b>,
        /// and it is done here anyway, on a deliberate trade: this only ever runs
        /// when the application has ALREADY stopped answering and the reader is
        /// about to kill it, so the worst case costs a session that was lost
        /// regardless. It is the last thing in the report and is allowed to fail
        /// — the breadcrumbs and the wait reason above are the payload, and they
        /// are gathered without touching the thread at all.</para></summary>
        private static string Stack()
        {
            Thread t = uiThread;
            if (t == null) return "       (no UI thread recorded)";
#pragma warning disable 618
            try
            {
                t.Suspend();
                try
                {
                    var st = new StackTrace(t, true);
                    var sb = new StringBuilder();
                    foreach (StackFrame f in st.GetFrames() ?? new StackFrame[0])
                    {
                        var m = f.GetMethod();
                        if (m == null) continue;
                        sb.Append("       ")
                          .Append(m.DeclaringType != null ? m.DeclaringType.Name + "." : "")
                          .Append(m.Name);
                        if (f.GetFileLineNumber() > 0)
                            sb.Append("  (").Append(System.IO.Path.GetFileName(f.GetFileName()))
                              .Append(':').Append(f.GetFileLineNumber()).Append(')');
                        sb.AppendLine();
                    }
                    return sb.Length > 0 ? sb.ToString().TrimEnd() : "       (empty — probably blocked in native code)";
                }
                finally { t.Resume(); }
            }
            catch (Exception ex) { return "       (unavailable: " + ex.Message + ")"; }
#pragma warning restore 618
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
