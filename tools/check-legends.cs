using System;
using System.Drawing;
using System.IO;

// Does every drawn panel legend fit its key?
//
//   check-legends.exe <lang file> [<lang file> ...]
//
// THE INSTRUMENT MATTERS MORE THAN THE NUMBER HERE, and getting it wrong cost
// two unnecessary shortenings (CLAUDE.md 8k, the 2026-08-22 correction).
//
// The first version measured Graphics.MeasureString against a column of 91 and
// called anything above it over. Both halves were wrong. The column is
// NewPlayerSkin.CellW = 108 - the width of the key itself, handed to
// DrawString by SkinCanvas.PaintLegends with no inset. And MeasureString is not
// what decides: it pads, so it reads high, while DrawString's own layout box
// includes side bearings, so a word whose INK is 101 can still ellipsise inside
// 108. Measured on real legends the two disagree in both directions.
//
// So this asks the only question that cannot be wrong: it draws the legend
// exactly as the skin does - same font, same rectangle, same StringFormat with
// EllipsisCharacter trimming - once into the real cell and once into a cell far
// too wide to trim, and compares the ink. Narrower in the real cell means the
// ellipsis bit, which is the definition of not fitting.
//
// Build:
//   csc -out:check-legends.exe -r:System.Drawing.dll check-legends.cs

class CheckLegends
{
    // NewPlayerSkin.CellW, and FLegend is new Font("Segoe UI", 12f).
    const int CellW = 108, CellH = 24;

    [STAThread]
    static int Main(string[] files)
    {
        if (files.Length == 0)
        {
            Console.Error.WriteLine("usage: check-legends.exe <lang file> [...]");
            return 2;
        }

        int clipped = 0;
        using (var f = new Font("Segoe UI", 12f))
            foreach (string path in files)
            {
                Console.WriteLine(Path.GetFileNameWithoutExtension(path) + ":");
                foreach (string line in File.ReadAllLines(path, System.Text.Encoding.UTF8))
                {
                    if (!line.StartsWith("Btn.") || !line.Contains(".Legend=")) continue;
                    int eq = line.IndexOf('=');
                    string key = line.Substring(0, eq), text = line.Substring(eq + 1).Trim();
                    if (text.Length == 0) continue;

                    int real = Ink(text, f, CellW), whole = Ink(text, f, 500);
                    bool fits = real >= whole;
                    if (!fits) clipped++;
                    Console.WriteLine(string.Format("    {0,-30} {1,-18} ink {2,3} of {3}   {4}",
                        key, Ascii(text), whole, CellW,
                        fits ? "fits, " + (CellW - whole) + " free" : "*** CLIPPED ***"));
                }
                Console.WriteLine();
            }

        Console.WriteLine(clipped == 0
            ? "every legend fits its key"
            : clipped + " legend(s) clipped - shorten them, and keep the full wording in .Accessible");
        return clipped == 0 ? 0 : 1;
    }

    /// <summary>Width of the actual marks, drawn the way the skin draws them.</summary>
    static int Ink(string s, Font f, int w)
    {
        using (var bm = new Bitmap(w, CellH))
        using (var g = Graphics.FromImage(bm))
        {
            g.Clear(Color.White);
            using (var sf = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            })
            using (var br = new SolidBrush(Color.Black))
                g.DrawString(s, f, br, new RectangleF(0, 0, w, CellH), sf);

            int lo = w, hi = -1;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < CellH; y++)
                    if (bm.GetPixel(x, y).R < 160) { if (x < lo) lo = x; if (x > hi) hi = x; break; }
            return hi < lo ? 0 : hi - lo + 1;
        }
    }

    /// <summary>The console here cannot print Cyrillic or Greek; the KEY says
    /// which legend it is, so the text is only a reminder.</summary>
    static string Ascii(string s)
    {
        var b = new System.Text.StringBuilder();
        foreach (char c in s) b.Append(c < 128 ? c : '?');
        return b.ToString();
    }
}
