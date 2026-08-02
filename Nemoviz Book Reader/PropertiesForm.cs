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
        private TabControl tabs;
        private Button btnOK, btnCancel;
        private GroupBox gPlayback;

        /// <summary>The way in for the new look. Same rule as the player: the skin
        /// only moves and repaints what this form already built, so every role,
        /// name, handler and tab stop survives untouched.</summary>
        internal PropParts SkinParts
        {
            get
            {
                return new PropParts
                {
                    Info = tbInfo,
                    Master = chkMaster,
                    Bypass = chkBypass,
                    ResetAll = btnResetAll,
                    OK = btnOK,
                    Cancel = btnCancel,
                    Stages = stageCells,
                    Playback = gPlayback,
                    TextInfo = tbTextInfo,
                    Tabs = tabs
                };
            }
        }

        // Text tab (per-book reading options; mirrors Settings -> Text Books).
        private TextBox tbTextInfo;
        private NumericUpDown numPlayVolume, numPlaySpeed;
        private ComboBox cmbTLanguage, cmbTVoice;
        // Shown only when nothing installed speaks the book's language.
        private TextBox tbTNoVoice;
        private NumericUpDown numTWpm, numTVolume, numTPitch;
        private CheckBox chkTBraille; private ComboBox cmbTBrailleTable;
        private CheckBox chkTVisual;
        private ComboBox cmbTVisualMode, cmbTHighlight, cmbTHighlightColour, cmbTTextColour, cmbTBackColour;
        private List<(string Name, string Engine, string Language)> textCatalog;
        private readonly List<string> textLanguageCodes = new List<string>();
        private CompositeSpeechBackend textSpeech;

        private bool suppressAnnounce;
        // True while the dialog is still being built: filling the pickers fires
        // change events, and those must not be mistaken for the user editing â€”
        // otherwise opening Properties would immediately push its starting values
        // onto live playback.
        private bool initialising = true;

        // Live-preview hook: when the dialog is opened from the player, this
        // applies the (unsaved) settings to playback on every change so the user
        // hears edits on the fly. Null when opened from the library (no audio).
        private readonly Action<SoundSettings, bool> onPreview;
        // Live preview for playback level and speed, so they are heard while being
        // adjusted just like the processing stages. Cancel restores the old values.
        private readonly Action<int, int> onPlaybackPreview;
        // Same idea for a text book: the voice and how it reads are heard while
        // being chosen, not only after OK.
        private readonly Action<string, int, int, int> onTextPreview;

        /// <summary>Whether the user has toggled Bypass (compare processed vs.
        /// raw).</summary>
        public bool Bypass { get { return chkBypass.Checked; } }

        private static readonly string[] L5 =
            { "Prop.Level.Minimal", "Prop.Level.Light", "Prop.Level.Medium", "Prop.Level.Strong", "Prop.Level.Maximum" };

        // The global speech settings, for the fallback when this book has never
        // been read with the voice being picked. Null when the caller has none.
        private readonly AppSettings appSettings;

        public PropertiesForm(BookData book, Action<SoundSettings, bool> onPreview = null,
                              Action<int, int> onPlaybackPreview = null,
                              Action<string, int, int, int> onTextPreview = null,
                              AppSettings appSettings = null)
        {
            this.book = book;
            this.onPreview = onPreview;
            this.onPlaybackPreview = onPlaybackPreview;
            this.onTextPreview = onTextPreview;
            this.appSettings = appSettings;
            SoundSettings s = book.Sound;

            this.Text = ShelfName(book);
            // Tall enough that the Text tab's three groups fit without scrolling
            // (a scrollbar there also stole width and brought a horizontal one
            // with it), and still short enough for a 1080p screen at 150 %.
            this.ClientSize = new Size(730, 606);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Column A â€” full-height info + live technical read-out.
            tbInfo = new TextBox();
            tbInfo.Multiline = true;
            tbInfo.ReadOnly = true;
            tbInfo.ScrollBars = ScrollBars.Vertical;
            tbInfo.BackColor = SystemColors.Window;
            tbInfo.Location = new Point(8, 8);
            tbInfo.Size = new Size(232, 510);
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
            cmbNrmType.AccessibleName = gNrm.Text + " â€” " + Localization.T("Prop.Normalize.Method");
            cmbNrmType.TabIndex = 1;
            cmbNrmType.Items.Add(Localization.T("Prop.Normalize.Type.Speech"));   // 0 â†’ speechnorm
            cmbNrmType.Items.Add(Localization.T("Prop.Normalize.Type.Dynamic"));  // 1 â†’ dynaudnorm
            cmbNrmType.SelectedIndex =
                string.Equals(s.NormalizeType, "dynaudnorm", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            gNrm.Controls.Add(cmbNrmType);
            cmbNrm = new ComboBox();
            cmbNrm.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbNrm.Location = new Point(10, 70);
            cmbNrm.Size = new Size(CellW - 24, 24);
            cmbNrm.AccessibleName = gNrm.Text + " â€” " + Localization.T("Prop.Stage.Level");
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
            btnOK.Location = new Point(438, 564);
            btnOK.TabIndex = 10;
            btnOK.DialogResult = DialogResult.OK;
            btnOK.Click += (s2, e) => Persist();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("Btn.Cancel");
            btnCancel.Size = new Size(90, 30);
            btnCancel.Location = new Point(534, 564);
            btnCancel.TabIndex = 11;
            btnCancel.DialogResult = DialogResult.Cancel;

            // A book's properties are grouped by what it IS: sound processing for
            // anything with audio, reading options for anything with text. A hybrid
            // book (audio and text together) simply shows both tabs.
            // A HYBRID gets both, and its reading page is not decoration: even
            // where the narration sets the pace, the voice, pitch and volume still
            // decide how a word looked up on demand is spoken, and the braille and
            // visual outputs are switched on there (Gordan, 2026-07-30).
            bool hasAudio = book.Chapters.Count > 0 || !book.IsTextBook;
            bool hasText = book.IsTextBook || book.IsHybrid;

            tabs = new TabControl();
            tabs.Location = new Point(8, 8);
            tabs.Size = new Size(714, 548);
            tabs.TabIndex = 0;

            if (hasAudio)
            {
                TabPage audio = new TabPage(Localization.T("Prop.Tab.Audio"));
                audio.Controls.Add(tbInfo);
                audio.Controls.Add(chkMaster);
                foreach (GroupBox g in stageCells) audio.Controls.Add(g);
                audio.Controls.Add(btnResetAll);
                audio.Controls.Add(chkBypass);
                gPlayback = BuildPlaybackGroup(248, 404);
                audio.Controls.Add(gPlayback);
                tabs.TabPages.Add(audio);
            }
            if (hasText) tabs.TabPages.Add(BuildTextPage());

            this.Controls.Add(tabs);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
            initialising = false;   // from here on, changes really are the user's

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

            // The new look takes the dialog over here, at the very end, exactly as
            // it does with the player — after everything is built and wired, so
            // nothing it does can be mistaken for the user editing.
            if (UiTheme.Current.BuildsOwnLayout) PropertiesSkin.Apply(this);
        }

        /// <summary>F1 opens the help for whatever the focus is sitting in, so the
        /// keyboard never has to travel to the ? button the mouse uses.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1 && HintSystem.HandleF1(this)) return true;
            // Ctrl+Tab, Ctrl+Shift+Tab and Ctrl+1…9, cyclic — a hybrid's two
            // pages are the only place NBR has real tabs, and they should feel
            // like anyone else's (see TabKeys for why WinForms does not).
            if (TabKeys.Handle(tabs, keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
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

        // â”€â”€ Cell builders â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
            // No "Use" label â€” the group already names the stage and the check
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
            cb.AccessibleName = g.Text + " â€” " + Localization.T("Prop.Stage.Level");
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

        // â”€â”€ Info column (live technical read-out) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void RefreshInfo()
        {
            string dash = Localization.T("Common.Dash");
            StringBuilder sb = new StringBuilder();

            // The book's own facts, in the order every info box uses — see
            // BookInfo.cs. The processing read-out below is this page's business
            // and follows after them.
            var info = new BookInfoBuilder();
            info.AddAlways(BookInfoField.Title, book.Title, dash);
            info.Add(BookInfoField.Author, book.Author);
            info.Add(BookInfoField.Publisher, BookData.NormalizeProducer(book.Publisher));
            info.Add(BookInfoField.Producer, BookData.NormalizeProducer(book.Producer));
            info.AddAlways(BookInfoField.Format, book.Format, dash);
            info.AddAlways(BookInfoField.Time, book.Duration, dash);
            sb.Append(info.ToText(Environment.NewLine));
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
            return string.IsNullOrWhiteSpace(b.Author) ? b.Title : b.Author + " â€” " + b.Title;
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
            PersistTextOptions();
            if (numPlayVolume != null) book.Volume = (int)numPlayVolume.Value;
            if (numPlaySpeed != null) book.Speed = (int)Math.Round(numPlaySpeed.Value * 100);
            book.Save();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // The catalog backend starts the 32-bit host; let it go with the dialog.
            try { if (textSpeech != null) { textSpeech.Dispose(); textSpeech = null; } } catch { }
            base.OnFormClosed(e);
        }

        // â”€â”€ Text tab: the SAME options as Settings â†’ Text Books, but for THIS book.
        // Settings holds the defaults; a book only departs from them when the user
        // says so, which is what the "custom" switch at the top means. Left off, the
        // book simply follows Settings and every control below is dimmed and out of
        // the tab order.
        private TabPage BuildTextPage()
        {
            TabPage page = new TabPage(Localization.T("Prop.Tab.Text"));
            page.AutoScroll = true;

            // Column A mirrors the Audio tab: book basics plus a live read-out of
            // the reading settings, so the same shape is familiar on both tabs.
            tbTextInfo = new TextBox();
            tbTextInfo.Multiline = true;
            tbTextInfo.ReadOnly = true;
            tbTextInfo.ScrollBars = ScrollBars.Vertical;
            tbTextInfo.BackColor = SystemColors.Window;
            tbTextInfo.Location = new Point(8, 8);
            tbTextInfo.Size = new Size(232, 510);
            tbTextInfo.TabStop = true;
            tbTextInfo.TabIndex = 0;
            tbTextInfo.AccessibleName = Localization.T("Prop.Info.Accessible");
            page.Controls.Add(tbTextInfo);

            page.Controls.Add(BuildTextSpeechGroup(248, 8));
            page.Controls.Add(BuildTextBrailleGroup(248, 226));
            page.Controls.Add(BuildTextVisualGroup(248, 314));

            UpdateTextEnabled();
            return page;
        }

        private GroupBox BuildTextSpeechGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.SpeechGroup");
            box.Location = new Point(x, y);
            box.Size = new Size(452, 212);

            // The field column starts far enough right for the longest caption
            // ("Reading speed (words per minute):") to be written out in full.
            int lx = 10, cx = 210, cw = 232, yy = 22, tab = 0;

            // Same two steps as Settings, for the same reasons: the engine was a
            // question about a vendor name, which is nobody's decision. Here it is
            // "read THIS book with", overriding the global rule for one book —
            // which is the whole point of the page.
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Language"), lx, yy + 3));
            cmbTLanguage = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.Language"), cx, yy, cw, tab++);
            cmbTLanguage.SelectedIndexChanged += (s, e) => TextVoicesForSelection();
            box.Controls.Add(cmbTLanguage);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Voice"), lx, yy + 3));
            cmbTVoice = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.Voice"), cx, yy, cw, tab++);
            // Speed / volume / pitch belong to the VOICE: picking one shows what
            // this book was last read with using it, else how that voice is set up
            // in Settings, else the neutral default — never the numbers of the
            // voice being left behind.
            cmbTVoice.SelectedIndexChanged += (s, e) =>
            {
                // No speech has no preferences to load and nothing to preview —
                // previewing it would start the 32-bit speech host to say nothing.
                if (cmbTVoice.SelectedIndex == 0) { UpdateTextEnabled(); RefreshTextInfo(); return; }
                LoadPrefsForSelectedVoice(); UpdateTextEnabled(); RefreshTextInfo(); PreviewText();
            };
            box.Controls.Add(cmbTVoice);

            yy += 34;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Speed"), lx, yy + 3));
            numTWpm = SettingsForm.MakeNumeric(Localization.T("Settings.TextBooks.Speed"), cx, yy, 80, 400,
                                               book.TextWpm >= 0 ? book.TextWpm : 175, tab++, 5);
            box.Controls.Add(numTWpm);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Volume"), lx, yy + 3));
            numTVolume = SettingsForm.MakeNumeric(Localization.T("Settings.TextBooks.Volume"), cx, yy, 0, 100,
                                                  book.TextVolume >= 0 ? book.TextVolume : Clamp(book.Volume, 0, 100), tab++, 5);
            box.Controls.Add(numTVolume);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Pitch"), lx, yy + 3));
            numTPitch = SettingsForm.MakeNumeric(Localization.T("Settings.TextBooks.Pitch"), cx, yy, -10, 10,
                                                 book.TextPitch >= -10 && book.TextPitch <= 10 ? book.TextPitch : 0, tab++);
            numTWpm.ValueChanged += (s, e) => { RefreshTextInfo(); PreviewText(); };
            numTVolume.ValueChanged += (s, e) => { RefreshTextInfo(); PreviewText(); };
            numTPitch.ValueChanged += (s, e) => { RefreshTextInfo(); PreviewText(); };
            box.Controls.Add(numTPitch);

            // The one line the reader has to see when their book's language has no
            // voice at all. It is a read-only TEXTBOX, not a label, for the reason
            // the hint system already learned the hard way: a screen reader driven
            // by Tab never visits a label, and this is the message that matters
            // most on the page.
            yy += 32;
            tbTNoVoice = new TextBox();
            tbTNoVoice.Multiline = true;
            tbTNoVoice.ReadOnly = true;
            tbTNoVoice.BorderStyle = BorderStyle.None;
            tbTNoVoice.BackColor = SystemColors.Control;
            tbTNoVoice.SetBounds(lx, yy, cw + cx - lx - 4, NoVoiceHeight);
            tbTNoVoice.TabIndex = tab++;
            tbTNoVoice.Visible = false;
            tbTNoVoice.TabStop = false;
            box.Controls.Add(tbTNoVoice);

            try { textCatalog = TextSpeech().GetVoiceCatalog(); }
            catch { textCatalog = new List<(string, string, string)>(); }
            PopulateTextLanguages();

            // The saved name may predate the switch to plain voice names (it could
            // be SAPI's description, "â€¦ - English (United States)"), so fall back
            // to matching the bare name â€” OK then rewrites it in the current form.
            // With no voice of its own, the book starts on the one the player would
            // have chosen for it — the same rule, asked in the same place.
            string want = !string.IsNullOrEmpty(book.TextVoice) ? book.TextVoice : DefaultVoiceForLanguage();
            foreach (var c in textCatalog)
                if (!string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(BareVoiceName(c.Name), BareVoiceName(want), StringComparison.OrdinalIgnoreCase))
                { want = c.Name; break; }

            // Open on the language of the voice the book will actually be read
            // with — and where there is no such voice, open on NOTHING. Falling
            // back to the first row looked harmless and was the worst bug of the
            // lot: the list is sorted by name, so a Spanish book with no Spanish
            // voice opened on Croatian and Karmela, which is precisely the
            // "near enough" choice NBR is not allowed to make. Both boxes stay
            // empty, the notice says why, and the lists are there to be used.
            string wantLang = "";
            foreach (var c in textCatalog)
                if (string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase))
                { wantLang = LanguageDetector.Primary(c.Language); break; }

            int li2 = wantLang.Length > 0 ? textLanguageCodes.IndexOf(wantLang) : -1;
            if (li2 >= 0)
            {
                cmbTLanguage.SelectedIndex = li2;
                int vi = cmbTVoice.Items.IndexOf(want);
                if (vi >= 0) cmbTVoice.SelectedIndex = vi;
            }
            LoadPrefsForSelectedVoice();
            UpdateNoVoiceNotice();
            // The group is snug around its contents, so it has to be told when the
            // notice is there — otherwise the message it exists to deliver is cut
            // off by the bottom edge of the box.
            if (tbTNoVoice.Visible) box.Height = tbTNoVoice.Bottom + 12;
            return box;
        }

        // One line at 12 pt. The short wording is what keeps it to one, and one
        // is what keeps the three groups inside the dialog — the full sentence
        // goes on the info panel, which has the room.
        private const int NoVoiceHeight = 24;

        /// <summary>Every language something installed speaks. Nothing else: a row
        /// with no voice under it would be a dead end, and no language may stand in
        /// for another.</summary>
        private void PopulateTextLanguages()
        {
            cmbTLanguage.Items.Clear();
            textLanguageCodes.Clear();
            var codes = new List<string>();
            foreach (var c in textCatalog)
            {
                string p = LanguageDetector.Primary(c.Language);
                if (p.Length > 0 && !codes.Contains(p)) codes.Add(p);
            }
            codes.Sort((a, b) => string.Compare(SettingsForm.LanguageName(a), SettingsForm.LanguageName(b),
                                                StringComparison.CurrentCultureIgnoreCase));
            foreach (string p in codes)
            {
                textLanguageCodes.Add(p);
                cmbTLanguage.Items.Add(SettingsForm.LanguageName(p) + " (" + p + ")");
            }
        }

        /// <summary>Says so when nothing installed speaks the book's language, and
        /// otherwise says nothing at all. NBR does not pick a near-enough language
        /// on the reader's behalf — it tells them, and the language list beside the
        /// message is theirs to do as they like with, including badly.</summary>
        private void UpdateNoVoiceNotice()
        {
            if (tbTNoVoice == null) return;
            string lang = LanguageDetector.Primary(book.TextLanguage);
            bool none = lang.Length > 0 && VoiceChooser.VoicesFor(textCatalog, lang).Count == 0;
            tbTNoVoice.Visible = none;
            tbTNoVoice.TabStop = none;
            if (none)
                tbTNoVoice.Text = Localization.T("Prop.Text.NoVoiceShort",
                                                 SettingsForm.LanguageName(lang));
        }

        /// <summary>The same fact at length, for the info panel — which has the
        /// room the one line between the controls does not.</summary>
        private string NoVoiceExplanation()
        {
            string lang = LanguageDetector.Primary(book.TextLanguage);
            if (lang.Length == 0 || VoiceChooser.VoicesFor(textCatalog, lang).Count > 0) return "";
            return Localization.T("Prop.Text.NoVoiceForLanguage", SettingsForm.LanguageName(lang));
        }

        // Voices adjusted during this visit, staged so several can be set up in one
        // go and Cancel still discards them all.
        private readonly VoicePrefsTable stagedTextPrefs = new VoicePrefsTable();
        private string textPrefsVoice = "";

        /// <summary>Shows the selected voice's speed / volume / pitch for THIS book:
        /// what it was last read with here, else how the voice is set up in
        /// Settings, else the neutral default. What is on screen is first filed
        /// under the voice being left, so switching back restores it.</summary>
        private void LoadPrefsForSelectedVoice()
        {
            if (cmbTVoice == null || numTWpm == null || numTVolume == null || numTPitch == null) return;
            string voice = cmbTVoice.SelectedItem != null ? cmbTVoice.SelectedItem.ToString() : "";
            if (string.IsNullOrEmpty(voice) || string.Equals(voice, textPrefsVoice, StringComparison.OrdinalIgnoreCase))
                return;

            StageTextPrefs();
            textPrefsVoice = voice;
            VoicePrefs fallback = appSettings != null ? appSettings.PrefsFor(voice) : VoicePrefs.Default;
            VoicePrefs p = stagedTextPrefs.Get(voice, book.TextVoicePrefs.Get(voice, fallback));
            // One quiet update: set all three, then let the caller refresh and
            // preview once, instead of restarting the sentence three times.
            bool wasInit = initialising;
            initialising = true;
            numTWpm.Value = Clamp(p.Wpm, (int)numTWpm.Minimum, (int)numTWpm.Maximum);
            numTVolume.Value = Clamp(p.Volume, (int)numTVolume.Minimum, (int)numTVolume.Maximum);
            numTPitch.Value = Clamp(p.Pitch, (int)numTPitch.Minimum, (int)numTPitch.Maximum);
            initialising = wasInit;
        }

        private void StageTextPrefs()
        {
            if (string.IsNullOrEmpty(textPrefsVoice) || numTWpm == null) return;
            stagedTextPrefs.Set(textPrefsVoice,
                new VoicePrefs((int)numTWpm.Value, (int)numTVolume.Value, (int)numTPitch.Value));
        }

        private GroupBox BuildTextBrailleGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.BrailleGroup");
            box.Location = new Point(x, y);
            box.Size = new Size(452, 80);

            chkTBraille = new CheckBox();
            chkTBraille.Text = Localization.T("Settings.TextBooks.UseBraille");
            chkTBraille.AccessibleName = Localization.T("Settings.TextBooks.UseBraille");
            chkTBraille.Location = new Point(14, 20);
            chkTBraille.Size = new Size(424, 24);
            chkTBraille.TabIndex = 0;
            chkTBraille.CheckedChanged += (s, e) =>
            {
                // Done HERE, on the transition, and not in UpdateTextEnabled: that
                // one runs on every refresh, so setting the mode there would snap
                // the choice back to two rows each time anything else was touched.
                // Ticking braille SETS the smallest form; it does not nail it down.
                if (chkTBraille.Checked)
                {
                    if (chkTVisual != null) chkTVisual.Checked = true;
                    if (cmbTVisualMode != null && cmbTVisualMode.Items.Count > 0)
                        cmbTVisualMode.SelectedIndex = 0;      // two rows, the subtitle strip
                }
                UpdateTextEnabled();
            };
            box.Controls.Add(chkTBraille);

            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.BrailleTable"), 10, 51));
            cmbTBrailleTable = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.BrailleTable"), 210, 48, 232, 1);
            cmbTBrailleTable.Items.Add(Localization.T("Settings.TextBooks.BrailleTableAuto"));
            foreach (BrailleTableInfo t in BrailleTables.All) cmbTBrailleTable.Items.Add(t.Display);
            // The table this book was last sent to the display with. Deliberately
            // NOT book.BrailleTable: that one back-translates a .brf being READ,
            // and this one forward-translates a text book being WRITTEN out. Same
            // library, opposite directions, no reason they should agree.
            int bi = 0;
            for (int i = 0; i < BrailleTables.All.Length; i++)
                if (string.Equals(BrailleTables.All[i].Id, book.TextBrailleTable, StringComparison.OrdinalIgnoreCase))
                { bi = i + 1; break; }
            cmbTBrailleTable.SelectedIndex = bi;
            chkTBraille.Checked = book.TextBraille;
            box.Controls.Add(cmbTBrailleTable);
            return box;
        }

        private GroupBox BuildTextVisualGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Settings.TextBooks.VisualGroup");
            box.Location = new Point(x, y);
            box.Size = new Size(452, 204);

            chkTVisual = new CheckBox();
            chkTVisual.Text = Localization.T("Settings.TextBooks.UseVisual");
            chkTVisual.AccessibleName = Localization.T("Settings.TextBooks.UseVisual");
            chkTVisual.Location = new Point(14, 20);
            chkTVisual.Size = new Size(424, 24);
            chkTVisual.TabIndex = 0;
            chkTVisual.CheckedChanged += (s, e) => UpdateTextEnabled();
            box.Controls.Add(chkTVisual);

            int lx = 10, cx = 210, cw = 232, yy = 48, tab = 1;

            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.VisualMode"), lx, yy + 3));
            cmbTVisualMode = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.VisualMode"), cx, yy, cw, tab++);
            cmbTVisualMode.Items.Add(Localization.T("Settings.TextBooks.VisualMode.TwoRows"));
            cmbTVisualMode.Items.Add(Localization.T("Settings.TextBooks.VisualMode.FullInstant"));
            cmbTVisualMode.Items.Add(Localization.T("Settings.TextBooks.VisualMode.FullScrolling"));
            cmbTVisualMode.SelectedIndex = book.TextVisualMode >= 0 && book.TextVisualMode < 3
                ? book.TextVisualMode : 0;
            chkTVisual.Checked = book.TextVisual;
            box.Controls.Add(cmbTVisualMode);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.Highlight"), lx, yy + 3));
            cmbTHighlight = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.Highlight"), cx, yy, cw, tab++);
            cmbTHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.None"));
            cmbTHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Word"));
            cmbTHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Sentence"));
            cmbTHighlight.SelectedIndex = 2;
            box.Controls.Add(cmbTHighlight);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.HighlightColour"), lx, yy + 3));
            cmbTHighlightColour = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.HighlightColour"), cx, yy, cw, tab++);
            box.Controls.Add(cmbTHighlightColour);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.TextColour"), lx, yy + 3));
            cmbTTextColour = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.TextColour"), cx, yy, cw, tab++);
            box.Controls.Add(cmbTTextColour);

            yy += 30;
            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Settings.TextBooks.BackColour"), lx, yy + 3));
            cmbTBackColour = SettingsForm.MakeCombo(Localization.T("Settings.TextBooks.BackColour"), cx, yy, cw, tab++);
            box.Controls.Add(cmbTBackColour);

            string[] colours =
            {
                Localization.T("Settings.Colour.White"), Localization.T("Settings.Colour.Black"),
                Localization.T("Settings.Colour.Yellow"), Localization.T("Settings.Colour.Blue"),
                Localization.T("Settings.Colour.Green"), Localization.T("Settings.Colour.Red")
            };
            foreach (string c in colours)
            {
                cmbTHighlightColour.Items.Add(c); cmbTTextColour.Items.Add(c); cmbTBackColour.Items.Add(c);
            }
            cmbTHighlightColour.SelectedIndex = 3;
            cmbTTextColour.SelectedIndex = 2;
            cmbTBackColour.SelectedIndex = 1;
            return box;
        }

        /// <summary>Each switch gates what belongs to it: the book only overrides the
        /// Settings defaults while "custom" is on, and the braille / visual options
        /// only matter while their own output is on.
        ///
        /// <para><b>Braille takes the visual switch with it</b> (Gordan,
        /// 2026-08-01). The two are not independent outputs, because the braille
        /// display is fed by the screen reader FOLLOWING FOCUS into the reading
        /// surface — so the window is not merely how braille is preferably done,
        /// it is the only way it happens at all. Ticking braille therefore turns
        /// the window on, drops it to the smallest form (two rows, the subtitle
        /// strip), and DISABLES the visual box so it cannot be turned off
        /// underneath. Untick braille and the box is handed back, still ticked,
        /// for the user to do as they like with.</para>
        ///
        /// <para>The reverse does NOT hold, deliberately. Visual on with braille
        /// off is an ordinary sighted setting and opens no braille channel of
        /// ours. Note that a reader who HAS a display will still get braille from
        /// it, because that is the screen reader's doing and not something we
        /// could switch off if we wanted to — which is exactly why there is no
        /// check box claiming to.</para></summary>
        private void UpdateTextEnabled()
        {
            // No speech keeps the SPEED and loses the rest. Speed still means
            // something — it is what paces the walk through the book, and the one
            // number a reader on braille or on the screen is steering with
            // (Gordan). Volume and pitch describe a voice that is not there.
            bool noSpeech = cmbTVoice != null && cmbTVoice.SelectedIndex == 0;
            SettingsForm.SetEnabled(!noSpeech, numTVolume, numTPitch);

            bool braille = chkTBraille != null && chkTBraille.Checked;
            // Say it ON THE CONTROL as well as on the glass. Gordan looked beside
            // the control first, which is where anyone would look — the glass is
            // where NBR puts state, but a consequence of ticking THIS box belongs
            // to this box. And it is the only control in the pair Tab can still
            // reach once the whole visual group is disabled.
            if (chkTBraille != null)
                chkTBraille.AccessibleDescription = braille
                    ? Localization.T("Settings.TextBooks.VisualForBraille") : null;
            if (chkTVisual != null)
            {
                // Repairs an inconsistent book as well as enforcing the rule. The
                // braille group is BUILT BEFORE the visual one, so on load the
                // transition handler above fires while chkTVisual is still null
                // and cannot do this; and a book stored while the two switches
                // were independent can carry braille on with visual off. Either
                // way the dialog would show braille ticked beside an unticked,
                // greyed visual box — a state the rule says cannot exist.
                if (braille && !chkTVisual.Checked) chkTVisual.Checked = true;
                chkTVisual.Enabled = !braille;
            }

            SettingsForm.SetEnabled(braille, cmbTBrailleTable);
            // The WHOLE visual group goes with the check box, not just the check
            // box (Gordan, 2026-08-01). A braille reader has no use for a display
            // mode, a highlight or three colours, and the form is already pinned
            // to the smallest one — the subtitle strip — so there is nothing left
            // to choose. Leaving them live was mine, on the theory that someone
            // might still want full screen for a sighted companion; his answer is
            // that it is clutter in the one place a braille reader has to work,
            // and that reusing this box at its smallest is the point of putting
            // braille in it rather than drawing another.
            SettingsForm.SetEnabled(!braille && chkTVisual != null && chkTVisual.Checked,
                                    cmbTVisualMode, cmbTHighlight, cmbTHighlightColour,
                                    cmbTTextColour, cmbTBackColour);
            RefreshTextInfo();
        }

        /// <summary>The voice a book with no voice of its own opens on: the
        /// Settings default when it speaks the book's language, otherwise the first
        /// installed voice that does — the shared rule in VoiceChooser, which the
        /// player asks with the same book, so the two cannot disagree.</summary>
        private string DefaultVoiceForLanguage()
        {
            string settingsVoice = appSettings != null ? (appSettings.TtsVoice ?? "") : "";
            string lang = book.TextLanguage;
            if (string.IsNullOrEmpty(lang) || textCatalog == null) return settingsVoice;

            return VoiceChooser.ForLanguage(appSettings, textCatalog, lang);
        }

        /// <summary>A voice name without SAPI's " - language" tail, so a name saved
        /// in either form still finds its voice.</summary>
        private static string BareVoiceName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            int dash = n.IndexOf(" - ", StringComparison.Ordinal);
            return (dash > 0 ? n.Substring(0, dash) : n).Trim();
        }

        private void TextVoicesForSelection()
        {
            if (cmbTVoice == null || textCatalog == null) return;
            int li = cmbTLanguage != null ? cmbTLanguage.SelectedIndex : -1;
            string lang = (li >= 0 && li < textLanguageCodes.Count) ? textLanguageCodes[li] : "";
            cmbTVoice.Items.Clear();
            // "No speech" heads the list, and is a real choice rather than an
            // absence of one: the book is still read, the position still walks it
            // sentence by sentence, only nothing is spoken. It is where a reader
            // on braille or on the screen goes when they do not want a voice over
            // their reading — and where the player itself lands when no installed
            // voice can speak the book, which used to mean a book that opened and
            // then would not move.
            cmbTVoice.Items.Add(Localization.T("Settings.TextBooks.NoSpeech"));
            foreach (string name in VoiceChooser.VoicesFor(textCatalog, lang))
                cmbTVoice.Items.Add(name);

            // Never silently land on No speech just because it happens to be
            // first. It is chosen, or it is what is left when there is no voice.
            int want = book.TextNoSpeech ? 0 : -1;
            if (want < 0 && !string.IsNullOrEmpty(book.TextVoice))
                want = cmbTVoice.Items.IndexOf(book.TextVoice);
            if (want < 0) want = cmbTVoice.Items.Count > 1 ? 1 : 0;
            cmbTVoice.SelectedIndex = want;
        }

        /// <summary>The voice catalog for the pickers. Created on demand (it starts
        /// the 32-bit host) and released when the dialog closes.</summary>
        private CompositeSpeechBackend TextSpeech()
        {
            if (textSpeech == null) textSpeech = new CompositeSpeechBackend();
            return textSpeech;
        }

        /// <summary>Writes the per-book reading settings. "Custom" off clears them,
        /// which is how a book goes back to following Settings.</summary>
        private void PersistTextOptions()
        {
            if (cmbTVoice == null) return;   // no Text tab on this book
            // Index 0 is No speech. TextVoice is deliberately left ALONE in that
            // case rather than blanked: switching speech back on should return
            // the voice the book was last read with, not lose it.
            book.TextNoSpeech = cmbTVoice.SelectedIndex == 0;
            if (!book.TextNoSpeech)
                book.TextVoice = cmbTVoice.SelectedItem != null
                    ? cmbTVoice.SelectedItem.ToString() : "";
            book.TextWpm = numTWpm != null ? (int)numTWpm.Value : -1;
            book.TextVolume = numTVolume != null ? (int)numTVolume.Value : -1;
            book.TextPitch = numTPitch != null ? (int)numTPitch.Value : -99;
            // The visual-output controls were scaffolding until now: they were
            // built, shown and read by nobody, so every visit forgot the last
            // choice. They are per book, like the voice.
            book.TextVisual = chkTVisual != null && chkTVisual.Checked;
            book.TextVisualMode = cmbTVisualMode != null && cmbTVisualMode.SelectedIndex >= 0
                ? cmbTVisualMode.SelectedIndex : 0;
            // Braille was scaffolding in exactly the same way. Index 0 of the table
            // list is "automatic", which is stored as an empty id so that a book
            // set to automatic keeps following its language if that language later
            // gains a better table.
            book.TextBraille = chkTBraille != null && chkTBraille.Checked;
            book.TextBrailleTable = cmbTBrailleTable != null && cmbTBrailleTable.SelectedIndex > 0
                && cmbTBrailleTable.SelectedIndex <= BrailleTables.All.Length
                ? BrailleTables.All[cmbTBrailleTable.SelectedIndex - 1].Id : "";
            // File the numbers under the voice they were set for — every voice this
            // book has been read with keeps its own, so coming back to one restores
            // it instead of inheriting whatever was used last.
            StageTextPrefs();
            foreach (var kv in stagedTextPrefs.All()) book.TextVoicePrefs.Set(kv.Key, kv.Value);
            // A text book has no playback volume of its own: the player's Volume
            // field IS this speech volume, so keep the two the same number â€” the
            // player reads book.Volume back when the dialog closes. (On a hybrid
            // book the Audio tab's own field is written afterwards and wins.)
            if (book.TextVolume >= 0) book.Volume = book.TextVolume;

            if (cmbTBrailleTable != null)
            {
                int i = cmbTBrailleTable.SelectedIndex - 1;   // 0 = auto-detect
                book.BrailleTable = (i >= 0 && i < BrailleTables.All.Length)
                    ? BrailleTables.All[i].Id : "";
            }
        }

        /// <summary>Per-book playback level and speed, alongside the processing
        /// stages â€” they are what the book sounds like just as much as the filters.
        /// The player writes these back as the user adjusts them live, so the dialog
        /// simply shows and edits the stored values.</summary>
        private GroupBox BuildPlaybackGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Prop.Playback.Title");
            box.Location = new Point(x, y);
            box.Size = new Size(CellW * 2 + 8, 70);

            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Prop.Playback.Volume"), 10, 28));
            numPlayVolume = SettingsForm.MakeNumeric(Localization.T("Prop.Playback.Volume"),
                                                     120, 25, 0, 100, Clamp(book.Volume, 0, 100), 0, 5);
            box.Controls.Add(numPlayVolume);

            box.Controls.Add(SettingsForm.MakeLabel(Localization.T("Prop.Playback.Speed"), 240, 28));
            // Speed is the same multiplier the player and the library show (1,4×),
            // not a percentage, and it steps by the player's own Ctrl+←/→ step.
            numPlaySpeed = SettingsForm.MakeDecimal(Localization.T("Prop.Playback.Speed"), 340, 25,
                                                    0.5m, 3.0m, Clamp(book.Speed, 50, 300) / 100m, 1, 0.1m, 1);
            box.Controls.Add(numPlaySpeed);
            numPlayVolume.ValueChanged += (s, e) => PreviewPlayback();
            numPlaySpeed.ValueChanged += (s, e) => PreviewPlayback();
            return box;
        }

        /// <summary>Applies the reading settings to the live reader so the change is
        /// heard at once; the values are only committed on OK.</summary>
        private void PreviewText()
        {
            if (initialising || onTextPreview == null || cmbTVoice == null) return;
            string v = cmbTVoice.SelectedItem != null ? cmbTVoice.SelectedItem.ToString() : "";
            onTextPreview(v, (int)numTWpm.Value, (int)numTVolume.Value, (int)numTPitch.Value);
        }

        private void PreviewPlayback()
        {
            if (initialising || onPlaybackPreview == null || numPlayVolume == null || numPlaySpeed == null) return;
            onPlaybackPreview((int)numPlayVolume.Value, (int)Math.Round(numPlaySpeed.Value * 100));
        }

        /// <summary>The Text tab's read-out: what this book will actually be read
        /// with, and where each value comes from â€” the book's own setting, or the
        /// Settings default it inherits.</summary>
        private void RefreshTextInfo()
        {
            if (tbTextInfo == null) return;
            string nl = Environment.NewLine;
            var sb = new StringBuilder();

            // The book's own facts, in the order every info box uses — see
            // BookInfo.cs. The separator is added by the builder, and that is not
            // merely tidiness: the player's glass splits a line on ": " to tell
            // the silkscreened label from the lit value, so a line without one
            // has no label at all. The reading settings below are this page's own
            // business and follow after them.
            var info = new BookInfoBuilder();
            info.AddAlways(BookInfoField.Title, book.Title, "");
            info.Add(BookInfoField.Author, book.Author);
            info.Add(BookInfoField.Publisher, BookData.NormalizeProducer(book.Publisher));
            info.Add(BookInfoField.Producer, BookData.NormalizeProducer(book.Producer));
            info.AddAlways(BookInfoField.Format, book.Format, "");
            if (book.TextPages.Count > 0)
                info.Add(BookInfoField.Pages, book.TextPages.Count.ToString());
            if (book.TextHeadings.Count > 0)
                info.Add(BookInfoField.Headings, book.TextHeadings.Count.ToString());
            if (book.TextChars > 0)
                info.Add(BookInfoField.Characters, book.TextChars.ToString("N0"));
            if (!string.IsNullOrEmpty(book.TextLanguage))
                info.Add(BookInfoField.Language, LanguageDetector.DisplayName(book.TextLanguage));
            sb.Append(info.ToText(nl));
            sb.Append(nl);

            // No indent under the section names. The column is 262 units wide,
            // which is about thirty characters, so "Speech engine: Microsoft
            // OneCore (64-bit)" always wraps — and a wrapped line in a TextBox
            // comes back to the left margin. An indented item whose second line
            // is NOT indented reads as two entries: the glass showed "OneCore
            // (64-bit)" and "minute): 175 WPM" sitting at the same level as the
            // things they are the tail of. One level throughout, and the blank
            // line before each section name is what separates the sections.
            sb.Append(Localization.T("Settings.TextBooks.SpeechGroup")).Append(nl);
            string voice = cmbTVoice != null && cmbTVoice.SelectedItem != null ? cmbTVoice.SelectedItem.ToString() : "";
            // An empty Voice line on its own is a puzzle. The glass is where the
            // reader looks first, so the reason goes here too, not only on the
            // control that caused it.
            string why = voice.Length == 0 ? NoVoiceExplanation() : "";
            if (why.Length > 0)
                sb.Append(why).Append(nl);
            else
                sb.Append(Localization.T("Settings.TextBooks.Voice")).Append(' ').Append(voice).Append(nl);
            // What the book IS and what it is being READ IN are two different
            // facts and used to sit on the same screen looking like a
            // contradiction — "Language: Serbian" above a picker showing
            // Croatian. Both are now labelled for what they are, so a book read
            // by a voice from another language says so instead of looking wrong.
            int lsel = cmbTLanguage != null ? cmbTLanguage.SelectedIndex : -1;
            if (lsel >= 0 && lsel < textLanguageCodes.Count)
                sb.Append(Localization.T("Prop.Text.ReadingIn")).Append(' ')
                  .Append(SettingsForm.LanguageName(textLanguageCodes[lsel])).Append(nl);
            if (numTWpm != null)
                sb.Append(Localization.T("Settings.TextBooks.Speed")).Append(' ')
                  .Append((int)numTWpm.Value).Append(" WPM").Append(nl);
            if (numTVolume != null)
                sb.Append(Localization.T("Settings.TextBooks.Volume")).Append(' ')
                  .Append((int)numTVolume.Value).Append('%').Append(nl);
            if (numTPitch != null)
                sb.Append(Localization.T("Settings.TextBooks.Pitch")).Append(' ')
                  .Append((int)numTPitch.Value).Append(nl);
            sb.Append(nl);

            sb.Append(Localization.T("Settings.TextBooks.BrailleGroup")).Append(": ")
              .Append(Localization.T(chkTBraille != null && chkTBraille.Checked ? "Prop.On" : "Prop.Off")).Append(nl);
            if (chkTBraille != null && chkTBraille.Checked && cmbTBrailleTable != null && cmbTBrailleTable.SelectedItem != null)
                sb.Append(cmbTBrailleTable.SelectedItem).Append(nl);

            // Why it is on, when the user did not turn it on. The visual group is
            // DISABLED while braille is ticked — all of it now, not just the check
            // box — and Windows skips a disabled control in the tab order, so a
            // screen-reader user never lands on any of it and would otherwise have
            // no way at all to learn that it is set, let alone by what.
            //
            // ON the line it qualifies, not under it. It was a line of its own and
            // Gordan went through both dialogs without meeting it; a reason tacked
            // onto the statement it explains cannot be passed over separately.
            sb.Append(Localization.T("Settings.TextBooks.VisualGroup")).Append(": ")
              .Append(Localization.T(chkTVisual != null && chkTVisual.Checked ? "Prop.On" : "Prop.Off"));
            if (chkTBraille != null && chkTBraille.Checked)
                sb.Append(" — ").Append(Localization.T("Settings.TextBooks.VisualForBraille"));
            sb.Append(nl);
            if (chkTVisual != null && chkTVisual.Checked && cmbTVisualMode != null && cmbTVisualMode.SelectedItem != null)
                sb.Append(cmbTVisualMode.SelectedItem).Append(nl);

            tbTextInfo.Text = sb.ToString();
        }
    }
}
