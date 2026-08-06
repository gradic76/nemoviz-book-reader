using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>A yes/no question the reader can switch off once they have met it.
    ///
    /// <para><b>Why not <c>MessageForm.ShowConfirm</c>.</b> That one is a real
    /// <c>MessageBox</c> on purpose — "a question is a notice too… always the real
    /// thing", so every screen reader's built-in handling of a system dialog
    /// applies. A system dialog cannot carry a check box, and this question needs
    /// one: a reader hunting for the right braille table meets it several times in
    /// a row, and Gordan's call (2026-08-04) is that they should be able to stop
    /// it — *"kroz par puta će se naučiti i isključiti"*.</para>
    ///
    /// <para>So it is built the way <c>ArchivePasswordPrompt</c> is: ordinary
    /// controls, keyboard-reachable, nothing drawn. The message is a read-only
    /// multiline TextBox rather than a Label, for the reason the hint system
    /// already had to learn — a reader driven by Tab never visits a Label, and
    /// this text is the whole point of the dialog.</para></summary>
    internal sealed class ConfirmOnceForm : Form
    {
        private readonly CheckBox chkDontAsk;

        private ConfirmOnceForm(string text, string title, string dontAskLabel)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(470, 240);

            var body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.BackColor = SystemColors.Window;
            body.Text = text;
            body.SetBounds(14, 14, 442, 130);
            body.TabStop = true;
            body.TabIndex = 0;
            body.AccessibleName = title;
            Controls.Add(body);

            chkDontAsk = new CheckBox();
            chkDontAsk.Text = dontAskLabel;
            chkDontAsk.AccessibleName = dontAskLabel;
            chkDontAsk.SetBounds(14, 156, 442, 24);
            chkDontAsk.TabIndex = 1;
            Controls.Add(chkDontAsk);

            var ok = new Button();
            ok.Text = Localization.T("Btn.Continue");
            ok.AccessibleName = Localization.T("Btn.Continue");
            ok.SetBounds(256, 194, 96, 30);
            ok.TabIndex = 2;
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = Localization.T("Btn.Cancel");
            cancel.AccessibleName = Localization.T("Btn.Cancel");
            cancel.SetBounds(360, 194, 96, 30);
            cancel.TabIndex = 3;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            // Focus starts on the question, not on the check box and not on a
            // button: the reader is being asked something, and the something is
            // the text.
            ActiveControl = body;
        }

        /// <summary>Asks, unless it has been switched off. Returns true to go
        /// ahead. <paramref name="dontAskAgain"/> comes back true when the reader
        /// ticked the box, and it is the CALLER that persists that — this dialog
        /// knows nothing about settings.</summary>
        public static bool Ask(IWin32Window owner, string text, string title,
                               out bool dontAskAgain)
        {
            dontAskAgain = false;
            using (var f = new ConfirmOnceForm(text, title,
                       Localization.T("Dialog.DontShowAgain")))
            {
                bool go = f.ShowDialog(owner) == DialogResult.OK;
                // Only honoured when they went ahead. Ticking "don't ask again"
                // and then cancelling is not a decision to skip the warning next
                // time; it is a decision not to do this.
                if (go) dontAskAgain = f.chkDontAsk.Checked;
                return go;
            }
        }
    }
}
