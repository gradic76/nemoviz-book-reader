using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Optical character recognition, using the one that is already on the
    /// machine: <c>Windows.Media.Ocr</c>.
    ///
    /// <para><b>No SDK, no vendored winmd, nothing shipped.</b> Everything is
    /// late-bound through
    /// <c>Type.GetType("…, Windows, ContentType=WindowsRuntime")</c>, exactly as
    /// <see cref="OneCoreBackend"/> reaches the OneCore voices — the metadata
    /// ships with Windows in <c>System32\WinMetadata</c>. The async results are
    /// bare <c>__ComObject</c>s that reflection cannot inspect, so they are
    /// unwrapped with <c>AsTask</c> from <c>System.Runtime.WindowsRuntime</c>,
    /// which is part of the framework and lives in the GAC. The alternative was
    /// Tesseract at ~30 MB plus per-language data, against a 16 MB installer, to
    /// do worse on Croatian.</para>
    ///
    /// <para><b>The numbers here were measured, and two of them are the opposite
    /// of the obvious guess.</b> They are constants so they can be retuned
    /// without going looking for where the decision lives:</para>
    /// <list type="bullet">
    /// <item><b>More resolution buys nothing.</b> The same page rendered from
    /// 1700 to 6800 pixels on its long side returned the same word count within
    /// noise (732/726/692/687/692) while recognition time grew sevenfold. Once
    /// the type clears the floor, extra pixels are pure cost — hence
    /// <see cref="TargetLongSide"/> at 2400 rather than "300 dpi".</item>
    /// <item><b>Never binarize.</b> Thresholding is the classic move for a
    /// scanned page and it was the WORST result measured: Otsu lost 87 words and
    /// 11 points of plausibility against the untouched render. The engine wants
    /// greyscale and does its own work on it. A mild contrast stretch was the
    /// only gain (<see cref="Stretch"/>).</item>
    /// <item>The engine is <b>polarity-invariant</b> — inverting a page changed
    /// nothing at all — so white-on-black needs no special handling.</item>
    /// <item>Recognition collapses below roughly <b>14 pixels</b> of cap height
    /// and is steady above about 20, which is what <see cref="TargetLongSide"/>
    /// is really chosen to guarantee.</item>
    /// </list>
    ///
    /// <para><b>What it cannot do:</b> only the languages the user's Windows has
    /// (see <see cref="Languages"/>), and NBR cannot install one — that needs
    /// elevation — <see cref="BeginInstall"/> is how that is done. See
    /// <see cref="OcrPageSource"/> for the one input Windows really cannot
    /// handle.</para>
    ///
    /// <para><b>THE LANGUAGE MATTERS, and an earlier version of this comment said
    /// it did not.</b> One clean synthetic English paragraph through the Croatian
    /// engine measured 0.0 % character error, and I generalised that into "one
    /// Latin pack reads all Latin languages". Gordan corrected it out of years of
    /// listening to real OCR'd books: the English engine turns <i>Vatikan</i> into
    /// <i>Yatikan</i>, a Serbian recognizer on a Croatian book turns
    /// <i>William</i> into <i>Vvilliam</i> — and if it really were only a matter
    /// of Latin letters, Microsoft would ship ONE pack instead of thirty-five.
    /// The per-language models exist because the language decides the ambiguous
    /// glyphs. So the reader is offered the choice, and the fallback in
    /// <see cref="EngineFor"/> is a way of not refusing — never a reason to skip
    /// asking.</para>
    /// </summary>
    public static class WindowsOcr
    {
        /// <summary>Pixels to aim the long side of a page at. Not a DPI: scanned
        /// PDFs disagree wildly about what a page "is" — real ones measured at
        /// 3507 points a page (laid out 1:1 with their scan pixels) and 749
        /// (ordinary units). Scaling by dpi/72 overshoots the first into an
        /// outright failure and leaves the second far too small to read.</summary>
        public const int TargetLongSide = 2400;

        /// <summary><see cref="MaxImageDimension"/> reports 10000 and is not to be
        /// believed: 4948x7000 recognized fine and 5655x8000 threw
        /// "Image dimensions are too large". Stay well under, and still catch.</summary>
        public const int HardMaxDimension = 6800;

        /// <summary>Percentile pushed to black / white by <see cref="Stretch"/>.</summary>
        public const double StretchPercentile = 0.02;

        private const string WinRT = ", Windows, ContentType=WindowsRuntime";
        private const string Facade = ", System.Runtime.WindowsRuntime, Version=4.0.0.0, " +
                                      "Culture=neutral, PublicKeyToken=b77a5c561934e089";

        private static readonly object gate = new object();
        private static bool probed;
        private static Type tEngine, tResult, tLanguage, tDecoder, tSoftBmp, tPixFmt, tAlpha;
        private static MethodInfo miAsTask, miAsTaskAction, miAsRandomAccessStream, miAsStreamForRead;

        // One engine per language tag, built on demand. Creating one is not free
        // and a book is a few hundred pages of the same language.
        private static readonly Dictionary<string, object> engines =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when this machine can do OCR at all: the WinRT types
        /// resolve and at least one recognizer language is installed.</summary>
        public static bool Available
        {
            get
            {
                Probe();
                return tEngine != null && miAsTask != null && Languages.Count > 0;
            }
        }

        /// <summary>The recognizer languages Windows has, as (tag, display name).
        ///
        /// <para><b>OCR is not something the user installs on its own.</b> It is a
        /// FEATURE of an installed Windows language — measured on this machine,
        /// <c>Get-InstalledLanguage</c> reports hr-HR with features "BasicTyping,
        /// Handwriting, TextToSpeech, OCR". Hunting for an OCR checkbox in Windows
        /// finds nothing, because there isn't one; the user adds the LANGUAGE. The
        /// Help text has to say that, and <see cref="OpenWindowsLanguageSettings"/>
        /// takes them there.</para></summary>
        public static List<(string Tag, string Name)> Languages
        {
            get
            {
                Probe();
                var list = new List<(string, string)>();
                if (tEngine == null) return list;
                try
                {
                    object all = tEngine.GetProperty("AvailableRecognizerLanguages",
                        BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    foreach (object l in (IEnumerable)all)
                    {
                        Type tl = l.GetType();
                        string tag = tl.GetProperty("LanguageTag").GetValue(l) as string;
                        if (string.IsNullOrEmpty(tag)) continue;
                        // Its own language, like every other language list in NBR
                        // — WinRT's DisplayName follows the user's Windows, which
                        // would put "engleski" beside "Hrvatski" on this machine.
                        string name = LanguageDetector.DisplayName(tag);
                        if (string.IsNullOrEmpty(name))
                            name = tl.GetProperty("NativeName").GetValue(l) as string ?? tag;
                        list.Add((tag, name));
                    }
                }
                catch { }
                return list;
            }
        }

        /// <summary>One recognized line, with the two facts about it that matter
        /// to a book: where it sits down the page, and how big its type is.
        ///
        /// <para><b>Line structure is only available HERE.</b>
        /// <c>OcrResult.Text</c> joins the lines of a page with SPACES and no
        /// break at all, so anything that has to know where a line begins or ends
        /// — a running head, a footer, a page number sitting alone at the top —
        /// cannot be done on the text afterwards. Measured, not assumed: splitting
        /// a page of <c>Text</c> on newlines yields exactly one line.</para></summary>
        public class OcrLine
        {
            public string Text = "";
            /// <summary>Top of the line in page pixels.</summary>
            public double Top;
            /// <summary>Bottom of the line in page pixels.</summary>
            public double Bottom;
            /// <summary>Mean height of its words' boxes — a proxy for type size,
            /// and <b>a poor one on short lines</b>: measured on a real book, a
            /// line reading "temama." comes out 25 px against a body median of 35
            /// simply for having no tall letters in it. Usable for grouping, not
            /// for deciding that something is small print.</summary>
            public double Height;
        }

        /// <summary>Recognizes one page image as its LINES. Null when the engine
        /// could not be built; empty when the page holds no text.</summary>
        public static List<OcrLine> ReadLines(byte[] pageImage, string languageTag)
        {
            if (pageImage == null || pageImage.Length == 0) return new List<OcrLine>();
            object engine = EngineFor(languageTag);
            if (engine == null) return null;
            try
            {
                byte[] prepared = Stretch(pageImage);
                object soft = Decode(prepared ?? pageImage);
                if (soft == null) return null;
                object res = Await(engine.GetType().GetMethod("RecognizeAsync")
                                         .Invoke(engine, new object[] { soft }), tResult);

                var lines = new List<OcrLine>();
                foreach (object ln in (IEnumerable)tResult.GetProperty("Lines").GetValue(res))
                {
                    var line = new OcrLine
                    {
                        Text = (string)ln.GetType().GetProperty("Text").GetValue(ln) ?? "",
                        Top = double.MaxValue
                    };
                    double sum = 0; int n = 0;
                    foreach (object w in (IEnumerable)ln.GetType().GetProperty("Words").GetValue(ln))
                    {
                        object r = w.GetType().GetProperty("BoundingRect").GetValue(w);
                        Type tr = r.GetType();
                        double y = Convert.ToDouble(tr.GetProperty("Y").GetValue(r));
                        double h = Convert.ToDouble(tr.GetProperty("Height").GetValue(r));
                        if (y < line.Top) line.Top = y;
                        if (y + h > line.Bottom) line.Bottom = y + h;
                        sum += h; n++;
                    }
                    if (n == 0) continue;
                    line.Height = sum / n;
                    lines.Add(line);
                }
                lines.Sort((a, b) => a.Top.CompareTo(b.Top));
                return lines;
            }
            catch { return null; }
        }

        /// <summary>Recognizes one page image. Returns the text, or null when the
        /// engine could not be built. An image with no text in it returns an empty
        /// string, not null — measured, and the caller needs the difference to
        /// tell "nothing on this page" from "OCR is not working".</summary>
        /// <param name="languageTag">Empty for the user's own languages.</param>
        public static string Read(byte[] pageImage, string languageTag)
        {
            if (pageImage == null || pageImage.Length == 0) return "";
            object engine = EngineFor(languageTag);
            if (engine == null) return null;
            try
            {
                byte[] prepared = Stretch(pageImage);
                object soft = Decode(prepared ?? pageImage);
                if (soft == null) return null;
                object res = Await(engine.GetType().GetMethod("RecognizeAsync")
                                         .Invoke(engine, new object[] { soft }), tResult);
                return (string)tResult.GetProperty("Text").GetValue(res) ?? "";
            }
            catch { return null; }
        }

        /// <summary>The engine for a tag, or the user's own if the tag is empty or
        /// not installed. Cached — see <see cref="engines"/>.</summary>
        private static object EngineFor(string languageTag)
        {
            Probe();
            if (tEngine == null) return null;
            string key = languageTag ?? "";
            lock (gate)
            {
                object cached;
                if (engines.TryGetValue(key, out cached)) return cached;

                object engine = null;
                try
                {
                    if (key.Length > 0)
                    {
                        object lang = Activator.CreateInstance(tLanguage, new object[] { key });
                        engine = tEngine.GetMethod("TryCreateFromLanguage",
                            BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { lang });
                    }
                }
                catch { engine = null; }

                // No tag, or a tag this machine does not have: fall back rather
                // than fail — a reading in the wrong language beats no reading at
                // all, and a setting made on one machine must not break NBR on
                // another. It is NOT a claim that the language does not matter:
                // it does (see the class comment), which is why the reader is
                // asked whenever there is more than one to ask about.
                if (engine == null)
                {
                    try
                    {
                        engine = tEngine.GetMethod("TryCreateFromUserProfileLanguages",
                            BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                    }
                    catch { }
                }

                engines[key] = engine;
                return engine;
            }
        }

        /// <summary>The tag the engine actually used, for the book to remember —
        /// so a later Windows language install can be recognised as a reason to
        /// run the book again.</summary>
        public static string ResolvedLanguage(string requested)
        {
            object engine = EngineFor(requested);
            if (engine == null) return "";
            try
            {
                object l = engine.GetType().GetProperty("RecognizerLanguage").GetValue(engine);
                return (string)l.GetType().GetProperty("LanguageTag").GetValue(l) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Opens the Windows page where a language is added.
        ///
        /// <para>NBR cannot install one itself and does not try. Measured:
        /// <c>Install-Language</c> throws access-denied BEFORE it even validates
        /// the language tag, so the elevation check comes first — a "Download"
        /// button here would be a lie unless it relaunched elevated, and elevating
        /// to install operating-system components is not a thing a book reader
        /// should do.</para>
        ///
        /// <para><b>The page ids are read out of Windows' own Settings binary,
        /// not guessed.</b> The first attempt guessed <c>ms-settings:language</c>,
        /// which simply is not a page — Gordan got the front of Settings and had
        /// to find the rest himself. Scanning
        /// <c>ImmersiveControlPanel\SystemSettings.dll</c> lists what really
        /// exists: <c>regionlanguage</c>, <c>regionlanguage-adddisplaylanguage</c>,
        /// <c>regionlanguage-languageoptions</c> and the input-method pages. An
        /// unknown id does not fail — it opens Settings at the top, which is
        /// exactly the useless outcome this is avoiding, so the specific page goes
        /// first and the general one is only the safety net.</para></summary>
        public static bool OpenWindowsLanguageSettings()
        {
            foreach (string uri in new[] { "ms-settings:regionlanguage-adddisplaylanguage",
                                           "ms-settings:regionlanguage" })
            {
                try
                {
                    using (var p = System.Diagnostics.Process.Start(uri))
                        return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Every OCR pack Windows can install, as its capability name.
        ///
        /// <para><b>Read off a real machine, not from documentation</b> — Gordan
        /// ran <c>Get-WindowsCapability -Online -Name "Language.OCR*"</c>
        /// elevated on Windows 11 26200 and this is the answer, 35 packs with
        /// hr-HR installed and the rest NotPresent. NBR cannot produce this list
        /// itself: enumerating capabilities needs elevation, the same as
        /// installing one.</para>
        ///
        /// <para><b>Note the casing, because it is not BCP-47 and guessing it
        /// would fail.</b> The script subtag is UPPER case in a capability name —
        /// <c>sr-LATN-RS</c>, <c>bs-LATN-BA</c>, <c>sr-CYRL-RS</c> — where the
        /// language tag the OCR engine reports is <c>sr-Latn-RS</c>. So the two
        /// are kept side by side rather than derived from each other.</para>
        ///
        /// <para>A catalogue, not a promise: it is what one build offered. A pack
        /// that a given Windows does not have simply fails to install, and says
        /// so, which is a better failure than a list we refuse to show.</para></summary>
        public static readonly string[] InstallableLanguages =
        {
            "ar-SA", "bg-BG", "bs-LATN-BA", "cs-CZ", "da-DK", "de-DE", "el-GR",
            "en-GB", "en-US", "es-ES", "es-MX", "fi-FI", "fr-CA", "fr-FR",
            "hr-HR", "hu-HU", "it-IT", "ja-JP", "ko-KR", "nb-NO", "nl-NL",
            "pl-PL", "pt-BR", "pt-PT", "ro-RO", "ru-RU", "sk-SK", "sl-SI",
            "sr-CYRL-RS", "sr-LATN-RS", "sv-SE", "tr-TR", "zh-CN", "zh-HK", "zh-TW"
        };

        /// <summary>The Windows capability name for a pack.</summary>
        public static string CapabilityName(string catalogueTag)
        {
            return "Language.OCR~~~" + catalogueTag + "~0.0.1.0";
        }

        /// <summary>A catalogue entry's name, <b>in its own language</b> — see
        /// <see cref="LanguageDetector.DisplayName"/> for why that is the rule
        /// everywhere, and asked THERE so there is one answer and not two.</summary>
        public static string DisplayNameFor(string catalogueTag)
        {
            // BCP-47 wants Latn, the capability name spells LATN — normalise
            // before asking .NET, or every scripted tag comes back unknown.
            string[] parts = (catalogueTag ?? "").Split('-');
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].Length == 4)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
            string name = LanguageDetector.DisplayName(string.Join("-", parts));
            return string.IsNullOrEmpty(name) ? catalogueTag : name;
        }

        /// <summary>Whether a catalogue entry is already on this machine. Compared
        /// on the language, not the exact tag: the catalogue says
        /// <c>sr-LATN-RS</c> and the engine reports <c>sr-Latn-RS</c>.</summary>
        public static bool IsInstalled(string catalogueTag)
        {
            string want = (catalogueTag ?? "").Replace("-", "").ToLowerInvariant();
            foreach (var l in Languages)
                if ((l.Tag ?? "").Replace("-", "").ToLowerInvariant() == want) return true;
            return false;
        }

        /// <summary>Starts an elevated install of one OCR pack, and hands back the
        /// process so the caller can wait for it.
        ///
        /// <para><b>A separate process, because a running one cannot elevate
        /// itself</b> — the token is fixed at launch. Windows shows its own
        /// consent prompt, which is the right gate: NBR asks for nothing and
        /// decides nothing, the user approves an operating-system change in the
        /// operating system's own dialog. (On a machine set to "never notify"
        /// that prompt does not appear, which is that machine's setting and not
        /// something NBR arranged.)</para>
        ///
        /// <para>Returns null if the user dismissed the consent prompt, or if
        /// nothing could be started. This installs ONLY the recognition pack —
        /// about a quarter of a megabyte of model — and not a whole Windows
        /// display language, which was Gordan's objection to the obvious route
        /// (<c>Install-Language</c>) and a fair one.</para></summary>
        public static System.Diagnostics.Process BeginInstall(params string[] catalogueTags)
        {
            if (catalogueTags == null) return null;
            var names = new List<string>();
            foreach (string t in catalogueTags) names.Add(CapabilityName(t));
            return BeginInstallCapabilities(names.ToArray());
        }

        /// <summary>The same, for any Feature on Demand — voices as well as
        /// recognition. See <see cref="LanguagePackFamily"/>.</summary>
        public static System.Diagnostics.Process BeginInstallCapabilities(params string[] capabilityNames)
        {
            try
            {
                if (capabilityNames == null || capabilityNames.Length == 0) return null;

                // ONE elevated process for the whole batch, not one per language.
                // Each would carry its own consent prompt, and a reader choosing
                // four languages would be answering Windows four times for a
                // single decision they have already made. The servicing stack
                // takes them one after another anyway.
                var sb = new StringBuilder();
                foreach (string name in capabilityNames)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append("Add-WindowsCapability -Online -Name '").Append(name).Append("'");
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                                "-Command \"" + sb + "\"",
                    UseShellExecute = true,      // required for the elevation verb
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                return System.Diagnostics.Process.Start(psi);
            }
            catch { return null; }               // includes "the user said no"
        }

        /// <summary>Forget the cached engines and re-probe the language list —
        /// after an install, so a pack that has just arrived can be used without
        /// restarting NBR.</summary>
        public static void Rescan()
        {
            lock (gate) { engines.Clear(); }
        }

        // ---------------------------------------------------------------
        // Image preparation

        /// <summary>Pushes the darkest and lightest few per cent to black and
        /// white, in greyscale.
        ///
        /// <para>The only pre-processing that helped. Measured on a faded
        /// two-column photocopy: untouched 692 words / 65 % plausible, this 654 /
        /// 66, <b>Otsu binarize 605 / 54</b>, sharpen 656 / 63, invert identical.
        /// The gain is modest but real in kind rather than degree — the same
        /// passage went from <c>porodica (tx)raźine)</c> to
        /// <c>porodica Boraginacee (boraźine)</c>. Note what the word counts do:
        /// the BETTER pass returned FEWER words. Never tune this on word count.</para>
        ///
        /// <para>Returns null if anything goes wrong, and the caller then uses the
        /// image untouched — this is an improvement, never a requirement.</para></summary>
        public static byte[] Stretch(byte[] png)
        {
            try
            {
                using (var src = new Bitmap(new MemoryStream(png)))
                {
                    int w = src.Width, h = src.Height;
                    if (w < 2 || h < 2) return null;

                    var grey = new byte[w * h];
                    var hist = new int[256];
                    using (var flat = src.PixelFormat == PixelFormat.Format24bppRgb
                        ? null : new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
                    {
                        Bitmap use = src;
                        if (flat != null)
                        {
                            using (var g = Graphics.FromImage(flat)) g.DrawImage(src, 0, 0, w, h);
                            use = flat;
                        }
                        var data = use.LockBits(new Rectangle(0, 0, w, h),
                            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            for (int y = 0; y < h; y++)
                            {
                                IntPtr row = data.Scan0 + y * data.Stride;
                                for (int x = 0; x < w; x++)
                                {
                                    int bch = System.Runtime.InteropServices.Marshal.ReadByte(row, x * 3);
                                    int gch = System.Runtime.InteropServices.Marshal.ReadByte(row, x * 3 + 1);
                                    int rch = System.Runtime.InteropServices.Marshal.ReadByte(row, x * 3 + 2);
                                    int v = (rch * 299 + gch * 587 + bch * 114) / 1000;
                                    grey[y * w + x] = (byte)v;
                                    hist[v]++;
                                }
                            }
                        }
                        finally { use.UnlockBits(data); }
                    }

                    long total = (long)w * h, acc = 0;
                    int lo = 0, hi = 255;
                    for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc > total * StretchPercentile) { lo = i; break; } }
                    acc = 0;
                    for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc > total * StretchPercentile) { hi = i; break; } }
                    if (hi <= lo + 8) return null;      // nothing to gain; leave it alone

                    using (var outp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        var d = outp.LockBits(new Rectangle(0, 0, w, h),
                            ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            for (int y = 0; y < h; y++)
                            {
                                IntPtr row = d.Scan0 + y * d.Stride;
                                for (int x = 0; x < w; x++)
                                {
                                    int v = (grey[y * w + x] - lo) * 255 / (hi - lo);
                                    byte c = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                                    System.Runtime.InteropServices.Marshal.WriteByte(row, x * 3, c);
                                    System.Runtime.InteropServices.Marshal.WriteByte(row, x * 3 + 1, c);
                                    System.Runtime.InteropServices.Marshal.WriteByte(row, x * 3 + 2, c);
                                }
                            }
                        }
                        finally { outp.UnlockBits(d); }

                        using (var ms = new MemoryStream())
                        {
                            outp.Save(ms, ImageFormat.Png);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------
        // WinRT plumbing

        /// <summary>Encoded image bytes → a SoftwareBitmap the engine accepts.</summary>
        private static object Decode(byte[] image)
        {
            object ras = miAsRandomAccessStream.Invoke(null, new object[] { new MemoryStream(image) });
            MethodInfo miCreate = tDecoder.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "CreateAsync" && m.GetParameters().Length == 1);
            object decoder = Await(miCreate.Invoke(null, new object[] { ras }), tDecoder);
            MethodInfo miGet = tDecoder.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "GetSoftwareBitmapAsync" && m.GetParameters().Length == 2);
            return Await(miGet.Invoke(decoder, new object[] {
                Enum.Parse(tPixFmt, "Bgra8"), Enum.Parse(tAlpha, "Premultiplied") }), tSoftBmp);
        }

        /// <summary>Unwraps an IAsyncOperation&lt;T&gt;.
        ///
        /// <para><b>The overload has to be picked by PARAMETER TYPE, not by
        /// shape.</b> Several AsTask overloads have one parameter and one generic
        /// argument — <c>IAsyncActionWithProgress&lt;T&gt;</c> among them — and
        /// selecting on the shape silently picks whichever came last, which fails
        /// later with a cast error a long way from the cause.</para></summary>
        internal static object Await(object op, Type resultType)
        {
            var task = (Task)miAsTask.MakeGenericMethod(resultType).Invoke(null, new object[] { op });
            task.Wait();
            return task.GetType().GetProperty("Result").GetValue(task);
        }

        internal static void AwaitAction(object action)
        {
            ((Task)miAsTaskAction.Invoke(null, new object[] { action })).Wait();
        }

        internal static Type Rt(string name) { return Type.GetType(name + WinRT); }

        internal static MethodInfo AsStreamForRead { get { Probe(); return miAsStreamForRead; } }

        private static void Probe()
        {
            lock (gate)
            {
                if (probed) return;
                probed = true;
                try
                {
                    tEngine = Rt("Windows.Media.Ocr.OcrEngine");
                    tResult = Rt("Windows.Media.Ocr.OcrResult");
                    tLanguage = Rt("Windows.Globalization.Language");
                    tDecoder = Rt("Windows.Graphics.Imaging.BitmapDecoder");
                    tSoftBmp = Rt("Windows.Graphics.Imaging.SoftwareBitmap");
                    tPixFmt = Rt("Windows.Graphics.Imaging.BitmapPixelFormat");
                    tAlpha = Rt("Windows.Graphics.Imaging.BitmapAlphaMode");

                    Type sysExt = Type.GetType("System.WindowsRuntimeSystemExtensions" + Facade);
                    Type strExt = Type.GetType("System.IO.WindowsRuntimeStreamExtensions" + Facade);
                    if (sysExt != null)
                        foreach (MethodInfo mi in sysExt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (mi.Name != "AsTask" || mi.GetParameters().Length != 1) continue;
                            Type p = mi.GetParameters()[0].ParameterType;
                            string def = p.IsGenericType ? p.GetGenericTypeDefinition().FullName : p.FullName;
                            if (def == "Windows.Foundation.IAsyncOperation`1" && mi.IsGenericMethodDefinition)
                                miAsTask = mi;
                            else if (def == "Windows.Foundation.IAsyncAction")
                                miAsTaskAction = mi;
                        }
                    if (strExt != null)
                        foreach (MethodInfo mi in strExt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (mi.GetParameters().Length != 1) continue;
                            if (mi.Name == "AsRandomAccessStream") miAsRandomAccessStream = mi;
                            if (mi.Name == "AsStreamForRead") miAsStreamForRead = mi;
                        }
                }
                catch
                {
                    tEngine = null;
                }
            }
        }
    }
}
