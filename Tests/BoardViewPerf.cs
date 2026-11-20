using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using QingJie;

namespace QingJie { public enum DrawingTool { Select, Rectangle, Arrow, Pen, Mosaic, Text } }

internal static class BoardViewPerf
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [STAThread] private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        try
        {
            for (int pass = 0; pass < 2; pass++)
            using (BoardCanvas board = new BoardCanvas())
            using (Bitmap surface = new Bitmap(1000, 520, PixelFormat.Format32bppPArgb))
            {
                board.Size = surface.Size;
                using (Bitmap source = new Bitmap(3840, 2160, PixelFormat.Format32bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(source))
                    using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(Point.Empty, source.Size),
                        Color.Coral, Color.RoyalBlue, 45f)) g.FillRectangle(brush, new Rectangle(Point.Empty, source.Size));
                    board.AddImage(source);
                    board.AddImage(source);
                }
                IntPtr handle = board.Handle;
                Paint(board, surface);
                List<double> zoom = new List<double>(), pan = new List<double>();
                object first = ((IList)Field(board, "_images"))[0];
                RectangleF firstBounds = (RectangleF)first.GetType().GetField("Bounds").GetValue(first);
                Point anchor = Point.Round(board.BoardToClient(new PointF(
                    firstBounds.Left + firstBounds.Width / 2, firstBounds.Top + firstBounds.Height / 2)));
                for (int i = 0; i < 70; i++)
                {
                    float target = 30f * (float)Math.Pow(1.08, i < 35 ? i : 69 - i);
                    long start = Stopwatch.GetTimestamp();
                    board.ZoomAt(anchor, target);
                    Paint(board, surface);
                    if (i >= 5) zoom.Add(Ms(start));
                }
                Print("wheel-2x4k", pass, zoom);
                board.ZoomAt(anchor, 200);
                Paint(board, surface);
                long refine = Stopwatch.GetTimestamp();
                Invoke(board, "FinishViewInteraction", null, EventArgs.Empty);
                Paint(board, surface);
                double dispatch200 = Ms(refine);
                WaitForRefinement(board);
                Paint(board, surface);
                Console.WriteLine("refine-200 pass=" + pass + " dispatch_ms=" + dispatch200.ToString("F3") +
                    " completion_ms=" + Ms(refine).ToString("F3"));
                for (int i = 0; i < 65; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    board.PanBy(i < 33 ? 4 : -4, i < 33 ? 2 : -2);
                    Paint(board, surface);
                    if (i >= 5) pan.Add(Ms(start));
                }
                Print("pan-200-2x4k", pass, pan);
                board.ZoomAt(new Point(500, 260), 400);
                Paint(board, surface);
                refine = Stopwatch.GetTimestamp();
                Invoke(board, "FinishViewInteraction", null, EventArgs.Empty);
                Paint(board, surface);
                double dispatch400 = Ms(refine);
                WaitForRefinement(board);
                Paint(board, surface);
                Console.WriteLine("refine-400 pass=" + pass + " dispatch_ms=" + dispatch400.ToString("F3") +
                    " completion_ms=" + Ms(refine).ToString("F3"));
                foreach (object image in (IList)Field(board, "_images"))
                {
                    Bitmap preview = (Bitmap)image.GetType().GetField("preview", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(image);
                    if (preview != null && (preview.Width > 1024 || preview.Height > 1024)) throw new Exception("Preview exceeded cap.");
                }
                Bitmap viewport = (Bitmap)Field(board, "_viewportCache");
                if (viewport.Size != board.ClientSize) throw new Exception("Viewport cache grew beyond client.");
                Console.WriteLine("cache_bounds_ok=true");
            }
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
    private static void Paint(BoardCanvas board, Bitmap surface)
    {
        using (Graphics graphics = Graphics.FromImage(surface))
            Invoke(board, "OnPaint", new PaintEventArgs(graphics, board.ClientRectangle));
    }
    private static object Invoke(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    private static void WaitForRefinement(BoardCanvas board)
    {
        PropertyInfo pending = typeof(BoardCanvas).GetProperty("IsRefinementPending",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (pending == null) return;
        Stopwatch timeout = Stopwatch.StartNew();
        while ((bool)pending.GetValue(board, null))
        {
            Invoke(board, "FinishViewInteraction", null, EventArgs.Empty);
            Application.DoEvents();
            if (timeout.ElapsedMilliseconds > 15000) throw new TimeoutException("Refinement did not finish.");
            Thread.Sleep(1);
        }
    }
    private static double Ms(long start) { return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency; }
    private static void Print(string label, int pass, List<double> values)
    {
        values.Sort();
        double sum = 0; foreach (double value in values) sum += value;
        Console.WriteLine(label + " pass=" + pass + " n=" + values.Count +
            " mean_ms=" + (sum / values.Count).ToString("F3") +
            " p95_ms=" + values[(int)Math.Ceiling(values.Count * .95) - 1].ToString("F3") +
            " max_ms=" + values[values.Count - 1].ToString("F3"));
    }
}
