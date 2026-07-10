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

        public GoToForm(string[] partNames, int currentIndex, bool autoPlayDefault, bool plainItems = false)
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
            chkAutoPlay.TabIndex = 1;
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
            tbAutoPlayHint.TabIndex = 2;
            tbAutoPlayHint.Location = new Point(10, 284);
            tbAutoPlayHint.Size = new Size(400, 42);
            tbAutoPlayHint.Text = Localization.T("GoTo.AutoPlay.Hint");
            tbAutoPlayHint.AccessibleName = Localization.T("GoTo.Hint.Accessible");

            btnOK = new Button();
            btnOK.Text = Localization.T("Btn.OK");
            btnOK.AccessibleName = Localization.T("GoTo.OK.Accessible");
            btnOK.Size = new Size(120, 32);
            btnOK.Location = new Point(160, 338);
            btnOK.TabIndex = 3;
            btnOK.DialogResult = DialogResult.OK;

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("GoTo.Cancel.Accessible");
            btnCancel.Size = new Size(120, 32);
            btnCancel.Location = new Point(290, 338);
            btnCancel.TabIndex = 4;
            btnCancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(lstParts);
            this.Controls.Add(chkAutoPlay);
            this.Controls.Add(tbAutoPlayHint);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);

            // Enter anywhere = OK, Escape anywhere = Cancel.
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }
    }
}