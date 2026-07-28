using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// How NBR looks. Two of them live side by side while the redesign is being
    /// worked out: <b>Classic</b> is exactly what the app has looked like all
    /// along and must stay untouched, because that is what regular testing runs
    /// on; <b>New</b> is where the redesign happens. The switch is in
    /// Settings → Misc and is meant to be temporary — when the new look is
    /// finished and tested, Classic goes and this class collapses into one.
    ///
    /// <para><b>The seam.</b> A window builds itself as it always did and then
    /// calls <see cref="Apply"/> once, at the end. Classic does nothing there, so
    /// the old look cannot drift. New restyles what BuildUI produced. When the new
    /// design needs its own LAYOUT rather than a new coat of paint,
    /// <see cref="BuildsOwnLayout"/> becomes true and
    /// <see cref="BuildPlayerLayout"/> takes over the whole window — again without
    /// touching the classic path.</para>
    ///
    /// <para><b>High contrast wins over both.</b> When Windows is in a
    /// high-contrast scheme the user has told the system exactly what they need to
    /// see; hand-picked colours would override that and make the app unreadable.
    /// So Apply is a no-op there, whichever theme is chosen.</para>
    ///
    /// <para>Nothing here touches roles, names or the tab order — the look changes,
    /// what a screen reader gets does not.</para>
    /// </summary>
    public abstract class UiTheme
    {
        public const string ClassicId = "classic";
        public const string NewId = "new";

        private static UiTheme current;

        /// <summary>The theme in force. Set once at startup from AppSettings.</summary>
        public static UiTheme Current
        {
            get { return current ?? (current = new ClassicTheme()); }
        }

        public static void Select(string id)
        {
            current = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase)
                ? (UiTheme)new NewTheme() : new ClassicTheme();
        }

        /// <summary>Id as stored in Settings.ini.</summary>
        public abstract string Id { get; }

        /// <summary>True once this theme lays the player out itself, instead of
        /// only restyling the classic layout.</summary>
        public virtual bool BuildsOwnLayout { get { return false; } }

        /// <summary>Builds the player's own layout. Only called when
        /// <see cref="BuildsOwnLayout"/> is true; the redesign grows in here.</summary>
        public virtual void BuildPlayerLayout(Form player) { }

        /// <summary>Restyles a window that has already built itself. Called at the
        /// end of BuildUI, and again after a live change.</summary>
        public void Apply(Control root)
        {
            if (root == null) return;
            // The user's own high-contrast scheme outranks any theme we ship.
            if (SystemInformation.HighContrast) return;
            try { Style(root); } catch { }
        }

        protected abstract void Style(Control root);

        /// <summary>Walks the whole control tree, deepest last, so a container can
        /// be styled before its children override anything.</summary>
        protected static void ForEachControl(Control root, Action<Control> action)
        {
            action(root);
            foreach (Control c in root.Controls) ForEachControl(c, action);
        }
    }

    /// <summary>The look NBR has always had: whatever BuildUI made, untouched.
    /// Deliberately empty — the classic path must not change while the new one is
    /// being designed, because that is the build being tested day to day.</summary>
    public class ClassicTheme : UiTheme
    {
        public override string Id { get { return ClassicId; } }
        protected override void Style(Control root) { }
    }

    /// <summary>
    /// Where the redesign happens. Right now it only proves the switch works and
    /// shows the shapes are available: flat, rounded buttons with a pressed state,
    /// a quiet accent, slightly larger button text. Layout is still the classic
    /// 3x4 grid — <see cref="UiTheme.BuildsOwnLayout"/> flips when there is a new
    /// one to build.
    /// </summary>
    public class NewTheme : UiTheme
    {
        public override string Id { get { return NewId; } }

        // A restrained palette; the point for now is that it is visibly NOT the
        // classic one, not that it is final.
        private static readonly Color Window = Color.FromArgb(246, 247, 250);
        private static readonly Color Face = Color.FromArgb(233, 237, 246);
        private static readonly Color FaceHover = Color.FromArgb(214, 224, 241);
        private static readonly Color FaceDown = Color.FromArgb(196, 210, 233);
        private static readonly Color Edge = Color.FromArgb(120, 140, 170);
        private static readonly Color Ink = Color.FromArgb(24, 28, 36);

        protected override void Style(Control root)
        {
            ForEachControl(root, c =>
            {
                Form form = c as Form;
                if (form != null) { form.BackColor = Window; return; }

                Panel panel = c as Panel;
                if (panel != null) { panel.BackColor = Window; return; }

                Button button = c as Button;
                if (button != null) { StyleButton(button); return; }

                TextBox box = c as TextBox;
                if (box != null && box.ReadOnly)
                {
                    // The display fields keep a plain, quiet edit look — that was a
                    // hard-won accessibility decision, so only the colours change.
                    box.BackColor = Color.White;
                    box.ForeColor = Ink;
                    return;
                }
            });
        }

        /// <summary>Flat, rounded, with a face that lightens under the mouse and
        /// darkens when pressed. It stays a real Button — role, name, keyboard and
        /// tab order are the framework's, not ours.</summary>
        private static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Face;
            b.ForeColor = Ink;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Edge;
            b.FlatAppearance.MouseOverBackColor = FaceHover;
            b.FlatAppearance.MouseDownBackColor = FaceDown;
            b.UseVisualStyleBackColor = false;

            RoundOff(b);
            b.SizeChanged -= OnButtonResized;
            b.SizeChanged += OnButtonResized;
        }

        private static void OnButtonResized(object sender, EventArgs e)
        {
            RoundOff(sender as Button);
        }

        /// <summary>Rounds the corners by clipping the control's region. The mouse
        /// follows the shape, so the corners belong to whatever is behind.</summary>
        private static void RoundOff(Button b)
        {
            if (b == null || b.Width < 8 || b.Height < 8) return;
            int r = Math.Min(14, Math.Min(b.Width, b.Height) / 2);
            using (var path = new GraphicsPath())
            {
                var rect = new Rectangle(0, 0, b.Width, b.Height);
                path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                path.AddArc(rect.Right - r * 2 - 1, rect.Y, r * 2, r * 2, 270, 90);
                path.AddArc(rect.Right - r * 2 - 1, rect.Bottom - r * 2 - 1, r * 2, r * 2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r * 2 - 1, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                Region old = b.Region;
                b.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }
    }
}
