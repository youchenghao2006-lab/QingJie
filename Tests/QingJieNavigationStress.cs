using System;
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

internal static class QingJieNavigationStress
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo MouseDown = typeof(BoardCanvas).GetMethod("OnMouseDown", Hidden);
    private static readonly MethodInfo MouseMove = typeof(BoardCanvas).GetMethod("OnMouseMove", Hidden);
    private static readonly MethodInfo MouseUp = typeof(BoardCanvas).GetMethod("OnMouseUp", Hidden);
    private static readonly MethodInfo Wheel = typeof(BoardCanvas).GetMethod("OnMouseWheel", Hidden);
    private static readonly MethodInfo PaintMethod = typeof(BoardCanvas).GetMethod("OnPaint", Hidden);
    private static readonly MethodInfo Refine = typeof(BoardCanvas).GetMethod("FinishViewInteraction", Hidden);
    private static readonly FieldInfo CacheField = typeof(BoardCanvas).GetField("_viewportCache", Hidden);
    private static readonly PropertyInfo Pending = typeof(BoardCanvas).GetProperty("IsRefinementPending",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            AppDomain.MonitoringIsEnabled = true;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Console.WriteLine("METHOD: synthetic 2560x1440 viewport at process per-monitor DPI; four 4K images; direct mouse/wheel handlers + full offscreen canvas paint. Idle refinement invokes the timer callback explicitly. Not desktop FPS.");
            string mode = args.Length > 0 ? args[0] : "both";
            if (mode == "both" || mode == "standalone")
                using (BoardCanvas board = new BoardCanvas())
                {
                    board.Size = new Size(2560, 1440);
                    IntPtr handle = board.Handle;
                    Run(board, "standalone");
                }
            if (mode == "both" || mode == "host")
                using (MainForm main = new MainForm())
                {
                    main.ShowInTaskbar = false;
                    main.Opacity = 0;
                    ((NotifyIcon)Field(main, "tray")).Visible = false;
                    main.Show();
                    main.ClientSize = new Size(3350, 2000);
                    Call(main, "OpenBoard", false);
                    Application.DoEvents();
                    BoardWorkspace workspace = (BoardWorkspace)Field(main, "board");
                    BoardCanvas board = (BoardCanvas)Field(workspace, "canvas");
                    main.ClientSize = new Size(main.ClientSize.Width + 2560 - board.Width,
                        main.ClientSize.Height + 1440 - board.Height);
                    main.PerformLayout();
                    Application.DoEvents();
                    Run(board, "host");
                    main.Size = main.MinimumSize;
                    main.PerformLayout();
                    Application.DoEvents();
                    RunThresholdZoom(main, board);
                    main.Hide();
                }
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }

    private static void Run(BoardCanvas board, string mode)
    {
        using (Graphics graphics = board.CreateGraphics())
            Console.WriteLine("SCENE mode=" + mode + " viewport=" + board.ClientSize + " dpi=" + graphics.DpiX);
        using (Bitmap source = new Bitmap(3840, 2160, PixelFormat.Format32bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(source))
            using (LinearGradientBrush fill = new LinearGradientBrush(new Rectangle(Point.Empty, source.Size),
                Color.Coral, Color.RoyalBlue, 35))
            using (Pen grid = new Pen(Color.White, 3))
            {
                graphics.FillRectangle(fill, new Rectangle(Point.Empty, source.Size));
                for (int x = 0; x < source.Width; x += 180) graphics.DrawLine(grid, x, 0, x + 600, source.Height);
            }
            board.AddImageAt(source, new PointF(-560, -340));
            board.AddImageAt(source, new PointF(560, -340));
            board.AddImageAt(source, new PointF(-560, 340));
            board.AddImageAt(source, new PointF(560, 340));
        }
        using (Bitmap surface = new Bitmap(board.Width, board.Height, PixelFormat.Format32bppPArgb))
        {
            Center(board, surface, 200);
            Paint(board, surface);
            board.PanBy(1, 1);
            Paint(board, surface);
            RunPan(board, surface, mode, "pan_burst_100", 100, 100);
            RunPan(board, surface, mode, "pan_burst_500", 500, 500);
            RunPan(board, surface, mode, "pan_500_events_16_per_frame", 500, 16);
            RunWheel(board, surface, mode, "wheel_burst_100", 100, 100);
            RunWheel(board, surface, mode, "wheel_96_events_8_per_frame", 96, 8);
            RunWheel(board, surface, mode, "wheel_96_events_4_per_frame", 96, 4);
            Stats idle = new Stats(mode + "/wheel_then_idle_refine");
            for (int i = 0; i < 8; i++)
            {
                long handler = Stopwatch.GetTimestamp();
                for (int j = 0; j < 4; j++) SendWheel(board, i % 2 == 0 ? 120 : -120);
                double h = Ms(handler);
                long paint = Stopwatch.GetTimestamp();
                Paint(board, surface);
                idle.Add(h, Ms(paint), board);
                long refine = Stopwatch.GetTimestamp();
                Refine.Invoke(board, new object[] { null, EventArgs.Empty });
                Paint(board, surface);
                idle.Refine.Add(Ms(refine));
                WaitForRefinement(board);
                Paint(board, surface);
                idle.Completion.Add(Ms(refine));
            }
            idle.Print();
        }
    }

    private static void RunPan(BoardCanvas board, Bitmap surface, string mode, string name, int count, int perFrame)
    {
        Center(board, surface, 200);
        Paint(board, surface);
        board.PanBy(1, 1);
        Paint(board, surface);
        Stats stats = new Stats(mode + "/" + name);
        Point start = new Point(board.Width / 2, board.Height / 2);
        SendMouse(board, MouseDown, start);
        Point end = start;
        for (int i = 0; i < count;)
        {
            long handler = Stopwatch.GetTimestamp();
            int finish = Math.Min(count, i + perFrame);
            for (; i < finish; i++)
            {
                int distance = i < count / 2 ? i + 1 : count - i;
                end = new Point(start.X + distance * 2, start.Y + distance);
                SendMouse(board, MouseMove, end);
            }
            double h = Ms(handler);
            long paint = Stopwatch.GetTimestamp();
            Paint(board, surface);
            stats.Add(h, Ms(paint), board);
        }
        SendMouse(board, MouseUp, end);
        stats.Print();
    }
    private static void RunWheel(BoardCanvas board, Bitmap surface, string mode, string name, int count, int perFrame)
    {
        Center(board, surface, 100);
        Paint(board, surface);
        Stats stats = new Stats(mode + "/" + name);
        for (int i = 0; i < count;)
        {
            long handler = Stopwatch.GetTimestamp();
            int finish = Math.Min(count, i + perFrame);
            for (; i < finish; i++) SendWheel(board, (i / 4) % 2 == 0 ? 120 : -120);
            double h = Ms(handler);
            long paint = Stopwatch.GetTimestamp();
            Paint(board, surface);
            stats.Add(h, Ms(paint), board);
        }
        stats.Print();
    }
    private static void Center(BoardCanvas board, Bitmap surface, float zoom)
    {
        Point center = new Point(board.Width / 2, board.Height / 2);
        board.ZoomAt(center, zoom);
        PointF origin = board.BoardToClient(PointF.Empty);
        board.PanBy(center.X - origin.X, center.Y - origin.Y);
        Refine.Invoke(board, new object[] { null, EventArgs.Empty });
        Paint(board, surface);
        WaitForRefinement(board);
    }
    private static void WaitForRefinement(BoardCanvas board)
    {
        if (Pending == null) return;
        Stopwatch timeout = Stopwatch.StartNew();
        while ((bool)Pending.GetValue(board, null))
        {
            Refine.Invoke(board, new object[] { null, EventArgs.Empty });
            Application.DoEvents();
            if (timeout.ElapsedMilliseconds > 15000) throw new TimeoutException("View refinement did not settle.");
            Thread.Sleep(1);
        }
    }
    private static void RunThresholdZoom(MainForm main, BoardCanvas board)
    {
        int layouts = 0, resizes = 0;
        main.Layout += delegate { layouts++; };
        board.Resize += delegate { resizes++; };
        using (Bitmap surface = new Bitmap(board.Width, board.Height, PixelFormat.Format32bppPArgb))
        {
            Center(board, surface, 100);
            Paint(board, surface);
            layouts = resizes = 0;
            Stats stats = new Stats("host-minimum/zoom_99_100_burst_500");
            Point anchor = new Point(board.Width / 2, board.Height / 2);
            long handler = Stopwatch.GetTimestamp();
            for (int i = 0; i < 500; i++) board.ZoomAt(anchor, i % 2 == 0 ? 99 : 100);
            double h = Ms(handler);
            long paint = Stopwatch.GetTimestamp();
            Paint(board, surface);
            stats.Add(h, Ms(paint), board);
            stats.Print();
            Console.WriteLine("host-minimum viewport=" + board.ClientSize +
                " main_layout_events=" + layouts + " canvas_resize_events=" + resizes);
        }
    }
    private static void SendMouse(BoardCanvas board, MethodInfo method, Point point)
    {
        method.Invoke(board, new object[] { new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0) });
    }
    private static void SendWheel(BoardCanvas board, int delta)
    {
        Wheel.Invoke(board, new object[] { new MouseEventArgs(MouseButtons.None, 0, board.Width / 2, board.Height / 2, delta) });
    }
    private static void Paint(BoardCanvas board, Bitmap surface)
    {
        using (Graphics graphics = Graphics.FromImage(surface))
            PaintMethod.Invoke(board, new object[] { new PaintEventArgs(graphics, board.ClientRectangle) });
    }
    private static object Field(object instance, string name) { return instance.GetType().GetField(name, Hidden).GetValue(instance); }
    private static object Call(object instance, string name, params object[] args) { return instance.GetType().GetMethod(name, Hidden).Invoke(instance, args); }
    private static double Ms(long start) { return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency; }

    private sealed class Stats
    {
        private readonly string name;
        private readonly List<double> handlers = new List<double>(), paints = new List<double>(), totals = new List<double>();
        public readonly List<double> Refine = new List<double>();
        public readonly List<double> Completion = new List<double>();
        private readonly HashSet<object> caches = new HashSet<object>();
        private readonly long allocatedBefore, privateBefore;
        private readonly int gc0, gc1, gc2;
        private long privatePeak;
        public Stats(string label)
        {
            name = label;
            allocatedBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
            privatePeak = privateBefore;
            gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        }
        public void Add(double handler, double paint, BoardCanvas board)
        {
            handlers.Add(handler); paints.Add(paint); totals.Add(handler + paint);
            object cache = CacheField.GetValue(board);
            if (cache != null) caches.Add(cache);
            using (Process process = Process.GetCurrentProcess())
                privatePeak = Math.Max(privatePeak, process.PrivateMemorySize64);
        }
        public void Print()
        {
            Console.WriteLine(name + " frames=" + totals.Count + " handler_sum_ms=" + Sum(handlers).ToString("F3") +
                " handler_max_ms=" + Max(handlers).ToString("F3") + " paint_mean_ms=" + (Sum(paints) / paints.Count).ToString("F3") +
                " paint_max_ms=" + Max(paints).ToString("F3") + " frame_mean_ms=" + (Sum(totals) / totals.Count).ToString("F3") +
                " frame_max_ms=" + Max(totals).ToString("F3") + " viewport_instances=" + caches.Count +
                " managed_alloc_MB=" + ((AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocatedBefore) / 1048576.0).ToString("F2") +
                " private_peak_delta_MB=" + ((privatePeak - privateBefore) / 1048576.0).ToString("F2") +
                " gc=" + (GC.CollectionCount(0) - gc0) + "/" + (GC.CollectionCount(1) - gc1) + "/" + (GC.CollectionCount(2) - gc2));
            if (Refine.Count > 0) Console.WriteLine(name + " idle_dispatch_mean_ms=" + (Sum(Refine) / Refine.Count).ToString("F3") +
                " idle_dispatch_max_ms=" + Max(Refine).ToString("F3") +
                " idle_completion_mean_ms=" + (Sum(Completion) / Completion.Count).ToString("F3") +
                " idle_completion_max_ms=" + Max(Completion).ToString("F3"));
        }
        private static double Sum(List<double> values) { double result = 0; foreach (double value in values) result += value; return result; }
        private static double Max(List<double> values) { double result = 0; foreach (double value in values) result = Math.Max(result, value); return result; }
    }
}
