using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QingJie;

internal static class QingJiePerf
{
    private static readonly Size SourceSize = new Size(3840, 2160);
    private static readonly Size ViewSize = new Size(1600, 900);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern int GetUpdateRgn(IntPtr window, IntPtr region, bool erase);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr window, IntPtr rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        SetProcessDpiAwarenessContext(new IntPtr(-1));
        try
        {
            using (Bitmap source = MakeSource(args.Length > 1 && args[1] == "rgb"))
            {
                Console.WriteLine("SOURCE format=" + source.PixelFormat + " size=" + source.Size);
                if (args.Length > 0 && args[0] == "regression") Regressions(source);
                else if (args.Length > 0 && args[0] == "pen")
                {
                    Canvas(source, 0);
                    Canvas(source, 1);
                }
                else if (args.Length > 0 && args[0] == "overlay")
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Overlay(source, false, pass);
                        Overlay(source, true, pass);
                    }
                }
                else
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Overlay(source, false, pass);
                        Overlay(source, true, pass);
                        Canvas(source, pass);
                    }
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
            Console.WriteLine(error.StackTrace);
            return 1;
        }
    }

    private static Bitmap MakeSource(bool rgb)
    {
        Bitmap result = new Bitmap(SourceSize.Width, SourceSize.Height,
            rgb ? PixelFormat.Format32bppRgb : PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        using (LinearGradientBrush fill = new LinearGradientBrush(new Rectangle(Point.Empty, SourceSize),
            Color.FromArgb(225, 238, 251), Color.FromArgb(82, 116, 165), 35f))
        using (Pen lines = new Pen(Color.FromArgb(150, 70, 100, 140)))
        {
            g.FillRectangle(fill, new Rectangle(Point.Empty, SourceSize));
            for (int x = 0; x < SourceSize.Width; x += 64) g.DrawLine(lines, x, 0, x, SourceSize.Height);
            for (int y = 0; y < SourceSize.Height; y += 64) g.DrawLine(lines, 0, y, SourceSize.Width, y);
        }
        return result;
    }

    private static void Overlay(Bitmap source, bool coalesced, int pass)
    {
        Stopwatch setup = Stopwatch.StartNew();
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, SourceSize)))
        using (PaintDriver driver = new PaintDriver(overlay, SourceSize))
        {
            driver.Full();
            Mouse(overlay, "OnMouseDown", new Point(200, 150));
            driver.Flush(coalesced);
            double prepare = setup.Elapsed.TotalMilliseconds;
            List<double> samples = new List<double>();
            for (int i = 0; i < 180; i++)
            {
                Point point = new Point(800 + i * 15, 500 + i * 7 + (int)(65 * Math.Sin(i * .08)));
                long start = Stopwatch.GetTimestamp();
                Mouse(overlay, "OnMouseMove", point);
                driver.Flush(coalesced);
                if (i >= 20) samples.Add(Milliseconds(start));
            }
            Print("overlay-" + (coalesced ? "bounding" : "region"), pass, samples, prepare, 0, driver);
        }
    }

    private static void Canvas(Bitmap source, int pass)
    {
        Stopwatch setup = Stopwatch.StartNew();
        using (AnnotationCanvas canvas = new AnnotationCanvas())
        {
            canvas.Size = ViewSize;
            canvas.SetImage(source);
            canvas.Tool = DrawingTool.Pen;
            canvas.StrokeColor = Color.Red;
            canvas.StrokeWidth = 8;
            using (PaintDriver driver = new PaintDriver(canvas, ViewSize))
            {
                driver.Full();
                double prepare = setup.Elapsed.TotalMilliseconds;
                Stopwatch prefix = Stopwatch.StartNew();
                Mouse(canvas, "OnMouseDown", PenPoint(0));
                for (int i = 1; i < 1016; i++) Mouse(canvas, "OnMouseMove", PenPoint(i));
                driver.Flush(false);
                double prefixMs = prefix.Elapsed.TotalMilliseconds;
                List<double> samples = new List<double>();
                for (int i = 1016; i < 1200; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    Mouse(canvas, "OnMouseMove", PenPoint(i));
                    driver.Flush(false);
                    if (i >= 1040) samples.Add(Milliseconds(start));
                }
                long commit = Stopwatch.GetTimestamp();
                Mouse(canvas, "OnMouseUp", PenPoint(1199));
                double handlerMs = Milliseconds(commit);
                long paintStart = Stopwatch.GetTimestamp();
                driver.Flush(false);
                double paintMs = Milliseconds(paintStart);
                double commitMs = Milliseconds(commit);
                Print("pen-tail-1200points", pass, samples, prepare, commitMs, driver);
                Console.WriteLine("commit_handler_ms=" + handlerMs.ToString("F3") +
                    " commit_paint_ms=" + paintMs.ToString("F3"));
                Console.WriteLine("prefix_no_paint_ms=" + prefixMs.ToString("F3"));
            }
        }
    }

    private static Point PenPoint(int index)
    {
        return new Point(100 + (int)(1400.0 * index / 1199),
            450 + (int)(220 * Math.Sin(index * .020) + 55 * Math.Sin(index * .081)));
    }

    private static void Regressions(Bitmap source)
    {
#if !LEGACY_BASELINE
        try
        {
            using (ScreenCapture.CaptureArea(new Rectangle(-1920, -1080, 0, 1080))) { }
            throw new Exception("CaptureArea accepted an empty target.");
        }
        catch (ArgumentOutOfRangeException) { }
#endif
        using (Bitmap small = new Bitmap(32, 24))
        {
            for (int y = 0; y < small.Height; y++)
                for (int x = 0; x < small.Width; x++)
                    small.SetPixel(x, y, Color.FromArgb(255, x * 7, y * 9, (x + y) * 4));
            using (RegionOverlay offset = new RegionOverlay(small, new Rectangle(-1920, -1080, 32, 24)))
            {
                Mouse(offset, "OnMouseDown", new Point(4, 3));
                Mouse(offset, "OnMouseMove", new Point(20, 18));
                Mouse(offset, "OnMouseUp", new Point(20, 18));
                Assert(offset.SelectedRegion == new Rectangle(4, 3, 16, 15),
                    "Negative screen origin altered image-relative selection.");
                using (Bitmap selected = offset.Result)
                using (Bitmap expected = ScreenCapture.Crop(small, new Rectangle(4, 3, 16, 15)))
                    AssertPixels(expected, selected, "Negative-origin image crop");
            }
        }
        Console.WriteLine("PASS empty target rejected without capture; negative-origin synthetic selection preserves pixels");
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, SourceSize)))
        using (PaintDriver driver = new PaintDriver(overlay, SourceSize))
        {
            driver.Full();
            Mouse(overlay, "OnMouseDown", new Point(400, 300));
            driver.Flush(false);
            Point[] positions = { new Point(2300, 1700), new Point(3100, 900),
                new Point(700, 500), new Point(100, 100), new Point(1900, 1500) };
            foreach (Point point in positions)
            {
                Mouse(overlay, "OnMouseMove", point);
                driver.Flush(false);
                driver.AssertMatchesFull("selection dirty redraw at " + point);
            }
            Call(overlay, "ProcessCmdKey", new Message(), Keys.Escape);
            Assert(overlay.DialogResult == DialogResult.Cancel && overlay.Result == null,
                "Cancelling selection produced an image.");
        }
        Console.WriteLine("PASS region dirty redraw restores pixels and cancel has no result");
        using (AnnotationCanvas canvas = new AnnotationCanvas())
        {
            canvas.Size = ViewSize;
            canvas.SetImage(source);
            canvas.Tool = DrawingTool.Pen;
            canvas.StrokeWidth = 8;
            canvas.StrokeColor = Color.Red;
            using (PaintDriver driver = new PaintDriver(canvas, ViewSize))
            {
                driver.Full();
                Mouse(canvas, "OnMouseDown", PenPoint(0));
                driver.Flush(false);
                for (int i = 1; i < 1200; i++)
                {
                    Mouse(canvas, "OnMouseMove", PenPoint(i));
                    if (i % 40 == 0 || i == 1199) driver.Flush(false);
                    if (i == 200 || i == 600 || i == 1199)
                        driver.AssertMatchesFull("pen dirty redraw at " + i);
                }
                Mouse(canvas, "OnMouseUp", PenPoint(1199));
                driver.Flush(false);
                driver.AssertMatchesFull("pen committed redraw");
                using (Bitmap exported = canvas.ExportImage())
                using (Bitmap expected = (Bitmap)source.Clone())
                using (Graphics g = Graphics.FromImage(expected))
                using (Pen pen = new Pen(Color.Red, 8))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    RectangleF bounds = AnnotationCanvas.CalculateImageBounds(SourceSize, ViewSize, 32);
                    PointF[] points = new PointF[1200];
                    for (int i = 0; i < 1200; i++)
                        points[i] = AnnotationCanvas.MapClientToImage(PenPoint(i), bounds, SourceSize, true);
                    DrawExpectedCurve(g, pen, points);
                    Assert(exported.Size == SourceSize, "Export lost original dimensions.");
                    AssertPixels(expected, exported, "Full-resolution stroke export");
                }
                canvas.Undo();
                driver.Flush(false);
                using (Bitmap exported = canvas.ExportImage()) AssertPixels(source, exported, "Long stroke undo");
                driver.AssertMatchesFull("undo redraw");
                Mouse(canvas, "OnMouseDown", PenPoint(0));
                Mouse(canvas, "OnMouseMove", PenPoint(700));
                canvas.Size = new Size(1620, 920);
                Assert(!(bool)Field(canvas, "_dragging"), "Resize did not cancel the active gesture.");
                Mouse(canvas, "OnMouseUp", PenPoint(700));
                using (Bitmap exported = canvas.ExportImage()) AssertPixels(source, exported, "Resize cancellation");
                Assert(!canvas.CanUndo, "Resize cancellation committed an unwanted annotation.");
            }
        }
        Console.WriteLine("PASS long pen preview, original-resolution export, undo, resize cancellation");
        foreach (DrawingTool tool in new DrawingTool[] { DrawingTool.Rectangle, DrawingTool.Arrow, DrawingTool.Mosaic })
        {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            {
                canvas.Size = ViewSize;
                canvas.SetImage(source);
                canvas.Tool = tool;
                using (PaintDriver driver = new PaintDriver(canvas, ViewSize))
                {
                    driver.Full();
                    Mouse(canvas, "OnMouseDown", new Point(350, 300));
                    driver.Flush(false);
                    foreach (Point endpoint in new Point[] { new Point(1300, 700),
                        new Point(550, 400), new Point(100, 150), new Point(1200, 500) })
                    {
                        Mouse(canvas, "OnMouseMove", endpoint);
                        driver.Flush(false);
                        driver.AssertMatchesFull(tool + " dirty redraw");
                    }
                    Mouse(canvas, "OnMouseUp", new Point(1200, 500));
                    driver.Flush(false);
                    driver.AssertMatchesFull(tool + " committed redraw");
                    canvas.Undo();
                    driver.Flush(false);
                    driver.AssertMatchesFull(tool + " undo redraw");
                    using (Bitmap exported = canvas.ExportImage()) AssertPixels(source, exported, tool + " undo source");
                }
            }
        }
        Console.WriteLine("PASS rectangle, arrow, mosaic grow/shrink/reverse previews, commit, undo without ghost pixels");
    }

    // Independent reference geometry: convert successive midpoint quadratics to
    // cubic Beziers; deliberately do not call the production SmoothStroke helper.
    private static void DrawExpectedCurve(Graphics graphics, Pen pen, IList<PointF> points)
    {
        using (GraphicsPath path = new GraphicsPath())
        {
            PointF start = points[0];
            for (int index = 1; index < points.Count; index++)
            {
                PointF control = points[index - 1];
                PointF end = new PointF(control.X + (points[index].X - control.X) * .5f,
                    control.Y + (points[index].Y - control.Y) * .5f);
                PointF first = new PointF(start.X + (control.X - start.X) * (2f / 3f),
                    start.Y + (control.Y - start.Y) * (2f / 3f));
                PointF second = new PointF(end.X + (control.X - end.X) * (2f / 3f),
                    end.Y + (control.Y - end.Y) * (2f / 3f));
                path.AddBezier(start, first, second, end);
                start = end;
            }
            path.AddLine(start, points[points.Count - 1]);
            graphics.DrawPath(pen, path);
        }
    }

    private sealed class PaintDriver : IDisposable
    {
        private readonly Control control;
        private readonly List<Rectangle> invalid = new List<Rectangle>();
        public readonly Bitmap Surface;
        public long DirtyPixels;
        public int Paints, NativeRegions, FallbackRegions;
        public PaintDriver(Control target, Size size)
        {
            control = target;
            IntPtr handle = target.Handle;
            if (target.ClientSize != size) target.ClientSize = size;
            Assert(target.ClientSize == size, "Benchmark viewport size changed.");
            Surface = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            Surface.SetResolution(96, 96);
            target.Invalidated += delegate(object sender, InvalidateEventArgs e) { invalid.Add(e.InvalidRect); };
            Clear();
        }
        public void Full()
        {
            using (Region region = new Region(control.ClientRectangle)) Paint(region, control.ClientRectangle, Surface);
            Clear();
        }
        public void Flush(bool coalesced)
        {
            using (Region region = Pending())
            using (Graphics g = Graphics.FromImage(Surface))
            {
                Rectangle bounds = Rectangle.Ceiling(region.GetBounds(g));
                bounds.Intersect(control.ClientRectangle);
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    if (coalesced)
                    {
                        region.MakeEmpty();
                        region.Union(bounds);
                    }
                    using (Matrix identity = new Matrix())
                        foreach (RectangleF scan in region.GetRegionScans(identity))
                            DirtyPixels += (long)(scan.Width * scan.Height);
                    Paint(region, bounds, Surface);
                    Paints++;
                }
            }
            Clear();
        }
        private Region Pending()
        {
            IntPtr native = CreateRectRgn(0, 0, 0, 0);
            try
            {
                int kind = GetUpdateRgn(control.Handle, native, false);
                if (kind > 1)
                {
                    NativeRegions++;
                    return Region.FromHrgn(native);
                }
            }
            finally { DeleteObject(native); }
            Region region = new Region();
            region.MakeEmpty();
            foreach (Rectangle rect in invalid) region.Union(rect);
            if (invalid.Count > 0) FallbackRegions++;
            return region;
        }
        private void Clear()
        {
            ValidateRect(control.Handle, IntPtr.Zero);
            invalid.Clear();
        }
        private void Paint(Region clip, Rectangle bounds, Bitmap bitmap)
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SetClip(clip, CombineMode.Replace);
                Call(control, "OnPaint", new PaintEventArgs(g, bounds));
            }
        }
        public void AssertMatchesFull(string label)
        {
            using (Bitmap reference = new Bitmap(Surface.Width, Surface.Height, PixelFormat.Format32bppPArgb))
            using (Region region = new Region(control.ClientRectangle))
            {
                reference.SetResolution(96, 96);
                Paint(region, control.ClientRectangle, reference);
                AssertPixels(reference, Surface, label);
            }
        }
        public void Dispose() { Surface.Dispose(); }
    }

    private static void Print(string name, int pass, List<double> samples, double setup, double commit, PaintDriver driver)
    {
        samples.Sort();
        double sum = 0;
        foreach (double sample in samples) sum += sample;
        Console.WriteLine("BENCH name=" + name + " pass=" + pass + " n=" + samples.Count +
            " mean_ms=" + (sum / samples.Count).ToString("F3") +
            " p95_ms=" + samples[(int)Math.Ceiling(samples.Count * .95) - 1].ToString("F3") +
            " setup_ms=" + setup.ToString("F3") + " commit_ms=" + commit.ToString("F3") +
            " paints=" + driver.Paints + " native_regions=" + driver.NativeRegions +
            " fallback_regions=" + driver.FallbackRegions +
            " avg_dirty_px=" + (driver.Paints == 0 ? 0 : driver.DirtyPixels / driver.Paints));
    }
    private static double Milliseconds(long start)
    {
        return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }
    private static void Mouse(Control control, string method, Point point)
    {
        Call(control, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
    }
    private static object Call(Control control, string method, params object[] arguments)
    {
        string key = control.GetType().FullName + "." + method;
        MethodInfo info;
        if (!Methods.TryGetValue(key, out info))
        {
            info = control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Methods[key] = info;
        }
        return info.Invoke(control, arguments);
    }
    private static object Field(Control control, string name)
    {
        return control.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);
    }
    private static void AssertPixels(Bitmap expected, Bitmap actual, string label)
    {
        Assert(expected.Size == actual.Size, label + " size mismatch");
        Rectangle area = new Rectangle(Point.Empty, expected.Size);
        BitmapData a = expected.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData b = actual.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] rowA = new int[expected.Width], rowB = new int[expected.Width];
            long differences = 0;
            for (int y = 0; y < expected.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), rowA, 0, rowA.Length);
                Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), rowB, 0, rowB.Length);
                for (int x = 0; x < rowA.Length; x++) if (rowA[x] != rowB[x]) differences++;
            }
            Assert(differences == 0, label + ": " + differences + " pixels differ");
        }
        finally { expected.UnlockBits(a); actual.UnlockBits(b); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
