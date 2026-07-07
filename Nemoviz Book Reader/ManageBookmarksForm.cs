using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Manage Bookmarks dialog. Lists the book's bookmarks as
    /// "Bookmark NN (H:MM)"; numbering and the time shown are always
    /// recomputed live from the working list's sorted position — nothing is
    /// stored as text. OK (button, double-click, or Enter on the list)
    /// commits any staged deletions and jumps to whichever bookmark is
    /// selected — same single-confirmation model as the Go To dialog. If
    /// nothing is selected (e.g. right after a Delete), OK still commits
    /// deletions but does not jump. Delete only stages a removal in the
    /// working copy and clears the selection; nothing is written to disk
    /// until OK. Cancel discards the working copy entirely.
    /// </summary>
    public class ManageBookmarksForm : Form
    {
        private ListBox lstBookmarks;
        private Button btnDelete;
        private Button btnOK;
        private Button btnCancel;
        private Label lblAnnounce;

        private List<double> working;
        private int lastSelectedIndex = -1;

        /// <summary>The bookmark list after any staged deletions. Valid when DialogResult is OK.</summary>
        public List<double> ResultBookmarks
        {
            get { return working; }
        }

        /// <summary>Index into ResultBookmarks selected when OK was confirmed
        /// (button, double-click, or Enter); -1 if nothing was selected, in
        /// which case deletions are still committed but there is no jump.</summary>
        public int PlayIndex { get; private set; } = -1;

        public ManageBookmarksForm(List<double> bookmarks)
        {
            working = new List<double>(bookmarks);

            this.Text = Localization.T("Dialog.ManageBookmarks.Title");
            this.ClientSize = new Size(420, 380);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            lstBookmarks = new ListBox();
            lstBookmarks.Location = new Point(10, 10);
            lstBookmarks.Size = new Size(400, 280);
            lstBookmarks.TabIndex = 0;
            lstBookmarks.AccessibleName = Localization.T("ManageBookmarks.List.Accessible");
            lstBookmarks.DoubleClick += (s, e) => ConfirmOK();
            lstBookmarks.KeyDown += LstBookmarks_KeyDown;

            ContextMenuStrip ctxMenu = new ContextMenuStrip();
            ToolStripMenuItem miDelete = new ToolStripMenuItem(Localization.T("ManageBookmarks.Delete"));
            miDelete.Click += (s, e) => DeleteSelected();
            ctxMenu.Items.Add(miDelete);
            lstBookmarks.ContextMenuStrip = ctxMenu;

            btnDelete = new Button();
            btnDelete.Text = Localization.T("ManageBookmarks.Delete");
            btnDelete.AccessibleName = Localization.T("ManageBookmarks.Delete.Accessible");
            btnDelete.Size = new Size(100, 32);
            btnDelete.Location = new Point(10, 300);
            btnDelete.TabIndex = 1;
            btnDelete.Click += (s, e) => DeleteSelected();

            btnOK = new Button();
            btnOK.Text = Localization.T("Btn.OK");
            btnOK.AccessibleName = Localization.T("ManageBookmarks.OK.Accessible");
            btnOK.Size = new Size(100, 32);
            btnOK.Location = new Point(150, 300);
            btnOK.TabIndex = 2;
            btnOK.Click += (s, e) => ConfirmOK();

            btnCancel = new Button();
            btnCancel.Text = Localization.T("Btn.Cancel");
            btnCancel.AccessibleName = Localization.T("ManageBookmarks.Cancel.Accessible");
            btnCancel.Size = new Size(100, 32);
            btnCancel.Location = new Point(290, 300);
            btnCancel.TabIndex = 3;
            btnCancel.DialogResult = DialogResult.Cancel;

            // Off-screen label for screen-reader announcements (selection
            // toggle), same pattern as the player's own announce labels.
            lblAnnounce = new Label();
            lblAnnounce.Location = new Point(-600, -600);
            lblAnnounce.Size = new Size(10, 10);
            lblAnnounce.TabStop = false;

            this.Controls.Add(lstBookmarks);
            this.Controls.Add(btnDelete);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);
            this.Controls.Add(lblAnnounce);

            this.CancelButton = btnCancel;

            RefreshList(working.Count > 0 ? 0 : -1);
        }

        private void LstBookmarks_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                ConfirmOK();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Space)
            {
                ToggleSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        /// <summary>Ctrl+Space: deselects the current row (so OK won't jump
        /// anywhere) or, if nothing is selected, re-selects the last one that
        /// was — a reversible, keyboard-only way to "arm"/"disarm" the jump
        /// without touching Delete. Announced via the off-screen label.</summary>
        private void ToggleSelection()
        {
            if (lstBookmarks.SelectedIndex >= 0)
            {
                lastSelectedIndex = lstBookmarks.SelectedIndex;
                lstBookmarks.SelectedIndex = -1;
                AnnounceToScreenReader(Localization.T("ManageBookmarks.NotSelected"));
            }
            else if (lastSelectedIndex >= 0 && lastSelectedIndex < working.Count)
            {
                lstBookmarks.SelectedIndex = lastSelectedIndex;
                AnnounceToScreenReader(Localization.T("ManageBookmarks.Selected"));
            }
        }

        private void AnnounceToScreenReader(string text)
        {
            Control focusBefore = this.ActiveControl;
            lblAnnounce.Text = text;
            lblAnnounce.Focus();

            System.Windows.Forms.Timer restoreFocus = new System.Windows.Forms.Timer();
            restoreFocus.Interval = 150;
            restoreFocus.Tick += (s, ev) =>
            {
                restoreFocus.Stop();
                restoreFocus.Dispose();
                if (focusBefore != null && !focusBefore.IsDisposed)
                    focusBefore.Focus();
            };
            restoreFocus.Start();
        }

        /// <summary>Commits any staged deletions and, if a bookmark is
        /// currently selected, jumps to it. Sets DialogResult.OK either way
        /// — the caller only jumps when PlayIndex is >= 0.</summary>
        private void ConfirmOK()
        {
            PlayIndex = lstBookmarks.SelectedIndex;
            this.DialogResult = DialogResult.OK;
        }

        /// <summary>Stages a deletion (not written to disk until OK) and
        /// clears the selection — the user must deliberately pick a
        /// bookmark again before OK will jump anywhere.</summary>
        private void DeleteSelected()
        {
            int index = lstBookmarks.SelectedIndex;
            if (index < 0) return;
            working.RemoveAt(index);
            RefreshList(-1);
        }

        /// <summary>Rebuilds the visible list from the working copy — numbering
        /// and the H:MM time are always derived fresh from sorted position,
        /// so a deletion immediately renumbers the remaining entries.</summary>
        private void RefreshList(int selectIndex)
        {
            lstBookmarks.Items.Clear();
            int width = working.Count > 99 ? 3 : 2;

            for (int i = 0; i < working.Count; i++)
            {
                string number = (i + 1).ToString("D" + width);
                TimeSpan t = TimeSpan.FromSeconds(working[i]);
                string time = string.Format("{0:D2}:{1:D2}", (int)t.TotalHours, t.Minutes);
                lstBookmarks.Items.Add(Localization.T("Bookmark.Item.Format", number, time));
            }

            if (selectIndex >= 0 && selectIndex < working.Count)
                lstBookmarks.SelectedIndex = selectIndex;
        }
    }
}
