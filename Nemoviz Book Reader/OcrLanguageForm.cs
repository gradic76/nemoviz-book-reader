using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Adding a language for reading pictures.
    ///
    /// <para><b>This exists because the obvious route was too much.</b> Windows'
    /// own way of getting OCR for a language is to install the whole display
    /// language, and Gordan's objection to that was right: a reader who wants to
    /// read a German scan should not have to install German Windows to do it.
    /// The narrow route — <c>Add-WindowsCapability</c> with the
    /// <c>Language.OCR</c> capability — installs the recognition model alone,
    /// about a quarter of a megabyte, and is what this dialog uses.</para>
    ///
    /// <para><b>Windows owns the consent, not NBR.</b> Installing an
    /// operating-system component needs elevation, a running process cannot
    /// elevate itself, and so the work is done by a short-lived process that
    /// Windows puts its own prompt in front of. NBR asks for nothing and decides
    /// nothing; a reader who says no simply comes back here unchanged.</para>
    ///
    /// <para>Built as a list rather than a combo on purpose: a reader arriving
    /// here does not know what is on offer, and 35 entries with their installed
    /// state read aloud one arrow-press at a time is what answers that. The
    /// route into Windows' own language settings stays available beside it, for
    /// anyone who wants the display language too.</para>
    /// </summary>
    internal class OcrLanguageForm : Form
    {
        private readonly ListBox list;
        private readonly Button install, windows, close;
        private readonly TextBox status;
        private readonly System.Windows.Forms.Timer watch;
        private readonly List<string> tags = new List<string>();

        private Process running;
        private string installingTag = "";
        private string statusText = "";

        /// <summary>True when something was actually installed, so the caller can
        /// rebuild whatever was showing the old list.</summary>
        public bool Changed { get; private set; }

        public OcrLanguageForm()
        {
            Text = Localization.T("Ocr.Add.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 340);

            list = new ListBox
            {
                Location = new Point(12, 12),
                Size = new Size(416, 186),
                TabIndex = 0
            };
            list.AccessibleName = Localization.T("Ocr.Add.List");
            list.SelectedIndexChanged += (s, e) => Retitle();

            // A read-only tabbable TextBox and never a Label — a reader driven by
            // Tab never visits a label, and this line is the only place the
            // outcome of an install is reported (§8b).
            statusText = Localization.T("Ocr.Add.Hint");
            status = new TextBox
            {
                Location = new Point(12, 206),
                Size = new Size(416, 56),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Text = statusText,
                TabIndex = 1
            };
            status.AccessibleName = statusText;
            status.Enter += (s, e) => PushStatus();

            install = new Button
            {
                Text = Localization.T("Ocr.Add.Install"),
                Location = new Point(12, 274),
                Size = new Size(150, 30),
                TabIndex = 2
            };
            install.Click += (s, e) => Install();

            windows = new Button
            {
                Text = Localization.T("Ocr.Add.WindowsSettings"),
                Location = new Point(172, 274),
                Size = new Size(150, 30),
                TabIndex = 3
            };
            windows.Click += (s, e) => WindowsOcr.OpenWindowsLanguageSettings();

            close = new Button
            {
                Text = Localization.T("Ocr.Add.Close"),
                Location = new Point(332, 274),
                Size = new Size(96, 30),
                TabIndex = 4
            };
            close.Click += (s, e) => Close();
            CancelButton = close;

            foreach (Control c in new Control[] { install, windows, close })
                c.AccessibleName = c.Text;

            Controls.Add(list);
            Controls.Add(status);
            Controls.Add(install);
            Controls.Add(windows);
            Controls.Add(close);

            watch = new System.Windows.Forms.Timer { Interval = 500 };
            watch.Tick += (s, e) => Poll();

            Fill();
        }

        /// <summary>The catalogue, each entry saying whether it is already here.
        /// Installed ones are shown rather than hidden — a reader looking for a
        /// language wants to know it is already there, not to find it missing
        /// from a list and wonder.</summary>
        private void Fill()
        {
            string keep = list.SelectedIndex >= 0 && list.SelectedIndex < tags.Count
                ? tags[list.SelectedIndex] : null;

            list.BeginUpdate();
            list.Items.Clear();
            tags.Clear();
            foreach (string tag in WindowsOcr.InstallableLanguages
                         .OrderBy(t => WindowsOcr.DisplayNameFor(t), StringComparer.CurrentCultureIgnoreCase))
            {
                bool have = WindowsOcr.IsInstalled(tag);
                tags.Add(tag);
                list.Items.Add(Localization.T(have ? "Ocr.Add.RowInstalled" : "Ocr.Add.Row",
                    WindowsOcr.DisplayNameFor(tag)));
            }
            list.EndUpdate();

            int at = keep == null ? 0 : Math.Max(0, tags.IndexOf(keep));
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(at, list.Items.Count - 1);
            Retitle();
        }

        /// <summary>Keeps the button honest about what pressing it would do.</summary>
        private void Retitle()
        {
            bool busy = running != null;
            int i = list.SelectedIndex;
            bool have = i >= 0 && i < tags.Count && WindowsOcr.IsInstalled(tags[i]);
            install.Enabled = !busy && i >= 0 && !have;
            list.Enabled = !busy;
            windows.Enabled = !busy;
        }

        private void Install()
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= tags.Count || running != null) return;

            installingTag = tags[i];
            string name = WindowsOcr.DisplayNameFor(installingTag);

            running = WindowsOcr.BeginInstall(installingTag);
            if (running == null)
            {
                // The commonest cause by far is the consent prompt being
                // dismissed, which is not an error and must not be dressed as one.
                Report(Localization.T("Ocr.Add.NotStarted"));
                installingTag = "";
                return;
            }
            Report(Localization.T("Ocr.Add.Installing", name));
            Retitle();
            watch.Start();
        }

        private void Poll()
        {
            if (running == null) { watch.Stop(); return; }

            // Ask the ENGINE, not the process. Measured on Gordan's first real
            // install: the helper sat for eight minutes while Windows Update
            // fetched the pack, with TrustedInstaller at zero CPU the whole time
            // — waiting on the network looks exactly like being wedged. Watching
            // for the pack to appear reports success the moment it is usable,
            // whatever the helper process is doing.
            if (++ticks % 8 == 0)
            {
                WindowsOcr.Rescan();
                if (WindowsOcr.IsInstalled(installingTag)) { Settle(0); return; }
            }

            try { if (!running.HasExited) return; }
            catch { }
            Settle(-1);
        }

        private int ticks;

        private void Settle(int unused)
        {
            watch.Stop();
            int code = -1;
            try { code = running.ExitCode; } catch { }
            try { running.Dispose(); } catch { }
            running = null;

            // The exit code is a hint; whether the language is THERE is the
            // answer, so ask the engine rather than the process.
            WindowsOcr.Rescan();
            string name = WindowsOcr.DisplayNameFor(installingTag);
            bool arrived = WindowsOcr.IsInstalled(installingTag);
            installingTag = "";

            Fill();
            if (arrived)
            {
                Changed = true;
                Report(Localization.T("Ocr.Add.Done", name));
            }
            // NOT "failed": the helper exiting is not the same as the install
            // being over, and saying so would send a reader looking for a fault
            // that may be a download still in flight.
            else Report(Localization.T("Ocr.Add.Failed", name));
        }

        private void Report(string text)
        {
            statusText = text;
            PushStatus();
            ScreenReader.Announce(this, text);
        }

        private void PushStatus()
        {
            if (status.Focused) return;
            if (status.Text == statusText) return;
            status.Text = statusText;
            status.AccessibleName = statusText;
        }

        /// <summary>Closing while an install runs is ALLOWED, and the first
        /// version of this was wrong to refuse it.
        ///
        /// <para>The analysis dialog holds its window shut because the work is
        /// ours and stopping it takes a moment. This work is not ours: Windows is
        /// downloading a component, NBR only started it and cannot cancel it. So
        /// refusing to close bought nothing and cost the one thing that matters —
        /// Gordan's first real install sat for eight minutes on a Windows Update
        /// fetch, in a modal dialog with no cancel and no way out. The install
        /// carries on in Windows either way; all that closing does is stop us
        /// watching.</para></summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running != null && e.CloseReason == CloseReason.UserClosing)
                Report(Localization.T("Ocr.Add.LeftRunning"));
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { watch.Dispose(); } catch { }
            try { if (running != null) running.Dispose(); } catch { }
            ScreenReader.Forget(this);
            base.OnFormClosed(e);
        }
    }
}
