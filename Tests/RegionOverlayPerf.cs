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

internal static class RegionOverlayPerf
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [STAThread] private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        SetProcessDpiAwarenessContext(new IntPtr(-1));
        using (Bitmap warm = MakeSource(512, 256))
        using (RegionOverlay unused = new RegionOverlay(warm, new Rectangle(Point.Empty, warm.Size))) { }
        foreach (int width in new int[] { 3840, 7680 })
        using (Bitmap source = MakeSource(width, 2160))
        {
            for (int pass = 0; pass < 2; pass++) Measure(source, pass, args.Length > 0);
        }
        return 0;
    }

    private static Bitmap MakeSource(int width, int height)
    {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(Point.Empty, image.Size),
            Color.FromArgb(230, 239, 251), Color.FromArgb(90, 144, 204), 25F))
            graphics.FillRectangle(brush, new Rectangle(Point.Empty, image.Size));
        return image;
    }

    private static void Measure(Bitmap source, int pass, bool burst)
    {
        Stopwatch timer = Stopwatch.StartNew();
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size)))
        {
            double construction = timer.Elapsed.TotalMilliseconds;
            IntPtr handle = overlay.Handle;
            if (overlay.ClientSize != source.Size) throw new Exception("Overlay dimensions were truncated; benchmark invalid.");
            List<Rectangle> dirty = new List<Rectangle>();
            overlay.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty.Add(e.InvalidRect); };
            using (NativeSurface surface = new NativeSurface(source.Size))
            using (Graphics graphics = Graphics.FromHdc(surface.Dc))
            {
                timer.Restart();
                Paint(overlay, graphics, overlay.ClientRectangle);
                double firstPaint = timer.Elapsed.TotalMilliseconds;
                Mouse(overlay, "OnMouseDown", new Point(130, 200));
                List<double> samples = new List<double>();
                double handler = 0;
                int eventsPerFrame = burst ? 16 : 1;
                for (int frame = 0; frame < 100; frame++)
                {
                    dirty.Clear();
                    timer.Restart();
                    for (int sample = 0; sample < eventsPerFrame; sample++)
                        Mouse(overlay, "OnMouseMove", new Point(800 + frame * 20 + sample, 600 + frame * 7 + sample / 3));
                    handler += timer.Elapsed.TotalMilliseconds;
                    using (Region region = new Region())
                    {
                        region.MakeEmpty();
                        foreach (Rectangle rectangle in dirty) region.Union(rectangle);
                        Rectangle bounds = Rectangle.Ceiling(region.GetBounds(graphics));
                        if (burst && pass == 1) graphics.SetClip(bounds, CombineMode.Replace);
                        else graphics.SetClip(region, CombineMode.Replace);
                        Paint(overlay, graphics, bounds);
                    }
                    if (frame >= 10) samples.Add(timer.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                double mean = 0;
                foreach (double sample in samples) mean += sample;
                Console.WriteLine("size=" + source.Size + " pass=" + pass + " source=" + source.PixelFormat +
                    " events_per_frame=" + eventsPerFrame + " clip=" + (burst && pass == 1 ? "bounding" : "region") +
                    " constructor_ms=" + construction.ToString("F3") + " first_paint_ms=" + firstPaint.ToString("F3") +
                    " drag_mean_ms=" + (mean / samples.Count).ToString("F3") +
                    " drag_p95_ms=" + samples[(int)Math.Ceiling(samples.Count * .95) - 1].ToString("F3") +
                    " handler_mean_ms=" + (handler / 100).ToString("F3"));
            }
        }
    }
    private static void Paint(RegionOverlay overlay, Graphics graphics, Rectangle clip)
    {
        typeof(RegionOverlay).GetMethod("OnPaint", Hidden).Invoke(overlay,
            new object[] { new PaintEventArgs(graphics, clip) });
    }
    private static void Mouse(RegionOverlay overlay, string method, Point point)
    {
        typeof(RegionOverlay).GetMethod(method, Hidden).Invoke(overlay,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
    }

    private sealed class NativeSurface : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Info
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int X, Y; public uint Colors, Important;
        }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc,
            ref Info info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        private IntPtr bitmap, previous;
        internal IntPtr Dc { get; private set; }
        internal NativeSurface(Size size)
        {
            Dc = CreateCompatibleDC(IntPtr.Zero);
            Info info = new Info { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            IntPtr pixels;
            bitmap = CreateDIBSection(Dc, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (Dc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Exception("DIB allocation failed");
            previous = SelectObject(Dc, bitmap);
        }
        public void Dispose()
        {
            SelectObject(Dc, previous); DeleteObject(bitmap); DeleteDC(Dc);
        }
    }
}
