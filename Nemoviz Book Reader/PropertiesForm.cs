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

        /// <summary>The Tone cell alone is taller: five rows at 40, 64, 88, 112,
        /// 136 and a spin box 22 tall end at 158.
        ///
        /// <para>Only this one, because under the NEW look the skin stretches
        /// every cell to DialogSkin.StageH (166 since Properties gave up its
        /// Playback row) and the difference never shows — while under the
        /// CLASSIC look nothing resizes anything, and at 112 the bottom two
        /// bands would simply have been cut off the page. A cell built for three
        /// rows cannot hold five.</para></summary>
        /// <summary>The Tone cell and the pitch of its five rows.
        ///
        /// <para>The cell CANNOT grow: it shares row three with the loudness
        /// cell and the skin pins that row bottom at 570, so 166 is all there is.
        /// Tried 30 and the fifth band fell off the bottom of the dialog
        /// altogether — measured on a render, which is the only way that was ever
        /// going to be noticed.</para>
        ///
        /// <para>So the box gets the space instead of the gap. Gordan asked about
        /// "the number box and the arrows", and those are what 70 x 22 was
        /// starving — the spin arrows came out half the size of the ones on the
        /// Speech page. At 90 x 24 they match, and the pitch pays for it: five
        /// rows from y=36 at 25 end at 160, four clear of the cell.</para>
        /// </summary>
        private const int EqRowH = 25;
        private const int EqCellH = 166;

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
        /// <summary>One spin box per band of SoundSettings.EqBandHz. Built from
        /// that array rather than named one by one, so moving a band or adding
        /// one is a change in the data and nowhere else.</summary>
        private CheckBox chkEq; private NumericUpDown[] numEq;
        private CheckBox chkNrm; private ComboBox cmbNrm;
        private CheckBox chkGate; private ComboBox cmbGate;

        private Button btnResetAll;
        private CheckBox chkBypass;
        private TabControl tabs;
        private Button btnOK, btnCancel;

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
                    TextInfo = tbTextInfo,
                    Tabs = tabs
                };
            }
        }

        // Text tab (per-book reading options; mirrors Settings -> Text Books).
        private TextBox tbTextInfo;
        private ComboBox cmbTLanguage, cmbTVoice;
        // Shown only when nothing installed speaks the book's language.
        private TextBox tbTNoVoice;
        private NumericUpDown numTWpm, numTVolume, numTPitch;
        // The table a braille book was READ with. Not a preference: changing it
        // re-runs the import, which is why it is committed on OK and nowhere else.
        private ComboBox cmbTInTable;
        private List<BrailleTableInfo> inTables = new List<BrailleTableInfo>();
        private CheckBox chkTVisual;
        private ComboBox cmbTVisualMode, cmbTHighlight, cmbTHighlightColour, cmbTTextColour, cmbTBackColour;
        private List<(string Name, string Engine, string Language)> textCatalog;
        private readonly List<string> textLanguageCodes = new List<string>();
        private CompositeSpeechBackend textSpeech;

        private bool suppressAnnounce;
        // True while the dialog is still being built: filling the pickers fires
        // change events, and those must not be mistaken for the user editing —
        // otherwise opening Properties would immediately push its starting values
        // onto live playback.
        private bool initialising = true;

        // Live-preview hook: when the dialog is opened from the player, this
        // applies the (unsaved) settings to playback on every change so the user
        // hears edits on the fly. Null when opened from the library (no audio).
        private readonly Action<SoundSettings, bool> onPreview;
        // Live preview for playback level and speed, so they are heard while being
        // adjusted just like the processing stages. Cancel restores the old values.
        // Same idea for a text book: the voice and how it reads are heard while
        // being chosen, not only after OK.
        private readonly Action<string, int, int, int> onTextPreview;

        /// <summary>true to hold playback, false to put it back as it was. Null
        /// from the Library, where there is nothing playing.</summary>
        private readonly Action<bool> onHoldPlayback;

        /// <summary>Whether the user has toggled Bypass (compare processed vs.
        /// raw).</summary>
        public bool Bypass { get { return chkBypass.Checked; } }

        private static readonly string[] L5 =
            { "Prop.Level.Minimal", "Prop.Level.Light", "Prop.Level.Medium", "Prop.Level.Strong", "Prop.Level.Maximum" };

        // The global speech settings, for the fallback when this book has never
        // been read with the voice being picked. Null when the caller has none.
        private readonly AppSettings appSettings;

        // The playback-preview hook went with the Playback controls: nothing in
        // this dialog changes volume or speed any more, so there is nothing to
        // preview. The player still owns both, live, from its own keys.
        public PropertiesForm(BookData book, Action<SoundSettings, bool> onPreview = null,
                              Action<string, int, int, int> onTextPreview = null,
                              AppSettings appSettings = null,
                              Action<bool> onHoldPlayback = null)
        {
            this.book = book;
            this.onPreview = onPreview;
            this.onTextPreview = onTextPreview;
            this.appSettings = appSettings;
            this.onHoldPlayback = onHoldPlayback;
            SoundSettings s = book.Sound;
            gainDb = s.GainDb;
            gainEnabled = s.GainEnabled;

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

            // Column A — full-height info + live technical read-out.
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
            // Below the tallest stage row: the Tone cell grew to five bands and
            // now ends at 244 + 166 = 410 under the classic look, where nothing
            // moves it. The new look ignores these positions entirely.
            chkMaster.Location = new Point(256, 420);
            chkMaster.Size = new Size(420, 24);
            chkMaster.TabIndex = 1;
            chkMaster.Checked = s.Enabled;

            int xB = 248, xC = 470;
            // Spaced by the cell height plus 14, so the three rows follow the
            // cells rather than a number written down beside them.
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

            GroupBox gEq = StageBox("Prop.Eq.Title", xB, y3, 6, EqCellH);
            chkEq = StageEnable(gEq); chkEq.Checked = s.EqEnabled;
            numEq = new NumericUpDown[SoundSettings.EqBandHz.Length];
            for (int i = 0; i < numEq.Length; i++)
                numEq[i] = EqBand(gEq, BandLabel(i), 36 + i * EqRowH,
                                  i < s.EqGain.Length ? s.EqGain[i] : 0);

            // One method, not a choice of two (Gordan, decided long before
            // 2026-08-03 and settled here): speech normalisation is what a
            // spoken recording wants, and the music-safe alternative was a
            // question asked of a reader who has no way to answer it. The cell
            // is now shaped like every other stage — a switch and a level.
            // TWO subjects in one cell, and Gordan's reasoning for pairing them
            // beats the one I offered (2026-08-09). Two arguments:
            //
            //   The six cells already read the chain in order — rumble, noise,
            //   sibilance, dynamics, tone, loudness IS highpass, afftdn,
            //   deesser, acompressor, EQ, speechnorm. The dialog is a picture of
            //   the signal path. The gate acts LAST, so it belongs in the last
            //   cell; putting it with Noise reduction, as I had proposed, would
            //   have stood it second and acted it sixth.
            //
            //   And these two fight each other directly: speechnorm lifts quiet
            //   passages, the gate pushes them down, and the quiet passage is
            //   the pause. A reader who turns loudness up and hears the noise
            //   come up with it has the remedy in the same box — which is the
            //   exact thing that happened to him today.
            //
            // It costs no layout: this cell shares row three with Tone, which is
            // already the tall one (166 against 112), so the height is there to
            // be used.
            GroupBox gNrm = StageBox("Prop.Loudness.Title", xC, y3, 7, EqCellH);
            chkNrm = StageEnable(gNrm, "Prop.Normalize.Title");
            chkNrm.Checked = s.NormalizeEnabled;
            cmbNrm = LevelCombo(gNrm, L5, s.NormalizeLevel);
            chkGate = StageEnable(gNrm, "Prop.Gate.Title", 78);
            chkGate.Checked = s.GateEnabled;
            chkGate.TabIndex = 2;
            cmbGate = LevelCombo(gNrm, L5, s.GateLevel, 102);
            cmbGate.TabIndex = 3;
            cmbGate.AccessibleName = Localization.T("Prop.Gate.Title") + " — " +
                                     Localization.T("Prop.Stage.Level");

            stageCells = new[] { gHp, gDn, gDs, gCmp, gEq, gNrm };
            stages = new List<(CheckBox, Control[])>
            {
                (chkHp, new Control[] { cmbHp }),
                (chkDn, new Control[] { cmbDn }),
                (chkDs, new Control[] { cmbDs }),
                (chkCmp, new Control[] { cmbCmp }),
                (chkEq, numEq),
                (chkNrm, new Control[] { cmbNrm }),
                (chkGate, new Control[] { cmbGate }),
            };

            btnResetAll = new Button();
            btnResetAll.Text = Localization.T("Prop.ResetAll");
            btnResetAll.AccessibleName = Localization.T("Prop.ResetAll");
            btnResetAll.Size = new Size(90, 30);
            btnResetAll.Location = new Point(256, 452);
            btnResetAll.TabIndex = 8;
            btnResetAll.Click += (s2, e) => ResetAll();

            chkBypass = new CheckBox();
            chkBypass.Text = Localization.T("Prop.Bypass");
            // The shortcut is named on the control, so it can be found by anyone
            // who lands on it rather than only by whoever read the manual.
            chkBypass.AccessibleName = Localization.T("Prop.Bypass");
            chkBypass.AccessibleDescription = Localization.T("Prop.Bypass.Shortcut");
            chkBypass.Size = new Size(90, 30);
            chkBypass.Location = new Point(352, 452);
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
            chkMaster.CheckedChanged += (s2, e) =>
            {
                WarnAboutSoundProcessing();
                UpdateEnabledStates();
                OnAnyChange();
                // After the warning, not before: declining it puts the switch
                // back off, and measuring a recording nobody asked to process
                // is the very work this feature exists to avoid.
                AnalyseIfNeeded();
            };
            foreach (var st in stages)
                st.Enable.CheckedChanged += (s2, e) => { UpdateEnabledStates(); OnAnyChange(); };
            WireCombo(cmbHp); WireCombo(cmbDn); WireCombo(cmbDs); WireCombo(cmbCmp);
            WireCombo(cmbNrm);
            WireCombo(cmbGate);
            foreach (NumericUpDown n in numEq) n.ValueChanged += (s2, e) => OnAnyChange();

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

            // Ctrl+B toggles Bypass from anywhere on the audio page (Gordan,
            // 2026-08-02). Tuning sound is A against B: you change a stage, then
            // want to hear it against the untouched signal, then change it again.
            // Walking back across a hillside of controls to a check box each
            // time is the whole reason nobody does that comparison as often as
            // they should. The processing is previewed live, so the switch is
            // heard the instant it is pressed — the sound IS the confirmation,
            // and the spoken line is for anyone whose reader will carry it.
            if (keyData == (Keys.Control | Keys.B) && chkBypass != null && chkBypass.Enabled)
            {
                chkBypass.Checked = !chkBypass.Checked;
                NvdaController.Speak(Localization.T("Prop.Bypass") + ": " +
                    Localization.T(chkBypass.Checked ? "Prop.On" : "Prop.Off"));
                return true;
            }

            // Enter in an info column opens the description — and it has to be
            // caught here for the same reason it did in the Library: this form
            // has an AcceptButton, a read-only TextBox does not claim Enter, so
            // Form.ProcessDialogKey would press OK and close the dialog before
            // the column was ever asked. The column says "press Enter to read
            // it", so pressing Enter there must not mean OK.
            //
            // Only in the info columns, and only when there IS a description:
            // Enter anywhere else on this page still means OK, which is what a
            // dialog's Enter has always meant.
            if (keyData == Keys.Enter && book != null && book.HasDescription)
            {
                Control on = DeepActiveControl();
                if (ReferenceEquals(on, tbInfo) || ReferenceEquals(on, tbTextInfo))
                {
                    OpenDescription();
                    return true;
                }
            }
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

        // ── Cell builders ─────────────────────────────────────────────────
        private GroupBox StageBox(string titleKey, int x, int y, int tabIndex, int h = CellH)
        {
            GroupBox g = new GroupBox();
            g.Text = Localization.T(titleKey);
            g.Location = new Point(x, y);
            g.Size = new Size(CellW, h);
            g.TabIndex = tabIndex;
            return g;
        }

        /// <summary>A stage's on/off switch. Normally unlabelled — the group
        /// already names the stage and the check state alone says whether it is
        /// on, so a reader hears "Soften sibilance, checkbox".
        ///
        /// <para><c>captionKey</c> is for the one cell that holds TWO subjects.
        /// There the group name cannot identify either of them, so each switch
        /// carries its own name — visibly and in its accessible name, since a
        /// nameless second checkbox in a box called something else is exactly
        /// how a control becomes unreachable by ear.</para></summary>
        private CheckBox StageEnable(GroupBox g, string captionKey = null, int y = 18)
        {
            CheckBox c = new CheckBox();
            string name = captionKey == null ? g.Text : Localization.T(captionKey);
            c.Text = captionKey == null ? "" : name;
            c.AccessibleName = name;
            c.Location = new Point(10, y);
            c.Size = new Size(CellW - 24, 20);
            c.TabIndex = 0;
            g.Controls.Add(c);
            return c;
        }

        private ComboBox LevelCombo(GroupBox g, string[] itemKeys, int selected, int y = 46)
        {
            ComboBox cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Location = new Point(10, y);
            cb.Size = new Size(CellW - 24, 24);
            cb.AccessibleName = g.Text + " — " + Localization.T("Prop.Stage.Level");
            cb.TabIndex = 1;
            foreach (string k in itemKeys) cb.Items.Add(Localization.T(k));
            cb.SelectedIndex = Clamp(selected, 0, itemKeys.Length - 1);
            g.Controls.Add(cb);
            return cb;
        }

        /// <summary>A band is named by its frequency, not by a word. "Bass",
        /// "voice" and "treble" worked for three; with five there is no honest
        /// word for 1800 Hz, and the number is what the reader is actually
        /// choosing. The top one says so, because a shelf behaves differently
        /// from a bell and the reader can hear that it does.</summary>
        private static string BandLabel(int i)
        {
            int hz = SoundSettings.EqBandHz[i];
            string n = hz >= 1000 ? (hz / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz"
                                  : hz + " Hz";
            return i == SoundSettings.EqShelfIndex ? Localization.T("Prop.Eq.Shelf", n) : n;
        }

        private string EqReadout()
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < numEq.Length; i++)
                parts.Add(BandLabel(i) + " " + Sign((int)numEq[i].Value));
            return string.Join(", ", parts.ToArray()) + " dB";
        }

        private NumericUpDown EqBand(GroupBox g, string label, int y, int value)
        {
            // 118, not 90: the top band's caption is "5 kHz and above" and at
            // 90 it was being cut to "5 kHz and", which reads as a different
            // band rather than as a truncation.
            Label lbl = new Label();
            lbl.Text = label;
            lbl.Location = new Point(10, y + 4);
            lbl.Size = new Size(146, 20);

            NumericUpDown n = new NumericUpDown();
            // 20, not 15 (2026-08-09). Gordan hit the old wall twice in one
            // listening round -- 300 Hz at -15 on one book, 800 Hz at -15 on
            // another -- and a control a reader pins at its limit is a control
            // that has run out of travel. Both of those were him reaching for
            // the 200 Hz excess through the wrong band, which the band move
            // above now addresses directly; the wider range is what is left
            // over for the cases it does not.
            n.Minimum = -SoundSettings.EqMaxDb; n.Maximum = SoundSettings.EqMaxDb; n.Increment = 1;
            // The SAME box the Speech page uses (SettingsForm.MakeNumeric is
            // 90 x 24). It was 70 x 22 on a 24-unit pitch, so five of them
            // stacked with no gap at all and their arrows were half the size of
            // the ones three cells away — Gordan: "controls in EQ are too
            // squeezed, number box and the arrows; in the speech part they are
            // more relaxed." Two spin boxes in one dialog should be one spin box.
            n.Location = new Point(162, y);
            n.Size = new Size(90, 24);
            n.TextAlign = HorizontalAlignment.Right;
            n.AccessibleName = label;
            n.TabIndex = g.Controls.Count;
            value = Clamp(value, -SoundSettings.EqMaxDb, SoundSettings.EqMaxDb);
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

            // The book's own facts, in the order every info box uses — see
            // BookInfo.cs. The processing read-out below is this page's business
            // and follows after them.
            var info = new BookInfoBuilder();
            info.AddAlways(BookInfoField.Title, book.Title, dash);
            info.Add(BookInfoField.Author, book.Author);
            string pubA = BookData.NormalizeProducer(book.Publisher);
            info.Add(BookInfoField.Publisher, BookData.WithYear(pubA, book.Year));
            info.Add(BookInfoField.Producer, BookData.NormalizeProducer(book.Producer));
            if (pubA.Length == 0) info.Add(BookInfoField.Year, book.Year);
            info.AddAlways(BookInfoField.Format, book.Format, dash);
            info.AddAlways(BookInfoField.Time, book.Duration, dash);
            AddDescriptionRow(info);
            // The two numbers this dialog used to hold controls for. The controls
            // went so the tone bands could have the room; the VALUES had to stay,
            // because until they were put here they were legible nowhere at all.
            info.AddAlways(BookInfoField.Volume, book.Volume + " %", dash);
            info.AddAlways(BookInfoField.Speed, book.Speed + " %", dash);
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
                EqReadout());

            int nl = cmbNrm.SelectedIndex;
            AppendStage(sb, "Prop.Normalize.Title", chkNrm.Checked,
                "speechnorm, " + cmbNrm.Text +
                " (e=" + SoundSettings.SpeechnormExpansion[nl].ToString("0.0") + ")");

            int gl = cmbGate.SelectedIndex;
            AppendStage(sb, "Prop.Gate.Title", chkGate.Checked,
                "agate, " + cmbGate.Text +
                " (" + SoundSettings.GateThresholdDb[gl].ToString("0") + " dB, " +
                SoundSettings.GateRangeDb.ToString("0") + " dB max)");

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
            ShowSettings(new SoundSettings());   // fresh defaults
        }

        /// <summary>Puts a set of settings into the controls. Two callers now —
        /// Reset all, and the analysis — so the six stages are loaded in one
        /// place rather than in two that could drift.</summary>
        private void ShowSettings(SoundSettings d)
        {
            suppressAnnounce = true;

            chkHp.Checked = d.HighpassEnabled; cmbHp.SelectedIndex = d.HighpassLevel;
            chkDn.Checked = d.DenoiseEnabled; cmbDn.SelectedIndex = d.DenoiseLevel;
            chkDs.Checked = d.DeesserEnabled; cmbDs.SelectedIndex = d.DeesserLevel;
            chkCmp.Checked = d.CompressorEnabled; cmbCmp.SelectedIndex = d.CompressorLevel;
            chkEq.Checked = d.EqEnabled;
            for (int i = 0; i < numEq.Length; i++)
                numEq[i].Value = i < d.EqGain.Length
                    ? Clamp(d.EqGain[i], -SoundSettings.EqMaxDb, SoundSettings.EqMaxDb) : 0;
            chkNrm.Checked = d.NormalizeEnabled;
            cmbNrm.SelectedIndex = d.NormalizeLevel;
            chkGate.Checked = d.GateEnabled;
            cmbGate.SelectedIndex = d.GateLevel;

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
            for (int i = 0; i < numEq.Length && i < s.EqGain.Length; i++)
                s.EqGain[i] = (int)numEq[i].Value;

            s.NormalizeEnabled = chkNrm.Checked;
            s.NormalizeLevel = cmbNrm.SelectedIndex;

            s.GateEnabled = chkGate.Checked;
            s.GateLevel = cmbGate.SelectedIndex;

            // CARRIED, not rebuilt. The loudness target is the one part of the
            // chain that is not a control: it is a single number worked out from
            // the measurement, so there is nothing on screen to read it off.
            //
            // It was simply being dropped. FillSettings starts from a fresh
            // SoundSettings, so anything it does not write stays at the default —
            // and this pair defaults to off. Every live preview was built through
            // here, and so was Persist, which means the −16 LUFS target had never
            // once reached the reader's ears and was wiped from the book on OK.
            // Measured on Gordan's four tuned books, 2026-08-09: the advisor had
            // computed −9.1 dB for one and +2.4 for another, and all four came
            // back from Book.ini with the gain OFF. That is also why he had to
            // reach for speechnorm to get loudness — the static stage that was
            // supposed to provide it was not in the chain.
            s.GainDb = gainDb;
            s.GainEnabled = gainEnabled;
        }

        /// <summary>The loudness target, held on the form because no control
        /// holds it. Seeded from the book, replaced when an analysis lands.
        /// </summary>
        private double gainDb;
        private bool gainEnabled;

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
            // Volume and speed are no longer written from here: their controls
            // are gone, and the player owns both. It already saves them per book
            // as they change, so a second writer could only disagree with it.
            book.Save();
            RereadBrailleIfAsked();
        }

        /// <summary>Re-reads the braille file with the newly chosen table, if one
        /// was chosen. LAST, after the book has been saved, because it does its own
        /// save and there is no sense in it racing the one above.
        ///
        /// <para><b>It asks first, and it may be told not to ask again.</b> The
        /// re-read throws away the reading position, the bookmarks and the
        /// percentage — they are offsets into a text that is about to stop
        /// existing. Gordan's reasoning for warning rather than preserving: a
        /// reader notices a wrong table long before they start setting bookmarks,
        /// so the 99% case loses nothing, and the 1% is owed the sentence. And a
        /// reader hunting for the right table meets that sentence several times in
        /// a row, which is what the switch-off is for.</para>
        ///
        /// <para>Declining leaves everything exactly as it was — the choice was
        /// only ever staged in the combo, which is the whole reason this runs on
        /// OK and not on the combo's own event.</para></summary>
        private void RereadBrailleIfAsked()
        {
            BrailleTableInfo want = ChosenInputTable();
            if (want == null) return;

            // Properties can be opened from the library without settings handed in,
            // so fall back to the live ones rather than silently losing the
            // switch-off the reader asked for.
            AppSettings st = appSettings ?? AppSettings.Current;
            if (st == null || st.WarnBrailleReread)
            {
                bool off;
                if (!ConfirmOnceForm.Ask(this,
                        Localization.T("Dialog.BrailleReread.Message", want.Display),
                        Localization.T("Dialog.BrailleReread.Title"), out off))
                    return;
                if (off && st != null) st.SetWarnBrailleReread(false);
            }

            if (!book.RetranslateBraille(want.Id))
                MessageForm.ShowInfo(this, Localization.T("Dialog.BrailleReread.Failed"),
                                     Localization.T("Dialog.BrailleReread.Title"));
        }

        /// <summary>Says what sound processing can and cannot do, the first few
        /// times it is switched on (Gordan, 2026-08-07 — "da ne očekuje baš nešto
        /// previše").
        ///
        /// <para><b>Why it is worth a box at all.</b> The name promises more than
        /// the thing delivers. A reader who turns on something called "sound
        /// processing" on a bad recording expects it repaired; what it does is
        /// make a poor recording easier to listen to for an hour. Meeting that
        /// expectation head-on once is kinder than letting someone conclude the
        /// feature is broken — and it is the same text the Help carries, so
        /// nobody has to go and look it up.</para>
        ///
        /// <para><b>Only on the way ON, and only until told otherwise.</b>
        /// Switching it off needs no warning, and a box that appeared every time
        /// would be the thing people remember instead of what it said. It uses
        /// the ConfirmOnceForm the braille re-read already established, so
        /// "don't show this again" behaves the way it does everywhere else — and
        /// Cancel leaves the switch OFF, which is the only honest thing a Cancel
        /// button on this question can mean.</para></summary>
        private bool warningInProgress;

        private void WarnAboutSoundProcessing()
        {
            if (chkMaster == null || !chkMaster.Checked) return;
            // Un-ticking below re-enters this handler; without the guard the box
            // would ask about its own answer.
            if (warningInProgress) return;

            AppSettings st = appSettings ?? AppSettings.Current;
            if (st != null && !st.WarnSoundProcessing) return;

            bool off;
            bool go = ConfirmOnceForm.Ask(this,
                          Localization.T("Dialog.SoundProcessing.Message"),
                          Localization.T("Dialog.SoundProcessing.Title"), out off);
            if (go)
            {
                if (off && st != null) st.SetWarnSoundProcessing(false);
                return;
            }

            warningInProgress = true;
            try { chkMaster.Checked = false; }
            finally { warningInProgress = false; }
        }

        private bool analysing;

        /// <summary>Measures the recording and sets the six stages from it — the
        /// autoscan.
        ///
        /// <para><b>Once per book, and only when there is no stored
        /// measurement.</b> A book that has been measured already had its stages
        /// set from that measurement, and the reader has had every chance to
        /// correct them since; running again would walk over exactly the
        /// corrections Gordan said the reader should be free to make.</para>
        ///
        /// <para><b>It waits behind its own window now, and that is because the
        /// job grew.</b> At three segments it was 1.6 s and an announcement over
        /// a dialog nobody was stopped from using was the right answer. At twenty
        /// it is 22 s here and four to seven minutes on the minimum machine —
        /// long enough that the reader needs to see it moving, to know how much
        /// is left, and to be able to stop it. `AnalysisProgressForm` is all
        /// three, and being modal also settles what the earlier version left
        /// vague: the stages cannot be edited halfway through a measurement that
        /// is about to overwrite them.</para></summary>
        private void AnalyseIfNeeded()
        {
            if (chkMaster == null || !chkMaster.Checked || analysing) return;
            if (book == null || book.Chapters == null || book.Chapters.Count == 0) return;
            if (book.Analysis != null && book.Analysis.Measured) return;

            analysing = true;

            // PAUSED for the duration, then put back exactly as it was (Gordan).
            // Playback cannot be reached from this dialog, and it is wanted while
            // the controls are being tried — but not while the measurement runs,
            // where it would be a voice under a progress bar for anything up to
            // several minutes. The pause is PROGRAMMATIC, through the same pair
            // the sleep timer uses, so it does not read as the reader pausing.
            if (onHoldPlayback != null) onHoldPlayback(true);
            try
            {
                using (var dlg = new AnalysisProgressForm(book))
                {
                    dlg.ShowDialog(this);
                    // A reader who cancelled has already been told what happened,
                    // and nothing failed — so the failure line is not said here.
                    // "Could not be analysed" for a job somebody stopped on
                    // purpose is the wrong sentence twice over.
                    if (!dlg.Cancelled) AnalysisDone(dlg.Result);
                }
            }
            finally
            {
                analysing = false;
                if (onHoldPlayback != null) onHoldPlayback(false);
            }
        }

        private void AnalysisDone(SoundAnalysis found)
        {
            analysing = false;
            if (IsDisposed || Disposing) return;

            if (found == null || !found.Measured)
            {
                // Nothing measurable — an unreadable file, or every segment
                // silence. Say so rather than leaving the reader waiting for an
                // announcement that never comes, and leave the stages alone.
                ScreenReader.Announce(this, Localization.T("Prop.Analysing.Failed"));
                return;
            }

            // The measurement is kept on the book even though the STAGES are
            // only staged in the dialog: it is a fact about the recording, not a
            // setting, and Cancel does not make it untrue. It is also what stops
            // the next visit measuring the same book again.
            book.SetAnalysis(found);

            SoundSettings suggested = BuildCurrent();
            SoundAdvisor.Apply(found, suggested);
            // Before ShowSettings, which previews through BuildCurrent and would
            // otherwise hand the player the old gain with the new stages.
            gainDb = suggested.GainDb;
            gainEnabled = suggested.GainEnabled;
            ShowSettings(suggested);

            ScreenReader.Announce(this, Localization.T("Prop.Analysing.Done", StagesOn(suggested)));
        }

        private static int StagesOn(SoundSettings s)
        {
            int n = 0;
            if (s.HighpassEnabled) n++;
            if (s.DenoiseEnabled) n++;
            if (s.DeesserEnabled) n++;
            if (s.CompressorEnabled) n++;
            if (s.EqEnabled) n++;
            if (s.NormalizeEnabled) n++;
            return n;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // The catalog backend starts the 32-bit host; let it go with the dialog.
            try { if (textSpeech != null) { textSpeech.Dispose(); textSpeech = null; } } catch { }
            ScreenReader.Forget(this);
            base.OnFormClosed(e);
        }

        // ── Text tab: the SAME options as Settings → Text Books, but for THIS book.
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
            // The input-table group appears ONLY for a book that came from a
            // braille file, and the visual group closes the gap when it does not —
            // an empty group invites the question of what it is for, and §10b's
            // rule is that slack goes to the gaps and not into the boxes.
            int visualY = 226;
            if (book.BrailleSourcePath != null)
            {
                page.Controls.Add(BuildTextBrailleGroup(248, 226));
                visualY = 290;
            }
            page.Controls.Add(BuildTextVisualGroup(248, visualY));

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
            // The language drives two branches now, not one: which voices can read
            // the book, and which braille tables it could have been written in.
            // Gordan's idea, and it saves a control — the alternative was a second
            // language combo saying the same thing beside the first.
            cmbTLanguage.SelectedIndexChanged += (s, e) =>
            {
                TextVoicesForSelection();
                FillInputTablesForSelection();
            };
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
                DimPitchForCloudVoice();
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

            // THE ONE PLACE THE CLOUD VOICES ARE OFFERED, and only when the reader
            // has switched them on over in Settings → Advanced. They are still
            // PLAYABLE either way — the composite knows them regardless, so a book
            // that already has one does not fall silent because the switch is off.
            // What the switch governs is whether they clutter this list for
            // somebody who has no intention of using them.
            if (appSettings == null || !appSettings.UseCloudVoices)
                textCatalog = SettingsForm.WithoutCloudVoices(textCatalog);
            PopulateTextLanguages();

            // The saved name may predate the switch to plain voice names (it could
            // be SAPI's description, "… - English (United States)"), so fall back
            // to matching the bare name — OK then rewrites it in the current form.
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

        /// <summary>The table this book's braille file was READ with, and the way
        /// to have it read again with another.
        ///
        /// <para><b>This is the slot the fake output table used to occupy, and it
        /// is the opposite thing.</b> That one claimed to say how the text became
        /// cells on the way OUT, which NBR cannot do and should not — the screen
        /// reader translates for the display, with the table set in its own
        /// braille settings. This one is the table that turned CELLS INTO TEXT at
        /// import, and it is the only braille table in the app that ever meant
        /// anything. Hence the caption: <b>Input Braille Table</b>, so the two can
        /// never be confused again.</para>
        ///
        /// <para><b>Shown only for a book that came from braille</b> — the whole
        /// group is left out otherwise, rather than greyed, because a book with no
        /// braille file has nothing to re-read and an empty control invites the
        /// question of what it is for.</para>
        ///
        /// <para><b>Nothing happens here.</b> Choosing a table only stages it; the
        /// re-read runs on OK, after a warning. Gordan, 2026-08-04, and the
        /// reasoning is better than the separate button I argued for: with a
        /// button, Cancel cannot undo what it already did, while a staged choice
        /// leaves Cancel meaning exactly what it says.</para></summary>
        private GroupBox BuildTextBrailleGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            box.Text = Localization.T("Prop.Text.InputBrailleGroup");
            box.Location = new Point(x, y);
            box.Size = new Size(452, 56);
            // Named, not numbered: this group is not always on the page, and the
            // skin used to hand out help by position. See DialogSkin.
            box.Tag = "Hint.TextBrailleSource";

            box.Controls.Add(SettingsForm.MakeLabel(
                Localization.T("Prop.Text.InputBrailleTable"), 10, 25));
            cmbTInTable = SettingsForm.MakeCombo(
                Localization.T("Prop.Text.InputBrailleTable"), 210, 22, 232, 0);
            box.Controls.Add(cmbTInTable);
            FillInputTablesForSelection();

            return box;
        }

        /// <summary>Refills the table list for whatever language is selected, and
        /// keeps the book's current table selected if it is still in the list.
        ///
        /// <para>Filtering by language is what makes the list short — measured, 81
        /// of 85 languages have three tables or fewer. Filtering by the DETECTED
        /// language alone would be a trap, though: the language is read off the
        /// text, and when the table is wrong the text is gibberish, so §10g's two
        /// NALIS books are English detected as French. Hanging the list off the
        /// language COMBO is what saves it — the reader changes the language and
        /// the tables follow.</para></summary>
        private void FillInputTablesForSelection()
        {
            if (cmbTInTable == null) return;
            string lang = "";
            if (cmbTLanguage != null && cmbTLanguage.SelectedIndex >= 0
                && cmbTLanguage.SelectedIndex < textLanguageCodes.Count)
                lang = textLanguageCodes[cmbTLanguage.SelectedIndex];
            if (string.IsNullOrEmpty(lang)) lang = book.TextLanguage ?? "";

            inTables = new List<BrailleTableInfo>(BrailleTables.ForLanguage(lang));
            // The table the book is actually on always stands in the list, even
            // when it belongs to another language — otherwise the box would show
            // something the book is not using, which is the one thing it must not.
            BrailleTableInfo cur = BrailleTables.ById(book.BrailleTable);
            if (cur != null && !inTables.Exists(t => t.Id == cur.Id)) inTables.Insert(0, cur);

            cmbTInTable.Items.Clear();
            foreach (BrailleTableInfo t in inTables) cmbTInTable.Items.Add(t.Display);
            int i = cur != null ? inTables.FindIndex(t => t.Id == cur.Id) : -1;
            if (i < 0 && cmbTInTable.Items.Count > 0) i = 0;
            if (i >= 0) cmbTInTable.SelectedIndex = i;
        }

        /// <summary>The table the reader has chosen, or null when nothing changed
        /// — which is what the OK path asks before warning about anything.</summary>
        private BrailleTableInfo ChosenInputTable()
        {
            if (cmbTInTable == null || cmbTInTable.SelectedIndex < 0
                || cmbTInTable.SelectedIndex >= inTables.Count) return null;
            BrailleTableInfo t = inTables[cmbTInTable.SelectedIndex];
            return string.Equals(t.Id, book.BrailleTable, StringComparison.OrdinalIgnoreCase)
                ? null : t;
        }

        private GroupBox BuildTextVisualGroup(int x, int y)
        {
            GroupBox box = new GroupBox();
            // Named for the same reason the braille group is: it sits at a
            // different index depending on whether that group is there at all.
            box.Tag = "Hint.TextVisual";
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
            cmbTHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Line"));
            cmbTHighlight.Items.Add(Localization.T("Settings.TextBooks.Highlight.Sentence"));
            cmbTHighlight.SelectedIndex = book.TextHighlight >= 0 && book.TextHighlight <= 2
                ? book.TextHighlight : 1;
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
            // From the BOOK, not from a constant — these went back to yellow on
            // black at every visit, whatever the reader had chosen last time.
            cmbTHighlightColour.SelectedIndex = ReadingColours.Clamp(book.TextHighlightColour);
            cmbTTextColour.SelectedIndex = ReadingColours.Clamp(book.TextColour);
            cmbTBackColour.SelectedIndex = ReadingColours.Clamp(book.TextBackColour);
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

            // The visual switch stands alone now. It used to be forced on and then
            // disabled whenever braille was ticked, along with the whole group
            // below it — a rule that existed only because there were two switches
            // for one output. With the braille box gone there is one, and it means
            // what it says.
            SettingsForm.SetEnabled(chkTVisual != null && chkTVisual.Checked,
                                    cmbTVisualMode, cmbTHighlight, cmbTHighlightColour,
                                    cmbTTextColour, cmbTBackColour);
            if (chkTVisual != null) chkTVisual.Enabled = true;
            RefreshTextInfo();
        }

        /// <summary>The voice a book with no voice of its own opens on: the
        /// Settings default when it speaks the book's language, otherwise the first
        /// installed voice that does — the shared rule in VoiceChooser, which the
        /// player asks with the same book, so the two cannot disagree.</summary>
        private string DefaultVoiceForLanguage()
        {
            // Empty, not a global default — an unknown language is the one case
            // where nothing may be chosen for the reader. See VoiceChooser.
            string lang = book.TextLanguage;
            if (string.IsNullOrEmpty(lang) || textCatalog == null) return "";

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

        /// <summary>A cloud voice has no pitch, so the control must not pretend
        /// otherwise. Google documents pitch as unavailable for hr-HR and simply
        /// ignores it — and a spin box that moves while nothing changes is worse
        /// than one that plainly cannot be moved.
        ///
        /// <para>Dimmed rather than hidden: a control that vanishes and returns as
        /// the voice changes moves everything below it, and the reader loses their
        /// place. Windows drops a disabled control from the tab order, which here
        /// is the right outcome — there is nothing to set.</para></summary>
        private void DimPitchForCloudVoice()
        {
            if (numTPitch == null || cmbTVoice == null) return;
            string name = cmbTVoice.SelectedIndex > 0 ? (cmbTVoice.SelectedItem as string) : null;
            string google, lang;
            bool cloud = name != null && GoogleCloudVoices.Split(name, out google, out lang);
            numTPitch.Enabled = !cloud;
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
            DimPitchForCloudVoice();
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
            if (cmbTTextColour != null && cmbTTextColour.SelectedIndex >= 0)
                book.TextColour = cmbTTextColour.SelectedIndex;
            if (cmbTBackColour != null && cmbTBackColour.SelectedIndex >= 0)
                book.TextBackColour = cmbTBackColour.SelectedIndex;
            if (cmbTHighlight != null && cmbTHighlight.SelectedIndex >= 0)
                book.TextHighlight = cmbTHighlight.SelectedIndex;
            if (cmbTHighlightColour != null && cmbTHighlightColour.SelectedIndex >= 0)
                book.TextHighlightColour = cmbTHighlightColour.SelectedIndex;
            // Braille writes nothing here. The reading window IS the braille
            // output, so the visual switch above already carries it; and the input
            // table is not saved but ACTED ON, in the OK path, because changing it
            // re-runs the import rather than setting a preference.
            // File the numbers under the voice they were set for — every voice this
            // book has been read with keeps its own, so coming back to one restores
            // it instead of inheriting whatever was used last.
            StageTextPrefs();
            foreach (var kv in stagedTextPrefs.All()) book.TextVoicePrefs.Set(kv.Key, kv.Value);
            // A text book has no playback volume of its own: the player's Volume
            // field IS this speech volume, so keep the two the same number — the
            // player reads book.Volume back when the dialog closes. (On a hybrid
            // book the Audio tab's own field is written afterwards and wins.)
            if (book.TextVolume >= 0) book.Volume = book.TextVolume;

            // book.BrailleTable used to be written here, from the OUTPUT table
            // combo — three lines under a comment in that combo's own group saying
            // in as many words that it must not be. It is the table a .brf was
            // back-translated with at import, and saving this page overwrote it
            // with whatever the output combo showed: on "Detect from the book",
            // index 0, that meant erasing it to "". Both the combo and this write
            // are gone (2026-08-04). The import table is the parser's, and nothing
            // in a dialog may reach in and change what a book was read from.
        }

        /// <summary>Per-book playback level and speed, alongside the processing
        /// stages — they are what the book sounds like just as much as the filters.
        /// The player writes these back as the user adjusts them live, so the dialog
        /// simply shows and edits the stored values.</summary>
        /// <summary>Applies the reading settings to the live reader so the change is
        /// heard at once; the values are only committed on OK.</summary>
        private void PreviewText()
        {
            if (initialising || onTextPreview == null || cmbTVoice == null) return;
            string v = cmbTVoice.SelectedItem != null ? cmbTVoice.SelectedItem.ToString() : "";
            onTextPreview(v, (int)numTWpm.Value, (int)numTVolume.Value, (int)numTPitch.Value);
        }

        /// <summary>The Text tab's read-out: what this book will actually be read
        /// with, and where each value comes from — the book's own setting, or the
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
            string pubT = BookData.NormalizeProducer(book.Publisher);
            info.Add(BookInfoField.Publisher, BookData.WithYear(pubT, book.Year));
            if (pubT.Length == 0) info.Add(BookInfoField.Year, book.Year);
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
            AddDescriptionRow(info);
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

            // The table the book was read with, for a book that came from braille.
            // It belongs on the glass because it is the one fact behind every
            // strange word a reader might be meeting, and the info panel is where
            // NBR states what a book IS.
            if (cmbTInTable != null && cmbTInTable.SelectedItem != null)
                sb.Append(Localization.T("Prop.Text.InputBrailleTable")).Append(' ')
                  .Append(cmbTInTable.SelectedItem).Append(nl);

            sb.Append(Localization.T("Settings.TextBooks.VisualGroup")).Append(": ")
              .Append(Localization.T(chkTVisual != null && chkTVisual.Checked ? "Prop.On" : "Prop.Off"));
            sb.Append(nl);
            if (chkTVisual != null && chkTVisual.Checked && cmbTVisualMode != null && cmbTVisualMode.SelectedItem != null)
                sb.Append(cmbTVisualMode.SelectedItem).Append(nl);

            tbTextInfo.Text = sb.ToString();
        }

        /// <summary>The publisher's blurb, LAST in the column and nowhere else.
        ///
        /// <para>It goes here rather than into a dialog of its own, unlike the
        /// Library's: this column is already a read-only, tabbable, wrapping text
        /// box — the same control shape NBR uses for prose everywhere — so the
        /// paragraph simply belongs in it. The Library could not do that because
        /// its details pane is a two-column GRID, where a paragraph has nowhere
        /// to wrap to.</para>
        ///
        /// <para><b>Last, and that is the whole placement decision.</b> A blurb
        /// runs to about 935 characters; put anywhere above, it would push the
        /// reading settings and the processing read-out off the bottom, and those
        /// are what a reader opens this page FOR. At the foot it costs nothing to
        /// anyone who does not want it and is one arrow key away for anyone who
        /// does.</para></summary>
        /// <summary>The description's door, in the SAME place the Library puts it
        /// (Gordan, 2026-08-07 — "možeš to malo uniformirati?").
        ///
        /// <para>It went in through the BookInfoBuilder rather than being appended
        /// at the foot, which is what makes the two agree: the builder orders by
        /// <see cref="BookInfoField"/>, so one enum decides the position
        /// everywhere and neither page can drift from the other. That is the
        /// convention both columns already state at the top — the book's own
        /// facts in the canonical order first, the page's own business after — and
        /// a description is a fact about the book.</para>
        ///
        /// <para>The reason it was ever last has gone: a paragraph at the foot
        /// would have pushed the reading settings and the processing read-out off
        /// the bottom. A single line saying "press Enter" pushes nothing.</para></summary>
        private void AddDescriptionRow(BookInfoBuilder info)
        {
            if (book == null || !book.HasDescription) return;
            info.Add(BookInfoField.Description, Localization.T("Details.Description.Open"));
        }

        /// <summary>Opens the blurb in the same window the Library uses.
        ///
        /// <para><b>A door here too, after all (Gordan, 2026-08-07).</b> The
        /// description was inline at the foot of this column first, which the
        /// control could carry — it wraps and it scrolls, so nothing was ever cut.
        /// His question was the right one: with sound processing ON there are ten
        /// more lines above it, so a thousand characters sat well below the fold
        /// and had to be scrolled to. A line saying it is there, and a window that
        /// is only the description, beats a paragraph nobody scrolls to. It also
        /// makes the two places agree.</para></summary>
        /// <summary>The control that really has the focus, not the container it
        /// lives in.
        ///
        /// <para><c>Form.ActiveControl</c> would answer "the TabControl" here,
        /// because both info boxes are children of a tab page — LibraryForm hit
        /// exactly this and wrote it down, which is the only reason it did not
        /// cost a second round of "the key does nothing". Descending through each
        /// container's own ActiveControl gets to the leaf.</para></summary>
        private Control DeepActiveControl()
        {
            Control c = this;
            IContainerControl box = c as IContainerControl;
            while (box != null && box.ActiveControl != null)
            {
                c = box.ActiveControl;
                box = c as IContainerControl;
            }
            return c;
        }

        private void OpenDescription()
        {
            if (book == null || !book.HasDescription) return;
            string text = book.Description;
            if (string.IsNullOrWhiteSpace(text)) return;
            string title = Localization.T("Dialog.Description.Title", book.Title ?? "");
            using (var f = new TextHelpForm(title, text, true))
                f.ShowDialog(this);
        }
    }
}
