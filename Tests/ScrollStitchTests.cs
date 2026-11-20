using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QingJie;

internal static class ScrollStitchTests
{
    private static int passed;
    private static int Main()
    {
        try
        {
            Run("multiple exact downward seams and exports", MultipleFrames);
            Run("unchanged and ambiguous repeated pages", DuplicateAndAmbiguous);
            Run("unrelated, horizontal, upward, wrong-size and jump rejection", RejectAndRecover);
            Run("fixed header/footer retained only once", FixedEdges);
            Run("sparse text-like document and small scroll", SparseDocument);
            Run("small local pixel noise", LocalNoise);
            Run("height/pixel limits leave state intact", Limits);
            Run("independent ownership, disposal and alpha pixels", Ownership);
            Run("minimum integration viewport", SmallViewport);
            Run("white text pages and chat with moving scrollbars", RealTextPages);
            Run("different sparse text is not a valid overlap", DifferentSparseText);
            Run("bounded independent previews include full stitched height", Previews);
            Console.WriteLine("PASS " + passed + " scrolling stitch regression groups (synthetic images only)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run(string name, Action test)
    { test(); passed++; Console.WriteLine("PASS " + name); }

    private static void MultipleFrames()
    {
        using (Bitmap document = Pattern(180, 1000))
        using (Bitmap first = Slice(document, 0, 240))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            foreach (int offset in new int[] { 45, 95, 173 })
            using (Bitmap frame = Slice(document, offset, 240))
            {
                Assert(stitcher.AddFrame(frame) == ScrollFrameResult.Appended, "append at " + offset);
                Assert(stitcher.Height == 240 + offset, "exact accumulated height");
                using (Bitmap expected = Slice(document, 0, 240 + offset))
                using (Bitmap output = stitcher.ExportImage()) Equal(expected, output);
            }
        }
    }

    private static void DuplicateAndAmbiguous()
    {
        using (Bitmap document = Pattern(96, 500))
        using (Bitmap first = Slice(document, 0, 180))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        using (Bitmap duplicate = (Bitmap)first.Clone())
        {
            Assert(stitcher.AddFrame(duplicate) == ScrollFrameResult.Unchanged, "duplicate is unchanged");
            Assert(stitcher.Height == 180, "duplicate adds no pixels");
        }
        using (Bitmap repeated = new Bitmap(80, 500, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < repeated.Height; y++)
                for (int x = 0; x < repeated.Width; x++)
                    repeated.SetPixel(x, y, Color.FromArgb((x * 19 + y % 20 * 73) % 256,
                        (x * 7 + y % 20 * 37) % 256, (x * 11 + y % 20 * 13) % 256));
            using (Bitmap first = Slice(repeated, 0, 180))
            using (Bitmap shifted = Slice(repeated, 7, 180))
            using (ScrollStitcher stitcher = new ScrollStitcher(first))
            {
                Assert(stitcher.AddFrame(shifted) == ScrollFrameResult.NoMatch, "periodic page offset is ambiguous");
                Assert(stitcher.Height == 180, "ambiguous page leaves height unchanged");
                using (Bitmap output = stitcher.ExportImage()) Equal(first, output);
            }
        }
        using (Bitmap blank = new Bitmap(100, 160))
        using (Graphics graphics = Graphics.FromImage(blank))
        {
            graphics.Clear(Color.White);
            using (ScrollStitcher stitcher = new ScrollStitcher(blank))
                Assert(stitcher.AddFrame(blank) == ScrollFrameResult.Unchanged, "blank page does not grow");
        }
    }

    private static void RejectAndRecover()
    {
        using (Bitmap document = Pattern(147, 1400))
        using (Bitmap first = document.Clone(new Rectangle(0, 150, 140, 240), PixelFormat.Format32bppArgb))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            foreach (Rectangle bounds in new Rectangle[] {
                new Rectangle(7, 210, 140, 240), new Rectangle(0, 80, 140, 240),
                new Rectangle(0, 900, 140, 240), new Rectangle(0, 210, 139, 240) })
            using (Bitmap bad = document.Clone(bounds, PixelFormat.Format32bppArgb))
            {
                Assert(stitcher.AddFrame(bad) == ScrollFrameResult.NoMatch, "misaligned/jump input refused " + bounds);
                Assert(stitcher.Height == 240, "rejected frame does not mutate accumulated height");
                using (Bitmap output = stitcher.ExportImage()) Equal(first, output);
            }
            using (Bitmap good = document.Clone(new Rectangle(0, 210, 140, 240), PixelFormat.Format32bppArgb))
                Assert(stitcher.AddFrame(good) == ScrollFrameResult.Appended, "valid frame recovers after rejects");
            using (Bitmap expected = document.Clone(new Rectangle(0, 150, 140, 300), PixelFormat.Format32bppArgb))
            using (Bitmap output = stitcher.ExportImage()) Equal(expected, output);
        }
    }

    private static void FixedEdges()
    {
        const int header = 28, footer = 22, body = 190;
        using (Bitmap document = Pattern(160, 800))
        using (Bitmap first = FixedViewport(document, 0, body, header, footer))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            foreach (int offset in new int[] { 35, 83, 127 })
            using (Bitmap frame = FixedViewport(document, offset, body, header, footer))
            {
                Assert(stitcher.AddFrame(frame) == ScrollFrameResult.Appended, "fixed edge append " + offset);
                using (Bitmap expected = FixedViewport(document, 0, body + offset, header, footer))
                using (Bitmap output = stitcher.ExportImage()) Equal(expected, output);
            }
        }
    }

