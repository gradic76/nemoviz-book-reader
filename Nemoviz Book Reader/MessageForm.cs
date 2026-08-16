using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// One shared design for every "a sentence or two, and a way out" dialog in
    /// the app (Gordan, 2026-07-29): the per-group hint pop-up (see
    /// <c>HintSystem</c>) and all of what used to be plain <see
    /// cref="MessageBox"/> calls — 21 of them, spread across the Library, the
    /// player and two other dialogs, each with its own icon and button
    /// combination. One dialog, changed only by its text and its buttons.
    ///
    /// <para><b>BOTH LOOKS TAKE THE SAME ROUTE</b> (Gordan, 2026-08-16: *"Kako se
    /// ponaša skin, tako se ponaša i classic. To je pravilo za sve."*). This used
    /// to say the classic theme fell straight back to a real <see
    /// cref="MessageBox"/> for everything, which was both a divergence and an
    /// imprecise description of one: <see cref="ShowInfo"/> and <see
    /// cref="ShowConfirm"/> are a real <c>MessageBox</c> in <b>both</b> looks and
    /// always were — a notice is an event, not material — so they were never the
    /// difference. Only <see cref="ShowHint"/> and <see cref="ShowContinue"/> had
    /// two paths, and now they have one. Under classic the shell simply does not
    /// paint (see <see cref="DialogSkin.Painting"/>): same window, same controls,
    /// same words, in the Windows theme's own colours.</para>
    ///
    /// <para><b>The message box GROWS DIAGONALLY with its text, then only
    /// taller</b> (Gordan's call): width and height scale together up to a
    /// width capped at a comfortable reading line, and once that cap is hit,
    /// only the height keeps growing. A short confirmation ("Delete this
    /// book?") never reaches the cap and every such dialog ends up the same
    /// size — it is only the rare long message that grows tall rather than
    /// wide.</para>
    /// </summary>
    internal static class MessageForm
    {
        private const int MinW = 360, MinH = 170;
        private const int CapW = 640;
        private const int OuterPad = 12;   // casing edge to the well, matching Margin elsewhere
        private const int WellPad = 17;    // well to the glass inside it, matching InfoPanel/InfoGlass
        private const int Gap = 16;        // well to the button row
        private const int ButtonGap = 12;

        /// <summary>A HINT: the skinned box, with the text in a field the reader
        /// can walk through.
        ///
        /// <para>This is what the shell was really for (Gordan, 2026-08-02). A
        /// hint is several sentences that may want reading slowly, twice, or a
        /// line at a time — so it belongs in a readable field rather than in a
        /// system dialog that speaks once and expects an answer.</para></summary>
        public static void ShowHint(IWin32Window owner, string text, string title)
        {
            using (Form f = Build(text, title, false))
                f.ShowDialog(owner);
        }

        /// <summary>A NOTICE: an ordinary system message box, always.
        ///
        /// <para>"Book added", "book deleted", an import that failed — a sentence
        /// you hear once and dismiss. Putting those in a readable field was going
        /// too far (Gordan): there is nothing to study, and a real MessageBox is
        /// what every screen reader already handles best, announced and dismissed
        /// without anyone having to learn a new shape.</para>
        ///
        /// <para>The distinction is what is being said, not how it looks. A hint
        /// is material; a notice is an event.</para></summary>
        public static void ShowInfo(IWin32Window owner, string text, string title)
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Yes/No. Returns true for Yes — matches the boolean the
        /// caller actually wants, rather than a DialogResult nobody outside
        /// this file needs to know about. <paramref name="defaultToNo"/> exists
        /// because the 21 calls this replaces did not agree with each other —
        /// some already defaulted Enter to the safe answer, some did not — and
        /// that is each call site's own decision to preserve or revisit, not
        /// something to flatten quietly while only changing how this looks.</summary>
        public static bool ShowConfirm(IWin32Window owner, string text, string title, bool defaultToNo = false)
        {
            // A question is a notice too, and the same reasoning applies: "are you
            // sure?" is answered, not studied. Always the real thing.
            var def = defaultToNo ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1;
            return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, def) == DialogResult.Yes;
        }

        /// <summary>A question whose answer is not "yes" — <b>Continue</b> and
        /// <b>Cancel</b> rather than Yes and No, for the one case where the
        /// dialog is several lines of numbers to weigh rather than a single
        /// sentence to agree with (the bulk import warning).
        ///
        /// <para>Windows cannot relabel the buttons of a real message box, which
        /// is why this is our own window rather than one: it says <b>Continue</b>
        /// and <b>Cancel</b>, the words that were asked for. The classic look used
        /// to fall back to a real box and settle for <c>OK / Cancel</c>; under
        /// Gordan's parity rule it no longer does, so both looks now ask the
        /// question in the same words.</para></summary>
        public static bool ShowContinue(IWin32Window owner, string text, string title)
        {
            using (Form f = Build(text, title, true, false, "Btn.Continue", "Btn.Cancel"))
                return f.ShowDialog(owner) == DialogResult.Yes;
        }

        private static Form Build(string text, string title, bool confirm, bool defaultToNo = false,
                                  string primaryKey = null, string secondaryKey = null)
        {
            // A multiline TextBox breaks lines on CRLF and NOTHING else: a bare
            // \n out of a language file draws a box glyph and runs the paragraphs
            // together. Done here, before the text is both measured and shown, so
            // the two cannot disagree about how many lines there are.
            text = (text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            DialogSkin.EnsureFonts();
            Size size = ComputeSize(text);
            int w = size.Width, h = size.Height;

            var f = new Form();
            f.Text = title;
            f.ShowInTaskbar = false;
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.StartPosition = FormStartPosition.CenterParent;

            DialogCanvas canvas = DialogSkin.Shell(f, w, h);

            int buttonRowY = h - OuterPad - DialogSkin.ButtonH;
            var well = new Rectangle(OuterPad, OuterPad, w - 2 * OuterPad, buttonRowY - Gap - OuterPad);
            canvas.Wells.Add(well);

            TextBox msg = DialogSkin.NewMessageBox(text);
            msg.TabIndex = 0;
            var glassRect = new Rectangle(well.X + WellPad, well.Y + WellPad,
                well.Width - 2 * WellPad, well.Height - 2 * WellPad);
            DialogSkin.AsGlass(msg, glassRect);
            f.Controls.Add(msg);

            Button primary = new Button();
            primary.Text = Localization.T(primaryKey ?? (confirm ? "Btn.Yes" : "Btn.OK"));
            primary.DialogResult = confirm ? DialogResult.Yes : DialogResult.OK;
            primary.TabIndex = 1;

            if (confirm)
            {
                Button secondary = new Button();
                secondary.Text = Localization.T(secondaryKey ?? "Btn.No");
                secondary.DialogResult = DialogResult.No;
                secondary.TabIndex = 2;

                DialogSkin.AsKey(primary, new Rectangle(
                    w - OuterPad - 2 * DialogSkin.ButtonW - ButtonGap, buttonRowY,
                    DialogSkin.ButtonW, DialogSkin.ButtonH));
                DialogSkin.AsKey(secondary, new Rectangle(
                    w - OuterPad - DialogSkin.ButtonW, buttonRowY,
                    DialogSkin.ButtonW, DialogSkin.ButtonH));

                f.Controls.Add(primary);
                f.Controls.Add(secondary);
                // Enter activates whichever button is the DEFAULT — Yes normally,
                // No when the call site says this one is destructive. Escape is
                // always No, regardless: Esc means "get me out", never "confirm".
                f.AcceptButton = defaultToNo ? secondary : primary;
                f.CancelButton = secondary;
            }
            else
            {
                DialogSkin.AsKey(primary, new Rectangle(
                    w - OuterPad - DialogSkin.ButtonW, buttonRowY,
                    DialogSkin.ButtonW, DialogSkin.ButtonH));
                f.Controls.Add(primary);
                f.AcceptButton = primary;
                f.CancelButton = primary;   // Esc dismisses an info box too
            }

            canvas.Rebuild();
            return f;
        }

        /// <summary>Grows diagonally from a baseline most messages never leave,
        /// up to a width cap chosen for a comfortable reading line — past that,
        /// only the height grows. Measured with the same font the glass renders
        /// in, so what is computed here is what actually gets shown.</summary>
        private static Size ComputeSize(string text)
        {
            int fixedChrome = 2 * OuterPad + 2 * WellPad + Gap + DialogSkin.ButtonH;
            int w = MinW, h = MinH;

            int textW = w - 2 * OuterPad - 2 * WellPad;
            int needed = MeasureHeight(text, textW) + fixedChrome;
            if (needed <= h) return new Size(w, h);

            double aspect = (double)MinH / MinW;
            while (true)
            {
                w += 24;
                if (w >= CapW) { w = CapW; break; }
                textW = w - 2 * OuterPad - 2 * WellPad;
                needed = MeasureHeight(text, textW) + fixedChrome;
                int aspectH = (int)Math.Round(w * aspect);
                if (needed <= aspectH) { h = aspectH; return new Size(w, h); }
            }

            textW = w - 2 * OuterPad - 2 * WellPad;
            needed = MeasureHeight(text, textW) + fixedChrome;
            h = Math.Max(MinH, needed);
            return new Size(w, h);
        }

        private static int MeasureHeight(string text, int width)
        {
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            return TextRenderer.MeasureText(text, DialogSkin.FBody, new Size(Math.Max(1, width), int.MaxValue), flags).Height;
        }
    }
}
