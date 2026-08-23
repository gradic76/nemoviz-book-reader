using System;
using System.Drawing;
using System.IO;
using System.Text;

// Does a caption that is laid into a FIXED cell still fit once it has been
// translated? Asked of every .lang file at once.
//
//   check-captions.exe [lang folder]
//
// This is tools/check-legends.cs's question for the DIALOGS. The panel legends
// have their own 108-unit key and their own checker; a dialog control sized by
// the skin has neither, and the two faults below sat there unnoticed because
// nothing asked.
//
// DRAWN, NOT MEASURED, and that is the whole method (CLAUDE.md 8k). The caption
// is drawn once into the real cell, with the same font and the same ellipsis
// trimming the control uses, and once into a cell far too wide to trim; if the
// ink is narrower in the real one, the ellipsis bit. MeasureString cannot
// answer this — it pads, so it reads high, while DrawString's layout box
// carries side bearings, so the two disagree in BOTH directions and no
// threshold on MeasureString can be right.
//
// FOUND ON ITS FIRST RUN, 2026-08-23, while changing the Croatian wording:
// Esperanto's "Preterpasi la prilaboradon" (182 units of 176) and Ancient
// Greek's "Τὴν θεραπείαν παρελθεῖν" (182) were both being cut off, and had been
// since they were written. Neither is a language anyone here reads, which is
// exactly why a machine has to ask.
//
// To cover another control, add a row to Cells: the key it is named by, and
// the width of the rectangle the skin gives it.

class CheckCaptions
{
    // key in the .lang file, the cell the skin lays it into, and what the
    // control's own furniture takes out of that cell before the text starts.
    static readonly (string Key, int Cell, int Chrome, string Where)[] Cells =
    {
        // PropertiesSkin: AsSwitch(p.Bypass, new Rectangle(624, StripY, 200, StripH)).
        // A check box's box and the gap after it come to about 24.
        ("Prop.Bypass", 200, 24, "Properties, the master strip"),
    };

    static int Ink(string s, int cell, Font f)
    {
        using (var bmp = new Bitmap(Math.Max(cell, 8), 40))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using (var fmt = new StringFormat(StringFormatFlags.NoWrap))
            {
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(s, f, Brushes.Black, new RectangleF(0, 0, cell, 40), fmt);
            }
            int right = 0;
            for (int x = 0; x < bmp.Width; x++)
                for (int y = 0; y < bmp.Height; y++)
                    if (bmp.GetPixel(x, y).R < 128) { right = x; break; }
            return right + 1;
        }
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0
            ? args[0]
            : Path.Combine(Directory.GetCurrentDirectory(), "Nemoviz Book Reader", "Lang");
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine("no such folder: " + dir);
            return 2;
        }

        int bad = 0;
        // 12 pt Segoe UI is what DialogSkin.EnsureFonts gives a dialog body.
        using (var f = new Font("Segoe UI", 12f))
            foreach (var c in Cells)
            {
                Console.WriteLine(c.Key + "  (" + c.Where + ", " + c.Cell + " units)");
                int room = c.Cell - c.Chrome;
                foreach (string path in Directory.GetFiles(dir, "*.lang"))
                {
                    string cap = null;
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                        if (line.StartsWith(c.Key + "="))
                        { cap = line.Substring(c.Key.Length + 1).Trim(); break; }
                    if (cap == null || cap.Length == 0) continue;

                    bool fits = Ink(cap, room, f) >= Ink(cap, 900, f);
                    if (!fits) bad++;
                    Console.WriteLine(string.Format("    {0} {1,-10} ink {2,4} of {3}   {4}",
                        fits ? "fits   " : "CLIPPED",
                        Path.GetFileNameWithoutExtension(path),
                        Ink(cap, 900, f), room, cap));
                }
                Console.WriteLine();
            }

        Console.WriteLine(bad == 0 ? "every caption fits its cell" : bad + " CLIPPED");
        return bad == 0 ? 0 : 1;
    }
}
