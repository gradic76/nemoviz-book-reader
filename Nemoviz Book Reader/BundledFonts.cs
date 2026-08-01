using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nemoviz_Book_Reader
{
    /// <summary>The five type families NBR carries for its reading window,
    /// loaded into THIS PROCESS ONLY and never installed.
    ///
    /// <para>Installing them would modify the user's Windows, and NBR is
    /// portable — it may be running from a stick that is about to be pulled out.
    /// So the fonts are embedded in the assembly and registered at startup;
    /// nothing is written anywhere and nothing survives the process.</para>
    ///
    /// <para><b>Why every font is registered twice.</b> GDI+ and GDI keep
    /// separate font tables and neither can see the other's private entries.
    /// <see cref="PrivateFontCollection"/> is what lets GDI+ RENDER the face,
    /// and <c>AddFontMemResourceEx</c> is what lets GDI see it — which matters
    /// because <see cref="ReadingWindow"/> asks a font which code points it has
    /// through <c>GetFontUnicodeRanges</c>, a GDI call reached via
    /// <c>Font.ToHfont</c>. Register with GDI+ alone and that probe silently
    /// answers for whatever face GDI SUBSTITUTED, so a bundled font would be
    /// judged on a stand-in's coverage. Registering both ways costs one extra
    /// call and makes the answer be about the font we actually shipped.</para>
    ///
    /// <para>The memory blocks are deliberately never freed. Both APIs keep
    /// pointers into the caller's buffer for as long as the font is in use, and
    /// "in use" here means until the process ends.</para></summary>
    internal static class BundledFonts
    {
        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont,
                                                          IntPtr pdv, ref uint pcFonts);

        private static readonly List<PrivateFontCollection> collections =
            new List<PrivateFontCollection>();
        private static readonly List<FontFamily> faces = new List<FontFamily>();
        private static readonly List<IntPtr> blocks = new List<IntPtr>();
        private static readonly List<string> families = new List<string>();
        private static bool loaded;

        /// <summary>Resource names, without the assembly prefix, GROUPED BY
        /// FAMILY — one row per family, its regular and bold together. The two
        /// variable fonts are a single file that already carries every weight,
        /// so a separate bold would be the same outlines twice.
        ///
        /// <para><b>The grouping is not cosmetic.</b> A
        /// <see cref="PrivateFontCollection"/> given several fonts by
        /// <c>AddMemoryFont</c> keeps only the FIRST FAMILY and silently drops
        /// the rest — no exception, no failed return, the extra families simply
        /// are not there afterwards. (<c>AddFontFile</c> does not behave this
        /// way, but it needs the fonts loose on disk, which is what embedding
        /// them was meant to avoid.) Two faces of the SAME family are fine, so
        /// one collection per family loads all five where one collection loaded
        /// one.</para></summary>
        private static readonly string[][] Groups =
        {
            new[] { "Fonts.Andika-Regular.ttf", "Fonts.Andika-Bold.ttf" },
            new[] { "Fonts.AtkinsonHyperlegibleNext-Variable.ttf" },
            new[] { "Fonts.Lexend-Variable.ttf" },
            new[] { "Fonts.Luciole-Regular.ttf", "Fonts.Luciole-Bold.ttf" },
            new[] { "Fonts.OpenDyslexic-Regular.otf", "Fonts.OpenDyslexic-Bold.otf" },
        };

        /// <summary>Family names of the bundled faces, in the order loaded.
        /// Empty if loading failed — which is a degraded reading window, not a
        /// dead player, so nothing here ever throws.</summary>
        public static IList<string> Families { get { Load(); return families; } }

        /// <summary>The bundled faces themselves. The font picker needs these
        /// rather than the names, because the coverage probe has to be run on a
        /// <see cref="FontFamily"/> the collection owns — see <see cref="Make"/>
        /// for why a name is not enough.</summary>
        public static IEnumerable<FontFamily> Faces { get { Load(); return faces; } }

        /// <summary>Builds a font by family NAME, looking in the bundled faces
        /// before the installed ones.
        ///
        /// <para>This exists because <c>new Font("Andika", 26f)</c> does NOT work
        /// for a privately loaded family: GDI+ resolves the name against
        /// installed fonts, finds nothing, and quietly hands back a substitute
        /// with the SAME requested name recorded on it — so the caller cannot
        /// even tell. A private family has to be built from the
        /// <see cref="FontFamily"/> instance the collection owns.</para></summary>
        public static Font Make(string family, float size, FontStyle style = FontStyle.Regular)
        {
            Load();
            if (!string.IsNullOrEmpty(family))
            {
                foreach (FontFamily f in faces)
                    if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase))
                    {
                        // A bundled face need not have every style; asking for one
                        // it lacks throws rather than substituting.
                        if (!f.IsStyleAvailable(style)) style = FontStyle.Regular;
                        return new Font(f, size, style);
                    }
            }
            return new Font(family, size, style);
        }

        /// <summary>True if the name belongs to a bundled family. Used to skip
        /// the installed-font enumeration, which will never list these.</summary>
        public static bool Has(string family)
        {
            Load();
            if (string.IsNullOrEmpty(family)) return false;
            foreach (string n in families)
                if (string.Equals(n, family, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            Assembly asm = Assembly.GetExecutingAssembly();
            string prefix = typeof(BundledFonts).Namespace + ".";
            foreach (string[] group in Groups)
            {
                try
                {
                    var pfc = new PrivateFontCollection();
                    bool any = false;
                    foreach (string name in group)
                    {
                        using (Stream s = asm.GetManifestResourceStream(prefix + name))
                        {
                            if (s == null) continue;      // a missing face is not fatal
                            byte[] data = new byte[s.Length];
                            int got = 0;
                            while (got < data.Length)
                            {
                                int n = s.Read(data, got, data.Length - got);
                                if (n <= 0) break;
                                got += n;
                            }
                            if (got != data.Length) continue;

                            IntPtr block = Marshal.AllocCoTaskMem(data.Length);
                            Marshal.Copy(data, 0, block, data.Length);
                            blocks.Add(block);            // held for the process's life

                            pfc.AddMemoryFont(block, data.Length);
                            uint installed = 0;
                            AddFontMemResourceEx(block, (uint)data.Length, IntPtr.Zero, ref installed);
                            any = true;
                        }
                    }
                    if (!any) { pfc.Dispose(); continue; }
                    collections.Add(pfc);
                    Take(pfc);
                }
                catch { }
            }
        }

        /// <summary>Takes the family a loaded file offers, and only the one worth
        /// offering back.
        ///
        /// <para>A variable font arrives as a whole shelf of families — Lexend
        /// brings eight (<c>Lexend</c>, <c>Lexend Black</c>, <c>Lexend Thin</c>
        /// …) and Atkinson five. That is a weight axis wearing family clothing,
        /// and putting it in a picker would bury four real choices under
        /// seventeen near-identical ones. GDI+ also truncates a family name at 31
        /// characters, so several arrive visibly chopped
        /// (<c>Atkinson Hyperlegible Next Extr</c>) — a second reason they are
        /// not fit to show.</para>
        ///
        /// <para>Kept: any family whose name is not another family's name plus a
        /// suffix. That is what a weight variant IS, and it does not depend on
        /// the order GDI+ happens to return them in.</para></summary>
        private static void Take(PrivateFontCollection pfc)
        {
            var all = new List<FontFamily>(pfc.Families);
            foreach (FontFamily f in all)
            {
                bool variant = false;
                foreach (FontFamily other in all)
                    if (!ReferenceEquals(f, other) && other.Name.Length < f.Name.Length &&
                        f.Name.StartsWith(other.Name + " ", StringComparison.OrdinalIgnoreCase))
                    { variant = true; break; }
                if (variant || families.Contains(f.Name)) continue;
                families.Add(f.Name);
                faces.Add(f);
            }
        }
    }
}
