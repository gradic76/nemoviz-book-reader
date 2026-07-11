using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Per-book Properties dialog (sound processing). Layout mirrors the
    /// player's boxy grid: a full-height info column (A) on the left, then two
    /// columns (B, C) of equal cells. Reading left-to-right, top-to-bottom, the
    /// six cells are the six processing stages; the merged bottom cell holds the
    /// master switch plus Reset all / Bypass / OK / Cancel. Column A shows the
    /// book basics plus a live, technical read-out of the current settings.
    ///
    /// Each stage is a simple named preset (a level) rather than raw DSP knobs;
    /// the numbers behind each level live in <see cref="SoundSettings"/>. The
    /// equalizer stays free-form (three dB bands). The safety limiter is fixed
    /// and not shown. On OK the values are written to the book's SoundSettings
    /// and persisted; the audio-engine wiring / live preview is a later step
    /// (Bypass becomes audible then too).
    ///
    /// Controls are combo/spin boxes (not track bars) so a screen reader
    /// announces the exact value. NVDA doesn't auto-announce a DropDownList
    /// value on arrow the way JAWS does, so combo changes are spoken explicitly
    /// through the NVDA controller (a no-op under JAWS, so no double-speak).</summary>
    public class PropertiesForm : Form
    {
        private const int CellW = 214;
        private const int CellH = 112;

        private readonly BookData book;

        private TextBox tbInfo;
        private CheckBox chkMaster;

        private GroupBox[] stageCells;
        // Each stage's enable checkbox with the parameter controls it gates.
        private List<(CheckBox Enable, Control[] Parms)> stages;

        private CheckBox chkHp; private ComboBox cmbHp;
        private CheckBox chkDn; private ComboBox cmbDn;
        private CheckBox chkDs; private ComboBox cmbDs;
        private CheckBox chkCmp; private ComboBox cmbCmp;
        private CheckBox chkEq; private NumericUpDown numBass, numVoice, numTreble;
        private CheckBox chkNrm; private ComboBox cmbNrmType; private ComboBox cmbNrm;

        private Button btnResetAll;
        private CheckBox chkBypass;
        private Button btnOK;
        private Button btnCancel;

        private bool suppressAnnounce;

        // Live-preview hook: when the dialog is opened from the player, this
        // applies the (unsaved) settings to playback on every change so the user
        // hears edits on the fly. Null when opened from the library (no audio).
        private readonly Action<SoundSettings, bool> onPreview;

        /// <summary>Whether the user has toggled Bypass (compare processed vs.
        /// raw).</summary>
        public bool Bypass { get { return chkBypass.Checked; } }

        private static readonly string[] L5 =
            { "Prop.Level.Minimal", "Prop.Level.Light", "Prop.Level.Medium", "Prop.Level.Strong", "Prop.Level.Maximum" };

        public PropertiesForm(BookData book, Action<SoundSettings, bool> onPreview = null)
        {
            this.book = book;
            this.onPreview = onPreview;
            SoundSettings s = book.Sound;

            this.Text = ShelfName(book);
            this.ClientSize = new Size(700, 486);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Column A — full-height info + live technical read-out.
            tbInfo = new TextBox();
            tbInfo.Multiline = true;
            tbInfo.ReadOnly = true;
            tbInfo.ScrollBars = ScrollBars.Vertical;
            tbInfo.BackColor = SystemColors.Window;
            tbInfo.Location = new Point(8, 8);
            tbInfo.Size = new Size(232, 470);
            tbInfo.TabStop = true;
            tbInfo.TabIndex = 0;
            tbInfo.AccessibleName = Localization.T("Prop.Info.Accessible");

            chkMaster = new CheckBox();
            chkMaster.Text = Localization.T("Prop.UseSoundProcessing");
            chkMaster.AccessibleName = Localization.T("Prop.UseSoundProcessing");
            chkMaster.Location = new Point(256, 372);
            chkMaster.Size = new Size(420, 24);
            chkMaster.TabIndex = 1;
            chkMaster.Checked = s.Enabled;

            int xB = 248, xC = 470;
            int y1 = 8, y2 = 126, y3 = 244;

            GroupBox gHp = StageBox("Prop.Highpass.Title", xB, y1, 2);
            chkHp = StageEnable(gHp); chkHp.Checked = s.HighpassEnabled;
            cmbHp = LevelCombo(gHp, L5, s.HighpassLevel);

            GroupBox gDn = StageBox("Prop.Denoise.Title", xC, y1, 3);
            chkDn = StageEnable(gDn); chkDn.Checked = s.DenoiseEnabled;
            cmbDn = LevelCombo(gDn, L5, s.DenoiseLevel);

            GroupBox gDs = StageBox("Prop.Deesser.Title", xB, y2, 4);
            chkDs = StageEnable(gDs); chkDs.Checked = s.DeesserEnabled;
            cmbDs = LevelCombo(gDs, L5, s.DeesserLevel);

            GroupBox gCmp = StageBox("Prop.Compressor.Title", xC, y2, 5);
            chkCmp = StageEnable(gCmp); chkCmp.Checked = s.CompressorEnabled;
            cmbCmp = LevelCombo(gCmp, L5, s.CompressorLevel);

            GroupBox gEq = StageBox("Prop.Eq.Title", xB, y3, 6);
            chkEq = StageEnable(gEq); chkEq.Checked = s.EqEnabled;
            numBass = EqBand(gEq, "Prop.Eq.Bass", 40, s.EqBass);
            numVoice = EqBand(gEq, "Prop.Eq.Voice", 64, s.EqVoice);
            numTreble = EqBand(gEq, "Prop.Eq.Treble", 88, s.EqTreble);

            GroupBox gNrm = StageBox("Prop.Normalize.Title", xC, y3, 7);
            chkNrm = StageEnable(gNrm); chkNrm.Checked = s.NormalizeEnabled;
            cmbNrmType = new ComboBox();
            cmbNrmType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbNrmType.Location = new Point(10, 40);
            cmbNrmType.Size = new Size(CellW - 24, 24);
            cmbNrmType.AccessibleName = gNrm.Text + " — " + Localization.T("Prop.Normalize.Method");
            cmbNrmType.TabIndex = 1;
            cmbNrmType.Items.Add(Localization.T("Prop.Normalize.Type.Speech"));   // 0 → speechnorm
            cmbNrmType.Items.Add(Localization.T("Prop.Normalize.Type.Dynamic"));  // 1 → dynaudnorm
            cmbNrmType.SelectedIndex =
                string.Equals(s.NormalizeType, "dynaudnorm", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            gNrm.Controls.Add(cmbNrmType);
            cmbNrm = new ComboBox();
            cmbNrm.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbNrm.Location = new Point(10, 70);
            cmbNrm.Size = new Size(CellW - 24, 24);
            cmbNrm.AccessibleName = gNrm.Text + " — " + Localization.T("Prop.Stage.Level");
            cmbNrm.TabIndex = 2;
            foreach (string k in L5) cmbNrm.Items.Add(Localization.T(k));
            cmbNrm.SelectedIndex = Clamp(s.NormalizeLevel, 0, L5.Length - 1);
            gNrm.Controls.Add(cmbNrm);

            stageCells = new[] { gHp, gDn, gDs, gCmp, gEq, gNrm };
            stages = new List<(CheckBox, Control[])>
            {
                (chkHp, new Control[] { cmbHp }),
                (chkDn, new Control[] { cmbDn }),
                (chkDs, new Control[] { cmbDs }),
                (chkCmp, new Control[] { cmbCmp }),
                (chkEq, new Control[] { numBass, numVoice, numTreble }),
                (chkNrm, new Control[] { cmbNrmType, cmbNrm }),
            };

            btnResetAll = new Button();
            btnResetAll.Text = Localization.T("Prop.ResetAll");
            btnResetAll.AccessibleName = Localization.T("Prop.ResetAll");
            btnResetAll.Size = new Size(90, 30);
            btnResetAll.Location = new Point(256, 404);
            btnResetAll.TabIndex = 8;
            btnResetAll.Click += (s2, e) => ResetAll();

            chkBypass = new CheckBox();
            chkBypass.Text = Localization.T("Prop.Bypass");
            chkBypass.AccessibleName = Localization.T("Prop.Bypass");
            chkBypass.Size = new Size(90, 30);
            chkBypass.Location = new Point(352, 404);
            chkBypass.TabIndex = 9;
            chkBypass.CheckedChanged += (s2, e) => OnAnyChange();

            btnOK = new Button();
            btnOK.Text = Localization.T("Btn.OK");
            btnOK.AccessibleName = Localization.T("Btn.OK");
            btnOK.Size = new Size(90, 30);
            btnOK.Location = new Point(448, 404);
            btnOK.TabIndex = 10;
            btnOK.DialogResult = DialogResult.OK;
            btnOK.Click += (s2, e) => Persist();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("Btn.Cancel");
            btnCancel.Size = new Size(90, 30);
            btnCancel.Location = new Point(544, 404);
            btnCancel.TabIndex = 11;
            btnCancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(tbInfo);
            this.Controls.Add(chkMaster);
            foreach (GroupBox g in stageCells) this.Controls.Add(g);
            this.Controls.Add(btnResetAll);
            this.Controls.Add(chkBypass);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            // Master gates the cells; each stage's own switch gates its params
            // (an unchecked stage can't have its parameters changed). Any change
            // refreshes column A.
            chkMaster.CheckedChanged += (s2, e) => { UpdateEnabledStates(); OnAnyChange(); };
            foreach (var st in stages)
                st.Enable.CheckedChanged += (s2, e) => { UpdateEnabledStates(); OnAnyChange(); };
            WireCombo(cmbHp); WireCombo(cmbDn); WireCombo(cmbDs); WireCombo(cmbCmp);
            WireCombo(cmbNrmType); WireCombo(cmbNrm);
            numBass.ValueChanged += (s2, e) => OnAnyChange();
            numVoice.ValueChanged += (s2, e) => OnAnyChange();
            numTreble.ValueChanged += (s2, e) => OnAnyChange();

            UpdateEnabledStates();
            RefreshInfo();
            Preview(); // start the live chain reflecting the current settings
        }

        private void OnAnyChange()
        {
            RefreshInfo();
            if (!suppressAnnounce) Preview();
        }

        private void Preview()
        {
            onPreview?.Invoke(BuildCurrent(), chkBypass.Checked);
        }

        // ── Cell builders ─────────────────────────────────────────────────
        private GroupBox StageBox(string titleKey, int x, int y, int tabIndex)
        {
            GroupBox g = new GroupBox();
            g.Text = Localization.T(titleKey);
            g.Location = new Point(x, y);
            g.Size = new Size(CellW, CellH);
            g.TabIndex = tabIndex;
            return g;
        }

        private CheckBox StageEnable(GroupBox g)
        {
            // No "Use" label — the group already names the stage and the check
            // state alone says whether it is on. Accessible name = the stage
            // name so a screen reader reads e.g. "Soften sibilance, checkbox".
            CheckBox c = new CheckBox();
            c.Text = "";
            c.AccessibleName = g.Text;
            c.Location = new Point(10, 18);
            c.Size = new Size(CellW - 24, 20);
            c.TabIndex = 0;
            g.Controls.Add(c);
            return c;
        }

        private ComboBox LevelCombo(GroupBox g, string[] itemKeys, int selected)
        {
            ComboBox cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Location = new Point(10, 46);
            cb.Size = new Size(CellW - 24, 24);
            cb.AccessibleName = g.Text + " — " + Localization.T("Prop.Stage.Level");
            cb.TabIndex = 1;
            foreach (string k in itemKeys) cb.Items.Add(Localization.T(k));
            cb.SelectedIndex = Clamp(selected, 0, itemKeys.Length - 1);
            g.Controls.Add(cb);
            return cb;
        }

        private NumericUpDown EqBand(GroupBox g, string labelKey, int y, int value)
        {
            Label lbl = new Label();
            lbl.Text = Localization.T(labelKey);
            lbl.Location = new Point(10, y + 3);
            lbl.Size = new Size(90, 18);

            NumericUpDown n = new NumericUpDown();
            n.Minimum = -15; n.Maximum = 15; n.Increment = 1;
            n.Location = new Point(120, y);
            n.Size = new Size(70, 22);
            n.TextAlign = HorizontalAlignment.Right;
            n.AccessibleName = Localization.T(labelKey);
            n.TabIndex = g.Controls.Count;
            if (value < -15) value = -15; if (value > 15) value = 15;
            n.Value = value;

            g.Controls.Add(lbl);
            g.Controls.Add(n);
            return n;
        }

        private void WireCombo(ComboBox cb)
        {
            cb.SelectedIndexChanged += (s, e) =>
            {
                RefreshInfo();
                if (!suppressAnnounce)
                {
                    // NVDA doesn't auto-read a collapsed DropDownList on arrow;
                    // speak the new value (silent no-op under JAWS / no NVDA).
                    NvdaController.Speak(cb.Text);
                    Preview();
                }
            };
        }

        private void UpdateEnabledStates()
        {
            bool master = chkMaster.Checked;
            foreach (GroupBox g in stageCells) g.Enabled = master;
            foreach (var st in stages)
                foreach (Control p in st.Parms)
                    p.Enabled = master && st.Enable.Checked;
        }

        // ── Info column (live technical read-out) ─────────────────────────
        private void RefreshInfo()
        {
            string dash = Localization.T("Common.Dash");
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(Localization.T("Details.Field.Title") + ": " +
                (string.IsNullOrWhiteSpace(book.Title) ? dash : book.Title));
            if (!string.IsNullOrWhiteSpace(book.Author))
                sb.AppendLine(Localization.T("Details.Field.Author") + ": " + book.Author);
            sb.AppendLine(Localization.T("Details.Field.Format") + ": " +
                (string.IsNullOrWhiteSpace(book.Format) ? dash : book.Format));
            sb.AppendLine(Localization.T("Details.Field.Duration") + ": " +
                (string.IsNullOrWhiteSpace(book.Duration) ? dash : book.Duration));
            sb.AppendLine();

            if (!chkMaster.Checked)
            {
                sb.AppendLine(Localization.T("Prop.Info.ProcessingOff"));
                tbInfo.Text = sb.ToString();
                return;
            }

            sb.AppendLine(Localization.T("Prop.Info.ProcessingOn"));
            if (chkBypass.Checked)
                sb.AppendLine(Localization.T("Prop.Info.Bypassed"));
            sb.AppendLine();

            AppendStage(sb, "Prop.Highpass.Title", chkHp.Checked,
                SoundSettings.HighpassHz[cmbHp.SelectedIndex] + " Hz");

            AppendStage(sb, "Prop.Denoise.Title", chkDn.Checked,
                "-" + SoundSettings.DenoiseDb[cmbDn.SelectedIndex] + " dB");

            AppendStage(sb, "Prop.Deesser.Title", chkDs.Checked,
                SoundSettings.DeesserIntensity[cmbDs.SelectedIndex].ToString("0.00"));

            var c = SoundSettings.Compressor[cmbCmp.SelectedIndex];
            AppendStage(sb, "Prop.Compressor.Title", chkCmp.Checked,
                c.Ratio.ToString("0.#") + ":1, thr " + c.Threshold + " dB, +" + c.Makeup +
                " dB, " + c.Attack + "/" + c.Release + " ms");

            AppendStage(sb, "Prop.Eq.Title", chkEq.Checked,
                "bass " + Sign((int)numBass.Value) + ", voice " + Sign((int)numVoice.Value) +
                ", treble " + Sign((int)numTreble.Value) + " dB");

            int nl = cmbNrm.SelectedIndex;
            string nrmTech = cmbNrmType.SelectedIndex == 1
                ? "dynaudnorm, " + cmbNrm.Text + " (max " + SoundSettings.DynaudnormMaxGain[nl] + " dB)"
                : "speechnorm, " + cmbNrm.Text + " (e=" + SoundSettings.SpeechnormExpansion[nl].ToString("0.0") + ")";
            AppendStage(sb, "Prop.Normalize.Title", chkNrm.Checked, nrmTech);

            sb.AppendLine(Localization.T("Prop.Info.Protection") + ": " +
                SoundSettings.LimiterCeilingDb.ToString("0.0") + " dB");

            tbInfo.Text = sb.ToString();
        }

        private void AppendStage(StringBuilder sb, string titleKey, bool enabled, string tech)
        {
            sb.AppendLine(Localization.T(titleKey) + ": " +
                (enabled ? tech : Localization.T("Prop.Info.Off")));
        }

        private void ResetAll()
        {
            SoundSettings d = new SoundSettings(); // fresh defaults
            suppressAnnounce = true;

            chkHp.Checked = d.HighpassEnabled; cmbHp.SelectedIndex = d.HighpassLevel;
            chkDn.Checked = d.DenoiseEnabled; cmbDn.SelectedIndex = d.DenoiseLevel;
            chkDs.Checked = d.DeesserEnabled; cmbDs.SelectedIndex = d.DeesserLevel;
            chkCmp.Checked = d.CompressorEnabled; cmbCmp.SelectedIndex = d.CompressorLevel;
            chkEq.Checked = d.EqEnabled;
            numBass.Value = d.EqBass; numVoice.Value = d.EqVoice; numTreble.Value = d.EqTreble;
            chkNrm.Checked = d.NormalizeEnabled;
            cmbNrmType.SelectedIndex = 0; // speechnorm
            cmbNrm.SelectedIndex = d.NormalizeLevel;

            suppressAnnounce = false;
            UpdateEnabledStates();
            RefreshInfo();
            Preview();
        }

        private static string Sign(int v)
        {
            return v.ToString("+0;-0;0");
        }

        private static string ShelfName(BookData b)
        {
            return string.IsNullOrWhiteSpace(b.Author) ? b.Title : b.Author + " — " + b.Title;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>Copies the current control values into a SoundSettings.</summary>
        private void FillSettings(SoundSettings s)
        {
            s.Enabled = chkMaster.Checked;

            s.HighpassEnabled = chkHp.Checked;
            s.HighpassLevel = cmbHp.SelectedIndex;

            s.DenoiseEnabled = chkDn.Checked;
            s.DenoiseLevel = cmbDn.SelectedIndex;

            s.DeesserEnabled = chkDs.Checked;
            s.DeesserLevel = cmbDs.SelectedIndex;

            s.CompressorEnabled = chkCmp.Checked;
            s.CompressorLevel = cmbCmp.SelectedIndex;

            s.EqEnabled = chkEq.Checked;
            s.EqBass = (int)numBass.Value;
            s.EqVoice = (int)numVoice.Value;
            s.EqTreble = (int)numTreble.Value;

            s.NormalizeEnabled = chkNrm.Checked;
            s.NormalizeType = cmbNrmType.SelectedIndex == 1 ? "dynaudnorm" : "speechnorm";
            s.NormalizeLevel = cmbNrm.SelectedIndex;
        }

        /// <summary>A SoundSettings snapshot of the live (unsaved) control state,
        /// used to build the preview chain.</summary>
        private SoundSettings BuildCurrent()
        {
            SoundSettings s = new SoundSettings();
            FillSettings(s);
            return s;
        }

        /// <summary>Writes the control values into the book and saves. Fires on
        /// OK before the dialog closes.</summary>
        private void Persist()
        {
            FillSettings(book.Sound);
            book.Save();
        }
    }
}
