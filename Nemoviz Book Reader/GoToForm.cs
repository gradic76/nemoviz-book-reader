using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// "Go To" dialog — navigation level 4. For plain multi-file audio it
    /// lists the book's parts ("N/M — file name"); arrows to pick, Enter to
    /// jump to the start of the selected part, Escape to cancel. Opens with
    /// the current part selected. DAISY/text books will later get structural
    /// lists here (headings by level, pages), depending on the format.
    /// </summary>
    public class GoToForm : Form
    {
        private ListBox lstParts;
        private CheckBox chkAutoPlay;
        private TextBox tbAutoPlayHint;
        private Button btnOK;
        private Button btnCancel;
        private GroupBox grpPage;
        private NumericUpDown numPage;
        private bool pageTouched;

        /// <summary>Index of the part chosen by the user (valid when DialogResult is OK).</summary>
        public int SelectedPartIndex
        {
            get { return lstParts.SelectedIndex; }
        }

        /// <summary>When checked, playback starts after the jump even if the
        /// player was paused. Unchecked (default) keeps the standard
        /// behavior: the playback state is preserved.</summary>
        public bool AutoPlayChecked
        {
            get { return chkAutoPlay.Checked; }
        }

        /// <summary>The printed page the user asked for, valid only when
        /// <see cref="PageChosen"/> is true.</summary>
        public int SelectedPage
        {
            get { return numPage != null ? (int)numPage.Value : 0; }
        }

        /// <summary>True when the jump should go to a PAGE rather than to the
        /// selected row of the list.
        ///
        /// <para>Two ways to mean it, because the dialog has two ways to be
        /// confirmed: the reader typed or stepped a number, or the number box
        /// is where they were standing when they pressed Enter. Merely opening
        /// the dialog does not count — the box starts on the page you are
        /// already on, so an untouched box means "I did not come here for
        /// this".</para></summary>
        public bool PageChosen
        {
            get { return numPage != null && numPage.Visible && (pageTouched || numPage.Focused); }
        }

        /// <param name="pageNumbers">The book's PRINTED page numbers, ascending,
        /// or null when it has none — then the group is not built at all. Only
        /// numeric labels belong here: measured across 400 EPUBs and the whole
        /// braille corpus, 98–100 % of page labels are plain numbers and the rest
        /// are roman numerals on the front matter, which are reached with the
        /// Page seek step rather than by typing.</param>
        /// <param name="currentPage">The page the reader is on, which is what the
        /// box opens on.</param>
        public GoToForm(string[] partNames, int currentIndex, bool autoPlayDefault, bool plainItems = false,
                        int[] pageNumbers = null, int currentPage = 0)
        {
            this.Text = Localization.T("Dialog.GoTo.Title");
            this.ClientSize = new Size(420, 380);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            lstParts = new ListBox();
            lstParts.Location = new Point(10, 10);
            lstParts.Size = new Size(400, 240);
            lstParts.TabIndex = 0;
            lstParts.AccessibleName = Localization.T("GoTo.List.Accessible");
            // Plain mode (DAISY headings): show the names as-is — each heading
            // name already carries its own numbering/description, so the
            // "N/M — " prefix would just be noise. Parts use the numbered format.
            for (int i = 0; i < partNames.Length; i++)
                lstParts.Items.Add(plainItems
                    ? partNames[i]
                    : Localization.T("GoTo.Item.Format", i + 1, partNames.Length, partNames[i]));
            if (partNames.Length > 0)
                lstParts.SelectedIndex = Math.Max(0, Math.Min(currentIndex, partNames.Length - 1));
            // Double-click = same as OK (mouse/visual mode).
            lstParts.DoubleClick += (s, e) => { this.DialogResult = DialogResult.OK; };

            chkAutoPlay = new CheckBox();
            chkAutoPlay.Text = Localization.T("GoTo.AutoPlay");
            chkAutoPlay.AccessibleName = Localization.T("GoTo.AutoPlay");
            chkAutoPlay.Location = new Point(10, 258);
            chkAutoPlay.Size = new Size(400, 22);
            chkAutoPlay.TabIndex = 3;
            // Initial state comes from the global setting (Settings.ini);
            // the caller saves the new state when the dialog is confirmed.
            chkAutoPlay.Checked = autoPlayDefault;

            // Read-only hint box explaining the control above it. The same
            // pattern is planned for the Settings window; a global on/off
            // switch for hints (toggling Visible + TabStop live) will come
            // with Settings — until then the hint is always shown.
            tbAutoPlayHint = new TextBox();
            tbAutoPlayHint.Multiline = true;
            tbAutoPlayHint.ReadOnly = true;
            tbAutoPlayHint.TabStop = true;
            tbAutoPlayHint.TabIndex = 4;
            tbAutoPlayHint.Location = new Point(10, 284);
            tbAutoPlayHint.Size = new Size(400, 42);
            tbAutoPlayHint.Text = Localization.T("GoTo.AutoPlay.Hint");
            tbAutoPlayHint.AccessibleName = Localization.T("GoTo.Hint.Accessible");

            btnOK = new Button();
            btnOK.Text = Localization.T("Btn.OK");
            btnOK.AccessibleName = Localization.T("GoTo.OK.Accessible");
            btnOK.Size = new Size(120, 32);
            btnOK.Location = new Point(160, 338);
            btnOK.TabIndex = 5;
            btnOK.DialogResult = DialogResult.OK;

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("GoTo.Cancel.Accessible");
            btnCancel.Size = new Size(120, 32);
            btnCancel.Location = new Point(290, 338);
            btnCancel.TabIndex = 6;
            btnCancel.DialogResult = DialogResult.Cancel;

            // GO TO PAGE — a group of its own, and only for a book that HAS
            // printed pages. Gordan's shape (beta notes): the group holds
            // nothing but a spin box opening on the page you are on, with its
            // number selected so typing replaces it, and Enter confirms exactly
            // as it does for the list.
            //
            // Why a spin box on the PRINTED number rather than a running count:
            // measured across 400 EPUBs and the whole braille corpus, 98–100 %
            // of page labels are plain numbers, and a reader looking for "page
            // 231" means the one printed on the paper. The few roman-numeral
            // pages of front matter cannot be typed here, and are reached with
            // the Page seek step, which walks every marker whatever it says.
            if (pageNumbers != null && pageNumbers.Length > 0)
            {
                grpPage = new GroupBox();
                grpPage.Text = Localization.T("GoTo.Page.Group");
                grpPage.TabStop = false;
                // Between the list and the auto-play check, so the reader meets
                // the pages where they SEE them rather than at the end.
                grpPage.TabIndex = 1;

                numPage = new NumericUpDown();
                numPage.Minimum = pageNumbers[0];
                numPage.Maximum = pageNumbers[pageNumbers.Length - 1];
                numPage.Value = Math.Max(numPage.Minimum, Math.Min(numPage.Maximum, currentPage));
                numPage.AccessibleName = Localization.T("GoTo.Page.Accessible");
                numPage.TabIndex = 0;
                numPage.ValueChanged += (s, e) => pageTouched = true;
                // Auto-select on arrival, so a typed number replaces the current
                // page instead of being appended to it.
                numPage.Enter += (s, e) => numPage.Select(0, numPage.Text.Length);
                grpPage.Controls.Add(numPage);
                this.Controls.Add(grpPage);
            }

            this.Controls.Add(lstParts);
            this.Controls.Add(chkAutoPlay);
            this.Controls.Add(tbAutoPlayHint);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);

            // Enter anywhere = OK, Escape anywhere = Cancel.
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            // One layout pass for both looks — see DialogSkin.Painting.
            WorkDialogSkin.ApplyGoTo(this);
        }

        internal GoToParts SkinParts
        {
            get
            {
                return new GoToParts
                {
                    List = lstParts,
                    AutoPlay = chkAutoPlay,
                    AutoPlayHint = tbAutoPlayHint,
                    OK = btnOK,
                    Cancel = btnCancel,
                    PageGroup = grpPage,
                    PageBox = numPage,
                };
            }
        }
    }
}