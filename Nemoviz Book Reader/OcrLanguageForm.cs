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
        private readonly CheckedListBox list;
        private readonly Button install, windows, close;
        private readonly TextBox status;
        private readonly System.Windows.Forms.Timer watch;
        private readonly List<string> tags = new List<string>();

        private Process running;
        private List<string> installingTags = new List<string>();
        private string statusText = "";

        /// <summary>True when something was actually installed, so the caller can
        /// rebuild whatever was showing the old list.</summary>
        public bool Changed { get; private set; }

        private readonly LanguagePackFamily family;

        public OcrLanguageForm() : this(LanguagePackFamily.Ocr) { }

        public OcrLanguageForm(LanguagePackFamily family)
        {
            this.family = family ?? LanguagePackFamily.Ocr;
            Text = Localization.T(this.family.TitleKey);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 340);

            // CHECK BOXES, not a single pick (Gordan, 2026-08-14): a reader who
            // knows they want German, Italian and Spanish should say so once and
            // wait once, not go round this dialog three times for three ten-minute
            // waits. CheckOnClick so a click is a choice — the default needs a
            // click to select and another to tick, which is a trap without sight.
            list = new CheckedListBox
            {
                CheckOnClick = true,
                Location = new Point(12, 12),
                Size = new Size(416, 186),
                TabIndex = 0
            };
            list.AccessibleName = Localization.T("Ocr.Add.List");
            list.SelectedIndexChanged += (s, e) => Retitle();
            // The Install button follows the TICKS, not the cursor, so it has to
            // be re-judged when one is put in or taken out.
            list.ItemCheck += (s, e) => BeginInvoke((MethodInvoker)Retitle);

            // A read-only tabbable TextBox and never a Label — a reader driven by
            // Tab never visits a label, and this line is the only place the
            // outcome of an install is reported (§8b).
            statusText = Localization.T(this.family.HintKey);
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
            foreach (string tag in family.Tags
                         .OrderBy(t => WindowsOcr.DisplayNameFor(t), StringComparer.CurrentCultureIgnoreCase))
            {
                bool have = family.IsInstalled(tag);
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
            install.Enabled = !busy && Ticked().Count > 0;
            list.Enabled = !busy;
            windows.Enabled = !busy;
        }

        /// <summary>The languages the reader has ticked and does not already have.
        /// An installed one that gets ticked is simply skipped rather than
        /// refused — the tick is a wish, not an instruction to reinstall.</summary>
        private List<string> Ticked()
        {
            var want = new List<string>();
            foreach (int i in list.CheckedIndices)
                if (i >= 0 && i < tags.Count && !family.IsInstalled(tags[i])) want.Add(tags[i]);
            return want;
        }

        private void Install()
        {
            if (running != null) return;
            installingTags = Ticked();
            if (installingTags.Count == 0) return;

            string name = string.Join(", ", installingTags.Select(WindowsOcr.DisplayNameFor));

            running = WindowsOcr.BeginInstallCapabilities(
                installingTags.Select(family.Capability).ToArray());
            if (running == null)
            {
                // The commonest cause by far is the consent prompt being
                // dismissed, which is not an error and must not be dressed as one.
                Report(Localization.T("Ocr.Add.NotStarted"));
                installingTags.Clear();
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
                family.Rescan();
                if (installingTags.All(family.IsInstalled)) { Settle(0); return; }
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
            family.Rescan();
            var arrivedTags = installingTags.Where(family.IsInstalled).ToList();
            var missing = installingTags.Where(t => !family.IsInstalled(t)).ToList();
            string name = string.Join(", ", arrivedTags.Select(WindowsOcr.DisplayNameFor));
            string missingNames = string.Join(", ", missing.Select(WindowsOcr.DisplayNameFor));
            bool arrived = arrivedTags.Count > 0;
            installingTags.Clear();

            Fill();
            if (arrived)
            {
                Changed = true;
                // Some of a batch can arrive while others do not, and saying only
                // the good half would leave a reader believing they had all four.
                Report(missing.Count == 0
                    ? Localization.T("Ocr.Add.Done", name)
                    : Localization.T("Ocr.Add.DonePartly", name, missingNames));
            }
            // NOT "failed": the helper exiting is not the same as the install
            // being over, and saying so would send a reader looking for a fault
            // that may be a download still in flight.
            else Report(Localization.T("Ocr.Add.Failed", missingNames));
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
