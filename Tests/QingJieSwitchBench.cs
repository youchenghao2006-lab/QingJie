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

internal static class QingJieSwitchBench
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-1));
            int iterations = args.Length > 0 ? Int32.Parse(args[0]) : 20;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm main = new MainForm())
            using (Bitmap source = MakeSource())
            {
                main.ShowInTaskbar = false;
                main.Opacity = 0;
                ((NotifyIcon)Field(main, "tray")).Visible = false;
                main.Show();
                main.ClientSize = new Size(1280, 800);
                main.PerformLayout();
                Application.DoEvents();
                Stopwatch setSource = Stopwatch.StartNew();
                Call(main, "SetCurrentImage", source);
                setSource.Stop();
                Stopwatch initial = Stopwatch.StartNew();
                Call(main, "OpenBoard", false);
                main.PerformLayout();
                using (Bitmap surface = new Bitmap(1280, 800, PixelFormat.Format32bppArgb))
                {
                    surface.SetResolution(96, 96);
                    Render(main, surface);
                    initial.Stop();
                    Console.WriteLine("METHOD: own opacity-zero synthetic form; 4K ARGB source; 1280x800 at 96 DPI; full DrawToBitmap exposure cost; not desktop FPS.");
                    Console.WriteLine("set_source_ms=" + setSource.Elapsed.TotalMilliseconds.ToString("F3"));
                    Console.WriteLine("first_open_plus_paint_ms=" + initial.Elapsed.TotalMilliseconds.ToString("F3"));
                    for (int i = 0; i < 4; i++)
                    {
                        Call(main, "ShowScreenshotEditor"); main.PerformLayout(); Application.DoEvents(); Render(main, surface);
                        Call(main, "OpenBoard", false); main.PerformLayout(); Application.DoEvents(); Render(main, surface);
                    }
                    object workspace = Field(main, "board");
                    object sourceReference = Field(workspace, "sourceImage");
                    object editor = Field(main, "canvas");
                    object board = Field(workspace, "canvas");
                    object editorCache = Field(editor, "_viewportCache");
                    object boardCache = Field(board, "_viewportCache");
                    int sourceChanges = 0, editorChanges = 0, boardChanges = 0;
                    List<double> toEditor = new List<double>(), editorPaint = new List<double>();
                    List<double> toBoard = new List<double>(), boardPaint = new List<double>();
                    List<double> editorFlush = new List<double>(), boardFlush = new List<double>();
                    List<double> editorTotal = new List<double>(), boardTotal = new List<double>();
                    for (int i = 0; i < iterations; i++)
                    {
                        Stopwatch clock = Stopwatch.StartNew();
                        Call(main, "ShowScreenshotEditor"); main.PerformLayout();
                        toEditor.Add(clock.Elapsed.TotalMilliseconds);
                        clock.Restart(); Application.DoEvents();
                        editorFlush.Add(clock.Elapsed.TotalMilliseconds);
                        clock.Restart(); Render(main, surface);
                        editorPaint.Add(clock.Elapsed.TotalMilliseconds);
                        editorTotal.Add(toEditor[i] + editorFlush[i] + editorPaint[i]);
                        CheckVisibility(main, false);
                        clock.Restart();
                        Call(main, "OpenBoard", false); main.PerformLayout();
                        toBoard.Add(clock.Elapsed.TotalMilliseconds);
                        clock.Restart(); Application.DoEvents();
                        boardFlush.Add(clock.Elapsed.TotalMilliseconds);
                        clock.Restart(); Render(main, surface);
                        boardPaint.Add(clock.Elapsed.TotalMilliseconds);
                        boardTotal.Add(toBoard[i] + boardFlush[i] + boardPaint[i]);
                        CheckVisibility(main, true);
                        if (!Object.ReferenceEquals(sourceReference, Field(workspace, "sourceImage"))) sourceChanges++;
                        if (!Object.ReferenceEquals(editorCache, Field(editor, "_viewportCache"))) editorChanges++;
                        if (!Object.ReferenceEquals(boardCache, Field(board, "_viewportCache"))) boardChanges++;
                    }
                    Print("switch_to_editor_handler", toEditor);
                    Print("editor_full_paint", editorPaint);
                    Print("editor_message_flush", editorFlush);
                    Print("editor_switch_flush_and_forced_paint_total", editorTotal);
                    Print("switch_to_board_handler", toBoard);
                    Print("board_full_paint", boardPaint);
                    Print("board_message_flush", boardFlush);
                    Print("board_switch_flush_and_forced_paint_total", boardTotal);
                    Console.WriteLine("warm_source_reference_changes=" + sourceChanges +
                        "; editor_cache_reference_changes=" + editorChanges +
                        "; board_cache_reference_changes=" + boardChanges);
                    Control preview = (Control)Field(workspace, "sourcePreview");
                    List<double> previewTimes = new List<double>();
                    using (Bitmap previewSurface = new Bitmap(preview.Width, preview.Height, PixelFormat.Format32bppArgb))
                    using (Graphics graphics = Graphics.FromImage(previewSurface))
                    {
                        previewSurface.SetResolution(96, 96);
                        MethodInfo paint = preview.GetType().GetMethod("OnPaint", Private);
                        for (int i = 0; i < 4; i++)
                            paint.Invoke(preview, new object[] { new PaintEventArgs(graphics, preview.ClientRectangle) });
                        for (int i = 0; i < iterations; i++)
                        {
                            Stopwatch clock = Stopwatch.StartNew();
                            paint.Invoke(preview, new object[] { new PaintEventArgs(graphics, preview.ClientRectangle) });
                            previewTimes.Add(clock.Elapsed.TotalMilliseconds);
                        }
                    }
                    Print("source_preview_paint", previewTimes);
                }
                main.Hide();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }
    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, Private).GetValue(target);
    }
    private static void Call(object target, string method, params object[] args)
    {
        target.GetType().GetMethod(method, Private).Invoke(target, args);
    }
    private static void Render(MainForm main, Bitmap target)
    {
        main.Controls[0].DrawToBitmap(target, new Rectangle(Point.Empty, target.Size));
    }
    private static void CheckVisibility(MainForm main, bool boardMode)
    {
        Control host = (Control)Field(main, "workspaceHost");
        Control board = (Control)Field(main, "board");
        Control editor = (Control)Field(main, "editorWorkspace");
        if (!main.Visible || !host.Visible || !IsWindowVisible(host.Handle) ||
            board.Visible != boardMode || editor.Visible == boardMode ||
            !IsWindowVisible((boardMode ? board : editor).Handle))
            throw new Exception("Mode switch left incorrect managed or native window visibility.");
    }
    private static void Print(string name, List<double> samples)
    {
        samples.Sort();
        double total = 0;
        foreach (double sample in samples) total += sample;
        Console.WriteLine(name + "_ms mean=" + (total / samples.Count).ToString("F3") +
            " median=" + samples[samples.Count / 2].ToString("F3") +
            " p95=" + samples[(int)Math.Ceiling(samples.Count * .95) - 1].ToString("F3"));
    }
    private static Bitmap MakeSource()
    {
        Bitmap source = new Bitmap(3840, 2160, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(source))
        using (LinearGradientBrush fill = new LinearGradientBrush(new Rectangle(Point.Empty, source.Size),
            Color.FromArgb(235, 244, 254), Color.FromArgb(65, 118, 218), 35f))
        using (Pen lines = new Pen(Color.FromArgb(220, 39, 70, 120), 3))
        {
            graphics.FillRectangle(fill, new Rectangle(Point.Empty, source.Size));
            for (int x = 80; x < source.Width; x += 170) graphics.DrawLine(lines, x, 50, x + 160, 2110);
            for (int y = 70; y < source.Height; y += 130) graphics.DrawLine(lines, 50, y, 3790, y + 90);
        }
        return source;
    }
}
