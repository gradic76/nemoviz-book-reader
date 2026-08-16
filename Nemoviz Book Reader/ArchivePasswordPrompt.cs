using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Accessible modal that asks for a password to open an encrypted
    /// archive during import. Returns the entered password, or null if the user
    /// cancels (or leaves it blank). The password is handed straight to the
    /// extractor and lives only in memory for that call — it is never stored,
    /// written to Book.ini/Settings.ini, or logged.</summary>
    public static class ArchivePasswordPrompt
    {
        /// <param name="retry">true after a wrong password, to switch the
        /// prompt text from "enter" to "that didn't work, try again".</param>
        public static string Show(IWin32Window owner, string archiveName, bool retry)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = Localization.T("Dialog.ArchivePassword.Title");
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(430, 132);

                Label lbl = new Label();
                lbl.Text = Localization.T(
                    retry ? "Dialog.ArchivePassword.Retry" : "Dialog.ArchivePassword.Prompt",
                    archiveName);
                lbl.Location = new Point(12, 12);
                lbl.Size = new Size(406, 40);

                TextBox tb = new TextBox();
                tb.Location = new Point(12, 58);
                tb.Size = new Size(406, 24);
                tb.UseSystemPasswordChar = true;
                tb.AccessibleName = Localization.T("Dialog.ArchivePassword.Field");

                Button ok = new Button();
                ok.Text = Localization.T("Btn.OK");
                ok.Size = new Size(100, 30);
                ok.Location = new Point(208, 92);
                ok.DialogResult = DialogResult.OK;

                Button cancel = new Button();
                cancel.Text = Localization.T("Btn.Cancel");
                cancel.Size = new Size(100, 30);
                cancel.Location = new Point(318, 92);
                cancel.DialogResult = DialogResult.Cancel;

                dlg.Controls.Add(lbl);
                dlg.Controls.Add(tb);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                // One layout pass for both looks — see DialogSkin.Painting.
                WorkDialogSkin.ApplyPassword(dlg, lbl, tb, ok, cancel);

                // FOCUS STARTS IN THE PASSWORD FIELD, and it has to be said here
                // rather than left to the tab order. Under the new look the skin
                // turns the message into a read-only TextBox — which is right,
                // because a reader driven by Tab never visits a Label — but that
                // box is focusable and comes first, so focus landed on the
                // MESSAGE. Gordan typed six passwords into it, pressed Enter, and
                // got six books skipped as "cancelled": the characters went
                // nowhere, Enter fired the accept button, and an empty field
                // reads as a cancel further down. Nothing about that was visible
                // or audible at the time.
                dlg.Shown += (s, e) => { try { tb.Focus(); tb.SelectAll(); } catch { } };

                // ONE RULE, and it is Gordan's (2026-08-10): anything you CONFIRM
                // is an attempt, and only Cancel is giving up. So an empty field
                // comes back as an empty string and is tried like any other
                // password — it fails, and the next prompt says "that password
                // didn't work", which is true and is what a wrong one says.
                //
                // My first version refused to close on an empty field instead.
                // That reads as helpful and sets a trap: somebody who presses OK
                // on an empty box meaning to move on is held in a window that
                // will not let them out. One rule beats a special case.
                //
                // null therefore means exactly one thing to the caller — Cancel,
                // Escape, or the window closed — see LibraryScanner.ExtractArchive.
                if (dlg.ShowDialog(owner) != DialogResult.OK)
                    return null;
                return tb.Text;
            }
        }
    }
}
