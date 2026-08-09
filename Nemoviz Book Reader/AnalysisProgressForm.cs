using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The wait while a recording is measured.
    ///
    /// <para><b>It exists because the measurement grew.</b> It was 1.6 seconds
    /// when three segments were sampled, and an announcement with nothing to
    /// look at was defensible. At twenty segments it is <b>22 seconds measured
    /// on a modern machine, and four to seven minutes on the minimum
    /// configuration</b> — a length at which saying "analysing" once and then
    /// falling silent is not something anyone should be asked to sit
    /// through.</para>
    ///
    /// <para><b>The estimate is measured, not guessed.</b> The first segment
    /// says how fast this machine is; twenty times that is the answer for the
    /// rest. So the dialog can say "about half a minute" or "about five
    /// minutes" and be right on hardware nobody here has seen.</para>
    ///
    /// <para><b>Spoken at the quarters, not per segment.</b> Twenty
    /// announcements in a row is noise; four across the whole job is not. The
    /// bar is for the eye and a screen reader does not follow one, so the
    /// quarters are the progress as far as the ear is concerned — which also
    /// closes the "spoken progress for screen-reader users" §8a has carried as
    /// open since the archive extractor.</para></summary>
    internal class AnalysisProgressForm : Form
    {
        public SoundAnalysis Result { get; private set; }
        public bool Cancelled { get; private set; }

        private readonly BookData book;
        private readonly ProgressBar bar;
        private readonly TextBox status;
        private readonly Button cancel;
        private readonly System.Windows.Forms.Timer poll;
        private volatile bool stop;
        private int spokenQuarter;
        private int shownDone = -1;
        private DateTime started;

        /// <summary>Set the moment the worker has really stopped. Until then the
        /// window refuses to close — see OnFormClosing.</summary>
        private bool finished;

        /// <summary>What the status line WOULD say. Kept apart from the control
        /// because the control is not written to while it has focus (§2, the
        /// focus echo guard): a screen reader re-reads an edit control whose text
        /// changes under the cursor, and this one would change four times a
        /// second. It is brought up to date on the way in instead.</summary>
        private string statusText = "";

        public AnalysisProgressForm(BookData book)
        {
            this.book = book;

            Text = Localization.T("Analyse.Progress.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 132);

            // A read-only tabbable TextBox, never a Label: a screen reader driven
            // by Tab never visits a label (§8b), and the whole point of this line
            // is that a reader can go back and read where the job has got to
            // rather than having to catch the announcement as it passes.
            statusText = Localization.T("Analyse.Progress.Starting");
            status = new TextBox
            {
                Location = new Point(12, 14),
                Size = new Size(396, 36),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Text = statusText,
                TabIndex = 1
            };
            status.AccessibleName = statusText;
            // The other half of the focus echo guard: no updates arrive while it
            // is focused, so the value announced ON focus has to be made current.
            status.Enter += (s, e) => PushStatus();

            bar = new ProgressBar
            {
                Location = new Point(12, 58),
                Size = new Size(396, 24),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = Math.Max(1, SoundAnalyser.Tenths * SoundAnalyser.PerTenth),
                TabIndex = 2
            };

            // FOCUS STARTS HERE, and it has to. The status line is a read-only
            // edit control under the focus echo guard, so it does not change
            // while it is focused (§2) — start focus there and the reader watches
            // a line that is frozen for the whole job. Focus therefore starts on
            // the only ACTION in the window; the line is one Tab away for anyone
            // who wants to go and read it, and refreshes as they arrive. What the
            // ear gets does not depend on any of this: the opening line and the
            // quarters are announced, which needs no focus at all.
            cancel = new Button
            {
                Text = Localization.T("Analyse.Progress.Cancel"),
                Location = new Point(308, 94),
                Size = new Size(100, 28),
                TabIndex = 0
            };
            cancel.AccessibleName = cancel.Text;
            cancel.Click += (s, e) => Give();
            CancelButton = cancel;

            Controls.Add(status);
            Controls.Add(bar);
            Controls.Add(cancel);

            poll = new System.Windows.Forms.Timer { Interval = 250 };
            poll.Tick += (s, e) => Refresh1();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            started = DateTime.UtcNow;
            // Cleared HERE and not only inside Measure: the counter is static, so
            // until the worker has actually started the first poll would read the
            // tally the last book left behind and show a full bar for a quarter
            // of a second.
            SoundAnalyser.Progress = 0;
            poll.Start();
            // Said rather than left on screen: focus is on Cancel, so without
            // this the window opens saying "Cancel, button" and nothing about
            // what it is cancelling.
            Say(statusText);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SoundAnalysis r = null;
                try { r = SoundAnalyser.Measure(book, () => stop, null); }
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
            int done = SoundAnalyser.Progress;
            if (done < 0) done = 0;
            if (done > bar.Maximum) done = bar.Maximum;
            bar.Value = done;

            // The wording is rebuilt only when a segment lands, never on every
            // tick. Rebuilding per tick made the estimate WALK BACKWARDS between
            // segments — measured: "about 80 seconds" then "about 85", then "65"
            // then "70" — because the elapsed time keeps growing while the count
            // that divides it does not. A remaining time that goes up while you
            // watch reads as the job getting worse.
            if (!Cancelled && done != shownDone)
            {
                shownDone = done;
                statusText = done < 1
                    ? Localization.T("Analyse.Progress.Starting")
                    : Localization.T("Analyse.Progress.Working", done, bar.Maximum, Remaining(done));
            }
            PushStatus();

            // Only at the quarters. Four utterances across the whole job.
            int quarter = done * 4 / bar.Maximum;
            if (quarter > spokenQuarter && quarter < 4)
            {
                spokenQuarter = quarter;
                Say(Localization.T("Analyse.Progress.Quarter", quarter * 25, Remaining(done)));
            }
        }

        /// <summary>Puts the current wording on the control, unless the control is
        /// where the reader is standing.</summary>
        private void PushStatus()
        {
            if (status.Focused) return;
            if (status.Text == statusText) return;
            status.Text = statusText;
            status.AccessibleName = statusText;
        }

        /// <summary>Give up. Asked for by the Cancel button, by Escape, and by the
        /// window's own close box, which all mean the same thing here.
        ///
        /// <para>It does NOT close the window. The measurement is checked between
        /// segments, so stopping takes up to one segment, and until the worker has
        /// really let go there is a decode running against the book. Closing on
        /// the keypress would put the reader back in Properties with that still
        /// going on — and a second visit could start another one.</para></summary>
        private void Give()
        {
            if (finished || Cancelled) return;
            stop = true;
            Cancelled = true;
            cancel.Enabled = false;
            statusText = Localization.T("Analyse.Progress.Cancelling");
            PushStatus();
            Say(statusText);
        }

        /// <summary>How long is left, in the reader's words, from how long the
        /// segments so far actually took on THIS machine.</summary>
        private string Remaining(int done)
        {
            if (done < 1) return Localization.T("Analyse.Progress.Estimating");
            double each = (DateTime.UtcNow - started).TotalSeconds / done;
            int left = (int)Math.Round(each * (bar.Maximum - done));
            if (left <= 5) return Localization.T("Analyse.Progress.AlmostDone");
            if (left < 90) return Localization.T("Analyse.Progress.Seconds", (left / 5) * 5);
            return Localization.T("Analyse.Progress.Minutes", (int)Math.Round(left / 60.0));
        }

        private void Say(string text)
        {
            ScreenReader.Announce(this, text);
        }

        private void Finish(SoundAnalysis r)
        {
            poll.Stop();
            finished = true;
            // The last tick never comes, so the bar would be left one segment
            // short of the end it has actually reached.
            if (!Cancelled) bar.Value = bar.Maximum;
            Result = Cancelled ? null : r;
            DialogResult = Cancelled ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        /// <summary>The close box means Cancel, and like Cancel it does not take
        /// effect until the worker has stopped.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only a person is made to wait. Windows shutting down, or the app
            // being closed underneath us, gets its window back at once.
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
