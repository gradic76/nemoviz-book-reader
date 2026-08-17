using System;
using System.Threading;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The long jobs, run so the machine stays usable while they run.
    ///
    /// <para><b>Gordan, 2026-08-17:</b> the fan comes on during an MP3 export, and
    /// he wants every long job — bulk import, unpacking, the sound analysis, the
    /// conversion — out of the way of whatever else he is doing.</para>
    ///
    /// <para><b>What this does and does not buy, plainly.</b> It does NOT make the
    /// work smaller or the machine cooler: the same passages still have to be
    /// encoded and the fan will still come on. What it changes is who WINS when
    /// something else wants the processor. At Lowest, every other program on the
    /// machine — and NBR's own playback and screen-reader announcements — takes
    /// the processor first, and the job fills what is left. On an idle machine it
    /// runs at exactly the speed it did before.</para>
    ///
    /// <para><b>Why a wrapper and not a line at each call site.</b> These jobs run
    /// on the THREAD POOL, and the pool does not reset a thread's priority when
    /// the thread goes back into it. Lowering it in place leaks: the next piece of
    /// pool work inherits it, and one of those is the screen-reader announcement,
    /// which is the last thing that should be waiting behind an encoder. So the
    /// priority is restored in a finally, whatever the work does or throws.</para>
    ///
    /// <para><b>The player's own threads are deliberately NOT here.</b> The
    /// announcements in Form1 are latency, not arithmetic — they carry a sentence
    /// to a reader and then stop — and the speech backends have to keep ahead of
    /// the ear. Slowing those to make an export polite would be trading the thing
    /// the app is for.</para>
    /// </summary>
    internal static class Background
    {
        /// <summary>Queue a long job at the back of the queue for the processor.</summary>
        public static void Queue(WaitCallback work)
        {
            if (work == null) return;
            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread t = Thread.CurrentThread;
                ThreadPriority was = t.Priority;
                try
                {
                    try { t.Priority = ThreadPriority.Lowest; } catch { }
                    work(state);
                }
                finally
                {
                    // ALWAYS, and this is the whole reason the wrapper exists.
                    try { t.Priority = was; } catch { }
                }
            });
        }

        /// <summary>The same for a job started as a Task.</summary>
        public static System.Threading.Tasks.Task Run(Action work)
        {
            if (work == null) return System.Threading.Tasks.Task.FromResult(0);
            return System.Threading.Tasks.Task.Run(() =>
            {
                Thread t = Thread.CurrentThread;
                ThreadPriority was = t.Priority;
                try
                {
                    try { t.Priority = ThreadPriority.Lowest; } catch { }
                    work();
                }
                finally { try { t.Priority = was; } catch { } }
            });
        }
    }
}
