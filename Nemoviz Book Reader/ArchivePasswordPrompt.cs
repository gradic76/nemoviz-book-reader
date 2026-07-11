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

                if (dlg.ShowDialog(owner) != DialogResult.OK)
                    return null;
                string password = tb.Text;
                return string.IsNullOrEmpty(password) ? null : password;
            }
        }
    }
}
