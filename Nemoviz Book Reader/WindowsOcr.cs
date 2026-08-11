using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// elevation. It matters less than it sounds: recognition is by SCRIPT, and
    /// an English page through the Croatian engine measured 0.0 % character
    /// error. See <see cref="OcrPageSource"/> for the one input Windows really
    /// cannot handle.</para>
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
                        string name = tl.GetProperty("DisplayName").GetValue(l) as string;
                        if (!string.IsNullOrEmpty(tag)) list.Add((tag, name ?? tag));
                    }
                }
                catch { }
                return list;
            }
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
                // than fail. A Latin recognizer reads Latin script whatever the
                // language — an English page through the hr-HR engine measured
                // 0.0 % character error — so the fallback is genuinely useful and
                // not just a way of avoiding an error message.
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
