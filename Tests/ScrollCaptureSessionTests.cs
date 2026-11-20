using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QingJie;

internal static class ScrollCaptureSessionTests
{
    private static int passed;
    [STAThread]
    private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            Run("safe controls prefer selected monitor and never cover sampled pixels", delegate
            {
                Rectangle primary = new Rectangle(0, 0, 1920, 1040);
                Rectangle secondary = new Rectangle(-1920, -150, 1920, 1040);
                Rectangle selected = new Rectangle(-1700, 0, 900, 600), placed;
                Size controls = new Size(430, 76);
                Require(ScrollCaptureSession.TryPlaceControl(selected, controls,
                    new Rectangle[] { primary, secondary }, out placed), "no placement");
                Require(secondary.Contains(placed) && !placed.IntersectsWith(selected), "wrong monitor/overlap");
                Require(ScrollCaptureSession.TryPlaceControl(primary, controls,
                    new Rectangle[] { primary, secondary }, out placed) && secondary.Contains(placed),
                    "full monitor did not use other screen");
                Require(!ScrollCaptureSession.TryPlaceControl(primary, controls,
                    new Rectangle[] { primary }, out placed), "fullscreen control contaminated sample");
                Rectangle almostFull = new Rectangle(0, 0, 1920, 1030);
                Require(!ScrollCaptureSession.TryPlaceControl(almostFull, controls,
                    new Rectangle[] { primary }, out placed), "thin gap accepted oversized bar");
                Rectangle preview = ScrollCaptureSession.PlacePreview(primary, new Size(246, 244), primary);
                Require(primary.Contains(preview) && preview.Left > primary.Left + primary.Width / 2 &&
                    preview.Top > primary.Top + primary.Height / 2, "fullscreen preview left selected monitor/corner");
                Rectangle spanning = new Rectangle(-700, -50, 1200, 600);
                preview = ScrollCaptureSession.PlacePreview(spanning, new Size(246, 244), primary);
                Require(primary.Contains(preview), "spanning preview ignored lower-right monitor");
            });
            Run("finish during capture includes one final frame and transfers independent pixels", delegate
            {
                using (Bitmap page = Pattern(240, 640))
                using (Bitmap first = Frame(page, 0))
                {
                    int captures = 0, active = 0, maximum = 0;
                    int previewUpdates = 0;
                    Bitmap lastProvided = null;
                    ScrollCaptureSession session = null;
                    session = new ScrollCaptureSession(new Rectangle(-300, -50, 240, 240), first,
                        delegate(Rectangle area)
                        {
                            Require(area.Location == new Point(-300, -50), "screen origin changed");
                            active++; maximum = Math.Max(maximum, active); captures++;
                            if (captures == 1)
                                using (Bitmap preview = session.CopyPreview())
                                    Require(preview != null && preview.Height == 240 &&
                                        preview.GetPixel(20, 20) == page.GetPixel(20, 20), "initial preview missing or incorrect");
                            lastProvided = Frame(page, captures == 1 ? 70 : 140);
                            if (captures == 1) session.RequestFinish();
                            active--;
                            return lastProvided;
                        }, false, 1);
                    session.ProgressChanged += delegate
                    {
                        using (Bitmap preview = session.CopyPreview())
                        {
                            Require(preview != null && preview.Height == session.Height,
                                "preview did not grow with accepted content");
                            Require(preview.GetPixel(20, preview.Height - 10) == page.GetPixel(20, session.Height - 10),
                                "preview bottom does not contain newest accepted pixels");
                            previewUpdates++;
                        }
                    };
                    first.Dispose();
                    using (session)
                    using (Bitmap result = Await(session.RunAsync(CancellationToken.None)))
                    {
                        Require(captures == 2 && maximum == 1, "finish skipped final sample or concurrent capture");
                        Require(previewUpdates == 2, "preview updates skipped");
                        Require(result != null && result.Width == 240 && result.Height >= 360,
                            "missing appended frames");
                        Color pixel = result.GetPixel(10, result.Height - 10);
                        Require(pixel == page.GetPixel(10, result.Height - 10), "final pixels differ");
                        Require(IsDisposed(lastProvided), "session retained supplied frame");
                        session.Dispose();
                        Require(result.GetPixel(10, 10) == page.GetPixel(10, 10), "result shares disposed session pixels");
                    }
                }
            });
            Run("pre-cancel performs no capture and cancellation during frame disposes it", delegate
            {
                using (Bitmap first = Pattern(240, 240))
                using (CancellationTokenSource stop = new CancellationTokenSource())
                using (ScrollCaptureSession session = new ScrollCaptureSession(new Rectangle(0, 0, 240, 240),
                    first, delegate { throw new Exception("captured despite pre-cancel"); }, false, 1))
                {
                    stop.Cancel();
                    Require(Await(session.RunAsync(stop.Token)) == null, "pre-cancel result");
                }
                using (Bitmap first = Pattern(240, 240))
                {
                    Bitmap provided = null;
                    ScrollCaptureSession session = null;
                    session = new ScrollCaptureSession(new Rectangle(0, 0, 240, 240), first,
                        delegate
                        {
                            provided = (Bitmap)first.Clone();
                            session.RequestCancel();
                            return provided;
                        }, false, 1);
                    using (session)
                    {
                        Require(Await(session.RunAsync(CancellationToken.None)) == null, "cancel returned pixels");
                        Require(IsDisposed(provided), "cancel leaked in-flight bitmap");
                    }
                }
            });
            Run("Dispose during frame cancels without waiting or accepting late results", delegate
            {
                using (Bitmap first = Pattern(240, 240))
                {
                    Bitmap provided = null;
                    ScrollCaptureSession session = null;
                    session = new ScrollCaptureSession(new Rectangle(0, 0, 240, 240), first,
                        delegate
                        {
                            provided = (Bitmap)first.Clone();
                            session.Dispose();
                            return provided;
                        }, false, 1);
                    Require(Await(session.RunAsync(CancellationToken.None)) == null, "disposed session returned late pixels");
                    Require(IsDisposed(provided), "Dispose leaked captured frame");
                    session.Dispose();
                }
            });
            Run("NoMatch is reported and capture exceptions release the session", delegate
            {
                using (Bitmap first = Pattern(240, 240))
                using (Bitmap unrelated = new Bitmap(240, 240))
                {
                    using (Graphics graphics = Graphics.FromImage(unrelated)) graphics.Clear(Color.Lime);
                    ScrollCaptureSession session = null;
                    int calls = 0;
                    session = new ScrollCaptureSession(new Rectangle(0, 0, 240, 240), first,
                        delegate { calls++; return (Bitmap)unrelated.Clone(); }, false, 1);
                    session.ProgressChanged += delegate { session.RequestFinish(); };
                    using (session)
                    using (Bitmap result = Await(session.RunAsync(CancellationToken.None)))
                    {
                        Require(session.LastFrameResult == ScrollFrameResult.NoMatch && calls == 2,
                            "missing final NoMatch status");
                        Require(result.Height == first.Height && session.StatusText.Contains("未接上"),
                            "unmatched frame appended or status lost");
                    }
                }
                using (Bitmap first = Pattern(240, 240))
                using (ScrollCaptureSession session = new ScrollCaptureSession(new Rectangle(0, 0, 240, 240), first,
                    delegate { throw new InvalidOperationException("synthetic capture failed"); }, false, 1))
                {
                    bool failed = false;
                    try { Await(session.RunAsync(CancellationToken.None)); }
                    catch (InvalidOperationException error) { failed = error.Message == "synthetic capture failed"; }
                    Require(failed, "capture error swallowed");
                    bool repeated = false;
                    try { Await(session.RunAsync(CancellationToken.None)); }
                    catch (InvalidOperationException) { repeated = true; }
                    Require(repeated, "finished session restarted");
                }
            });
            Run("native exclusion proof rejects black blocks and captured controls", delegate
            {
                Color panel = Color.FromArgb(29, 37, 51);
                using (Bitmap baseline = new Bitmap(64, 3))
                using (Bitmap actual = new Bitmap(64, 3))
                {
                    using (Graphics a = Graphics.FromImage(baseline)) a.Clear(Color.White);
                    using (Graphics b = Graphics.FromImage(actual)) b.Clear(Color.White);
                    Require(ScrollCaptureNative.ProvesExclusion(baseline, actual, panel), "valid exclusion rejected");
                    using (Graphics b = Graphics.FromImage(actual)) b.Clear(Color.Black);
                    Require(!ScrollCaptureNative.ProvesExclusion(baseline, actual, panel), "black block accepted");
                    using (Graphics a = Graphics.FromImage(baseline)) a.Clear(Color.Black);
                    Require(!ScrollCaptureNative.ProvesExclusion(baseline, actual, panel), "black background was false proof");
                    using (Graphics a = Graphics.FromImage(baseline)) a.Clear(panel);
                    using (Graphics b = Graphics.FromImage(actual)) b.Clear(panel);
                    Require(!ScrollCaptureNative.ProvesExclusion(baseline, actual, panel), "panel-colored background false proof");
                }
            });
            Run("control window is nonactivating and stays hidden in synthetic validation", delegate
            {
                using (ScrollCaptureControl control = new ScrollCaptureControl(delegate { }, delegate { }))
                {
                    CreateParams parameters = (CreateParams)typeof(ScrollCaptureControl).GetProperty("CreateParams",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(control, null);
                    Require((parameters.ExStyle & 0x08000000) != 0 && (parameters.ExStyle & 0x80) != 0,
                        "control can activate target or appear in taskbar");
                    control.SetScale(2);
                    Require(control.Size == new Size(492, 488), "preview is no longer compact at 200% DPI");
                    control.SetStatus("未接上：向上回滚一点，保留画面重叠");
                    foreach (Control child in control.Controls)
                        Require(control.ClientRectangle.Contains(child.Bounds), "high DPI control clipped");
                    Require(!control.Visible, "synthetic validation showed a window");
                }
            });
            Run("preview paints both full length and newest content and live surround has a hole", delegate
            {
                using (Bitmap page = new Bitmap(120, 600))
                using (Graphics fill = Graphics.FromImage(page))
                using (ScrollPreviewCanvas preview = new ScrollPreviewCanvas { Size = new Size(260, 242) })
                using (Bitmap painted = new Bitmap(260, 242))
                using (Graphics graphics = Graphics.FromImage(painted))
                {
                    fill.FillRectangle(Brushes.Red, 0, 0, 120, 200);
                    fill.FillRectangle(Brushes.Lime, 0, 200, 120, 200);
                    fill.FillRectangle(Brushes.Blue, 0, 400, 120, 200);
                    preview.SetPreview(page, 600, 200);
                    typeof(ScrollPreviewCanvas).GetMethod("OnPaint", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic).Invoke(preview, new object[] {
                            new PaintEventArgs(graphics, new Rectangle(Point.Empty, painted.Size)) });
                    Require(painted.GetPixel(40, 35).R > 240, "full overview lost first content");
                    Require(painted.GetPixel(40, 205).B > 240 && painted.GetPixel(130, 100).B > 240,
                        "latest content missing from overview/detail");
                }
                Rectangle monitor = new Rectangle(-500, -100, 400, 300);
                Rectangle selected = new Rectangle(-450, -60, 200, 180);
                using (ScrollCaptureHint shade = new ScrollCaptureHint(monitor, selected, false))
                {
                    Require(!shade.Region.IsVisible(new Point(80, 70)) && shade.Region.IsVisible(new Point(10, 10)),
                        "live shade painted over capture or omitted outside context");
                    CreateParams parameters = (CreateParams)typeof(ScrollCaptureHint).GetProperty("CreateParams",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(shade, null);
                    Require((parameters.ExStyle & 0x20) != 0 && (parameters.ExStyle & 0x08000000) != 0,
                        "live shade intercepts target input");
                    Require(!shade.Visible, "shade test displayed desktop UI");
                }
            });
            Console.WriteLine("RESULT: " + passed + " scroll capture session groups passed");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
    private static T Await<T>(Task<T> task)
    {
        // No WinForms context is installed in these hidden tests; asynchronous
        // continuations execute on the pool, and no user-input pump is required.
        if (Task.WhenAny(task, Task.Delay(15000)).GetAwaiter().GetResult() != task)
            throw new Exception("Session did not complete in 15 seconds.");
        return task.GetAwaiter().GetResult();
    }
    private static bool IsDisposed(Bitmap bitmap)
    {
        if (bitmap == null) return false;
        try { int ignored = bitmap.Width; return false; }
        catch (ArgumentException) { return true; }
    }
    private static Bitmap Frame(Bitmap page, int y)
    { return page.Clone(new Rectangle(0, y, 240, 240), PixelFormat.Format32bppArgb); }
    private static Bitmap Pattern(int width, int height)
    {
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int[] pixels = new int[width * height];
            uint state = 1993;
            for (int i = 0; i < pixels.Length; i++)
            {
                state = state * 1664525 + 1013904223;
                pixels[i] = unchecked((int)(0xff000000 | (state & 0x00ffffff)));
            }
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width, new IntPtr(data.Scan0.ToInt64() + y * data.Stride), width);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    private static void Run(string name, Action action)
    { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
