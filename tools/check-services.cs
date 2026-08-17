using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// The Services and accounts window, built and walked. Localization is started by
// hand -- Form1 does it in the real app and no harness builds one, so without it
// every caption measured is a KEY and not the words a reader sees.
//
//   services.exe <exe path> <theme>
class Svc
{
    [STAThread]
    static int Main(string[] a)
    {
        Assembly nbr = Assembly.LoadFrom(a[0]);
        Type U = nbr.GetType("Nemoviz_Book_Reader.UiTheme");
        U.GetMethod("Select", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { a[1] });

        Type A = nbr.GetType("Nemoviz_Book_Reader.AppSettings");
        object app = Activator.CreateInstance(A);
        Type L = nbr.GetType("Nemoviz_Book_Reader.Localization");
        L.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static).Invoke(null,
            new object[] { A.GetProperty("LangPath").GetValue(app), "en" });

        Form f = (Form)Activator.CreateInstance(nbr.GetType("Nemoviz_Book_Reader.ServicesForm"),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new object[0], null);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();

        Console.WriteLine("\"{0}\"  {1}x{2}  {3}", f.Text, f.ClientSize.Width, f.ClientSize.Height, f.FormBorderStyle);

        int bad = 0;
        ListBox list = null; TextBox body = null;
        var seen = new System.Collections.Generic.List<Control>();
        foreach (Control c in f.Controls)
        {
            if (c is ListBox) list = (ListBox)c;
            if (c is TextBox) body = (TextBox)c;
            string n = c.AccessibleName ?? "";
            Console.WriteLine("  {0,-9} {1,4} {2,4} {3,4} {4,4} tab{5}{6}  \"{7}\"",
                c.GetType().Name, c.Left, c.Top, c.Width, c.Height, c.TabIndex,
                c.TabStop ? " stop" : "", n.Length > 26 ? n.Substring(0, 26) : n);
            if (!(c is Label) && n.Length == 0) { Console.WriteLine("     *** bez imena"); bad++; }
            if (c is TextBox && !c.TabStop) { Console.WriteLine("     *** citljivo a nije tab stop"); bad++; }
            if (c.Right > f.ClientSize.Width || c.Bottom > f.ClientSize.Height || c.Left < 0 || c.Top < 0)
            { Console.WriteLine("     *** IZVAN"); bad++; }
            foreach (Control o in seen) if (o.Bounds.IntersectsWith(c.Bounds))
            { Console.WriteLine("     *** PREKLAPA " + o.GetType().Name); bad++; }
            seen.Add(c);
        }

        // Every service must name itself and say something under it.
        Console.WriteLine();
        for (int i = 0; i < list.Items.Count; i++)
        {
            list.SelectedIndex = i;
            Application.DoEvents();
            string t = body.Text ?? "";
            int steps = 0;
            foreach (string line in t.Split('\n'))
                if (System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^\d+\.")) steps++;
            Console.WriteLine("  {0,-22} {1,5} znakova, {2} koraka", list.Items[i], t.Length, steps);
            if (t.Length < 200) { Console.WriteLine("     *** premalo teksta"); bad++; }
            if (steps < 3) { Console.WriteLine("     *** premalo koraka"); bad++; }
        }

        Console.WriteLine("\n-> problema: " + bad);
        f.Dispose();
        return bad;
    }
}
