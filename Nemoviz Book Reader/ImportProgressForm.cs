using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// A bulk import, behind a bar, off the UI thread.
    ///
    /// <para><b>The fault this fixes was reported as a hang and is not one.</b>
    /// Gordan imported the whole of Test naslovi into an empty library and the
    /// window went "not responding" for between 80 and 160 seconds. The watchdog
    /// caught five samples in five different places -- a regex in
    /// <c>TextCleaner</c>, another mid-scan, <c>File.InternalReadAllBytes</c>,
    /// <c>LibLouis.lou_backTranslateString</c>, and the first regex again -- with
    /// the UI thread RUNNING at 88 % of a core. Nothing was stuck: <c>ImportFolder</c>
    /// did the whole job synchronously, so no message was pumped until every book
    /// had been parsed, cleaned and back-translated.</para>
    ///
    /// <para><b>Extraction was already behind a dialog</b> (<see cref="ExtractProgressForm"/>);
    /// everything AFTER it -- the per-book parsing, the text cleaning, liblouis,
    /// the rescan -- had neither progress nor a thread. This form covers the whole
    /// loop, book by book, which is the shape the brief asked for.</para>
    ///
    /// <para><b>Every accessibility decision here is <see cref="AnalysisProgressForm"/>'s,
    /// re-used rather than re-derived</b>, because that dialog paid for them:</para>
    ///
    /// <list type="bullet">
    /// <item>the status line is a read-only TABBABLE TextBox, never a Label, since
    /// a reader driven by Tab never visits a label;</item>
    /// <item>focus starts on <b>Cancel</b>. The status line sits under the focus
    /// echo guard, so it does not change while it is focused -- start focus there
    /// and the reader watches a line frozen for the whole import. It refreshes on
    /// the way in instead;</item>
    /// <item>the opening line and then <b>the quarters</b> are spoken, and nothing
    /// else. Analysis had twenty segments to choose from; a bulk import can have
    /// six hundred books, so speaking each one is not a smaller version of the
    /// same mistake, it is a much larger one;</item>
    /// <item><b>Cancel does not close the window</b> -- the worker does, when it
    /// notices between books. Closing on the keypress would leave a book being
    /// copied with the reader back on the shelf, and a second attempt could start
    /// another.</item>
    /// </list>
    ///
    /// <para><b>The stop flag is read BETWEEN books, never inside one.</b> A book
    /// half-copied into the library is worse than one not imported at all, and the
    /// books already in are kept -- which is what the cancelling line says.</para>
    /// </summary>
    internal sealed class ImportProgressForm : Form
    {
        public bool Cancelled { get; private set; }
        public Exception Error { get; private set; }

        private readonly int total;
        private readonly Action<ImportProgressForm> work;

        private readonly TextBox status;
        private readonly ProgressBar bar;
        private readonly Button cancel;

        private string statusText;
        private int at;               // books finished with, for the bar
        private int spokenQuarter;
        private volatile bool stop;
        private bool finished;

        public ImportProgressForm(int total, Action<ImportProgressForm> work)
        {
            this.total = Math.Max(1, total);
            this.work = work;

            Text = Localization.T("Import.Progress.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 132);

            statusText = Localization.T("Import.Progress.Starting");
            status = new TextBox
            {
                Location = new Point(12, 14),
                Size = new Size(436, 36),
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
                Size = new Size(436, 24),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = this.total,
                TabIndex = 2
            };

            cancel = new Button
            {
                Text = Localization.T("Import.Progress.Cancel"),
                Location = new Point(348, 94),
                Size = new Size(100, 28),
                TabIndex = 0
            };
            cancel.AccessibleName = cancel.Text;
            cancel.Click += (s, e) => Give();
            CancelButton = cancel;
            // ...and the DialogResult straight back off it, or the click closes
            // this window while the worker runs on. AnalysisProgressForm carries
            // the measurement: OnFormClosing cannot catch that path, because it
            // arrives as CloseReason.None rather than UserClosing.
            cancel.DialogResult = DialogResult.None;

            Controls.Add(status);
            Controls.Add(bar);
            Controls.Add(cancel);
        }

        /// <summary>True once the reader has asked to stop. Read it between books.</summary>
        public bool StopRequested { get { return stop; } }

        /// <summary>A book is starting. Safe from the worker thread.</summary>
        public void Book(int index, string name)
        {
            Post(() =>
            {
                at = Math.Max(0, index - 1);
                bar.Value = Math.Min(at, bar.Maximum);
                // ONCE CANCELLED, THE LINE BELONGS TO THE CANCELLING MESSAGE. A
                // report is posted rather than called, so one already in flight
                // when the reader presses Cancel arrives afterwards and used to
                // overwrite "stopping after this book" with the name of the book
                // it was starting -- measured on the real dialog, which is exactly
                // the kind of thing only driving it shows.
                if (Cancelled) return;
                Set(Localization.T("Import.Progress.Working", index, total, Short(name)));
                Quarter(at);
            });
        }

        /// <summary>An archive inside the current book is being unpacked. The inner
        /// count is the one thing a bulk import cannot infer from the book number:
        /// one archive can be most of the wait.</summary>
        public void Extract(string archiveName, int done, int howMany)
        {
            Post(() =>
            {
                Set(howMany > 0
                    ? Localization.T("Import.Progress.Unpacking", Short(archiveName), done, howMany)
                    : Localization.T("Import.Progress.UnpackingCount", Short(archiveName), done));
            });
        }

        /// <summary>The archive password prompt, on the UI thread where it belongs.
        /// Called from the worker, so it blocks until the reader answers.</summary>
        public string AskPassword(string archiveName, bool retry)
        {
            if (InvokeRequired)
                return (string)Invoke(new Func<string>(() => AskPassword(archiveName, retry)));
            return ArchivePasswordPrompt.Show(this, archiveName, retry);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Said rather than only shown: focus is on Cancel, so without this the
            // window opens saying "Cancel, button" and nothing about what it is
            // cancelling.
            Say(statusText);

            Background.Run(() =>
            {
                try { work(this); }
                catch (Exception ex) { Error = ex; }
                finally
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            finished = true;
                            DialogResult = DialogResult.OK;
                            Close();
                        }));
                    }
                    catch { }
                }
            });
        }

        private void Give()
        {
            if (finished || Cancelled) return;
            Cancelled = true;
            stop = true;
            cancel.Enabled = false;
            // PushStatus, not Set: disabling the button just above throws focus
            // onto the status line, and Set's focus echo guard then REFUSES to
            // write -- so the one line that says the import is stopping never
            // reached the screen. Measured on the real dialog. The guard yields
            // here because the reader has just pressed the button that caused
            // this line, which is the one moment it is not news to them.
            statusText = Localization.T("Import.Progress.Cancelling");
            PushStatus();
            Say(statusText);
        }

        /// <summary>The close box means Cancel, and like Cancel it does not take
        /// effect until the worker has noticed, between books.</summary>
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

        private void Set(string text)
        {
            statusText = text;
            // The focus echo guard: nothing is written into the box while a reader
            // is standing in it, or the line changes under their cursor.
            if (!status.Focused)
            {
                status.Text = statusText;
                status.AccessibleName = statusText;
            }
        }

        private void PushStatus()
        {
            status.Text = statusText;
            status.AccessibleName = statusText;
        }

        private void Quarter(int done)
        {
            if (Cancelled) return;
            int q = done * 4 / Math.Max(1, total);
            if (q > spokenQuarter && q < 4)
            {
                spokenQuarter = q;
                Say(Localization.T("Import.Progress.Quarter", q * 25, done, total));
            }
        }

        private void Post(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }   // the window can go while a report is in flight
        }

        private static string Short(string nameOrPath)
        {
            string s = nameOrPath ?? "";
            try { s = System.IO.Path.GetFileName(s.TrimEnd('\\', '/')); }
            catch { }
            if (s.Length > 44) s = s.Substring(0, 44) + "…";
            return s;
        }

        private void Say(string text)
        {
            ScreenReader.Announce(this, text);
        }
    }
}
