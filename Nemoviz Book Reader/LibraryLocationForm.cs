using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>Asked ONCE, on the first run after installing, when the reader did
    /// not choose a library folder during the installation.
    ///
    /// <para><b>The manual has promised this since it was written</b> — hr.txt:
    /// *"Pri prvom pokretanju Nemoviz Book Reader provjerit će želite li zadržati
    /// ili promijeniti putanju mape za vašu buduću knjižnicu."* It had never been
    /// built, which is why neither Gordan nor the reader who mentioned it could
    /// reproduce it. So this is missing behaviour rather than a new capability.</para>
    ///
    /// <para><b>Why it is worth asking at all.</b> The default sits in the Windows
    /// user profile, which is usually on C:, and a library fills a disk over the
    /// years. The readers who most need that warning are the ones who will never
    /// open the manual. And the first run is the ONLY cheap moment:
    /// <c>AppSettings.SetLibraryPath</c> writes the new path and moves nothing, so
    /// changing it later leaves the books behind in the old folder — which is why
    /// the text says plainly that moving them is the reader's own job.</para>
    ///
    /// <para><b>Three buttons, Gordan's shape.</b> Browse picks a folder and only
    /// STAGES it; Change applies it and closes; Escape and Cancel leave everything
    /// as it was. Change is disabled until a folder has actually been picked,
    /// because a button that applies nothing is worse than one that is not there —
    /// and being disabled it is skipped in the tab order, so a reader meets it
    /// exactly when it has become real.</para>
    ///
    /// <para>Built like <c>ConfirmOnceForm</c> and <c>ArchivePasswordPrompt</c>:
    /// ordinary controls, nothing drawn, and the message is a read-only multiline
    /// TextBox rather than a Label, because a reader driven by Tab never visits a
    /// Label and here the text is the whole dialog.</para></summary>
    internal sealed class LibraryLocationForm : Form
    {
        private readonly TextBox body;
        private readonly Button change;
        private string picked;

        private LibraryLocationForm(string currentPath)
        {
            picked = null;

            string title = Localization.T("Dialog.LibraryLocation.Title");
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(500, 250);

            body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.BackColor = SystemColors.Window;
            body.SetBounds(14, 14, 472, 168);
            body.TabStop = true;
            body.TabIndex = 0;
            body.AccessibleName = title;
            Controls.Add(body);

            var browse = new Button();
            browse.Text = Localization.T("Dialog.LibraryLocation.Browse");
            browse.AccessibleName = browse.Text;
            browse.SetBounds(14, 200, 130, 30);
            browse.TabIndex = 1;
            browse.Click += Browse_Click;
            Controls.Add(browse);

            change = new Button();
            change.Text = Localization.T("Dialog.LibraryLocation.Change");
            change.AccessibleName = change.Text;
            change.SetBounds(254, 200, 110, 30);
            change.TabIndex = 2;
            change.Enabled = false;
            change.DialogResult = DialogResult.OK;
            Controls.Add(change);

            var cancel = new Button();
            cancel.Text = Localization.T("Btn.Cancel");
            cancel.AccessibleName = cancel.Text;
            cancel.SetBounds(376, 200, 110, 30);
            cancel.TabIndex = 3;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            CancelButton = cancel;
            ShowText(currentPath);
        }

        /// <summary>The path in the message is the one that would be used if the
        /// reader pressed Change now, so it follows the picker rather than staying
        /// on the old folder — otherwise the dialog would be describing a choice
        /// the reader has already replaced.</summary>
        private void ShowText(string path)
        {
            body.Text = Localization.T("Dialog.LibraryLocation.Text", path);
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = Localization.T("Dialog.LibraryLocation.Picker");
                fbd.ShowNewFolderButton = true;
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrEmpty(fbd.SelectedPath)) return;
                picked = fbd.SelectedPath;
                ShowText(picked);
                change.Enabled = true;
                change.Focus();
            }
        }

        /// <summary>Shows the question and returns the folder the reader chose, or
        /// null when they left it alone. The caller writes the setting — this
        /// dialog changes nothing by itself, so an exception on the way out cannot
        /// leave the library pointing somewhere nobody asked for.</summary>
        public static string Ask(string currentPath)
        {
            try
            {
                using (var f = new LibraryLocationForm(currentPath))
                {
                    if (f.ShowDialog() != DialogResult.OK) return null;
                    if (string.IsNullOrEmpty(f.picked)) return null;
                    if (string.Equals(Path.GetFullPath(f.picked).TrimEnd('\\'),
                                      Path.GetFullPath(currentPath).TrimEnd('\\'),
                                      StringComparison.OrdinalIgnoreCase)) return null;
                    return f.picked;
                }
            }
            catch { return null; }
        }
    }
}
