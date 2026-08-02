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
        /// <summary><paramref name="wrap"/> decides which kind of page this is.
        /// Prose wraps to the window; a page whose lines are laid out in columns
        /// (a table of symbols, examples lined up under each other) must not, or
        /// the columns fold into each other at the first narrow window. The caller
        /// knows which it wrote, so the caller says.</summary>
        public TextHelpForm(string title, string body, bool wrap = false)
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
            text.ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both;
            text.WordWrap = wrap;
            text.Font = wrap ? SystemFonts.MessageBoxFont
                             : new Font(FontFamily.GenericMonospace, 9);
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

            // Wrapped prose is measured and the window closes down onto it, up to
            // the full page. A column-formatted page keeps the full height,
            // because measuring it would only ever say "as tall as it is".
            if (wrap)
            {
                Size need = TextRenderer.MeasureText(text.Text, text.Font,
                                                     new Size(text.Width - 24, int.MaxValue),
                                                     TextFormatFlags.WordBreak);
                int h = Math.Max(120, Math.Min(400, need.Height + 16));
                text.Height = h;
                this.ClientSize = new Size(620, h + 70);
            }

            Button close = new Button();
            close.Text = Localization.T("Btn.Close");
            close.AccessibleName = Localization.T("Btn.Close");
            close.Size = new Size(100, 32);
            close.Location = new Point(510, text.Bottom + 8);
            close.TabIndex = 1;
            close.DialogResult = DialogResult.Cancel;

            this.Controls.Add(text);
            this.Controls.Add(close);
            this.AcceptButton = close;
            this.CancelButton = close;
        }
    }
}
