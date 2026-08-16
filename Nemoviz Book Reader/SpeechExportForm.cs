using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The wait while a book becomes one MP3.
    ///
    /// <para>Built on <see cref="TranslationProgressForm"/>, and for the same
    /// reasons: focus starts on the only action, so the status line is not frozen
    /// under a reader's cursor; the wording is rebuilt only when a sentence
    /// LANDS, never per tick, or the estimate walks backwards while you watch;
    /// progress is spoken at the quarters rather than per sentence, which for a
    /// book would be thousands of utterances; and Cancel does not close the
    /// window — the worker does, when it has really stopped.</para>
    ///
    /// <para><b>Two phases, one bar.</b> Whatever is missing is made, then
    /// everything is joined. The joining is seconds against the making's
    /// minutes, so showing them separately would be a second bar that jumps
    /// straight to full — and for a book already prepared while it was read,
    /// there is nothing to make and the whole thing is the join.</para>
    ///
    /// <para>Giving up costs nothing and the window says so: every sentence made
    /// is in the cache, so stopping and coming back finishes the rest rather than
    /// starting again.</para></summary>
    internal class SpeechExportForm : Form
    {
        public bool Ok { get; private set; }
        public string OutPath { get; private set; }
        public int Written { get; private set; }
        public int Missing { get; private set; }
        public bool Cancelled { get; private set; }

        private readonly string bookFolder, voice;
        private readonly List<string> spoken;
        private readonly ProgressBar bar;
        private readonly TextBox status;
        private readonly Button cancel;
        private readonly System.Windows.Forms.Timer poll;

        private volatile bool stop;
        private volatile int done;
        private volatile bool joining;
        private int spokenQuarter;
        private int shownDone = -1;
        private DateTime started;
        private bool finished;
        private string statusText = "";

        public SpeechExportForm(string bookFolder, string voice, List<string> spoken, string outPath)
        {
            this.bookFolder = bookFolder;
            this.voice = voice;
            this.spoken = spoken;
            OutPath = outPath;

            Text = Localization.T("Export.Progress.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 150);

            statusText = Localization.T("Export.Progress.Starting");
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
                Maximum = Math.Max(1, spoken.Count),
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

            ThreadPool.QueueUserWorkItem(_ =>
            {
                int written = 0, missing = 0;
                try
                {
                    // Anything not already made. A cloud book read to the end has
                    // nothing here; a local one has everything, since its speech is
                    // free and is therefore not kept until somebody asks for it.
                    var prefill = SpeechPrefill.For(bookFolder, voice, spoken);
                    if (prefill != null)
                    {
                        prefill.Start();
                        while (prefill.Running && !stop)
                        {
                            done = prefill.Done;
                            Thread.Sleep(100);
                        }
                        if (stop) prefill.Stop();
                    }
                    else
                    {
                        MakeLocally();
                    }

                    if (!stop)
                    {
                        joining = true;
                        done = spoken.Count;
                        written = SpeechExport.ToFile(bookFolder, voice, spoken, OutPath,
                                                      out missing, (i, n) => !stop);
                    }
                    // The MP3 is written; what was made to build it is no longer
                    // worth its disk. Only for a local voice — see DropLocalPieces.
                    DropLocalPieces();
                }
                catch { DropLocalPieces(); }
                try
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) return;
                    int w = written, m = missing;
                    BeginInvoke((MethodInvoker)(() => Finish(w, m)));
                }
                catch { }
            });
        }

        /// <summary>A voice that is not a cloud one. There is no service to ask
        /// and nothing to pay, but the speech still has to exist before it can be
        /// joined — so it is made here and kept like any other. <b>This is the one
        /// case where a local voice fills the cache</b>, and it fills it because
        /// the reader asked for a file.</summary>
        private void MakeLocally()
        {
            CompositeSpeechBackend speech = null;
            try
            {
                speech = new CompositeSpeechBackend();
                speech.SelectVoice(voice);
                var renderer = speech as ISpeechRenderer;

                for (int i = 0; i < spoken.Count && !stop; i++)
                {
                    done = i;
                    string s = spoken[i];
                    if (string.IsNullOrWhiteSpace(s) || SpeechCache.Has(bookFolder, voice, s)) continue;

                    byte[] wav = renderer.Render(s);
                    // Nothing came back — a 32-bit voice, which renders behind an
                    // IPC line that has no command for this yet. Stop rather than
                    // grind through a thousand sentences producing nothing; the
                    // count of what is missing then says what happened.
                    if (wav == null) break;
                    SpeechCache.Put(bookFolder, voice, s, wav);
                }
                done = spoken.Count;
            }
            catch { }
            finally { if (speech != null) try { speech.Dispose(); } catch { } }
        }

        /// <summary>Throws away the pieces this export made with a LOCAL voice.
        ///
        /// <para><b>The cache is kept only where re-making costs something</b>
        /// (Gordan, 2026-08-16). A cloud sentence was paid for and must not be
        /// paid for twice; a local one is free and, measured on his own book,
        /// about ninety times faster than listening — an hour and three quarters
        /// of speech remade in seventy seconds. Keeping 41 MB a book to save
        /// seventy seconds is the wrong way round, and it was accumulating
        /// invisibly behind a command whose whole output is the MP3.</para>
        ///
        /// <para>Done whether the export finished or was given up on, so the rule
        /// is one a reader can predict rather than one they have to work
        /// out.</para></summary>
        private void DropLocalPieces()
        {
            // ONLY for a voice that costs nothing to use again. A cloud voice's
            // pieces are money already spent and are never touched here.
            if (CloudVoices.IsOne(voice)) return;

            // Every piece belonging to THIS voice, not merely the ones this run
            // made. An export that found most of them already there would
            // otherwise leave them for ever — which is exactly what the first
            // version of this did, and how 41 MB came to sit behind a command
            // whose whole output is one MP3. Addressed by key, so a piece
            // belonging to some other voice in the same folder is untouched.
            foreach (string s in spoken)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string p = SpeechCache.PathFor(bookFolder, voice, s);
                try { if (p != null && System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
            }
            // The folder too, but only if this left it empty — a book with cloud
            // speech in it keeps everything.
            try
            {
                string dir = SpeechCache.FolderFor(bookFolder);
                if (dir != null && System.IO.Directory.Exists(dir) &&
                    System.IO.Directory.GetFileSystemEntries(dir).Length == 0)
                    System.IO.Directory.Delete(dir);
            }
            catch { }
        }

        private void Refresh1()
        {
            int d = done, t = spoken.Count;
            bar.Maximum = Math.Max(1, t);
            bar.Value = Math.Min(bar.Maximum, Math.Max(0, d));

            if (!Cancelled && d != shownDone)
            {
                shownDone = d;
                statusText = joining
                    ? Localization.T("Export.Progress.Joining")
                    : Localization.T("Export.Progress.Making", d, t, Remaining(d, t));
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

        private void Finish(int written, int missing)
        {
            poll.Stop();
            finished = true;
            if (!Cancelled) bar.Value = bar.Maximum;
            Written = written;
            Missing = missing;
            Ok = !Cancelled && written > 0;
            DialogResult = Ok ? DialogResult.OK : DialogResult.Cancel;
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
