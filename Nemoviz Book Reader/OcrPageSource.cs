using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Nemoviz_Book_Reader
{
    /// <summary>Why a document cannot be read by OCR, when it cannot.</summary>
    public enum OcrRefusal
    {
        None = 0,
        /// <summary>OCR is not available at all — no recognizer language installed.</summary>
        NoEngine,
        /// <summary>The PDF stores its text as a JBIG2 mask, which Windows will not
        /// draw. See <see cref="OcrPageSource.UsesJbig2"/> — this is emphatically
        /// NOT an OCR failure and must not be reported as one.</summary>
        UndrawablePdf,
        /// <summary>Nothing in it that could hold text.</summary>
        NoPages
    }

    /// <summary>
    /// The pages of an image document, whatever shape it arrived in, handed out
    /// one encoded image at a time.
    ///
    /// <para>Gordan's list, and all of it is covered: a multi-page document, or
    /// "a pile of numbered jpegs", or a single image, or a PDF. Concretely —
    /// <b>PDF</b> (rasterized by <c>Windows.Data.Pdf</c>), <b>a folder of
    /// images</b> in natural name order, <b>a multi-frame TIFF</b> (measured:
    /// <c>FrameCount</c> and <c>GetFrameAsync</c> hand back each page separately,
    /// so one TIFF can be a whole book), and any single <b>PNG / JPEG / BMP /
    /// GIF / TIFF</b>.</para>
    ///
    /// <para><b>The one thing Windows cannot do</b> is draw a JBIG2 image mask,
    /// which is exactly how a mass-digitized scanned book stores its text: a
    /// JPEG 2000 background holding the paper, and a JBIG2 bitonal mask holding
    /// the words. Windows renders the background and silently omits the mask, so
    /// every page comes out as blank paper and OCR honestly reports nothing —
    /// measured, 0 words on 32 of 32 sampled pages of one archive.org book. That
    /// looks exactly like a broken scan and is not one, so it is detected up
    /// front (<see cref="UsesJbig2"/>) and reported as its own refusal. Rare in
    /// practice: 2 of 109 real local PDFs, both of them mass-digitized.</para>
    /// </summary>
    public class OcrPageSource : IDisposable
    {
        /// <summary>Image extensions we will read as book pages.</summary>
        public static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff" };

        private readonly List<string> files = new List<string>();   // folder / single image
        private object pdfDoc;                                      // Windows.Data.Pdf.PdfDocument
        private byte[] tiff;                                        // multi-frame TIFF bytes
        private uint frames;

        public int PageCount { get; private set; }
        public OcrRefusal Refusal { get; private set; }

        private OcrPageSource() { }

        public static bool IsImageFile(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ImageExtensions.Contains(ext);
        }

        /// <summary>Opens whatever the user pointed at. Never throws; a source
        /// that cannot be read comes back with <see cref="Refusal"/> set and
        /// <see cref="PageCount"/> at zero.</summary>
        public static OcrPageSource Open(string path)
        {
            var src = new OcrPageSource();
            try
            {
                if (!WindowsOcr.Available) { src.Refusal = OcrRefusal.NoEngine; return src; }

                if (Directory.Exists(path))
                {
                    src.files.AddRange(Directory.EnumerateFiles(path)
                        .Where(IsImageFile)
                        .OrderBy(f => Path.GetFileName(f), NaturalOrder.Comparer));
                    src.PageCount = src.files.Count;
                }
                else if (!File.Exists(path))
                {
                    src.Refusal = OcrRefusal.NoPages;
                }
                else if ((Path.GetExtension(path) ?? "").ToLowerInvariant() == ".pdf")
                {
                    if (UsesJbig2(path)) { src.Refusal = OcrRefusal.UndrawablePdf; return src; }
                    src.OpenPdf(path);
                }
                else if (IsImageFile(path))
                {
                    // A TIFF may be the whole book. Everything else is one page.
                    src.tiff = File.ReadAllBytes(path);
                    src.frames = FrameCount(src.tiff);
                    if (src.frames > 1) src.PageCount = (int)src.frames;
                    else { src.tiff = null; src.files.Add(path); src.PageCount = 1; }
                }
            }
            catch { src.PageCount = 0; }

            if (src.Refusal == OcrRefusal.None && src.PageCount == 0)
                src.Refusal = OcrRefusal.NoPages;
            return src;
        }

        /// <summary>One page as encoded image bytes, or null if that page cannot
        /// be produced. Zero-based.</summary>
        public byte[] Page(int index)
        {
            try
            {
                if (index < 0 || index >= PageCount) return null;
                if (pdfDoc != null) return RenderPdfPage((uint)index);
                if (tiff != null) return Frame(tiff, (uint)index);
                return File.ReadAllBytes(files[index]);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------
        // PDF

        private void OpenPdf(string path)
        {
            Type tStorageFile = WindowsOcr.Rt("Windows.Storage.StorageFile");
            Type tPdfDoc = WindowsOcr.Rt("Windows.Data.Pdf.PdfDocument");
            if (tStorageFile == null || tPdfDoc == null) { Refusal = OcrRefusal.NoPages; return; }

            object file = WindowsOcr.Await(
                tStorageFile.GetMethod("GetFileFromPathAsync", BindingFlags.Public | BindingFlags.Static)
                            .Invoke(null, new object[] { path }), tStorageFile);
            MethodInfo miLoad = tPdfDoc.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "LoadFromFileAsync" && m.GetParameters().Length == 1);
            pdfDoc = WindowsOcr.Await(miLoad.Invoke(null, new object[] { file }), tPdfDoc);
            PageCount = (int)(uint)tPdfDoc.GetProperty("PageCount").GetValue(pdfDoc);
        }

        private byte[] RenderPdfPage(uint index)
        {
            Type tPdfDoc = WindowsOcr.Rt("Windows.Data.Pdf.PdfDocument");
            Type tPdfPage = WindowsOcr.Rt("Windows.Data.Pdf.PdfPage");
            Type tOpts = WindowsOcr.Rt("Windows.Data.Pdf.PdfPageRenderOptions");
            Type tMem = WindowsOcr.Rt("Windows.Storage.Streams.InMemoryRandomAccessStream");

            object page = tPdfDoc.GetMethod("GetPage").Invoke(pdfDoc, new object[] { index });
            object size = tPdfPage.GetProperty("Size").GetValue(page);
            double wPt = Convert.ToDouble(size.GetType().GetProperty("Width").GetValue(size));
            double hPt = Convert.ToDouble(size.GetType().GetProperty("Height").GetValue(size));
            double biggest = Math.Max(wPt, hPt);
            if (biggest < 1) return null;

            // Aim the LONG SIDE at a pixel count. Not a DPI — see TargetLongSide.
            double scale = WindowsOcr.TargetLongSide / biggest;
            if (biggest * scale > WindowsOcr.HardMaxDimension)
                scale = WindowsOcr.HardMaxDimension / biggest;

            object opts = Activator.CreateInstance(tOpts);
            tOpts.GetProperty("DestinationHeight").SetValue(opts, (uint)Math.Max(1, Math.Round(hPt * scale)));
            object mem = Activator.CreateInstance(tMem);
            MethodInfo miRender = tPdfPage.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "RenderToStreamAsync" && m.GetParameters().Length == 2);
            WindowsOcr.AwaitAction(miRender.Invoke(page, new object[] { mem, opts }));
            tMem.GetMethod("Seek").Invoke(mem, new object[] { (ulong)0 });

            var src = (Stream)WindowsOcr.AsStreamForRead.Invoke(null, new object[] { mem });
            using (var ms = new MemoryStream())
            {
                src.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Does this PDF hide its text in a JBIG2 mask?
        ///
        /// <para>Read as raw bytes rather than parsed: the filter name appears in
        /// the image object's dictionary, which is not compressed, and this runs
        /// before we commit the user to minutes of work. Cheap and decisive.</para></summary>
        public static bool UsesJbig2(string path)
        {
            try
            {
                var needle = Encoding.ASCII.GetBytes("/JBIG2Decode");
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[1 << 20];
                    int carry = needle.Length - 1, n;
                    int fill = 0;
                    while ((n = fs.Read(buf, fill, buf.Length - fill)) > 0 || fill > 0)
                    {
                        int have = fill + n;
                        for (int i = 0; i + needle.Length <= have; i++)
                        {
                            int j = 0;
                            while (j < needle.Length && buf[i + j] == needle[j]) j++;
                            if (j == needle.Length) return true;
                        }
                        if (n <= 0) break;
                        // Keep the tail so a match spanning two buffers is found.
                        Array.Copy(buf, have - carry, buf, 0, carry);
                        fill = carry;
                    }
                }
            }
            catch { }
            return false;
        }

        // ---------------------------------------------------------------
        // Multi-frame TIFF

        private static uint FrameCount(byte[] image)
        {
            try
            {
                Type tDecoder = WindowsOcr.Rt("Windows.Graphics.Imaging.BitmapDecoder");
                object decoder = OpenDecoder(image, tDecoder);
                return (uint)tDecoder.GetProperty("FrameCount").GetValue(decoder);
            }
            catch { return 1; }
        }

        /// <summary>One frame of a multi-page TIFF, re-encoded as PNG so the rest
        /// of the pipeline sees an ordinary single image.</summary>
        private static byte[] Frame(byte[] image, uint index)
        {
            using (var ms = new MemoryStream(image))
            using (var img = Image.FromStream(ms))
            {
                img.SelectActiveFrame(FrameDimension.Page, (int)index);
                using (var frame = new Bitmap(img))
                using (var outp = new MemoryStream())
                {
                    frame.Save(outp, ImageFormat.Png);
                    return outp.ToArray();
                }
            }
        }

        private static object OpenDecoder(byte[] image, Type tDecoder)
        {
            MethodInfo asRas = WindowsOcr.AsStreamForRead.DeclaringType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "AsRandomAccessStream" && m.GetParameters().Length == 1);
            object ras = asRas.Invoke(null, new object[] { new MemoryStream(image) });
            MethodInfo miCreate = tDecoder.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "CreateAsync" && m.GetParameters().Length == 1);
            return WindowsOcr.Await(miCreate.Invoke(null, new object[] { ras }), tDecoder);
        }

        public void Dispose() { pdfDoc = null; tiff = null; }
    }

    /// <summary>Sorts "page2" before "page10", which is the whole point when the
    /// book arrived as a pile of numbered images.</summary>
    internal static class NaturalOrder
    {
        public static readonly IComparer<string> Comparer = new Impl();

        private class Impl : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                a = a ?? ""; b = b ?? "";
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string na = a.Substring(si, i - si).TrimStart('0');
                        string nb = b.Substring(sj, j - sj).TrimStart('0');
                        if (na.Length != nb.Length) return na.Length - nb.Length;
                        int c = string.CompareOrdinal(na, nb);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                return (a.Length - i) - (b.Length - j);
            }
        }
    }
}
