using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// What happens when the sleep timer expires.
    /// </summary>
    public enum SleepTimerAction
    {
        /// <summary>Pause playback and save progress; NBR stays open.</summary>
        Stop,
        /// <summary>Pause, save, and close NBR.</summary>
        StopClose,
        /// <summary>Pause, save, close NBR and shut down the computer
        /// (with a few seconds of grace so NBR can finish closing).</summary>
        StopCloseShutdown
    }

    /// <summary>
    /// Modal dialog for setting the sleep timer: duration presets
    /// (15/30/45/60 min) or a custom value, plus the action to perform
    /// when the time expires. Nothing is persisted — the timer is a
    /// one-shot, per-session thing.
    ///
    /// Keyboard: two radio groups (arrows move inside a group, Tab moves
    /// between groups), Enter = Start (AcceptButton), Escape = Cancel.
    /// </summary>
    public class SleepTimerForm : Form
    {
        private GroupBox grpDuration;
        private RadioButton rb15;
        private RadioButton rb30;
        private RadioButton rb45;
        private RadioButton rb60;
        private RadioButton rbCustom;
        private NumericUpDown numCustom;

        private GroupBox grpAction;
        private RadioButton rbActionStop;
        private RadioButton rbActionStopClose;
        private RadioButton rbActionStopShutdown;

        private Button btnStart;
        private Button btnCancel;

        /// <summary>Chosen duration in minutes (valid when DialogResult is OK).</summary>
        public int SelectedMinutes
        {
            get
            {
                if (rb15.Checked) return 15;
                if (rb30.Checked) return 30;
                if (rb45.Checked) return 45;
                if (rb60.Checked) return 60;
                return (int)numCustom.Value;
            }
        }

        /// <summary>Chosen expiry action (valid when DialogResult is OK).</summary>
        public SleepTimerAction SelectedAction
        {
            get
            {
                if (rbActionStopClose.Checked) return SleepTimerAction.StopClose;
                if (rbActionStopShutdown.Checked) return SleepTimerAction.StopCloseShutdown;
                return SleepTimerAction.Stop;
            }
        }

        public SleepTimerForm()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            this.Text = Localization.T("Dialog.Timer.Title");
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(380, 360);
            this.ShowInTaskbar = false;

            // ── Duration group ──
            grpDuration = new GroupBox();
            grpDuration.Text = Localization.T("SleepTimer.Duration.Group");
            grpDuration.Location = new Point(12, 10);
            grpDuration.Size = new Size(356, 150);
            grpDuration.TabIndex = 0;

            rb15 = new RadioButton();
            rb15.Text = Localization.T("SleepTimer.Duration.15");
            rb15.Location = new Point(15, 22);
            rb15.Size = new Size(160, 22);
            rb15.TabIndex = 0;

            rb30 = new RadioButton();
            rb30.Text = Localization.T("SleepTimer.Duration.30");
            rb30.Location = new Point(15, 46);
            rb30.Size = new Size(160, 22);
            rb30.TabIndex = 1;
            rb30.Checked = true; // sensible default

            rb45 = new RadioButton();
            rb45.Text = Localization.T("SleepTimer.Duration.45");
            rb45.Location = new Point(15, 70);
            rb45.Size = new Size(160, 22);
            rb45.TabIndex = 2;

            rb60 = new RadioButton();
            rb60.Text = Localization.T("SleepTimer.Duration.60");
            rb60.Location = new Point(15, 94);
            rb60.Size = new Size(160, 22);
            rb60.TabIndex = 3;

            rbCustom = new RadioButton();
            rbCustom.Text = Localization.T("SleepTimer.Duration.Custom");
            rbCustom.Location = new Point(15, 118);
            rbCustom.Size = new Size(120, 22);
            rbCustom.TabIndex = 4;
            rbCustom.CheckedChanged += (s, e) =>
            {
                numCustom.Enabled = rbCustom.Checked;
                if (rbCustom.Checked)
                {
                    // Focus the spin box and SELECT its whole value, so a
                    // typed number overwrites it immediately — no manual
                    // clearing needed. Up/Down arrows still adjust the
                    // value as usual.
                    numCustom.Focus();
                    numCustom.Select(0, numCustom.Text.Length);
                }
            };

            numCustom = new NumericUpDown();
            numCustom.Location = new Point(140, 118);
            numCustom.Size = new Size(80, 24);
            numCustom.Minimum = 1;
            numCustom.Maximum = 720;
            numCustom.Value = 90;
            numCustom.Enabled = false;
            numCustom.TabIndex = 5;
            numCustom.AccessibleName = Localization.T("SleepTimer.Custom.Accessible");
            // Also select-all whenever the box gains focus later (e.g. the
            // user tabs back to it) — same overwrite-friendly behavior.
            numCustom.Enter += (s, e) =>
            {
                numCustom.Select(0, numCustom.Text.Length);
            };

            grpDuration.Controls.Add(rb15);
            grpDuration.Controls.Add(rb30);
            grpDuration.Controls.Add(rb45);
            grpDuration.Controls.Add(rb60);
            grpDuration.Controls.Add(rbCustom);
            grpDuration.Controls.Add(numCustom);

            // ── Action group ──
            grpAction = new GroupBox();
            grpAction.Text = Localization.T("SleepTimer.Action.Group");
            grpAction.Location = new Point(12, 170);
            grpAction.Size = new Size(356, 110);
            grpAction.TabIndex = 1;

            rbActionStop = new RadioButton();
            rbActionStop.Text = Localization.T("SleepTimer.Action.Stop");
            rbActionStop.Location = new Point(15, 22);
            rbActionStop.Size = new Size(330, 22);
            rbActionStop.TabIndex = 0;
            rbActionStop.Checked = true; // least destructive default

            rbActionStopClose = new RadioButton();
            rbActionStopClose.Text = Localization.T("SleepTimer.Action.StopClose");
            rbActionStopClose.Location = new Point(15, 46);
            rbActionStopClose.Size = new Size(330, 22);
            rbActionStopClose.TabIndex = 1;

            rbActionStopShutdown = new RadioButton();
            rbActionStopShutdown.Text = Localization.T("SleepTimer.Action.StopShutdown");
            rbActionStopShutdown.Location = new Point(15, 70);
            rbActionStopShutdown.Size = new Size(330, 22);
            rbActionStopShutdown.TabIndex = 2;

            grpAction.Controls.Add(rbActionStop);
            grpAction.Controls.Add(rbActionStopClose);
            grpAction.Controls.Add(rbActionStopShutdown);

            // ── Buttons ──
            btnStart = new Button();
            btnStart.Text = Localization.T("SleepTimer.Start");
            btnStart.Size = new Size(120, 32);
            btnStart.Location = new Point(120, 295);
            btnStart.TabIndex = 2;
            btnStart.AccessibleName = Localization.T("SleepTimer.Start.Accessible");
            btnStart.DialogResult = DialogResult.OK;

            btnCancel = new Button();
            btnCancel.Text = Localization.T("SleepTimer.CancelBtn");
            btnCancel.Size = new Size(120, 32);
            btnCancel.Location = new Point(248, 295);
            btnCancel.TabIndex = 3;
            btnCancel.AccessibleName = Localization.T("SleepTimer.CancelBtn.Accessible");
            btnCancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(grpDuration);
            this.Controls.Add(grpAction);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnStart;
            this.CancelButton = btnCancel;
        }
    }
}
