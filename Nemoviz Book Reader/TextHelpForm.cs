using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// A small help window: a title and a page of text, nothing else. The text
    /// sits in a read-only multiline TextBox — tabbable, so a screen reader reads
    /// it on focus and the user can arrow through it line by line, which a Label
    /// would not allow. Escape closes it.
    ///
    /// Deliberately generic: the in-app Help will want the same shape.
    /// </summary>
    public class TextHelpForm : Form
    {
        public TextHelpForm(string title, string body)
        {
            this.Text = title;
            this.ClientSize = new Size(620, 460);
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;

            TextBox text = new TextBox();
            text.Multiline = true;
            text.ReadOnly = true;
            text.ScrollBars = ScrollBars.Both;
            text.WordWrap = false;          // the examples are laid out in columns
            text.Font = new Font(FontFamily.GenericMonospace, 9);
            text.BackColor = SystemColors.Window;
            text.Location = new Point(10, 10);
            text.Size = new Size(600, 400);
            text.TabIndex = 0;
            text.AccessibleName = title;
            // A multiline TextBox only breaks lines on CRLF; the language file
            // carries plain \n, which would otherwise render as one long line
            // with boxes in it.
            text.Text = (body ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            text.Select(0, 0);

            Button close = new Button();
            close.Text = Localization.T("Btn.Close");
            close.AccessibleName = Localization.T("Btn.Close");
            close.Size = new Size(100, 32);
            close.Location = new Point(510, 418);
            close.TabIndex = 1;
            close.DialogResult = DialogResult.Cancel;

            this.Controls.Add(text);
            this.Controls.Add(close);
            this.AcceptButton = close;
            this.CancelButton = close;
        }
    }
}
