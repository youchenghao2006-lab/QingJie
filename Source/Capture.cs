using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QingJie
{
    public static class ScreenCapture
    {
        /// <summary>
        /// Capture before showing the overlay. The application must enable DPI awareness
        /// before creating any windows so VirtualScreen and CopyFromScreen use pixels.
        /// The caller owns the returned bitmap.
        /// </summary>
        public static Bitmap CaptureDesktop(out Rectangle bounds)
        {
            bounds = SystemInformation.VirtualScreen;
            return CaptureArea(bounds);
        }

        /// <summary>
        /// Capture exactly the requested screen-space pixel rectangle. A monitor may
        /// have a negative origin; its position must not be converted to local pixels.
        /// The caller owns the returned bitmap.
        /// </summary>
        public static Bitmap CaptureArea(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new ArgumentOutOfRangeException("bounds", "截图区域的宽高必须大于零。");

            Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                        CopyPixelOperation.SourceCopy);
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Convert a drag in either direction to a rectangle inside the image.
        /// Right/bottom are exclusive edges, so a coordinate may equal width/height.
        /// </summary>
        public static Rectangle NormalizeSelection(Point start, Point end, Size size)
        {
            if (size.Width < 0 || size.Height < 0)
                throw new ArgumentOutOfRangeException("size");

            int x1 = Math.Max(0, Math.Min(size.Width, start.X));
            int y1 = Math.Max(0, Math.Min(size.Height, start.Y));
            int x2 = Math.Max(0, Math.Min(size.Width, end.X));
            int y2 = Math.Max(0, Math.Min(size.Height, end.Y));
            return Rectangle.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2),
                Math.Max(x1, x2), Math.Max(y1, y2));
        }

        /// <summary>Return an independent, caller-owned bitmap without resampling.</summary>
        public static Bitmap Crop(Bitmap source, Rectangle rectangle)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            // Avoid Rectangle.Right/Bottom arithmetic overflow for malformed inputs.
            if (rectangle.X < 0 || rectangle.Y < 0 ||
                rectangle.Width <= 0 || rectangle.Height <= 0 ||
                rectangle.Width > source.Width || rectangle.Height > source.Height ||
                rectangle.X > source.Width - rectangle.Width ||
                rectangle.Y > source.Height - rectangle.Height)
                throw new ArgumentOutOfRangeException("rectangle", "截图范围必须在图像内，且宽高不能为零。");

            return source.Clone(rectangle, PixelFormat.Format32bppArgb);
        }
    }

    public sealed class RegionOverlay : Form
    {
        private readonly Bitmap desktop;
        private readonly DesktopPixelCache brightCache;
        private readonly DesktopPixelCache dimmedCache;
        private readonly Rectangle desktopBounds;
        private readonly Rectangle[] monitors;
        private readonly Font instructionFont;
        private readonly Font detailFont;
        private readonly Color accent = Color.FromArgb(110, 190, 255);
        private readonly Pen outerPen;
        private readonly Pen borderPen;
        private readonly Pen instructionPen;
        private readonly Brush instructionBrush;
        private readonly Brush badgeBrush;
        private readonly Size normalTitleSize;
        private readonly Size smallTitleSize;
        private readonly Size instructionDetailSize;
        private readonly bool scrolling;
        private Point dragStart;
        private Point pointer;
        private Rectangle selection;
        private Rectangle pointerMonitor;
        private Rectangle instructionBounds;
        private Rectangle badgeBounds;
        private readonly Size badgeTextSize;
        private bool dragging;
        private bool tooSmall;

        private const string NormalInstruction = "拖动鼠标，框选截图区域";
        private const string SmallInstruction = "选区太小，请重新框选";
        private const string InstructionDetail = "松开完成截图  ·  Esc / 鼠标右键取消";
        private const string QuickInstructionDetail = "松开调整与标注  ·  Esc / 鼠标右键取消";
        private const string ScrollInstructionTitle = "框选要向下滚动的内容";
        private const string ScrollInstructionDetail = "松开后滚动页面  ·  Ctrl + Enter 完成  ·  Esc 取消";
        private const TextFormatFlags ClippedText = TextFormatFlags.NoPadding |
            TextFormatFlags.PreserveGraphicsClipping;

        /// <summary>Selected coordinates are relative to the captured bitmap.</summary>
        public Rectangle SelectedRegion { get; private set; }

        /// <summary>The caller owns Result, including after this form is disposed.</summary>
        public Bitmap Result { get; private set; }

        /// <summary>
        /// Opt in before showing: selection leaves this same dimmed window alive
        /// as the owner/backdrop of the quick editor instead of closing it.
        /// </summary>
        public bool KeepOpenAfterSelection { get; set; }

        public bool IsSelectionCompleted { get; private set; }

        /// <summary>
        /// Continuous mode only. The subscriber owns the independent cropped bitmap;
        /// rectangle coordinates are relative to the captured desktop. Result remains
        /// null in this mode. Esc, right-click, and Close still close this window and
        /// its owned editor; the host can observe FormClosed for cancellation.
        /// </summary>
        public event Action<Bitmap, Rectangle> SelectionCompleted;

        /// <summary>
        /// The supplied desktop remains owned by the caller. Bounds may start at a
        /// negative coordinate when another monitor is left/above the main display.
        /// </summary>
        public RegionOverlay(Bitmap desktop, Rectangle bounds)
            : this(desktop, bounds, false) { }

        internal RegionOverlay(Bitmap desktop, Rectangle bounds, bool scrolling)
        {
            if (desktop == null)
                throw new ArgumentNullException("desktop");
            if (bounds.Width <= 0 || bounds.Height <= 0 || desktop.Size != bounds.Size)
                throw new ArgumentException("屏幕范围必须与截图尺寸一致。", "bounds");

            this.desktop = desktop;
            this.scrolling = scrolling;
            desktopBounds = bounds;
            // Normalize both snapshots once. Drawing an ARGB source can otherwise
            // make GDI+ premultiply the full source repeatedly, even for a tiny clip.
            // The original bitmap is retained exclusively for the final exact crop.
            brightCache = CreateCachedDesktop(desktop);
            try
            {
                dimmedCache = CreateDimmedDesktop(brightCache);
            }
            catch { brightCache.Dispose(); throw; }
            instructionFont = new Font("Microsoft YaHei UI", 11.0f, FontStyle.Regular,
                GraphicsUnit.Point);
            detailFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular,
                GraphicsUnit.Point);
            outerPen = new Pen(Color.FromArgb(200, 9, 23, 31), 3.0f);
            borderPen = new Pen(accent, 1.0f);
            borderPen.DashStyle = DashStyle.Dash;
            instructionPen = new Pen(Color.FromArgb(95, accent));
            instructionBrush = new SolidBrush(Color.FromArgb(238, 17, 26, 39));
            badgeBrush = new SolidBrush(Color.FromArgb(245, 17, 26, 39));
            normalTitleSize = TextRenderer.MeasureText(scrolling ? ScrollInstructionTitle : NormalInstruction, instructionFont);
            smallTitleSize = TextRenderer.MeasureText(SmallInstruction, instructionFont);
            Size standardDetail = TextRenderer.MeasureText(scrolling ? ScrollInstructionDetail : InstructionDetail, detailFont);
            Size quickDetail = TextRenderer.MeasureText(QuickInstructionDetail, detailFont);
            instructionDetailSize = new Size(Math.Max(standardDetail.Width, quickDetail.Width),
                Math.Max(standardDetail.Height, quickDetail.Height));
            // Reserve the largest dimensions once. Pointer samples need only
            // integer layout, and crossing 99/100 or 999/1000 cannot move the badge.
            int digitsX = desktop.Width.ToString().Length;
            int digitsY = desktop.Height.ToString().Length;
            int digitWidth = 0;
            for (char digit = '0'; digit <= '9'; digit++)
                digitWidth = Math.Max(digitWidth, TextRenderer.MeasureText(digit.ToString(),
                    detailFont, Size.Empty, TextFormatFlags.NoPadding).Width);
            Size separators = TextRenderer.MeasureText(" ×  px", detailFont, Size.Empty,
                TextFormatFlags.NoPadding);
            badgeTextSize = new Size(digitWidth * (digitsX + digitsY) + separators.Width,
                separators.Height);

            // Monitor geometry is frozen along with the screenshot; querying Windows
            // for the monitor under every mouse sample is unnecessary.
            Screen[] screens = Screen.AllScreens;
            monitors = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle monitor = Rectangle.Intersect(screens[i].Bounds, bounds);
                monitor.Offset(-bounds.Left, -bounds.Top);
                monitors[i] = monitor;
            }

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            Text = "轻截 · 框选截图";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // Maximizing uses only one monitor and its working area; explicit bounds
            // preserve the entire virtual desktop, taskbars, and negative origins.
            Bounds = desktopBounds;
            pointer = new Point(Cursor.Position.X - bounds.Left, Cursor.Position.Y - bounds.Top);
            pointerMonitor = FindPointerMonitor(pointer);
            UpdateOverlayLayout();
        }

        private static DesktopPixelCache CreateCachedDesktop(Bitmap source)
        {
            DesktopPixelCache cached = new DesktopPixelCache(source.Size);
            try
            {
                using (Graphics graphics = Graphics.FromImage(cached.Image))
                {
                    Rectangle imageBounds = new Rectangle(Point.Empty, source.Size);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImage(source, imageBounds, imageBounds, GraphicsUnit.Pixel);
                }
                return cached;
            }
            catch
            {
                cached.Dispose();
                throw;
            }
        }

        private static DesktopPixelCache CreateDimmedDesktop(DesktopPixelCache source)
        {
            DesktopPixelCache cached = new DesktopPixelCache(source.Image.Size);
            try
            {
                Rectangle bounds = new Rectangle(Point.Empty, source.Image.Size);
                source.CopyTo(cached.Dc, bounds);
                DesktopPixelCache.GdiFlush();
                using (Graphics graphics = Graphics.FromImage(cached.Image))
                using (Brush shade = new SolidBrush(Color.FromArgb(144, 5, 12, 21)))
                    graphics.FillRectangle(shade, bounds);
                return cached;
            }
            catch { cached.Dispose(); throw; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // WinForms may adjust initial placement to one monitor while creating
            // the native handle. Reapply the exact rectangle after creation.
            Bounds = desktopBounds;
            Activate();
            Focus();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
            int width, int height, uint flags);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (desktopBounds.Width > 0 && desktopBounds.Height > 0)
                SetBoundsCore(desktopBounds.X, desktopBounds.Y, desktopBounds.Width,
                    desktopBounds.Height, BoundsSpecified.All);
        }

        protected override void SetBoundsCore(int x, int y, int width, int height,
            BoundsSpecified specified)
        {
            // Framework Form.SetBoundsCore clamps to the primary monitor's
            // MaximumWindowTrackSize even with a larger WM_GETMINMAXINFO value.
            // Once the HWND exists, set the requested physical rectangle directly;
            // WM_WINDOWPOSCHANGED keeps WinForms' managed Bounds synchronized.
            if (IsHandleCreated && desktopBounds.Width > 0 && desktopBounds.Height > 0)
            {
                if (!SetWindowPos(Handle, IntPtr.Zero, x, y, width, height,
                    0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */))
                    throw new Win32Exception();
                return;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OverlayMinMaxInfo
        {
            public Point Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x24 /* WM_GETMINMAXINFO */ && message.LParam != IntPtr.Zero)
            {
                OverlayMinMaxInfo info = (OverlayMinMaxInfo)Marshal.PtrToStructure(
                    message.LParam, typeof(OverlayMinMaxInfo));
                // Windows otherwise limits a borderless form to the current
                // monitor's tracking size, clipping a virtual-desktop selection.
                info.MinimumTrackSize = new Point(1, 1);
                info.MaximumTrackSize = new Point(32767, 32767);
                Marshal.StructureToPtr(info, message.LParam, false);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // OnPaint restores every requested pixel from immutable images.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle clip = Rectangle.Intersect(e.ClipRectangle,
                new Rectangle(Point.Empty, desktop.Size));
            if (clip.Width <= 0 || clip.Height <= 0) return;

            Graphics graphics = e.Graphics;
            GraphicsState state = graphics.Save();
            try
            {
                // Preserve a complex Windows update region or the caller's narrower
                // clip. In particular, do not turn changed edge bands into a full
                // selection repaint. Exposure/cover paints can still request any area.
                graphics.SetClip(clip, CombineMode.Intersect);
                PaintCachedDesktop(graphics, clip);
                graphics.CompositingMode = CompositingMode.SourceOver;

                if (IsSelectionCompleted)
                {
                    // The quick editor covers the chosen region. Outside it this
                    // original window paints only the cached dark desktop.
                }
                else if (HasArea(selection))
                {
                    Rectangle edge = new Rectangle(selection.X, selection.Y,
                        Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));
                    graphics.DrawRectangle(outerPen, edge);
                    graphics.DrawRectangle(borderPen, edge);
                    if (badgeBounds.IntersectsWith(clip)) DrawSize(graphics);
                }
                else if (instructionBounds.IntersectsWith(clip))
                {
                    DrawInstructions(graphics);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private static bool HasArea(Rectangle rectangle)
        {
            return rectangle.Width > 0 && rectangle.Height > 0;
        }

        private void PaintCachedDesktop(Graphics graphics, Rectangle clip)
        {
            using (Region region = graphics.Clip)
            {
                IntPtr nativeRegion = region.GetHrgn(graphics);
                IntPtr destination = IntPtr.Zero;
                int saved = 0;
                try
                {
                    destination = graphics.GetHdc();
                    saved = DesktopPixelCache.SaveDC(destination);
                    if (saved == 0 || DesktopPixelCache.ExtSelectClipRgn(destination, nativeRegion, 1) == 0)
                        throw new Win32Exception();
                    Rectangle bright = Rectangle.Intersect(selection, clip);
                    if (HasArea(bright))
                    {
                        // The caches are DIB sections shared with their Bitmap
                        // wrappers: no extra full-desktop native copy is retained.
                        dimmedCache.CopyTo(destination, Rectangle.FromLTRB(clip.Left, clip.Top, clip.Right, bright.Top));
                        dimmedCache.CopyTo(destination, Rectangle.FromLTRB(clip.Left, bright.Bottom, clip.Right, clip.Bottom));
                        dimmedCache.CopyTo(destination, Rectangle.FromLTRB(clip.Left, bright.Top, bright.Left, bright.Bottom));
                        dimmedCache.CopyTo(destination, Rectangle.FromLTRB(bright.Right, bright.Top, clip.Right, bright.Bottom));
                        brightCache.CopyTo(destination, bright);
                    }
                    else dimmedCache.CopyTo(destination, clip);
                }
                finally
                {
                    if (destination != IntPtr.Zero)
                    {
                        if (saved != 0) DesktopPixelCache.RestoreDC(destination, saved);
                        graphics.ReleaseHdc(destination);
                    }
                    DesktopPixelCache.DeleteObject(nativeRegion);
                }
            }
        }

        private Rectangle FindPointerMonitor(Point position)
        {
            if (pointerMonitor.Contains(position)) return pointerMonitor;
            Rectangle nearest = new Rectangle(Point.Empty, desktop.Size);
            long bestDistance = Int64.MaxValue;
            foreach (Rectangle monitor in monitors)
            {
                if (!HasArea(monitor)) continue;
                if (monitor.Contains(position)) return monitor;
                long dx = position.X < monitor.Left ? (long)monitor.Left - position.X :
                    position.X >= monitor.Right ? (long)position.X - monitor.Right + 1 : 0;
                long dy = position.Y < monitor.Top ? (long)monitor.Top - position.Y :
                    position.Y >= monitor.Bottom ? (long)position.Y - monitor.Bottom + 1 : 0;
                long distance = dx * dx + dy * dy;
                if (distance < bestDistance) { nearest = monitor; bestDistance = distance; }
            }
            return nearest;
        }

        private void UpdateOverlayLayout()
        {
            if (!HasArea(selection))
            {
                Size titleSize = tooSmall ? smallTitleSize : normalTitleSize;
                int panelWidth = Math.Min(Math.Max(titleSize.Width, instructionDetailSize.Width) + 40,
                    Math.Max(1, pointerMonitor.Width - 24));
                instructionBounds = new Rectangle(
                    pointerMonitor.Left + (pointerMonitor.Width - panelWidth) / 2,
                    pointerMonitor.Top + Math.Min(64, Math.Max(12, pointerMonitor.Height / 8)),
                    panelWidth, titleSize.Height + instructionDetailSize.Height + 36);
                badgeBounds = Rectangle.Empty;
                return;
            }

            instructionBounds = Rectangle.Empty;
            int width = badgeTextSize.Width + 20;
            int height = badgeTextSize.Height + 12;
            int x = Math.Max(pointerMonitor.Left + 4,
                Math.Min(selection.Left, pointerMonitor.Right - width - 4));
            int y = selection.Top - height - 8;
            if (y < pointerMonitor.Top + 4)
                y = selection.Bottom + 8;
            y = Math.Max(pointerMonitor.Top + 4,
                Math.Min(y, pointerMonitor.Bottom - height - 4));
            badgeBounds = new Rectangle(x, y, width, height);
        }

        private void DrawInstructions(Graphics graphics)
        {
            Size titleSize = tooSmall ? smallTitleSize : normalTitleSize;
            Rectangle panel = instructionBounds;
            graphics.FillRectangle(instructionBrush, panel);
            graphics.DrawRectangle(instructionPen, panel);
            string title = scrolling ? ScrollInstructionTitle : NormalInstruction;
            string help = scrolling ? ScrollInstructionDetail : InstructionDetail;
            TextRenderer.DrawText(graphics, tooSmall ? SmallInstruction : title,
                instructionFont,
                new Rectangle(panel.X + 16, panel.Y + 12, Math.Max(1, panel.Width - 32), titleSize.Height),
                Color.White, TextFormatFlags.HorizontalCenter | ClippedText);
            TextRenderer.DrawText(graphics, KeepOpenAfterSelection ? QuickInstructionDetail : help, detailFont,
                new Rectangle(panel.X + 12, panel.Y + 18 + titleSize.Height,
                    Math.Max(1, panel.Width - 24), instructionDetailSize.Height),
                Color.FromArgb(182, 197, 214), TextFormatFlags.HorizontalCenter | ClippedText);
        }

        private void DrawSize(Graphics graphics)
        {
            graphics.FillRectangle(badgeBrush, badgeBounds);
            // Several mouse samples may arrive before one WM_PAINT. Format only
            // the dimensions that will actually be shown, using the latest state.
            string text = selection.Width.ToString() + " × " + selection.Height.ToString() + " px";
            TextRenderer.DrawText(graphics, text, detailFont,
                new Rectangle(badgeBounds.X + 10, badgeBounds.Y + 6,
                    badgeTextSize.Width, badgeTextSize.Height), accent,
                TextFormatFlags.HorizontalCenter | ClippedText);
        }

        private void ChangeVisual(Point nextPointer, Rectangle nextSelection, bool nextTooSmall)
        {
            Rectangle nextMonitor = FindPointerMonitor(nextPointer);
            bool selectionChanged = nextSelection != selection;
            bool monitorChanged = nextMonitor != pointerMonitor;
            bool titleChanged = nextTooSmall != tooSmall;
            pointer = nextPointer;
            if (!selectionChanged && !monitorChanged && !titleChanged) return;

            Rectangle oldSelection = selection;
            Rectangle oldBadge = badgeBounds;
            Rectangle oldInstructions = instructionBounds;
            selection = nextSelection;
            pointerMonitor = nextMonitor;
            tooSmall = nextTooSmall;
            UpdateOverlayLayout();

            if (selectionChanged)
            {
                // Only pixels whose brightness changes and the two outlines need
                // restoring. The large intersection of old/new selections is stable.
                InvalidateDifference(oldSelection, selection);
                InvalidateDifference(selection, oldSelection);
                InvalidateOutline(oldSelection);
                InvalidateOutline(selection);
            }
            if (selectionChanged || monitorChanged)
            {
                InvalidateDirty(oldBadge);
                InvalidateDirty(badgeBounds);
            }
            if (oldInstructions != instructionBounds || titleChanged)
            {
                InvalidateDirty(InflateVisible(oldInstructions, 2));
                InvalidateDirty(InflateVisible(instructionBounds, 2));
            }
        }

        private static Rectangle InflateVisible(Rectangle rectangle, int amount)
        {
            if (!HasArea(rectangle)) return Rectangle.Empty;
            rectangle.Inflate(amount, amount);
            return rectangle;
        }

        private void InvalidateOutline(Rectangle rectangle)
        {
            if (!HasArea(rectangle)) return;
            Rectangle outside = InflateVisible(rectangle, 3);
            Rectangle inside = rectangle;
            inside.Inflate(-3, -3);
            InvalidateDifference(outside, inside);
        }

        private void InvalidateDifference(Rectangle area, Rectangle other)
        {
            if (!HasArea(area)) return;
            Rectangle inside = Rectangle.Intersect(area, other);
            if (!HasArea(inside)) { InvalidateDirty(area); return; }
            InvalidateDirty(Rectangle.FromLTRB(area.Left, area.Top, area.Right, inside.Top));
            InvalidateDirty(Rectangle.FromLTRB(area.Left, inside.Bottom, area.Right, area.Bottom));
            InvalidateDirty(Rectangle.FromLTRB(area.Left, inside.Top, inside.Left, inside.Bottom));
            InvalidateDirty(Rectangle.FromLTRB(inside.Right, inside.Top, area.Right, inside.Bottom));
        }

        private void InvalidateDirty(Rectangle rectangle)
        {
            rectangle.Intersect(new Rectangle(Point.Empty, desktop.Size));
            if (HasArea(rectangle)) Invalidate(rectangle, false);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                CancelCapture();
                return;
            }
            if (IsSelectionCompleted || e.Button != MouseButtons.Left)
                return;

            dragStart = e.Location;
            ChangeVisual(e.Location, Rectangle.Empty, false);
            dragging = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsSelectionCompleted) return;
            Rectangle next = dragging
                ? ScreenCapture.NormalizeSelection(dragStart, e.Location, desktop.Size) : selection;
            ChangeVisual(e.Location, next, tooSmall);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging || e.Button != MouseButtons.Left)
                return;

            Rectangle finalSelection = ScreenCapture.NormalizeSelection(dragStart, e.Location, desktop.Size);
            dragging = false;
            Capture = false;
            if (finalSelection.Width < (scrolling ? 32 : 3) || finalSelection.Height < (scrolling ? 64 : 3))
            {
                ChangeVisual(e.Location, Rectangle.Empty, true);
                return;
            }

            SelectedRegion = finalSelection;
            if (scrolling && !KeepOpenAfterSelection)
            {
                // The scroll session samples its live first frame after this
                // window is gone. Only the physical region is needed here.
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            Bitmap cropped = ScreenCapture.Crop(desktop, finalSelection);
            if (KeepOpenAfterSelection)
            {
                Action<Bitmap, Rectangle> handler = SelectionCompleted;
                if (handler == null)
                {
                    cropped.Dispose();
                    CancelCapture();
                    return;
                }
                CompleteSelectionBackdrop();
                try { handler(cropped, finalSelection); }
                catch
                {
                    cropped.Dispose();
                    CancelCapture();
                    throw;
                }
                return;
            }
            Result = cropped;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // Another window can take native capture before the button is
            // released. Do not turn later hover samples into a phantom drag.
            if (dragging && !Capture && !IsDisposed && !Disposing)
            {
                dragging = false;
                ChangeVisual(pointer, Rectangle.Empty, false);
            }
        }

        private void CompleteSelectionBackdrop()
        {
            Rectangle oldSelection = selection;
            Rectangle oldInstructions = instructionBounds;
            Rectangle oldBadge = badgeBounds;
            IsSelectionCompleted = true;
            selection = instructionBounds = badgeBounds = Rectangle.Empty;
            Cursor = Cursors.Default;
            // Only remove the old bright area and chrome. The rest of the desktop
            // is already dark and needs no second full-screen transition paint.
            InvalidateDirty(InflateVisible(oldSelection, 3));
            InvalidateDirty(InflateVisible(oldInstructions, 2));
            InvalidateDirty(oldBadge);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                CancelCapture();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CancelCapture()
        {
            dragging = false;
            Capture = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                brightCache.Dispose();
                dimmedCache.Dispose();
                instructionFont.Dispose();
                detailFont.Dispose();
                outerPen.Dispose();
                borderPen.Dispose();
                instructionPen.Dispose();
                instructionBrush.Dispose();
                badgeBrush.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// A top-down 32-bit DIB supplies both GDI+'s Bitmap and a reusable GDI
        /// memory DC. BitBlt copies only the device update region and avoids
        /// repeatedly entering the GDI+ image conversion path during a drag.
        /// </summary>
        private sealed class DesktopPixelCache : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct BitmapInfo
            {
                public uint Size;
                public int Width, Height;
                public ushort Planes, BitCount;
                public uint Compression, SizeImage;
                public int XPelsPerMeter, YPelsPerMeter;
                public uint ColorsUsed, ColorsImportant;
            }
            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
                uint usage, out IntPtr pixels, IntPtr section, uint offset);
            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
                IntPtr source, int sourceX, int sourceY, uint operation);
            [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
            [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
            [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll", SetLastError = true)] internal static extern int SaveDC(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool RestoreDC(IntPtr dc, int saved);
            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern int ExtSelectClipRgn(IntPtr dc, IntPtr region, int mode);
            private IntPtr dc, bitmap, previous;
            internal Bitmap Image { get; private set; }
            internal IntPtr Dc { get { return dc; } }

            internal DesktopPixelCache(Size size)
            {
                try
                {
                    dc = CreateCompatibleDC(IntPtr.Zero);
                    if (dc == IntPtr.Zero) throw new Win32Exception();
                    BitmapInfo info = new BitmapInfo { Size = (uint)Marshal.SizeOf(typeof(BitmapInfo)),
                        Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
                    IntPtr pixels;
                    bitmap = CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
                    if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) throw new Win32Exception();
                    previous = SelectObject(dc, bitmap);
                    if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
                    Image = new Bitmap(size.Width, size.Height, checked(size.Width * 4),
                        PixelFormat.Format32bppPArgb, pixels);
                }
                catch { Dispose(); throw; }
            }

            internal void CopyTo(IntPtr destination, Rectangle rectangle)
            {
                if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
                if (!BitBlt(destination, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
                    dc, rectangle.X, rectangle.Y, 0x00CC0020 /* SRCCOPY */))
                    throw new Win32Exception();
            }

            ~DesktopPixelCache() { Dispose(); }

            public void Dispose()
            {
                if (Image != null) { Image.Dispose(); Image = null; }
                if (dc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1))
                    SelectObject(dc, previous);
                previous = IntPtr.Zero;
                if (bitmap != IntPtr.Zero) { DeleteObject(bitmap); bitmap = IntPtr.Zero; }
                if (dc != IntPtr.Zero) { DeleteDC(dc); dc = IntPtr.Zero; }
                GC.SuppressFinalize(this);
            }
        }
    }
}
