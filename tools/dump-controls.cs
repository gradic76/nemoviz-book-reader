using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Dumps every control of every dialog as "path type name x y w h", one per line,
// so two builds can be DIFFED rather than eyeballed. The new look must come out
// byte for byte identical to the build before this change - that is the whole
// regression test for code CLAUDE.md 8k declares closed.
//
//   dump.exe <exe path> <theme> <out file>
class Dump
{
    static Assembly nbr;
    static TextWriter w;

    [STAThread]
    static int Main(string[] a)
    {
        nbr = Assembly.LoadFrom(a[0]);
        Type U = nbr.GetType("Nemoviz_Book_Reader.UiTheme");
        U.GetMethod("Select", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { a[1] });
        w = new StreamWriter(a[2]);
        w.WriteLine("theme " + U.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                                 .GetValue(null).GetType().Name);

        Type A = nbr.GetType("Nemoviz_Book_Reader.AppSettings");
        object app = Activator.CreateInstance(A);

        // LOCALIZATION HAS TO BE STARTED BY HAND. Form1 does it in the real app
        // and the harness never builds one, so every dump before this showed
        // KEYS where the app shows text -- which is fine for geometry and
        // useless for anything that depends on what a string actually says. The
        // empty-hint guard is exactly that: with no language loaded, T() hands
        // back the key, so an emptied hint looks non-empty and keeps its button.
        Type L = nbr.GetType("Nemoviz_Book_Reader.Localization");
        L.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static).Invoke(null,
            new object[] { A.GetProperty("LangPath").GetValue(app), "en" });

        Show("Settings", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.SettingsForm"),
            new object[] { app, null, null }));

        Type B = nbr.GetType("Nemoviz_Book_Reader.BookData");
        object book = Activator.CreateInstance(B, new object[] { a.Length > 3 ? a[3]
            : @"C:\Users\gorda\NBR Library\Tryon, Thomas - Harvest Home" });
        ConstructorInfo ci = null;
        foreach (ConstructorInfo c in nbr.GetType("Nemoviz_Book_Reader.PropertiesForm").GetConstructors()) ci = c;
        object[] args = new object[ci.GetParameters().Length];
        args[0] = book;
        for (int i = 1; i < args.Length; i++)
        {
            Type pt = ci.GetParameters()[i].ParameterType;
            args[i] = pt == A ? app : (pt.IsValueType ? Activator.CreateInstance(pt) : null);
        }
        Show("Properties", (Form)ci.Invoke(args));

        Show("GoTo", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.GoToForm"),
            new object[] { new string[] { "Prvo poglavlje", "Drugo poglavlje", "Trece" }, 0, true, false }));
        Show("Bookmarks", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.ManageBookmarksForm"),
            new object[] { new List<double> { 10.0, 200.0 }, null }));
        Show("SleepTimer", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.SleepTimerForm")));
        Show("Library", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.LibraryForm"),
            new object[] { app, null, null }));

        w.Close();
        return 0;
    }

    static void Show(string what, Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        // Every tab page must be SELECTED once or it reports what it was built
        // at rather than what it was laid out to - the trap CLAUDE.md 10c records.
        f.Show();
        Application.DoEvents();
        SelectEveryTab(f);
        Application.DoEvents();

        w.WriteLine();
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "== {0} client {1}x{2} border {3}", what, f.ClientSize.Width, f.ClientSize.Height,
            f.FormBorderStyle));
        Walk(f, what);
        f.Dispose();
    }

    static void SelectEveryTab(Control parent)
    {
        TabControl t = parent as TabControl;
        if (t != null) foreach (TabPage p in t.TabPages) { t.SelectedTab = p; Application.DoEvents(); }
        foreach (Control c in parent.Controls) SelectEveryTab(c);
    }

    static void Walk(Control parent, string path)
    {
        foreach (Control c in parent.Controls)
        {
            string name = c.Name.Length > 0 ? c.Name : (c.Text ?? "");
            if (name.Length > 30) name = name.Substring(0, 30);
            name = name.Replace("\r", " ").Replace("\n", " ");
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}/{1} [{2}] {3} {4} {5} {6} tab{7}{8}",
                path, c.GetType().Name, name, c.Left, c.Top, c.Width, c.Height,
                c.TabIndex, c.TabStop ? " stop" : ""));
            Walk(c, path + "/" + c.GetType().Name);
        }
    }
}
