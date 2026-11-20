using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace QingJie
{
    /// <summary>An independently owned screenshot that can be moved around the desktop.</summary>
    public sealed class PinnedImageForm : Form
    {
        private const int BorderPixels = 3;
        private Bitmap _image;
        private Bitmap _preview;
        private readonly ContextMenuStrip _menu;
        private readonly Timer _settleTimer;
        private float _zoom = 1f;
        private bool _dragging;
        private bool _zooming;
        private bool _closing;
        private Point _dragStartPointer;
        private Point _dragStartLocation;

        public Size ImageSize { get { return _image == null ? Size.Empty : _image.Size; } }
        public float ZoomPercent { get { return _zoom * 100; } }
        public Rectangle ImageScreenBounds
        {
            get { return new Rectangle(Left + BorderPixels, Top + BorderPixels,
                Math.Max(1, ClientSize.Width - BorderPixels * 2),
                Math.Max(1, ClientSize.Height - BorderPixels * 2)); }
        }
        public PinnedImageForm(Bitmap image, Point imageLocation)
        {
            if (image == null) throw new ArgumentNullException("image");
            _image = CaptureImageActions.Clone(image);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "轻截 · 贴屏";
            BackColor = Color.FromArgb(53, 67, 91);
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.SizeAll;
            Location = new Point(imageLocation.X - BorderPixels, imageLocation.Y - BorderPixels);
            ClientSize = new Size(image.Width + BorderPixels * 2, image.Height + BorderPixels * 2);
            BuildPreview();
            _settleTimer = new Timer { Interval = 140 };
            _settleTimer.Tick += delegate
            {
                if (_closing || IsDisposed || Disposing) return;
                _settleTimer.Stop();
                _zooming = false;
                Invalidate();
            };

            _menu = new ContextMenuStrip { Font = UiTheme.Font(9f) };
            AddMenu("复制图像    Ctrl+C", CopyToClipboard);
            AddMenu("保存图像    Ctrl+S", SaveImage);
            _menu.Items.Add(new ToolStripSeparator());
            AddMenu("原始尺寸    Ctrl+0", delegate { SetZoom(100, CenterPoint()); });
            ToolStripMenuItem sizes = new ToolStripMenuItem("缩放");
            foreach (int size in new int[] { 33, 50, 100, 200 })
            {
                int percent = size;
                sizes.DropDownItems.Add(percent + "%", null,
                    delegate { SetZoom(percent, CenterPoint()); });
            }
            _menu.Items.Add(sizes);
            AddMenu("向左旋转", RotateCounterClockwise);
            AddMenu("向右旋转", RotateClockwise);
            AddMenu("水平镜像", FlipHorizontal);
            AddMenu("垂直镜像", FlipVertical);
            ToolStripMenuItem opacity = new ToolStripMenuItem("透明度");
            foreach (int level in new int[] { 100, 90, 80, 60, 40 })
            {
                int percent = level;
                opacity.DropDownItems.Add(percent + "%", null,
                    delegate { SetOpacityPercent(percent); });
            }
            _menu.Items.Add(opacity);
            _menu.Items.Add(new ToolStripSeparator());
            AddMenu("关闭贴图    Esc", Close);
            ContextMenuStrip = _menu;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ClassStyle |= 0x20000; return value; }
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            CaptureWindowPlacement.SetMinimumTrackSize(ref message,
                new Size(BorderPixels * 2 + 1, BorderPixels * 2 + 1));
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_image != null)
            {
                Size size = DisplaySize();
                SetBoundsCore(Left, Top, size.Width + BorderPixels * 2,
                    size.Height + BorderPixels * 2, BoundsSpecified.All);
            }
        }
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (IsHandleCreated && !IsDisposed && !Disposing)
            {
                CaptureWindowPlacement.SetPhysicalBounds(Handle, x, y, width, height);
                return;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        private void AddMenu(string label, Action action)
        {
            _menu.Items.Add(label, null, delegate { RunAction(action); });
        }

        public Bitmap ExportImage()
        {
            if (_image == null) throw new ObjectDisposedException("PinnedImageForm");
            return CaptureImageActions.Clone(_image);
        }

        /// <summary>Zooms around a screen point, preserving the image point beneath it.</summary>
        public void SetZoom(float percent, Point screenAnchor)
        {
            if (Single.IsNaN(percent) || Single.IsInfinity(percent) || percent <= 0)
                throw new ArgumentOutOfRangeException("percent");
            Rectangle before = ImageScreenBounds;
            float x = (screenAnchor.X - before.X) / (float)before.Width;
            float y = (screenAnchor.Y - before.Y) / (float)before.Height;
            float max = Math.Min(4f, 20000f / Math.Max(_image.Width, _image.Height));
            float next = Math.Max(Math.Min(.1f, max), Math.Min(max, percent / 100f));
            if (Math.Abs(next - _zoom) < .00001f) return;
            _zoom = next;
            Size size = DisplaySize();
            Point location = new Point(
                (int)Math.Round(screenAnchor.X - x * size.Width) - BorderPixels,
                (int)Math.Round(screenAnchor.Y - y * size.Height) - BorderPixels);
            _zooming = true;
            SuspendLayout();
            SetBounds(location.X, location.Y, size.Width + BorderPixels * 2,
                size.Height + BorderPixels * 2);
            ResumeLayout(false);
            _settleTimer.Stop();
            _settleTimer.Start();
            Invalidate();
        }

        public void RotateClockwise() { TransformImage(RotateFlipType.Rotate90FlipNone); }
        public void RotateCounterClockwise() { TransformImage(RotateFlipType.Rotate270FlipNone); }
        public void FlipHorizontal() { TransformImage(RotateFlipType.RotateNoneFlipX); }
        public void FlipVertical() { TransformImage(RotateFlipType.RotateNoneFlipY); }
        public void SetOpacityPercent(int percent) { Opacity = Math.Max(20, Math.Min(100, percent)) / 100.0; }

        private void TransformImage(RotateFlipType transform)
        {
            Point center = CenterPoint();
            _image.RotateFlip(transform);
            BuildPreview();
            Size size = DisplaySize();
            SetBounds(center.X - size.Width / 2 - BorderPixels,
                center.Y - size.Height / 2 - BorderPixels,
                size.Width + BorderPixels * 2, size.Height + BorderPixels * 2);
            Invalidate();
        }

        private Size DisplaySize()
        {
            return new Size(Math.Max(1, (int)Math.Round(_image.Width * _zoom)),
                Math.Max(1, (int)Math.Round(_image.Height * _zoom)));
        }

        private Point CenterPoint()
        {
            Rectangle bounds = ImageScreenBounds;
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        private void BuildPreview()
        {
            float ratio = Math.Min(1f, 1600f / Math.Max(_image.Width, _image.Height));
            Bitmap next = new Bitmap(Math.Max(1, (int)Math.Round(_image.Width * ratio)),
                Math.Max(1, (int)Math.Round(_image.Height * ratio)), PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(next))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (ImageAttributes attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(_image, new Rectangle(Point.Empty, next.Size),
                            0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
            }
            catch { next.Dispose(); throw; }
            if (_preview != null) _preview.Dispose();
            _preview = next;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_image == null) return;
            Rectangle target = new Rectangle(BorderPixels, BorderPixels,
                Math.Max(1, ClientSize.Width - BorderPixels * 2),
                Math.Max(1, ClientSize.Height - BorderPixels * 2));
            Rectangle clip = Rectangle.Intersect(target, e.ClipRectangle);
            if (clip.Width > 0 && clip.Height > 0)
            {
                Graphics graphics = e.Graphics;
                using (Brush white = new SolidBrush(Color.White)) graphics.FillRectangle(white, clip);
                Bitmap source = _zooming ? _preview : _image;
                RectangleF region = new RectangleF(
                    (clip.Left - target.Left) * source.Width / (float)target.Width,
                    (clip.Top - target.Top) * source.Height / (float)target.Height,
                    clip.Width * source.Width / (float)target.Width,
                    clip.Height * source.Height / (float)target.Height);
                graphics.InterpolationMode = _zooming ? InterpolationMode.NearestNeighbor :
                    (_zoom == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear);
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(source, new PointF[] {
                        new PointF(clip.Left, clip.Top), new PointF(clip.Right, clip.Top),
                        new PointF(clip.Left, clip.Bottom) }, region, GraphicsUnit.Pixel, attributes);
                }
            }
            using (Pen border = new Pen(Color.FromArgb(179, 198, 227)))
                e.Graphics.DrawRectangle(border, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragStartPointer = PointToScreen(e.Location);
            _dragStartLocation = Location;
            Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            UpdateDragLocation(PointToScreen(e.Location));
        }
        private void UpdateDragLocation(Point pointer)
        {
            Location = new Point(_dragStartLocation.X + pointer.X - _dragStartPointer.X,
                _dragStartLocation.Y + pointer.Y - _dragStartPointer.Y);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _dragging)
            {
                UpdateDragLocation(PointToScreen(e.Location));
                _dragging = false;
                Capture = false;
            }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) _dragging = false;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_dragging && ClientRectangle.Contains(e.Location) && e.Delta != 0)
                SetZoom(ZoomPercent * (float)Math.Pow(1.15, e.Delta / 120.0), PointToScreen(e.Location));
        }
        public void CopyToClipboard()
        {
            RunAction(delegate { using (Bitmap image = ExportImage()) CaptureImageActions.Copy(image); });
        }
        public void SaveImage()
        {
            RunAction(delegate { using (Bitmap image = ExportImage()) CaptureImageActions.Save(this, image); });
        }
        private void RunAction(Action action)
        {
            if (_closing || IsDisposed || Disposing) return;
            try { action(); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "轻截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private bool HandleShortcut(Keys keys)
        {
            if (keys == Keys.Escape && _dragging)
            {
                _dragging = false;
                Capture = false;
                Location = _dragStartLocation;
                return true;
            }
            if (keys == Keys.Escape) { Close(); return true; }
            if (keys == (Keys.Control | Keys.C)) { CopyToClipboard(); return true; }
            if (keys == (Keys.Control | Keys.S)) { SaveImage(); return true; }
            if (keys == (Keys.Control | Keys.D0)) { SetZoom(100, CenterPoint()); return true; }
            return false;
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                _dragging = false;
                if (_settleTimer != null) _settleTimer.Dispose();
                if (_menu != null) _menu.Dispose();
                if (_preview != null) { _preview.Dispose(); _preview = null; }
                if (_image != null) { _image.Dispose(); _image = null; }
            }
            base.Dispose(disposing);
        }
    }
}
