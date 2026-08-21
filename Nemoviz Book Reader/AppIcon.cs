using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>The program's own icon, on every window it owns.
    ///
    /// <para><b>Setting it in the project file is not enough, and that was
    /// measured rather than assumed.</b> An icon embedded in the executable is
    /// what Explorer, a desktop shortcut and a pinned taskbar button show — but a
    /// WinForms <see cref="Form"/> does NOT pick it up. A probe built with the
    /// icon compiled in reported the exe's own icon as magenta and
    /// <c>new Form().Icon</c> as (232,168,1), which is the .NET default. So every
    /// window would have gone on wearing the stock icon in Alt+Tab, on the taskbar
    /// and — under the classic look, which has title bars — in its own corner.</para>
    ///
    /// <para><b>Why one sweep rather than a line in twenty-two constructors.</b>
    /// Only six of NBR's forms pass through <c>DialogSkin.Shell</c>, so there is no
    /// single door they all use; a line per form is twenty-two chances to forget
    /// the twenty-third. This watches <see cref="Application.OpenForms"/> instead
    /// and stamps whatever is standing there, which cannot miss a window that has
    /// not been written yet. It costs nothing to run: the sweep happens only when
    /// the number of open forms has CHANGED, which is exactly when a new one has
    /// appeared.</para>
    ///
    /// <para>The icon is read out of the running executable, so the project file
    /// stays the single source of truth for which icon this is.</para></summary>
    internal static class AppIcon
    {
        private static Icon icon;
        private static bool looked;
        private static int lastCount = -1;

        public static Icon Icon
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch { icon = null; }
                }
                return icon;
            }
        }

        /// <summary>Call once, before the message loop starts.</summary>
        public static void Install()
        {
            if (Icon == null) return;          // no icon compiled in: do nothing at all
            Application.Idle += Sweep;
        }

        public static void Apply(Form f)
        {
            if (f == null || Icon == null) return;
            try { if (!ReferenceEquals(f.Icon, Icon)) f.Icon = Icon; }
            catch { }                          // a form closing under us is not an error
        }

        private static void Sweep(object sender, EventArgs e)
        {
            FormCollection open = Application.OpenForms;
            if (open == null || open.Count == lastCount) return;
            lastCount = open.Count;
            for (int i = 0; i < open.Count; i++) Apply(open[i]);
        }
    }
}
