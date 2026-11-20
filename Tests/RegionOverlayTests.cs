using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QingJie;

// Synthetic windows are never shown; this runner never captures the desktop.
internal static class RegionOverlayTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [STAThread] private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        SetProcessDpiAwarenessContext(new IntPtr(-1));
        try
        {
            ContinuousHandoff();
            Cancellation();
            NormalSelection();
            ThrowingSubscriber();
            DesktopWindowBounds();
            NativeLifetime();
            BurstRedrawAndBadge();
            CaptureLoss();
            Console.WriteLine("PASS 8 persistent overlay lifecycle/pixel/native-resource groups");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ContinuousHandoff()
    {
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(-1200, -600, source.Width, source.Height)))
        using (Bitmap incremental = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(incremental))
        {
            IntPtr handle = overlay.Handle;
            overlay.KeepOpenAfterSelection = true;
            Bitmap result = null;
            Rectangle captured = Rectangle.Empty;
            int calls = 0, invalidations = 0;
            using (Region dirty = new Region())
            {
                dirty.MakeEmpty();
                overlay.Invalidated += delegate(object sender, InvalidateEventArgs e)
                { dirty.Union(e.InvalidRect); invalidations++; };
                overlay.SelectionCompleted += delegate(Bitmap image, Rectangle rectangle)
                { result = image; captured = rectangle; calls++; };
                Paint(overlay, graphics, overlay.ClientRectangle);
                Mouse(overlay, "OnMouseDown", MouseButtons.Left, 190, 140);
                Mouse(overlay, "OnMouseMove", MouseButtons.Left, 30, 35);
                PaintDirty(overlay, graphics, dirty);
                dirty.MakeEmpty();
                // MouseUp deliberately differs from the final move.
                Mouse(overlay, "OnMouseUp", MouseButtons.Left, 20, 25);
                Assert(calls == 1 && captured == new Rectangle(20, 25, 170, 115), "handoff exact last coordinates");
                Assert(overlay.IsSelectionCompleted && !overlay.IsDisposed &&
                    overlay.DialogResult == DialogResult.None && overlay.Result == null, "persistent window stays open");
                PaintDirty(overlay, graphics, dirty);
                using (Bitmap full = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
                using (Graphics fullGraphics = Graphics.FromImage(full))
                using (Bitmap expected = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb))
                using (Graphics expectedGraphics = Graphics.FromImage(expected))
                {
                    Paint(overlay, fullGraphics, overlay.ClientRectangle);
                    expectedGraphics.CompositingMode = CompositingMode.SourceCopy;
                    expectedGraphics.DrawImageUnscaled(source, Point.Empty);
                    expectedGraphics.CompositingMode = CompositingMode.SourceOver;
                    using (Brush shade = new SolidBrush(Color.FromArgb(144, 5, 12, 21)))
                        expectedGraphics.FillRectangle(shade, overlay.ClientRectangle);
                    Equal(full, incremental, "dirty completion erases border, badge and instructions");
                    Equal(expected, full, "completed backdrop contains only dim desktop");
                }
                int before = invalidations;
                Mouse(overlay, "OnMouseDown", MouseButtons.Left, 4, 4);
                Mouse(overlay, "OnMouseMove", MouseButtons.Left, 150, 160);
                Mouse(overlay, "OnMouseUp", MouseButtons.Left, 150, 160);
                Assert(calls == 1 && invalidations == before, "completed backdrop ignores new drags");
                Assert(result.GetPixel(0, 0) == source.GetPixel(20, 25), "crop source coordinates");
                Color pixel = result.GetPixel(8, 7);
                source.SetPixel(28, 32, Color.Magenta);
                Assert(result.GetPixel(8, 7) == pixel, "crop owns independent pixels");
                overlay.Dispose();
                Assert(result.Width == 170 && source.Width == 240, "caller owns result and desktop after overlay dispose");
                result.Dispose();
            }
        }
    }

    private static void Cancellation()
    {
        foreach (bool completed in new bool[] { false, true })
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size)))
        {
            overlay.KeepOpenAfterSelection = true;
            IntPtr handle = overlay.Handle;
            Bitmap handed = null;
            overlay.SelectionCompleted += delegate(Bitmap image, Rectangle rectangle) { handed = image; };
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 12, 14);
            Mouse(overlay, "OnMouseMove", MouseButtons.Left, 70, 80);
            if (completed) Mouse(overlay, "OnMouseUp", MouseButtons.Left, 70, 80);
            Message message = new Message();
            object[] parameters = new object[] { message, Keys.Escape };
            bool consumed = (bool)typeof(RegionOverlay).GetMethod("ProcessCmdKey", Hidden).Invoke(overlay, parameters);
            Assert(consumed && overlay.IsDisposed && overlay.DialogResult == DialogResult.Cancel, "escape closes overlay");
            Assert(source.Width == 240, "escape leaves caller snapshot alive");
            if (handed != null) { Assert(handed.Width == 58, "escape preserves transferred crop"); handed.Dispose(); }
        }
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size)))
        {
            overlay.KeepOpenAfterSelection = true;
            IntPtr handle = overlay.Handle;
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 1, 1);
            Mouse(overlay, "OnMouseUp", MouseButtons.Left, 30, 30);
            Assert(overlay.IsDisposed && overlay.Result == null, "missing recipient cancels safely");
        }
    }

    private static void NormalSelection()
    {
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size)))
        {
            IntPtr handle = overlay.Handle;
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 1, 1);
            Mouse(overlay, "OnMouseUp", MouseButtons.Left, 2, 2);
            Assert(!overlay.IsDisposed && overlay.Result == null, "too-small selection remains active");
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 20, 25);
            Mouse(overlay, "OnMouseUp", MouseButtons.Left, 50, 55);
            using (Bitmap result = overlay.Result)
            {
                Assert(overlay.IsDisposed && overlay.DialogResult == DialogResult.OK, "legacy selection closes");
                Assert(result.Size == new Size(30, 30) && result.GetPixel(0, 0) == source.GetPixel(20, 25),
                    "legacy result remains caller-owned");
            }
        }
    }

    private static void ThrowingSubscriber()
    {
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size)))
        {
            overlay.KeepOpenAfterSelection = true;
            IntPtr handle = overlay.Handle;
            overlay.SelectionCompleted += delegate { throw new InvalidOperationException("synthetic failure"); };
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 20, 25);
            try { Mouse(overlay, "OnMouseUp", MouseButtons.Left, 50, 55); throw new Exception("exception lost"); }
            catch (TargetInvocationException error) { Assert(error.InnerException is InvalidOperationException, "subscriber error propagated"); }
            Assert(overlay.IsDisposed && source.Width == 240, "subscriber error releases overlay, preserves caller image");
        }
    }

    private static void DesktopWindowBounds()
    {
        foreach (int width in new int[] { 3840, 7680 })
        using (Bitmap source = new Bitmap(width, 2160, PixelFormat.Format32bppRgb))
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(-3840, -120, width, 2160)))
        {
            IntPtr handle = overlay.Handle;
            Rectangle expected = new Rectangle(-3840, -120, width, 2160);
            Assert(overlay.Bounds == expected && overlay.ClientSize == source.Size,
                "virtual desktop bounds preserved after HWND creation");
            overlay.Bounds = expected;
            Assert(overlay.Bounds == expected, "native tracking limits never truncate multi-monitor overlay");
        }
    }

    private static void NativeLifetime()
    {
        using (Bitmap source = MakeSource())
        {
            for (int i = 0; i < 5; i++) CreateAndRelease(source);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            for (int i = 0; i < 100; i++) CreateAndRelease(source);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            Assert(after <= before + 2, "100 create/dispose cycles do not grow GDI handles: " + before + " -> " + after);
            Console.WriteLine("GDI handles after warmup=" + before + " after100=" + after);
        }
    }

    private static void BurstRedrawAndBadge()
    {
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(-800, -600, source.Width, source.Height)))
        using (Bitmap incremental = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        using (Bitmap full = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(incremental))
        using (Graphics reference = Graphics.FromImage(full))
        using (Region dirty = new Region())
        {
            IntPtr handle = overlay.Handle;
            overlay.KeepOpenAfterSelection = true;
            dirty.MakeEmpty();
            overlay.Invalidated += delegate(object sender, InvalidateEventArgs e) { dirty.Union(e.InvalidRect); };
            Paint(overlay, graphics, overlay.ClientRectangle);
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 120, 100);
            Size badgeSize = Size.Empty;
            for (int frame = 0; frame < 6; frame++)
            {
                for (int sample = 0; sample < 64; sample++)
                {
                    int step = frame * 64 + sample;
                    Mouse(overlay, "OnMouseMove", MouseButtons.Left,
                        6 + (step * 37) % 228, 6 + (step * 19) % 168);
                    Rectangle badge = (Rectangle)typeof(RegionOverlay).GetField("badgeBounds", Hidden).GetValue(overlay);
                    if (badge.IsEmpty) continue;
                    if (badgeSize.IsEmpty) badgeSize = badge.Size;
                    Assert(badgeSize == badge.Size, "dimensions badge stays stable across digit changes");
                }
                PaintDirty(overlay, graphics, dirty);
                Paint(overlay, reference, overlay.ClientRectangle);
                Equal(full, incremental, "64-sample grow/shrink/reverse burst redraw");
                dirty.MakeEmpty();
            }
            Bitmap delivered = null;
            overlay.SelectionCompleted += delegate(Bitmap image, Rectangle rectangle) { delivered = image; };
            Mouse(overlay, "OnMouseUp", MouseButtons.Left, -10, 200);
            using (delivered)
                Assert(delivered.Size == new Size(120, 80) &&
                    delivered.GetPixel(0, 0) == source.GetPixel(0, 100),
                    "burst release uses exact clamped last position, not last painted coordinates");
        }
    }

    private static void CaptureLoss()
    {
        using (Bitmap source = MakeSource())
        using (RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size)))
        {
            IntPtr handle = overlay.Handle;
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 12, 14);
            Mouse(overlay, "OnMouseMove", MouseButtons.Left, 70, 80);
            overlay.Capture = false;
            typeof(RegionOverlay).GetMethod("OnMouseCaptureChanged", Hidden).Invoke(overlay, new object[] { EventArgs.Empty });
            Mouse(overlay, "OnMouseMove", MouseButtons.None, 120, 130);
            Mouse(overlay, "OnMouseUp", MouseButtons.Left, 120, 130);
            Rectangle selection = (Rectangle)typeof(RegionOverlay).GetField("selection", Hidden).GetValue(overlay);
            Assert(selection.IsEmpty && overlay.Result == null && !overlay.IsDisposed,
                "lost native capture cancels unfinished drag without accepting a phantom selection");
            Mouse(overlay, "OnMouseDown", MouseButtons.Left, 20, 25);
            Mouse(overlay, "OnMouseUp", MouseButtons.Left, 50, 55);
            using (Bitmap result = overlay.Result)
                Assert(result != null && result.Size == new Size(30, 30), "new drag works after capture loss");
        }
    }
    private static void CreateAndRelease(Bitmap source)
    {
        RegionOverlay overlay = new RegionOverlay(source, new Rectangle(Point.Empty, source.Size));
        IntPtr handle = overlay.Handle;
        overlay.Dispose();
        overlay.Dispose();
    }

    private static Bitmap MakeSource()
    {
        Bitmap source = new Bitmap(240, 180, PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(source))
        using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(Point.Empty, source.Size),
            Color.FromArgb(224, 242, 250), Color.FromArgb(70, 115, 169), 25F))
            graphics.FillRectangle(brush, new Rectangle(Point.Empty, source.Size));
        return source;
    }
    private static void PaintDirty(RegionOverlay overlay, Graphics graphics, Region dirty)
    {
        graphics.SetClip(dirty, CombineMode.Replace);
        Paint(overlay, graphics, Rectangle.Ceiling(dirty.GetBounds(graphics)));
        graphics.ResetClip();
    }
    private static void Paint(RegionOverlay overlay, Graphics graphics, Rectangle clip)
    {
        typeof(RegionOverlay).GetMethod("OnPaint", Hidden).Invoke(overlay,
            new object[] { new PaintEventArgs(graphics, clip) });
    }
    private static void Mouse(RegionOverlay overlay, string method, MouseButtons button, int x, int y)
    {
        typeof(RegionOverlay).GetMethod(method, Hidden).Invoke(overlay,
            new object[] { new MouseEventArgs(button, 1, x, y, 0) });
    }
    private static void Equal(Bitmap expected, Bitmap actual, string message)
    {
        Assert(expected.Size == actual.Size, message + " size");
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                if (expected.GetPixel(x, y) != actual.GetPixel(x, y))
                    throw new Exception(message + " differs at " + x + "," + y);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
