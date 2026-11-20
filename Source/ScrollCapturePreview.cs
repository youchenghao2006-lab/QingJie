using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingJie
{
    /// <summary>Borrowed immutable thumbnail; the session owns/replaces its pixels.</summary>
    internal sealed class ScrollPreviewCanvas : Control
    {
        private Bitmap _image;
        private int _totalHeight;
        private int _viewportHeight;
        internal ScrollPreviewCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.FromArgb(17, 25, 38);
        }
        internal void SetPreview(Bitmap image, int totalHeight, int viewportHeight)
        {
            _image = image;
            _totalHeight = totalHeight;
            _viewportHeight = viewportHeight;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_image == null || Width < 24 || Height < 24) return;
            Graphics graphics = e.Graphics;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            float unit = Width / 260f;
            int header = Math.Max(16, (int)(22 * unit));
            Rectangle overview = new Rectangle((int)(7 * unit), header, (int)(66 * unit), Height - header - (int)(8 * unit));
            Rectangle latest = new Rectangle((int)(86 * unit), header, Width - (int)(94 * unit), overview.Height);
            TextRenderer.DrawText(graphics, "全长", Font, new Rectangle(overview.Left, 1, overview.Width, header),
                Color.FromArgb(158, 180, 211), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            TextRenderer.DrawText(graphics, "最新内容", Font, new Rectangle(latest.Left, 1, latest.Width, header),
                Color.FromArgb(158, 180, 211), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            float fit = Math.Min(overview.Width / (float)_image.Width, overview.Height / (float)_image.Height);
            RectangleF full = new RectangleF(overview.Left + (overview.Width - _image.Width * fit) / 2,
                overview.Top, _image.Width * fit, _image.Height * fit);
            graphics.FillRectangle(Brushes.White, full);
            graphics.DrawImage(_image, full);
            if (_totalHeight > 0)
            {
                float markerHeight = Math.Max(3, full.Height * Math.Min(1, _viewportHeight / (float)_totalHeight));
                using (Pen marker = new Pen(Color.FromArgb(104, 181, 255), Math.Max(1, unit)))
                    graphics.DrawRectangle(marker, full.Left, full.Bottom - markerHeight,
                        Math.Max(1, full.Width - 1), Math.Max(1, markerHeight - 1));
            }
            // Preserve readable width in the detail pane. A growing long thumbnail
            // moves upward so the newest accepted content stays visible at bottom.
            float detailScale = latest.Width / (float)_image.Width;
            float sourceHeight = Math.Min(_image.Height, latest.Height / detailScale);
            RectangleF source = new RectangleF(0, _image.Height - sourceHeight, _image.Width, sourceHeight);
            RectangleF target = new RectangleF(latest.Left, latest.Top, latest.Width, sourceHeight * detailScale);
            graphics.FillRectangle(Brushes.White, target);
            graphics.DrawImage(_image, target, source, GraphicsUnit.Pixel);
            using (Pen separator = new Pen(Color.FromArgb(54, 72, 98)))
                graphics.DrawLine(separator, latest.Left - 6 * unit, header, latest.Left - 6 * unit, Height - 8 * unit);
        }
    }

    /// <summary>Live desktop surround: region holes and transparent hit testing allow normal scrolling.</summary>
    internal sealed class ScrollCaptureHint : Form
    {
        private readonly Rectangle _desiredBounds;
        private readonly Rectangle _sample;
        internal ScrollCaptureHint(Rectangle bounds, Rectangle sample, bool border)
        {
            _desiredBounds = bounds;
            _sample = sample;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = border ? Color.FromArgb(62, 151, 255) : Color.Black;
            Opacity = border ? .99 : .23;
            Bounds = bounds;
            UpdateHole();
        }
        private void UpdateHole()
        {
            Region next = new Region(ClientRectangle);
            next.Exclude(new Rectangle(_sample.Left - Left, _sample.Top - Top, _sample.Width, _sample.Height));
            Region old = Region;
            Region = next;
            if (old != null) old.Dispose();
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CaptureWindowPlacement.SetPhysicalBounds(Handle, _desiredBounds.Left, _desiredBounds.Top,
                _desiredBounds.Width, _desiredBounds.Height);
            UpdateHole();
        }
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_sample.Width > 0) UpdateHole();
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_sample.Width > 0) UpdateHole();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x80 | 0x20; // NOACTIVATE, TOOLWINDOW, TRANSPARENT
                return value;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
            CaptureWindowPlacement.SetMinimumTrackSize(ref message, new Size(1, 1));
        }
    }

    internal static class ScrollCaptureNative
    {
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        internal static bool TryExclude(IntPtr window)
        {
            try
            {
                uint affinity;
                return SetWindowDisplayAffinity(window, 0x11) &&
                    GetWindowDisplayAffinity(window, out affinity) && affinity == 0x11;
            }
            catch (EntryPointNotFoundException) { return false; }
        }
        internal static void ClearExclusion(IntPtr window)
        {
            try { SetWindowDisplayAffinity(window, 0); }
            catch (EntryPointNotFoundException) { }
        }
        internal static bool IsExcluded(IntPtr window, IntPtr verified)
        {
            if (window == IntPtr.Zero || window != verified) return false;
            try
            {
                uint affinity;
                return GetWindowDisplayAffinity(window, out affinity) && affinity == 0x11;
            }
            catch (EntryPointNotFoundException) { return false; }
        }
        internal static bool IsWindowCovering(IntPtr window, Point point)
        { return GetAncestor(WindowFromPoint(point), 2 /* GA_ROOT */) == window; }

        internal static bool ProvesExclusion(Bitmap baseline, Bitmap captured, Color visibleControlColor)
        {
            if (baseline == null || captured == null || baseline.Size != captured.Size) return false;
            int informative = 0;
            for (int y = 0; y < baseline.Height; y++)
                for (int x = 0; x < baseline.Width; x++)
                {
                    Color before = baseline.GetPixel(x, y), after = captured.GetPixel(x, y);
                    if (before.R != after.R || before.G != after.G || before.B != after.B) return false;
                    // Black rectangles can masquerade as exclusion over a black
                    // page, as can an uncovered control-colored background.
                    if (before.R + before.G + before.B > 90 &&
                        Math.Abs(before.R - visibleControlColor.R) + Math.Abs(before.G - visibleControlColor.G) +
                        Math.Abs(before.B - visibleControlColor.B) > 60) informative++;
                }
            return informative >= Math.Max(8, baseline.Width * baseline.Height / 4);
        }
        internal static async Task WaitForCompositionAsync(CancellationToken token)
        {
            bool composed = await Task.Run(delegate
            {
                try { return DwmFlush() >= 0; }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            });
            if (!composed) await Task.Delay(32, token);
            token.ThrowIfCancellationRequested();
        }
    }
}
