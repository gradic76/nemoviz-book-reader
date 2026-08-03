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
        /// <summary>Let Windows decide — the default, and the only value that
        /// changes with the machine rather than with the reader.</summary>
        public const string FollowId = "follow";

        private static UiTheme current;

        /// <summary>The theme in force. Set once at startup from AppSettings.</summary>
        public static UiTheme Current
        {
            get { return current ?? (current = new ClassicTheme()); }
        }

        /// <summary>Puts a theme in force.
        ///
        /// <para><b>High contrast is the DEFAULT, not a lock</b> (Gordan asked
        /// the right question, 2026-08-03: does a high-contrast user lose the
        /// right to choose?). It does not. High contrast is a setting for the
        /// SYSTEM, not an instruction to this app, and somebody may run it for
        /// one reason and still want NBR to look the way they like. So it
        /// decides only while the reader has not said otherwise — which is the
        /// case for everyone who never opens Settings, and those are the people
        /// it protects.</para>
        ///
        /// <para>An explicit <c>classic</c> or <c>new</c> is honoured whatever
        /// Windows is doing. That is safe because the choice is <b>reversible
        /// without seeing</b>: the keyboard and the screen reader are untouched
        /// by any of this, and F2 opens Settings from anywhere. A choice that
        /// could not be undone would be a trap rather than a freedom.</para></summary>
        public static void Select(string id)
        {
            chosenByUser = !(string.IsNullOrEmpty(id)
                || string.Equals(id, FollowId, StringComparison.OrdinalIgnoreCase));
            if (!chosenByUser)
            {
                // Nothing chosen: Windows decides. Under high contrast that means
                // the system-colours layout, which is the classic one — it paints
                // nothing of its own, so the user's scheme comes through whole.
                current = SystemInformation.HighContrast
                    ? (UiTheme)new ClassicTheme() : new NewTheme();
                return;
            }
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
        /// <summary>Whether the theme in force was ASKED for, rather than worked
        /// out from Windows. It decides who wins under high contrast.</summary>
        private static bool chosenByUser;

        public void Apply(Control root)
        {
            if (root == null) return;
            // The user's own high-contrast scheme outranks a theme we PICKED for
            // them — but not one they picked for themselves. Somebody who went to
            // Settings and chose a look has said something; ignoring it because
            // Windows is in high contrast would be deciding on their behalf twice.
            if (SystemInformation.HighContrast && !chosenByUser) return;
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

        /// <summary>The redesign now brings its own layout: the borderless
        /// 960 × 480 hi-fi panel built by <see cref="NewPlayerSkin"/>.</summary>
        public override bool BuildsOwnLayout { get { return true; } }

        public override void BuildPlayerLayout(Form player)
        {
            Form1 f = player as Form1;
            if (f != null) NewPlayerSkin.Build(f);
        }

        /// <summary>Nothing to restyle: <see cref="NewPlayerSkin"/> lays the
        /// player out and paints every part of it, and Apply runs AFTER
        /// BuildPlayerLayout — so anything done here would undo the skin. The
        /// other windows (Library, Settings, Properties) also pass through here
        /// and are deliberately left alone until the panel is settled and they
        /// get a pass of their own.</summary>
        protected override void Style(Control root) { }

    }
}
