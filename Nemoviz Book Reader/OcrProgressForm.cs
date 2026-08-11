using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The wait while an image document is read.
    ///
    /// <para>Built on <see cref="AnalysisProgressForm"/>, whose accessibility
    /// lessons were paid for once and are not re-learned here: <b>focus starts on
    /// Cancel</b>, because the status line is a read-only edit control under the
    /// focus echo guard and a reader standing on it would watch a line that never
    /// changes for the whole job; the line is a <b>tabbable TextBox and never a
    /// Label</b>, so a reader can go back and read where the job has got to
    /// instead of having to catch the announcement; progress is spoken <b>at the
    /// quarters</b>, four utterances rather than one per page; and Cancel sets a
    /// flag while the <b>worker</b> keeps the right to close the window, so the
    /// reader is never handed back a dialog with a page still being recognized
    /// behind it.</para>
    ///
    /// <para>The estimate is measured on the machine it is running on — the first
    /// page says how fast this one is. About half a second a page was measured
    /// here, so a 300-page book is roughly three minutes; on a slower machine it
    /// will say so rather than guess.</para></summary>
    internal class OcrProgressForm : Form
    {
        /// <summary>The recognized text, page by page, or null if cancelled.</summary>
        public string Result { get; private set; }
        /// <summary>Where each page starts in <see cref="Result"/>, so Go To can
        /// move by page in a book that has no other structure to offer. Collected
        /// as the text is built, never derived afterwards — see
        /// <see cref="TextDoc.SyncIds"/> for what re-deriving offsets costs.</summary>
        public System.Collections.Generic.List<(string Label, int Offset)> Pages { get; private set; }
        /// <summary>How many pages actually yielded any text. A book where this is
        /// zero is not a book — see <see cref="OcrImport"/>.</summary>
        public int PagesWithText { get; private set; }
        public bool Cancelled { get; private set; }

        private readonly OcrPageSource source;
        private readonly string language;
        private readonly ProgressBar bar;
        private readonly TextBox status;
        private readonly Button cancel;
        private readonly System.Windows.Forms.Timer poll;

        private volatile bool stop;
        private volatile int done;
        private int spokenQuarter;
        private int shownDone = -1;
        private bool finished;
        private DateTime started;
        private string statusText = "";

        public OcrProgressForm(OcrPageSource source, string language)
        {
            this.source = source;
            this.language = language ?? "";

            Text = Localization.T("Ocr.Progress.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 132);

            statusText = Localization.T("Ocr.Progress.Starting");
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
            status.Enter += (s, e) => PushStatus();

            bar = new ProgressBar
            {
                Location = new Point(12, 58),
                Size = new Size(396, 24),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = Math.Max(1, source.PageCount),
                TabIndex = 2
            };

            cancel = new Button
            {
                Text = Localization.T("Ocr.Progress.Cancel"),
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
            poll.Start();
            Say(statusText);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string text = null;
                int withText = 0;
                try { text = ReadAll(out withText); }
                catch { }
                try
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)(() => Finish(text, withText)));
                }
                catch { }
            });
        }

        /// <summary>The job itself, on a worker thread.
        ///
        /// <para>A page marker goes in per page, the same shape
        /// <see cref="PdfParser"/> uses, so Go To can move by page in a book that
        /// never had any other structure to offer.</para></summary>
        private string ReadAll(out int withText)
        {
            var sb = new StringBuilder();
            var pages = new System.Collections.Generic.List<(string, int)>();
            withText = 0;
            for (int i = 0; i < source.PageCount; i++)
            {
                if (stop) return null;
                // A marker per page, INCLUDING the blank ones — a scanned book
                // really does have empty leaves, and skipping them would make the
                // page numbers stop matching the paper.
                pages.Add(((i + 1).ToString(), sb.Length));
                byte[] image = source.Page(i);
                string text = image == null ? null : WindowsOcr.Read(image, language);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    withText++;
                    sb.Append(text.Trim()).Append("\n\n");
                }
                done = i + 1;
            }
            Pages = pages;
            return sb.ToString();
        }

        private void Refresh1()
        {
            int d = done;
            if (d < 0) d = 0;
            if (d > bar.Maximum) d = bar.Maximum;
            bar.Value = d;

            // Rebuilt only when a page lands, never per tick — rebuilding per tick
            // makes the estimate walk backwards between pages, which reads as the
            // job getting worse while you watch (§ AnalysisProgressForm).
            if (!Cancelled && d != shownDone)
            {
                shownDone = d;
                statusText = d < 1
                    ? Localization.T("Ocr.Progress.Starting")
                    : Localization.T("Ocr.Progress.Working", d, bar.Maximum, Remaining(d));
            }
            PushStatus();

            int quarter = d * 4 / bar.Maximum;
            if (quarter > spokenQuarter && quarter < 4)
            {
                spokenQuarter = quarter;
                Say(Localization.T("Ocr.Progress.Quarter", quarter * 25, Remaining(d)));
            }
        }

        private void PushStatus()
        {
            if (status.Focused) return;
            if (status.Text == statusText) return;
            status.Text = statusText;
            status.AccessibleName = statusText;
        }

        private void Give()
        {
            if (finished || Cancelled) return;
            stop = true;
            Cancelled = true;
            cancel.Enabled = false;
            statusText = Localization.T("Ocr.Progress.Cancelling");
            PushStatus();
            Say(statusText);
        }

        private string Remaining(int d)
        {
            if (d < 1) return Localization.T("Ocr.Progress.Estimating");
            double each = (DateTime.UtcNow - started).TotalSeconds / d;
            int left = (int)Math.Round(each * (bar.Maximum - d));
            if (left <= 5) return Localization.T("Ocr.Progress.AlmostDone");
            if (left < 90) return Localization.T("Ocr.Progress.Seconds", (left / 5) * 5);
            return Localization.T("Ocr.Progress.Minutes", (int)Math.Round(left / 60.0));
        }

        private void Say(string text) { ScreenReader.Announce(this, text); }

        private void Finish(string text, int withText)
        {
            poll.Stop();
            finished = true;
            if (!Cancelled) bar.Value = bar.Maximum;
            Result = Cancelled ? null : text;
            PagesWithText = withText;
            DialogResult = Cancelled ? DialogResult.Cancel : DialogResult.OK;
            Close();
        }

        /// <summary>The close box means Cancel, and like Cancel it waits for the
        /// worker — a page is being recognized right now.</summary>
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
