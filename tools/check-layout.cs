using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// Does anything collide or hang off an edge? Asked of all six dialogs, in
// whichever look is named. Each container is checked among ITS OWN children -
// two controls in different panels cannot collide however their rectangles read.
//
//   fits.exe <exe path> <theme>
class Fits
{
    static Assembly nbr;
    static int problems;

    [STAThread]
    static int Main(string[] a)
    {
        nbr = Assembly.LoadFrom(a[0]);
        Type U = nbr.GetType("Nemoviz_Book_Reader.UiTheme");
        U.GetMethod("Select", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { a[1] });
        Console.WriteLine("tema: " + U.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                                       .GetValue(null).GetType().Name);

        Type A = nbr.GetType("Nemoviz_Book_Reader.AppSettings");
        object app = Activator.CreateInstance(A);

        // Localization is started by Form1 in the real app, and this harness never
        // builds one -- so without this every caption measured here is a KEY and
        // not the words the reader sees. Key strings are a different length, which
        // makes every overlap number quietly wrong.
        Type L = nbr.GetType("Nemoviz_Book_Reader.Localization");
        L.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static).Invoke(null,
            new object[] { A.GetProperty("LangPath").GetValue(app), "en" });

        Check("Settings", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.SettingsForm"),
            new object[] { app, null, null }));

        Type B = nbr.GetType("Nemoviz_Book_Reader.BookData");
        object book = Activator.CreateInstance(B, new object[] { a.Length > 2 ? a[2]
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
        Check("Properties (audio)", (Form)ci.Invoke(args));

        Check("Go To", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.GoToForm"),
            new object[] { new string[] { "Prvo poglavlje", "Drugo poglavlje", "Trece" }, 0, true, false }));
        Check("Bookmarks", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.ManageBookmarksForm"),
            new object[] { new List<double> { 10.0, 200.0 }, null }));
        Check("Sleep timer", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.SleepTimerForm")));
        Check("Library", (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.LibraryForm"),
            new object[] { app, null, null }));

        Console.WriteLine();
        Console.WriteLine("UKUPNO problema: " + problems);
        return problems;
    }

    static void Check(string what, Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();
        SelectEveryTab(f);
        Application.DoEvents();

        int before = problems;
        Console.WriteLine();
        Console.WriteLine("== {0}: {1} x {2}", what, f.ClientSize.Width, f.ClientSize.Height);
        Walk(f, 0);
        Console.WriteLine("   -> problema: {0}", problems - before);
        f.Dispose();
    }

    static void SelectEveryTab(Control parent)
    {
        TabControl t = parent as TabControl;
        if (t != null) foreach (TabPage p in t.TabPages) { t.SelectedTab = p; Application.DoEvents(); }
        foreach (Control c in parent.Controls) SelectEveryTab(c);
    }

    static void Walk(Control parent, int depth)
    {
        var kids = new List<Control>();
        foreach (Control c in parent.Controls)
            if (c.Width > 0 && c.Height > 0) kids.Add(c);
        if (kids.Count == 0) return;

        Size box = parent is Form ? ((Form)parent).ClientSize : parent.ClientSize;
        for (int i = 0; i < kids.Count; i++)
        {
            Control x = kids[i];
            // The read-only fields are PARKED below the client area on purpose
            // (CLAUDE.md 8k) - they are not overflow.
            bool parked = x.Top >= box.Height;
            if (!parked && (x.Right > box.Width || x.Bottom > box.Height || x.Left < -100 || x.Top < -100))
            {
                problems++;
                Console.WriteLine("   {0}IZVAN  {1} {2}  ({3},{4} {5}x{6}) u {7}x{8}",
                    new string(' ', depth * 2), x.GetType().Name, Short(x), x.Left, x.Top,
                    x.Width, x.Height, box.Width, box.Height);
            }
            for (int j = i + 1; j < kids.Count; j++)
            {
                // Sibling tab pages always share one rectangle - that is what a
                // tab control IS. A docked canvas underlies everything by design.
                if (x is TabPage && kids[j] is TabPage) continue;
                if (x.GetType().Name == "DialogCanvas" || kids[j].GetType().Name == "DialogCanvas") continue;
                if (x.Bounds.IntersectsWith(kids[j].Bounds))
                {
                    problems++;
                    Console.WriteLine("   {0}PREKLAP {1} {2}  s  {3} {4}",
                        new string(' ', depth * 2), x.GetType().Name, Short(x),
                        kids[j].GetType().Name, Short(kids[j]));
                }
            }
        }
        foreach (Control c in kids)
            if (c is Panel || c is GroupBox || c is TabPage || c is TabControl || c is SplitContainer
                || c is SplitterPanel)
                Walk(c, depth + 1);
    }

    static string Short(Control c)
    {
        string s = c.Name.Length > 0 ? c.Name : (c.Text ?? "");
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 26 ? s.Substring(0, 26) : s;
    }
}