    private static void SparseDocument()
    {
        using (Bitmap document = new Bitmap(280, 1600, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(document))
            using (Font font = new Font("Arial", 11))
            {
                graphics.Clear(Color.White);
                for (int line = 0; line < 52; line++)
                {
                    graphics.DrawString("Section " + line + " : item " + (line * 173), font, Brushes.Black, 15, line * 29);
                    graphics.FillRectangle(Brushes.SteelBlue, 15, line * 29 + 20, 40 + line * 17 % 210, 3);
                }
            }
            using (Bitmap first = Slice(document, 0, 400))
            using (ScrollStitcher stitcher = new ScrollStitcher(first))
            {
                foreach (int offset in new int[] { 1, 74, 163, 291 })
                using (Bitmap frame = Slice(document, offset, 400))
                    Assert(stitcher.AddFrame(frame) == ScrollFrameResult.Appended, "sparse page append " + offset);
                using (Bitmap expected = Slice(document, 0, 691))
                using (Bitmap output = stitcher.ExportImage()) Equal(expected, output);
            }
        }
    }

    private static void LocalNoise()
    {
        using (Bitmap document = Pattern(140, 600))
        using (Bitmap first = Slice(document, 0, 240))
        using (Bitmap next = Slice(document, 50, 240))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            next.SetPixel(42, 32, Color.White);
            Assert(stitcher.AddFrame(next) == ScrollFrameResult.Appended, "isolated change does not hide a strong match");
            using (Bitmap expected = Slice(document, 0, 290))
            using (Bitmap output = stitcher.ExportImage()) Equal(expected, output);
        }
    }

    private static void Limits()
    {
        using (Bitmap document = Pattern(96, 500))
        using (Bitmap first = Slice(document, 0, 160))
        using (Bitmap next = Slice(document, 40, 160))
        {
            using (ScrollStitcher heightLimited = new ScrollStitcher(first, 180, 80000000))
            using (ScrollStitcher pixelsLimited = new ScrollStitcher(first, 32767, 96 * 180))
            {
                Assert(heightLimited.AddFrame(next) == ScrollFrameResult.LimitReached, "height guarded before commit");
                Assert(pixelsLimited.AddFrame(next) == ScrollFrameResult.LimitReached, "pixel budget guarded before commit");
                Assert(heightLimited.Height == 160 && pixelsLimited.Height == 160, "limit leaves height unchanged");
                using (Bitmap output = heightLimited.ExportImage()) Equal(first, output);
                Assert(heightLimited.AddFrame(first) == ScrollFrameResult.Unchanged, "limit does not prevent duplicate detection");
            }
            bool rejected = false;
            try { using (ScrollStitcher invalid = new ScrollStitcher(first, 120, 80000000)) { } }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "first frame limit checked");
        }
    }

    private static void Ownership()
    {
        using (Bitmap document = Pattern(80, 500))
        {
            Bitmap first = Slice(document, 0, 160);
            ScrollStitcher stitcher = new ScrollStitcher(first);
            first.SetPixel(0, 0, Color.Magenta); first.Dispose();
            using (Bitmap next = Slice(document, 40, 160))
                Assert(stitcher.AddFrame(next) == ScrollFrameResult.Appended, "caller can dispose accepted inputs");
            using (Bitmap output = stitcher.ExportImage())
            {
                output.SetPixel(1, 1, Color.Magenta);
                using (Bitmap again = stitcher.ExportImage())
                using (Bitmap expected = Slice(document, 0, 200)) Equal(expected, again);
            }
            Bitmap survivor = stitcher.ExportImage();
            stitcher.Dispose(); stitcher.Dispose();
            using (survivor) Assert(survivor.Height == 200, "export survives stitcher disposal");
            bool threw = false;
            try { stitcher.ExportImage(); } catch (ObjectDisposedException) { threw = true; }
            Assert(threw, "disposed access rejected");
        }
        using (Bitmap alpha = new Bitmap(32, 64, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < alpha.Height; y++) for (int x = 0; x < alpha.Width; x++)
                alpha.SetPixel(x, y, Color.FromArgb((x * 7 + y) % 256, (x * 13) % 256, y * 3 % 256, 155));
            using (ScrollStitcher stitcher = new ScrollStitcher(alpha))
            using (Bitmap output = stitcher.ExportImage()) Equal(alpha, output);
        }
    }

    private static void SmallViewport()
    {
        using (Bitmap document = Pattern(32, 180))
        using (Bitmap first = Slice(document, 0, 64))
        using (Bitmap next = Slice(document, 20, 64))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            Assert(stitcher.AddFrame(next) == ScrollFrameResult.Appended, "32x64 viewport supported");
            using (Bitmap expected = Slice(document, 0, 84))
            using (Bitmap output = stitcher.ExportImage()) Equal(expected, output);
        }
    }

    private static Bitmap Pattern(int width, int height)
    {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    uint value = unchecked((uint)((y + 1) * 374761393) + (uint)(x * 668265263));
                    value = (value ^ (value >> 13)) * 1274126177;
                    row[x * 4] = (byte)value; row[x * 4 + 1] = (byte)(value >> 8);
                    row[x * 4 + 2] = (byte)(value >> 16); row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        finally { image.UnlockBits(data); }
        return image;
    }

    private static void RealTextPages()
    {
        foreach (bool chat in new bool[] { false, true })
        foreach (bool scrollbar in new bool[] { false, true })
        using (Bitmap page = RealPage(chat, false))
        using (Bitmap first = RealViewport(page, 0, chat ? 480 : 630, chat, scrollbar, false))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            int body = chat ? 480 : 630;
            foreach (int offset in new int[] { 36, 91, 173, 291, 430, 560 })
            using (Bitmap frame = RealViewport(page, offset, body, chat, scrollbar, offset % 2 != 0))
            {
                Assert(stitcher.AddFrame(frame) == ScrollFrameResult.Appended,
                    "sparse content matches despite whitespace, scrollbar/caret: " + chat + "/" + scrollbar + "/" + offset);
                Assert(stitcher.Height == 700 + offset, "exact sparse page scroll distance");
            }
            using (Bitmap expected = RealViewport(page, 0, body + 560, chat, scrollbar, false))
            using (Bitmap actual = stitcher.ExportImage())
                EqualInterior(expected, actual, scrollbar ? 16 : 0);
            // No scrolling, just a caret blink / scrollbar position: not a failed
            // overlap and not another copy of the same page.
            using (Bitmap idle = RealViewport(page, 560, body, chat, scrollbar, true))
                Assert(stitcher.AddFrame(idle) == ScrollFrameResult.Unchanged, "fixed-bar animation leaves content unchanged");
        }
    }

    private static void DifferentSparseText()
    {
        using (Bitmap firstPage = RealPage(true, false))
        using (Bitmap otherPage = RealPage(true, true))
        using (Bitmap first = RealViewport(firstPage, 0, 480, true, true, false))
        using (Bitmap unrelated = RealViewport(otherPage, 91, 480, true, true, false))
        using (Bitmap good = RealViewport(firstPage, 91, 480, true, true, false))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            Assert(stitcher.AddFrame(unrelated) == ScrollFrameResult.NoMatch, "white margins cannot validate unrelated text");
            Assert(stitcher.Height == 700, "bad sparse page does not advance anchor");
            Assert(stitcher.AddFrame(good) == ScrollFrameResult.Appended, "valid content can recover after sparse mismatch");
        }
    }

    private static Bitmap RealPage(bool chat, bool unrelated)
    {
        Bitmap page = new Bitmap(900, 3200, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(page))
        using (Font font = new Font("Microsoft YaHei UI", 11))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            for (int i = 0; i < 38; i++)
            {
                string text = unrelated ? "Different conversation number " + (i * 173 + 900) :
                    chat ? "message " + i : "Article paragraph " + i + " This is an ordinary line of text, followed by details.";
                graphics.DrawString(text, font, Brushes.Black, 110, 20 + i * 81);
            }
        }
        return page;
    }

    private static Bitmap RealViewport(Bitmap page, int offset, int body, bool chat, bool scrollbar, bool caret)
    {
        int footerHeight = chat ? 150 : 0;
        Bitmap image = new Bitmap(900, 70 + body + footerHeight, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (Font font = new Font("Microsoft YaHei UI", 12))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(page, new Rectangle(0, 70, 900, body),
                new Rectangle(0, offset, 900, body), GraphicsUnit.Pixel);
            graphics.FillRectangle(Brushes.WhiteSmoke, 0, 0, 900, 70);
            graphics.DrawString("Navigation", font, Brushes.Black, 110, 20);
            if (chat)
            {
                graphics.FillRectangle(Brushes.WhiteSmoke, 0, 70 + body, 900, footerHeight);
                graphics.DrawRectangle(Pens.Gray, 90, 70 + body + 22, 700, 83);
                graphics.DrawString("Type a message", font, Brushes.Gray, 110, 70 + body + 42);
                if (caret) graphics.FillRectangle(Brushes.Black, 229, 70 + body + 44, 2, 17);
            }
            if (scrollbar)
            {
                graphics.FillRectangle(Brushes.WhiteSmoke, 884, 70, 16, body);
                graphics.FillRectangle(Brushes.Gray, 887, 70 + offset / 7, 10, 110);
            }
        }
        return image;
    }

    private static void Previews()
    {
        using (Bitmap page = Pattern(120, 500))
        using (Bitmap first = Slice(page, 0, 160))
        using (Bitmap next = Slice(page, 60, 160))
        using (ScrollStitcher stitcher = new ScrollStitcher(first))
        {
            using (Bitmap preview = stitcher.CreatePreview(60, 90))
                Assert(preview.Size == new Size(60, 80), "first-frame preview fits without stretching");
            using (Graphics graphics = Graphics.FromImage(next))
                graphics.FillRectangle(Brushes.Lime, 0, 140, next.Width, 20);
            Assert(stitcher.AddFrame(next) == ScrollFrameResult.Appended, "preview fixture appends");
            Bitmap survivor = stitcher.CreatePreview(60, 90);
            Assert(survivor.Size == new Size(49, 90), "preview aspect uses complete new height");
            Assert(survivor.GetPixel(20, 85).ToArgb() == Color.Lime.ToArgb(), "preview includes the newest appended bottom");
            Color original = survivor.GetPixel(10, 10);
            survivor.SetPixel(10, 10, Color.Magenta);
            using (Bitmap again = stitcher.CreatePreview(60, 90))
                Assert(again.GetPixel(10, 10) == original, "preview is independent");
            using (Bitmap fullSizePreview = stitcher.CreatePreview(Int32.MaxValue, Int32.MaxValue))
                Assert(fullSizePreview.Size == new Size(120, 220), "preview never enlarges");
            bool rejected = false;
            try { stitcher.CreatePreview(0, 20); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "invalid preview bounds rejected");
            stitcher.Dispose();
            using (survivor) Assert(survivor.GetPixel(10, 10).ToArgb() == Color.Magenta.ToArgb(), "preview survives producer disposal");
        }
    }

    private static void EqualInterior(Bitmap expected, Bitmap actual, int ignoredRight)
    {
        Assert(expected.Size == actual.Size, "sparse export bounds");
        Rectangle bounds = new Rectangle(Point.Empty, expected.Size);
        BitmapData a = expected.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData b = actual.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] left = new byte[(expected.Width - ignoredRight) * 4], right = new byte[left.Length];
                for (int y = 0; y < expected.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), left, 0, left.Length);
                    Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), right, 0, right.Length);
                    for (int x = 0; x < left.Length; x++)
                        if (left[x] != right[x]) throw new Exception("sparse seam mismatch at " + x / 4 + "," + y);
                }
            }
            finally { actual.UnlockBits(b); }
        }
        finally { expected.UnlockBits(a); }
    }
    private static Bitmap Slice(Bitmap image, int y, int height)
    { return image.Clone(new Rectangle(0, y, image.Width, height), PixelFormat.Format32bppArgb); }
    private static Bitmap FixedViewport(Bitmap document, int offset, int body, int header, int footer)
    {
        Bitmap image = new Bitmap(document.Width, body + header + footer, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.DrawImage(document, new Rectangle(0, header, image.Width, body),
                new Rectangle(0, offset, image.Width, body), GraphicsUnit.Pixel);
            graphics.FillRectangle(Brushes.DarkSlateBlue, 0, 0, image.Width, header);
            graphics.FillRectangle(Brushes.Orange, 6, 4, 22, header - 8);
            graphics.FillRectangle(Brushes.DarkSlateGray, 0, header + body, image.Width, footer);
            graphics.FillRectangle(Brushes.LightGreen, 9, header + body + 4, 37, footer - 8);
        }
        return image;
    }
    private static void Equal(Bitmap expected, Bitmap actual)
    {
        Assert(expected.Size == actual.Size, "export dimensions " + expected.Size + " != " + actual.Size);
        for (int y = 0; y < expected.Height; y++) for (int x = 0; x < expected.Width; x++)
            if (expected.GetPixel(x, y).ToArgb() != actual.GetPixel(x, y).ToArgb())
                throw new Exception("pixel mismatch at " + x + "," + y);
    }
    private static void Assert(bool condition, string description)
    { if (!condition) throw new Exception(description); }
}
