using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// The wait while a book is translated.
    ///
    /// <para>Built on <see cref="AnalysisProgressForm"/>, deliberately and almost
    /// line for line, because every awkward thing about that window was learned the
    /// hard way and none of it is different here: focus starts on the only action
    /// so the status line is not frozen under the reader's cursor; the wording is
    /// rebuilt only when a piece LANDS, never per tick, or the estimate walks
    /// backwards while you watch; the progress is spoken at the quarters rather
    /// than per piece, which for a book would be a hundred and thirty utterances;
    /// and Cancel does not close the window — the worker does, when it has really
    /// stopped.</para>
    ///
    /// <para><b>What is different is the length.</b> The sound analysis is twenty
    /// seconds on a good machine; a book is a hundred-odd requests and ten to forty
    /// minutes. So two things matter more here than there. Giving up must be
    /// possible at any moment and must cost nothing — every piece already
    /// translated is in the cache, so stopping and coming back tomorrow resumes
    /// rather than restarts. And the window says so, because a reader who does not
    /// know that will sit through a job they could have left.</para>
    /// </summary>
    internal class TranslationProgressForm : Form
    {
        public TranslationReport Report { get; private set; }
        public bool Cancelled { get; private set; }

        private readonly string bookText;
        private readonly TranslationJob.Options options;
        private readonly ProgressBar bar;
        private readonly TextBox status;
        private readonly Button cancel;
        private readonly System.Windows.Forms.Timer poll;

        private volatile bool stop;
        private volatile int done;
        private volatile int total = 1;
        private int spokenQuarter;
        private int shownDone = -1;
        private DateTime started;
        private bool finished;
        /// <summary>When the passage now in flight began — the heartbeat's clock.
        /// The job reports only completions, so this is the window's own.</summary>
        private DateTime pieceStarted = DateTime.UtcNow;
        /// <summary>Whole minutes already spoken about the current passage, so the
        /// heartbeat says something once a minute rather than once a tick.</summary>
        private int spokenWait;
        private string statusText = "";

        public TranslationProgressForm(string bookText, TranslationJob.Options options)
        {
            this.bookText = bookText;
            this.options = options;

            Text = Localization.T("Translate.Progress.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 150);

            statusText = Localization.T("Translate.Progress.Starting");
            status = new TextBox
            {
                Location = new Point(12, 14),
                Size = new Size(416, 52),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Text = statusText,
                TabIndex = 1
            };
            status.AccessibleName = statusText;
            status.Enter += (s, e) => PushStatus();

            bar = new ProgressBar
            {
                Location = new Point(12, 74),
                Size = new Size(416, 24),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
                TabIndex = 2
            };

            cancel = new Button
            {
                Text = Localization.T("Translate.Progress.Cancel"),
                Location = new Point(328, 110),
                Size = new Size(100, 28),
                TabIndex = 0
            };
            cancel.AccessibleName = cancel.Text;
            cancel.Click += (s, e) => Give();
            CancelButton = cancel;

            Controls.Add(status);
            Controls.Add(bar);
            Controls.Add(cancel);

            poll = new System.Windows.Forms.Timer { Interval = 300 };
            poll.Tick += (s, e) => Refresh1();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            started = DateTime.UtcNow;
            poll.Start();
            Say(statusText);

            // The job's own callback arrives on the worker thread. It only stores
            // two numbers and answers whether to carry on; the window reads them on
            // its own clock. Marshalling per piece would be a hundred and thirty
            // trips across threads to move an integer.
            options.Progress = (d, t, what) =>
            {
                done = d;
                total = t < 1 ? 1 : t;
                return !stop;
            };

            Background.Queue(_ =>
            {
                TranslationReport r = null;
                try { r = TranslationJob.Run(bookText, options); }
                catch { }
                try
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)(() => Finish(r)));
                }
                catch { }
            });
        }

        private void Refresh1()
        {
            int d = done, t = total;
            bar.Maximum = Math.Max(1, t);
            bar.Value = Math.Min(bar.Maximum, Math.Max(0, d));

            if (!Cancelled && d != shownDone)
            {
                shownDone = d;
                pieceStarted = DateTime.UtcNow;
                spokenWait = 0;
                statusText = d < 1
                    ? Localization.T("Translate.Progress.Starting")
                    : Localization.T("Translate.Progress.Working", d, t, Remaining(d, t));
            }

            // THE HEARTBEAT. Reported from use, 2026-08-15: the window stood on
            // "Getting ready" for minutes and read as a frozen program. It was not
            // — a single passage can take four minutes when the service is
            // throttling, and the job only reports when one LANDS. Between those
            // moments nothing changed on screen at all, and for a reader who cannot
            // see it there was nothing to distinguish work from a hang.
            //
            // So the line carries how long the current passage has been going.
            // It costs no request and asks the job for nothing: the window already
            // has a clock of its own.
            if (!Cancelled && !finished)
            {
                int waited = (int)(DateTime.UtcNow - pieceStarted).TotalSeconds;
                if (waited >= 10)
                    statusText = (d < 1
                        ? Localization.T("Translate.Progress.Starting")
                        : Localization.T("Translate.Progress.Working", d, t, Remaining(d, t)))
                        + " " + Localization.T("Translate.Progress.StillOn", waited);

                // SPOKEN RARELY, and only once a passage has plainly gone long.
                // Every second would be noise over the reader's own work; silence
                // for four minutes is what caused the report. A minute apart is
                // the compromise, and it stops the moment a passage lands.
                if (waited >= 60 && waited / 60 > spokenWait)
                {
                    spokenWait = waited / 60;
                    Say(Localization.T("Translate.Progress.StillWorking", Math.Max(1, d + 1), t));
                }
            }
            PushStatus();

            int quarter = d * 4 / Math.Max(1, t);
            if (quarter > spokenQuarter && quarter < 4)
            {
                spokenQuarter = quarter;
                Say(Localization.T("Translate.Progress.Quarter", quarter * 25, Remaining(d, t)));
            }
        }

        private void PushStatus()
        {
            if (status.Focused) return;
            if (status.Text == statusText) return;
            status.Text = statusText;
            status.AccessibleName = statusText;
        }

        /// <summary>Give up — and here that is genuinely cheap, which the wording
        /// says out loud. The job checks between pieces, so stopping takes at most
        /// one request, and everything done so far is already cached.</summary>
        private void Give()
        {
            if (finished || Cancelled) return;
            stop = true;
            Cancelled = true;
            cancel.Enabled = false;
            statusText = Localization.T("Translate.Progress.Cancelling");
            PushStatus();
            Say(statusText);
        }

        private string Remaining(int d, int t)
        {
            if (d < 1) return Localization.T("Translate.Progress.Estimating");
            double each = (DateTime.UtcNow - started).TotalSeconds / d;
            int left = (int)Math.Round(each * (t - d));
            if (left <= 5) return Localization.T("Translate.Progress.AlmostDone");
            if (left < 90) return Localization.T("Translate.Progress.Seconds", (left / 5) * 5);
            return Localization.T("Translate.Progress.Minutes", (int)Math.Round(left / 60.0));
        }

        private void Say(string text) { ScreenReader.Announce(this, text); }

        private void Finish(TranslationReport r)
        {
            poll.Stop();
            finished = true;
            if (!Cancelled && r != null) bar.Value = bar.Maximum;
            Report = r;
            DialogResult = (Cancelled || r == null || !r.Ok) ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!finished && e.CloseReason == CloseReason.UserClosing)
            {
                Give();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            stop = true;
            try { poll.Dispose(); } catch { }
            ScreenReader.Forget(this);
            base.OnFormClosed(e);
        }
    }
}
