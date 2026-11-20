using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QingJie
{
    public enum DrawingTool { Select, Rectangle, Arrow, Pen, Mosaic, Text }

    /// <summary>
    /// Displays a fit-to-window image while storing all annotations in source pixels.
    /// Both the source and every bitmap returned by ExportImage have independent ownership.
    /// </summary>
    public sealed class AnnotationCanvas : Control
    {
        private Bitmap _source;
        // Only allocate writable pixels after the first mark. Until then the
        // independent source already contains exactly what should be displayed.
        private Bitmap _composite;
        private Bitmap CurrentPixels { get { return _composite ?? _source; } }
        private readonly List<Annotation> _annotations = new List<Annotation>();
        private readonly Stack<Annotation> _redo = new Stack<Annotation>();
        private bool _compactMode;
        private string _captureShortcutText = "F2";
        private DrawingTool _tool;
        private Color _strokeColor = Color.FromArgb(235, 76, 82);
        private float _strokeWidth = 3f;
        private MosaicStyle _mosaicStyle = QingJie.MosaicStyle.Pixelate;
        private int _mosaicSize = 24;
        private RectangleF _imageBounds;
        private bool _dragging;
        private PointF _start;
        private PointF _current;
        private StrokeAnnotation _activeStroke;
        private Bitmap _gestureMosaic;
        private Color _gestureColor;
        private float _gestureWidth;
        private float _uiScale = 1f;
        private Timer _hoverTimer;
        private float _emptyHover;
        private float _emptyHoverTarget;
        private bool _emptyHovered;
        private Bitmap _viewportCache;
        private bool _viewportDirty = true;
        private float _viewportDpiX = 96f;
        private float _viewportDpiY = 96f;
        private Bitmap _penPreview;
        private Graphics _penGraphics;
        private Pen _previewPen;
        private Bitmap _mosaicPreview;
        private Rectangle _gesturePaintBounds;

        public event EventHandler ContentChanged;
        public event EventHandler CaptureRequested;

        public AnnotationCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(240, 244, 252);
            ForeColor = Color.FromArgb(39, 60, 96);
            TabStop = true;
            AccessibleName = "截图编辑画布";
            AccessibleDescription = "拖动画布中的截图以添加标注。切换工具后可添加矩形、箭头、画笔、马赛克和文字。";
        }

        public bool HasImage { get { return _source != null; } }
        public Size ImageSize { get { return _source == null ? Size.Empty : _source.Size; } }
        public bool CanUndo { get { return _annotations.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }
        public string CaptureShortcutText
        {
            get { return _captureShortcutText; }
            set
            {
                _captureShortcutText = String.IsNullOrWhiteSpace(value) ? "F2" : value;
                if (!HasImage) Invalidate();
            }
        }

        /// <summary>Pixel-aligned editing without the surrounding card or padding.</summary>
        public bool CompactMode
        {
            get { return _compactMode; }
            set
            {
                if (_compactMode == value) return;
                CancelGesture();
                _compactMode = value;
                UpdateImageBounds();
                InvalidateViewportCache();
                Invalidate();
            }
        }

        public DrawingTool Tool
        {
            get { return _tool; }
            set
            {
                if (_tool == value) return;
                CancelGesture();
                _tool = value;
                UpdateToolCursor();
                Invalidate();
            }
        }

        public Color StrokeColor
        {
            get { return _strokeColor; }
            set { _strokeColor = Color.FromArgb(255, value.R, value.G, value.B); }
        }

        public float StrokeWidth
        {
            get { return _strokeWidth; }
            set
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value))
                    throw new ArgumentOutOfRangeException("value");
                _strokeWidth = Math.Max(1f, Math.Min(64f, value));
            }
        }

        public MosaicStyle MosaicStyle
        {
            get { return _mosaicStyle; }
            set
            {
                if (!Enum.IsDefined(typeof(MosaicStyle), value))
                    throw new ArgumentOutOfRangeException("value");
                if (_mosaicStyle == value) return;
                CancelGesture();
                _mosaicStyle = value;
            }
        }

        public int MosaicSize
        {
            get { return _mosaicSize; }
            set
            {
                int size = Math.Max(8, Math.Min(96, value));
                if (_mosaicSize == size) return;
                CancelGesture();
                _mosaicSize = size;
            }
        }

        public void SetImage(Bitmap bitmap)
        {
            // Make the new independent copy before releasing the previous image.
            Bitmap next = bitmap == null ? null : CopyBitmap(bitmap);
            CancelGesture();
            DisposeAnnotations();
            if (_source != null) _source.Dispose();
            if (_composite != null) _composite.Dispose();
            _source = next;
            _composite = null;
            InvalidateViewportCache();
            ResetEmptyHover();
            UpdateImageBounds();
            Invalidate();
            RaiseContentChanged();
        }

        public Bitmap ExportImage()
        {
            return CurrentPixels == null ? null : CopyBitmap(CurrentPixels);
        }

        /// <summary>
        /// Re-crops a frozen capture while keeping annotations at their screen
        /// positions. The offset is the old region origin minus the new origin.
        /// Called once when selection adjustment finishes, never on mouse move.
        /// </summary>
        public void ReplaceCaptureImage(Bitmap bitmap, PointF offset)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            if (Single.IsNaN(offset.X) || Single.IsInfinity(offset.X) ||
                Single.IsNaN(offset.Y) || Single.IsInfinity(offset.Y))
                throw new ArgumentOutOfRangeException("offset");
            Bitmap next = CopyBitmap(bitmap);
            CancelGesture();
            for (int i = 0; i < _annotations.Count; i++)
                _annotations[i] = TranslateAnnotation(_annotations[i], offset);
            Annotation[] redo = _redo.ToArray();
            _redo.Clear();
            for (int i = redo.Length - 1; i >= 0; i--)
                _redo.Push(TranslateAnnotation(redo[i], offset));
            if (_source != null) _source.Dispose();
            _source = next;
            RebuildComposite();
            UpdateImageBounds();
            Invalidate();
            RaiseContentChanged();
        }

        private static Annotation TranslateAnnotation(Annotation annotation, PointF offset)
        {
            if (offset == PointF.Empty) return annotation;
            TranslatedAnnotation translated = annotation as TranslatedAnnotation;
            if (translated != null)
            {
                translated.Offset = new PointF(translated.Offset.X + offset.X,
                    translated.Offset.Y + offset.Y);
                return translated;
            }
            return new TranslatedAnnotation(annotation, offset);
        }

        public void Undo()
        {
            CancelGesture();
            if (_annotations.Count == 0) return;
            int last = _annotations.Count - 1;
            _redo.Push(_annotations[last]);
            _annotations.RemoveAt(last);
            RebuildComposite();
            Invalidate();
            RaiseContentChanged();
        }

        public void ClearAnnotations()
        {
            ResetAnnotations();
        }

        public void Redo()
        {
            CancelGesture();
            if (_redo.Count == 0) return;
            Commit(_redo.Pop(), false);
        }

        /// <summary>Restores the original pixels and clears both annotation histories.</summary>
        public void ResetAnnotations()
        {
            CancelGesture();
            if (_annotations.Count == 0 && _redo.Count == 0) return;
            DisposeAnnotations();
            RebuildComposite();
            Invalidate();
            RaiseContentChanged();
        }

        /// <summary>Fits a source image into a padded viewport without enlarging pixels.</summary>
        public static RectangleF CalculateImageBounds(Size imageSize, Size clientSize, int padding)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0 ||
                clientSize.Width <= 0 || clientSize.Height <= 0) return RectangleF.Empty;
            float availableWidth = Math.Max(1, clientSize.Width - Math.Max(0, padding) * 2);
            float availableHeight = Math.Max(1, clientSize.Height - Math.Max(0, padding) * 2);
            float scale = Math.Min(1f, Math.Min(availableWidth / imageSize.Width,
                availableHeight / imageSize.Height));
            float width = imageSize.Width * scale;
            float height = imageSize.Height * scale;
            return new RectangleF((clientSize.Width - width) / 2f,
                (clientSize.Height - height) / 2f, width, height);
        }

        /// <summary>Maps a viewport point to original-image coordinates.</summary>
        public static PointF MapClientToImage(PointF point, RectangleF imageBounds,
            Size imageSize, bool clamp)
        {
            if (imageBounds.Width <= 0 || imageBounds.Height <= 0 ||
                imageSize.Width <= 0 || imageSize.Height <= 0) return PointF.Empty;
            float x = (point.X - imageBounds.X) * imageSize.Width / imageBounds.Width;
            float y = (point.Y - imageBounds.Y) * imageSize.Height / imageBounds.Height;
            if (clamp)
            {
                x = Math.Max(0, Math.Min(imageSize.Width, x));
                y = Math.Max(0, Math.Min(imageSize.Height, y));
            }
            return new PointF(x, y);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            CancelGesture();
            UpdateImageBounds();
            InvalidateViewportCache();
            Invalidate();
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            if (_dragging) CancelGesture();
            InvalidateViewportCache();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Every invalidated pixel comes from the opaque viewport cache.
            // Avoid clearing the entire control before each small pen update.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            float dpiScale = Math.Max(1f, g.DpiX / 96f);
            if (Math.Abs(dpiScale - _uiScale) > 0.01f)
            {
                CancelGesture();
                _uiScale = dpiScale;
                UpdateImageBounds();
                InvalidateViewportCache();
                Invalidate();
            }
            EnsureViewportCache(g.DpiX, g.DpiY);
            if (_viewportCache == null) return;
            Rectangle clip = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            BlitViewport(g, _dragging && _penPreview != null ? _penPreview : _viewportCache, clip);
            if (_source == null)
            {
                GraphicsState emptyState = g.Save();
                try
                {
                    g.SetClip(clip, CombineMode.Intersect);
                    PaintEmptyState(g);
                }
                finally { g.Restore(emptyState); }
                return;
            }
            if (_dragging)
            {
                if (_penPreview != null) PaintLivePenTail(g, clip);
                else PaintLiveShape(g, clip);
            }
        }

        private void InvalidateViewportCache()
        {
            _viewportDirty = true;
        }

        private void EnsureViewportCache(float dpiX, float dpiY)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            if (_viewportCache != null && _viewportCache.Size == ClientSize &&
                !_viewportDirty && Math.Abs(_viewportDpiX - dpiX) < 0.01f &&
                Math.Abs(_viewportDpiY - dpiY) < 0.01f) return;
            if (_viewportCache != null) _viewportCache.Dispose();
            _viewportCache = new Bitmap(ClientSize.Width, ClientSize.Height,
                PixelFormat.Format32bppPArgb);
            _viewportCache.SetResolution(dpiX, dpiY);
            _viewportDpiX = dpiX;
            _viewportDpiY = dpiY;
            using (Graphics background = Graphics.FromImage(_viewportCache))
                PaintViewport(background);
            _viewportDirty = false;
        }

        private static void BlitViewport(Graphics g, Bitmap bitmap, Rectangle clip)
        {
            GraphicsState state = g.Save();
            try
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bitmap, clip, clip.X, clip.Y, clip.Width, clip.Height,
                    GraphicsUnit.Pixel);
            }
            finally { g.Restore(state); }
        }

        // The thin selection frame shares these already-rendered pixels. It must
        // not cover edge annotations with the unannotated desktop underneath.
        internal void PaintViewportRegion(Graphics graphics, Rectangle clip)
        {
            EnsureViewportCache(graphics.DpiX, graphics.DpiY);
            clip.Intersect(ClientRectangle);
            if (_viewportCache != null && clip.Width > 0 && clip.Height > 0)
                BlitViewport(graphics, _viewportCache, clip);
        }

        private void PaintViewport(Graphics g)
        {
            g.Clear(BackColor);
            if (!_compactMode) PaintCanvasCard(g);
            Bitmap pixels = CurrentPixels;
            if (pixels == null) return;
            if (_imageBounds.Width <= 0 || _imageBounds.Height <= 0) return;
            if (_compactMode)
            {
                PaintCheckerboard(g, _imageBounds);
                if (ClientSize == pixels.Size)
                    g.DrawImage(pixels, ClientRectangle, 0, 0, pixels.Width, pixels.Height, GraphicsUnit.Pixel);
                else
                    DrawScaledImage(g, pixels);
                return;
            }
            RectangleF shadow = _imageBounds;
            shadow.Offset(0, 3 * _uiScale);
            shadow.Inflate(3 * _uiScale, 3 * _uiScale);
            using (Brush shadowBrush = new SolidBrush(Color.FromArgb(5, 28, 39, 57)))
                g.FillRectangle(shadowBrush, shadow);
            shadow = _imageBounds;
            shadow.Offset(0, 2 * _uiScale);
            shadow.Inflate(_uiScale, _uiScale);
            using (Brush shadowBrush = new SolidBrush(Color.FromArgb(7, 28, 39, 57)))
                g.FillRectangle(shadowBrush, shadow);
            PaintCheckerboard(g, _imageBounds);
            DrawScaledImage(g, pixels);
            using (Pen border = new Pen(Color.FromArgb(222, 227, 234), _uiScale))
                g.DrawRectangle(border, _imageBounds.X - _uiScale / 2,
                    _imageBounds.Y - _uiScale / 2,
                    _imageBounds.Width + _uiScale, _imageBounds.Height + _uiScale);
        }

        private void DrawScaledImage(Graphics g, Bitmap bitmap)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // TileFlipXY prevents the half-pixel edge from blending with transparent black.
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(bitmap, new PointF[] {
                    new PointF(_imageBounds.Left, _imageBounds.Top),
                    new PointF(_imageBounds.Right, _imageBounds.Top),
                    new PointF(_imageBounds.Left, _imageBounds.Bottom) },
                    new RectangleF(0, 0, bitmap.Width, bitmap.Height),
                    GraphicsUnit.Pixel, attributes);
            }
        }

        private void PaintLiveShape(Graphics g, Rectangle clip)
        {
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(clip, CombineMode.Intersect);
                g.SetClip(_imageBounds, CombineMode.Intersect);
                if (_tool == DrawingTool.Mosaic && _mosaicPreview != null)
                {
                    Rectangle region = GetPixelRegion(_start, _current, _source.Size);
                    if (region.Width > 0 && region.Height > 0)
                    {
                        PointF topLeft = MapImageToClient(new PointF(region.Left, region.Top));
                        PointF bottomRight = MapImageToClient(new PointF(region.Right, region.Bottom));
                        g.SetClip(RectangleF.FromLTRB(topLeft.X, topLeft.Y,
                            bottomRight.X, bottomRight.Y), CombineMode.Intersect);
                        // This image is already scaled to the viewport; no source
                        // resampling or pixelation occurs while the selection moves.
                        g.DrawImage(_mosaicPreview, clip, clip, GraphicsUnit.Pixel);
                    }
                    return;
                }
                g.TranslateTransform(_imageBounds.X, _imageBounds.Y);
                g.ScaleTransform(_imageBounds.Width / _source.Width,
                    _imageBounds.Height / _source.Height);
                ConfigureDrawing(g);
                using (Annotation preview = CreateShape())
                    if (preview != null) preview.Render(g);
            }
            finally { g.Restore(state); }
        }

        private void PaintLivePenTail(Graphics g, Rectangle clip)
        {
            if (_activeStroke == null || _previewPen == null) return;
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(clip, CombineMode.Intersect);
                g.SetClip(_imageBounds, CombineMode.Intersect);
                g.TranslateTransform(_imageBounds.X, _imageBounds.Y);
                g.ScaleTransform(_imageBounds.Width / _source.Width,
                    _imageBounds.Height / _source.Height);
                ConfigureDrawing(g);
                // The moving tail is not baked into the stable preview. A new
                // point replaces it with a smooth segment on the next paint.
                if (_activeStroke.Points.Count == 1)
                    SmoothStroke.Draw(g, _previewPen, _activeStroke.Points);
                else
                    SmoothStroke.DrawTail(g, _previewPen, _activeStroke.Points);
            }
            finally { g.Restore(state); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_source == null)
            {
                if (e.Button == MouseButtons.Left && Enabled &&
                    GetEmptyCaptureBounds().Contains(e.Location))
                {
                    Focus();
                    EventHandler handler = CaptureRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                return;
            }
            if (e.Button != MouseButtons.Left || !Enabled ||
                _tool == DrawingTool.Select || !_imageBounds.Contains(e.Location)) return;
            Focus();
            PointF point = MapClientToImage(e.Location, _imageBounds, _source.Size, true);
            if (_tool == DrawingTool.Text)
            {
                AddTextAt(point);
                return;
            }
            CancelGesture();
            _start = point;
            _current = point;
            _gestureColor = _strokeColor;
            _gestureWidth = _strokeWidth;
            if (_tool == DrawingTool.Pen)
            {
                _activeStroke = new StrokeAnnotation(_gestureColor, _gestureWidth);
                _activeStroke.Points.Add(point);
                BeginPenPreview();
            }
            if (_tool == DrawingTool.Mosaic)
            {
                // Process the cached composite once per gesture, then cheaply reveal
                // the selected region during dragging. Export uses this same bitmap.
                _gestureMosaic = MosaicEffects.Apply(CurrentPixels,
                    _mosaicSize, _mosaicStyle, _gestureColor);
                _mosaicPreview = new Bitmap(Math.Max(1, ClientSize.Width),
                    Math.Max(1, ClientSize.Height), PixelFormat.Format32bppPArgb);
                _mosaicPreview.SetResolution(_uiScale * 96f, _uiScale * 96f);
                using (Graphics preview = Graphics.FromImage(_mosaicPreview))
                    DrawScaledImage(preview, _gestureMosaic);
            }
            _dragging = true;
            _gesturePaintBounds = GetShapePaintBounds();
            Capture = true;
            Invalidate(_gesturePaintBounds);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_source == null)
            {
                SetEmptyHover(Enabled && GetEmptyCaptureBounds().Contains(e.Location));
                return;
            }
            if (!_dragging) return;
            Rectangle oldBounds = _gesturePaintBounds;
            _current = MapClientToImage(e.Location, _imageBounds, _source.Size, true);
            if (_activeStroke != null)
            {
                PointF last = _activeStroke.Points[_activeStroke.Points.Count - 1];
                float dx = last.X - _current.X;
                float dy = last.Y - _current.Y;
                if (dx * dx + dy * dy >= 0.25f)
                {
                    _activeStroke.Points.Add(_current);
                    DrawPenSegment();
                    // The last three points contain the old temporary tail,
                    // the new stable quadratic segment and the new tail.
                    RectangleF recent = SmoothStroke.GetRecentBounds(_activeStroke.Points);
                    Rectangle segment = GetSegmentPaintBounds(recent.Location,
                        new PointF(recent.Right, recent.Bottom), _gestureWidth);
                    _gesturePaintBounds = UnionDirtyBounds(_gesturePaintBounds, segment);
                    Invalidate(segment);
                }
                return;
            }
            _gesturePaintBounds = GetShapePaintBounds();
            Invalidate(UnionDirtyBounds(oldBounds, _gesturePaintBounds));
        }

        private PointF MapImageToClient(PointF point)
        {
            return new PointF(_imageBounds.Left + point.X * _imageBounds.Width / _source.Width,
                _imageBounds.Top + point.Y * _imageBounds.Height / _source.Height);
        }

        private Rectangle GetSegmentPaintBounds(PointF start, PointF end, float width)
        {
            PointF first = MapImageToClient(start);
            PointF last = MapImageToClient(end);
            float padding = Math.Max(2f, width * _imageBounds.Width / _source.Width / 2f + 2f);
            Rectangle bounds = Rectangle.FromLTRB(
                (int)Math.Floor(Math.Min(first.X, last.X) - padding),
                (int)Math.Floor(Math.Min(first.Y, last.Y) - padding),
                (int)Math.Ceiling(Math.Max(first.X, last.X) + padding),
                (int)Math.Ceiling(Math.Max(first.Y, last.Y) + padding));
            return Rectangle.Intersect(ClientRectangle, bounds);
        }

        private Rectangle GetShapePaintBounds()
        {
            float extraWidth = _tool == DrawingTool.Arrow
                ? Math.Max(24, _gestureWidth * 10) : _gestureWidth;
            return GetSegmentPaintBounds(_start, _current, extraWidth);
        }

        private static Rectangle UnionDirtyBounds(Rectangle first, Rectangle second)
        {
            if (first.Width <= 0 || first.Height <= 0) return second;
            if (second.Width <= 0 || second.Height <= 0) return first;
            return Rectangle.Union(first, second);
        }

        private void BeginPenPreview()
        {
            EnsureViewportCache(_uiScale * 96f, _uiScale * 96f);
            if (_viewportCache == null) return;
            _penPreview = _viewportCache.Clone(new Rectangle(Point.Empty, _viewportCache.Size),
                PixelFormat.Format32bppPArgb);
            _penGraphics = Graphics.FromImage(_penPreview);
            ConfigureDrawing(_penGraphics);
            _penGraphics.TranslateTransform(_imageBounds.X, _imageBounds.Y);
            _penGraphics.ScaleTransform(_imageBounds.Width / _source.Width,
                _imageBounds.Height / _source.Height);
            _penGraphics.SetClip(new Rectangle(Point.Empty, _source.Size));
            _previewPen = new Pen(_gestureColor, _gestureWidth);
            _previewPen.StartCap = LineCap.Round;
            _previewPen.EndCap = LineCap.Round;
            _previewPen.LineJoin = LineJoin.Round;
            // A single click is painted as a temporary round dot. The first
            // stable curve will supply its cap when a second point arrives.
        }

        private void DrawPenSegment()
        {
            if (_penGraphics == null || _activeStroke == null) return;
            // All earlier segments are already in the viewport bitmap. The point
            // vector is retained separately for one full-quality render on commit.
            SmoothStroke.DrawLatestSegment(_penGraphics, _previewPen, _activeStroke.Points);
            _penGraphics.Flush();
        }

        private void DisposeGesturePreview()
        {
            if (_penGraphics != null) _penGraphics.Dispose();
            _penGraphics = null;
            if (_previewPen != null) _previewPen.Dispose();
            _previewPen = null;
            if (_penPreview != null) _penPreview.Dispose();
            _penPreview = null;
            if (_mosaicPreview != null) _mosaicPreview.Dispose();
            _mosaicPreview = null;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_source == null) SetEmptyHover(false);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
            {
                CancelGesture();
                ResetEmptyHover();
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
            {
                CancelGesture();
                ResetEmptyHover();
            }
        }

        private RectangleF GetEmptyTileBounds(float scale)
        {
            float sizeScale = Math.Min(1f, Math.Min(
                Math.Max(1, ClientSize.Width) / (220f * scale),
                Math.Max(1, ClientSize.Height) / (260f * scale)));
            float tileWidth = 76 * scale * sizeScale;
            float tileHeight = 72 * scale * sizeScale;
            float top = ClientSize.Height / 2f - 91f * scale;
            return new RectangleF((ClientSize.Width - tileWidth) / 2f,
                Math.Max(18 * scale, top - 17 * scale), tileWidth, tileHeight);
        }

        private RectangleF GetEmptyCaptureBounds()
        {
            RectangleF bounds = GetEmptyTileBounds(_uiScale);
            bounds.Inflate(7 * _uiScale, 7 * _uiScale);
            return bounds;
        }

        private void UpdateToolCursor()
        {
            Cursor = _source == null
                ? (_emptyHovered && Enabled ? Cursors.Hand : Cursors.Default)
                : (_tool == DrawingTool.Select ? Cursors.Default :
                    (_tool == DrawingTool.Text ? Cursors.IBeam : Cursors.Cross));
        }

        private void SetEmptyHover(bool hovered)
        {
            if (_emptyHovered == hovered) return;
            _emptyHovered = hovered;
            _emptyHoverTarget = hovered ? 1f : 0f;
            UpdateToolCursor();
            if (_hoverTimer == null)
            {
                _hoverTimer = new Timer();
                _hoverTimer.Interval = 16;
                _hoverTimer.Tick += AdvanceEmptyHover;
            }
            _hoverTimer.Start();
        }

        private void AdvanceEmptyHover(object sender, EventArgs e)
        {
            if (IsDisposed || _source != null || !Visible)
            {
                ResetEmptyHover();
                return;
            }
            _emptyHover += (_emptyHoverTarget - _emptyHover) * 0.22f;
            if (Math.Abs(_emptyHoverTarget - _emptyHover) < 0.004f)
            {
                _emptyHover = _emptyHoverTarget;
                if (_hoverTimer != null) _hoverTimer.Stop();
            }
            RectangleF dirty = GetEmptyTileBounds(_uiScale);
            dirty.Inflate(18 * _uiScale, 18 * _uiScale);
            Invalidate(Rectangle.Ceiling(dirty));
        }

        private void ResetEmptyHover()
        {
            _emptyHovered = false;
            _emptyHover = 0;
            _emptyHoverTarget = 0;
            if (_hoverTimer != null) _hoverTimer.Stop();
            UpdateToolCursor();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_dragging || _source == null) return;
            _current = MapClientToImage(e.Location, _imageBounds, _source.Size, true);
            Annotation completed = null;
            if (_activeStroke != null)
            {
                if (_activeStroke.Points[_activeStroke.Points.Count - 1] != _current)
                    _activeStroke.Points.Add(_current);
                completed = _activeStroke;
                _activeStroke = null;
            }
            else if (_tool == DrawingTool.Mosaic && _gestureMosaic != null)
            {
                Rectangle region = GetPixelRegion(_start, _current, _source.Size);
                if (region.Width > 0 && region.Height > 0)
                    completed = new MosaicAnnotation(region,
                        _gestureMosaic.Clone(region, PixelFormat.Format32bppArgb));
            }
            else
            {
                float dx = _current.X - _start.X;
                float dy = _current.Y - _start.Y;
                if (dx * dx + dy * dy >= 1f) completed = CreateShape();
            }
            _dragging = false;
            DisposeGesturePreview();
            _gesturePaintBounds = Rectangle.Empty;
            if (_gestureMosaic != null)
            {
                _gestureMosaic.Dispose();
                _gestureMosaic = null;
            }
            Capture = false;
            if (completed != null) Commit(completed);
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragging && !Capture) CancelGesture();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _dragging)
            {
                CancelGesture();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Z))
            {
                Undo();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Y) ||
                keyData == (Keys.Control | Keys.Shift | Keys.Z))
            {
                Redo();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void AddTextAt(PointF point)
        {
            using (TextInputDialog dialog = new TextInputDialog(
                Math.Max(18, Math.Min(96, (int)(_strokeWidth * 7)))))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK ||
                    String.IsNullOrWhiteSpace(dialog.AnnotationText)) return;
                Commit(new TextAnnotation(point, dialog.AnnotationText,
                    _strokeColor, dialog.TextPixelSize));
            }
        }

        private Annotation CreateShape()
        {
            if (_tool == DrawingTool.Rectangle)
                return new RectangleAnnotation(_start, _current, _gestureColor, _gestureWidth);
            if (_tool == DrawingTool.Arrow)
                return new ArrowAnnotation(_start, _current, _gestureColor, _gestureWidth);
            return null;
        }

        private void Commit(Annotation annotation)
        {
            Commit(annotation, true);
        }

        private void Commit(Annotation annotation, bool clearRedo)
        {
            if (_composite == null) _composite = CopyBitmap(_source);
            using (Graphics g = Graphics.FromImage(_composite))
            {
                ConfigureDrawing(g);
                annotation.Render(g);
            }
            _annotations.Add(annotation);
            if (clearRedo) DisposeRedo();
            if (_viewportCache != null && !_viewportDirty &&
                _viewportCache.Size == ClientSize && _imageBounds.Width > 0 &&
                _imageBounds.Height > 0)
            {
                // Append the same vector to the already scaled background. This
                // avoids a redundant full-image resample when the mouse is released.
                // The original-resolution composite above remains authoritative for
                // export, undo, and any later viewport rebuild.
                _viewportDirty = true;
                using (Graphics viewport = Graphics.FromImage(_viewportCache))
                {
                    ConfigureDrawing(viewport);
                    viewport.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    viewport.TranslateTransform(_imageBounds.X, _imageBounds.Y);
                    viewport.ScaleTransform(_imageBounds.Width / _source.Width,
                        _imageBounds.Height / _source.Height);
                    viewport.SetClip(new Rectangle(Point.Empty, _source.Size));
                    annotation.Render(viewport);
                }
                _viewportDirty = false;
            }
            else InvalidateViewportCache();
            Invalidate();
            RaiseContentChanged();
        }

        private void RebuildComposite()
        {
            Bitmap next = _annotations.Count == 0 ? null : CopyBitmap(_source);
            if (_composite != null) _composite.Dispose();
            _composite = next;
            InvalidateViewportCache();
            if (_composite == null) return;
            using (Graphics g = Graphics.FromImage(_composite))
            {
                ConfigureDrawing(g);
                foreach (Annotation annotation in _annotations) annotation.Render(g);
            }
        }

        private void CancelGesture()
        {
            Rectangle dirty = _gesturePaintBounds;
            bool hadPreview = _dragging || _penPreview != null || _mosaicPreview != null;
            _dragging = false;
            DisposeGesturePreview();
            _gesturePaintBounds = Rectangle.Empty;
            if (_activeStroke != null) _activeStroke.Dispose();
            _activeStroke = null;
            if (_gestureMosaic != null) _gestureMosaic.Dispose();
            _gestureMosaic = null;
            if (Capture) Capture = false;
            if (hadPreview && dirty.Width > 0 && dirty.Height > 0) Invalidate(dirty);
        }

        private void DisposeAnnotations()
        {
            foreach (Annotation annotation in _annotations) annotation.Dispose();
            _annotations.Clear();
            DisposeRedo();
        }

        private void DisposeRedo()
        {
            while (_redo.Count > 0) _redo.Pop().Dispose();
        }

        private void UpdateImageBounds()
        {
            _imageBounds = CalculateImageBounds(ImageSize, ClientSize,
                _compactMode ? 0 : (int)Math.Round(32 * _uiScale));
        }

        private void RaiseContentChanged()
        {
            EventHandler handler = ContentChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static void ConfigureDrawing(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        private static Bitmap CopyBitmap(Bitmap source)
        {
            // A native clone also preserves RGB values at very low alpha; redrawing
            // such pixels through GDI+ can introduce premultiplication rounding.
            if (source.PixelFormat == PixelFormat.Format32bppArgb)
                return source.Clone(new Rectangle(Point.Empty, source.Size),
                    PixelFormat.Format32bppArgb);
            Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            if (source.HorizontalResolution > 0 && source.VerticalResolution > 0)
                copy.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(source, 0, 0);
            }
            return copy;
        }

        private static Rectangle GetPixelRegion(PointF start, PointF end, Size size)
        {
            int left = Math.Max(0, (int)Math.Floor(Math.Min(start.X, end.X)));
            int top = Math.Max(0, (int)Math.Floor(Math.Min(start.Y, end.Y)));
            int right = Math.Min(size.Width, (int)Math.Ceiling(Math.Max(start.X, end.X)));
            int bottom = Math.Min(size.Height, (int)Math.Ceiling(Math.Max(start.Y, end.Y)));
            return Rectangle.FromLTRB(left, top, Math.Max(left, right), Math.Max(top, bottom));
        }

        private static Bitmap Pixelate(Bitmap source, int blockSize)
        {
            // Retain the previous helper for compatibility with existing probes.
            return MosaicEffects.Apply(source, blockSize, QingJie.MosaicStyle.Pixelate, Color.Black);
        }

        private static void PaintCheckerboard(Graphics g, RectangleF bounds)
        {
            int cellSize = Math.Max(8, (int)Math.Round(8 * g.DpiX / 96f));
            using (Bitmap tile = new Bitmap(cellSize * 2, cellSize * 2))
            {
                using (Graphics tileGraphics = Graphics.FromImage(tile))
                {
                    tileGraphics.Clear(Color.White);
                    using (Brush gray = new SolidBrush(Color.FromArgb(242, 244, 247)))
                    {
                        tileGraphics.FillRectangle(gray, 0, 0, cellSize, cellSize);
                        tileGraphics.FillRectangle(gray, cellSize, cellSize, cellSize, cellSize);
                    }
                }
                using (TextureBrush checker = new TextureBrush(tile, WrapMode.Tile))
                    g.FillRectangle(checker, bounds);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0) return path;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void PaintCanvasCard(Graphics g)
        {
            if (ClientSize.Width < 4 || ClientSize.Height < 4) return;
            float scale = Math.Max(1f, g.DpiX / 96f);
            RectangleF card = new RectangleF(5 * scale, 3 * scale,
                Math.Max(1, ClientSize.Width - 10 * scale),
                Math.Max(1, ClientSize.Height - 11 * scale));
            GraphicsState state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int layer = 4; layer >= 1; layer--)
                {
                    RectangleF shadow = card;
                    shadow.Offset(0, (1.5f + layer * 0.35f) * scale);
                    shadow.Inflate(layer * 1.3f * scale, layer * 0.8f * scale);
                    using (GraphicsPath shadowPath = RoundedRectangle(shadow,
                        (20 + layer) * scale))
                    using (Brush shade = new SolidBrush(Color.FromArgb(
                        7 - layer, 74, 104, 151)))
                        g.FillPath(shade, shadowPath);
                }
                using (GraphicsPath outline = RoundedRectangle(card, 20 * scale))
                using (Brush paper = new LinearGradientBrush(card, Color.White,
                    Color.FromArgb(252, 254, 255), LinearGradientMode.Vertical))
                using (Pen border = new Pen(Color.FromArgb(221, 230, 245), scale))
                {
                    g.FillPath(paper, outline);
                    if (_source == null)
                    {
                        g.SetClip(outline, CombineMode.Intersect);
                        // A quiet corner motif ties the otherwise white card to the
                        // surrounding blue surface without distracting from editing.
                        using (Pen arc = new Pen(Color.FromArgb(244, 247, 253), scale))
                        {
                            float diameter = 188 * scale;
                            g.DrawEllipse(arc, card.Right - 128 * scale,
                                card.Top - 122 * scale, diameter, diameter);
                            g.DrawEllipse(arc, card.Right - 106 * scale,
                                card.Top - 100 * scale, diameter - 44 * scale,
                                diameter - 44 * scale);
                        }
                        using (Brush dot = new SolidBrush(Color.FromArgb(235, 241, 251)))
                        {
                            for (int column = 0; column < 4; column++)
                                for (int row = 0; row < 3; row++)
                                    g.FillEllipse(dot, card.Left + (29 + column * 10) * scale,
                                        card.Bottom - (32 + row * 10) * scale,
                                        2 * scale, 2 * scale);
                        }
                        g.ResetClip();
                    }
                    g.DrawPath(border, outline);
                    using (Pen highlight = new Pen(Color.FromArgb(220, 255, 255, 255), scale))
                        g.DrawLine(highlight, card.Left + 20 * scale,
                            card.Top + 1.3f * scale, card.Right - 20 * scale,
                            card.Top + 1.3f * scale);
                }
            }
            finally { g.Restore(state); }
        }

        private void PaintEmptyState(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float scale = Math.Max(1f, g.DpiX / 96f);
            float centerX = ClientSize.Width / 2f;
            float top = ClientSize.Height / 2f - 91f * scale;
            RectangleF tileBounds = GetEmptyTileBounds(scale);
            tileBounds.Offset(0, -3f * scale * _emptyHover);
            float glyphScale = tileBounds.Width / 76f;
            float glyphTop = tileBounds.Top + 17 * glyphScale;
            float left = centerX - 23f * glyphScale;
            float right = centerX + 23f * glyphScale;
            float bottom = glyphTop + 38f * glyphScale;
            float corner = 11f * glyphScale;
            for (int layer = 3; layer >= 1; layer--)
            {
                RectangleF shadow = tileBounds;
                shadow.Offset(0, (2 + _emptyHover * 3) * scale);
                shadow.Inflate(layer * (1 + _emptyHover) * scale,
                    layer * (1 + _emptyHover) * scale);
                using (GraphicsPath shadePath = RoundedRectangle(shadow, (19 + layer) * scale))
                using (Brush shade = new SolidBrush(Color.FromArgb(
                    (int)(3 + _emptyHover * 5), 75, 121, 195)))
                    g.FillPath(shade, shadePath);
            }
            using (GraphicsPath tile = RoundedRectangle(tileBounds, 19 * glyphScale))
            using (Brush tileFill = new LinearGradientBrush(tileBounds,
                BlendColor(Color.FromArgb(246, 249, 255), Color.FromArgb(238, 246, 255), _emptyHover),
                BlendColor(Color.FromArgb(236, 242, 255), Color.FromArgb(221, 234, 255), _emptyHover),
                LinearGradientMode.Vertical))
            using (Pen tileBorder = new Pen(BlendColor(Color.FromArgb(218, 230, 249),
                Color.FromArgb(179, 204, 242), _emptyHover), scale))
            {
                g.FillPath(tileFill, tile);
                g.DrawPath(tileBorder, tile);
                using (Pen highlight = new Pen(Color.FromArgb(220, 255, 255, 255), scale))
                    g.DrawLine(highlight, tileBounds.Left + 19 * glyphScale,
                        tileBounds.Top + 1.3f * scale, tileBounds.Right - 19 * glyphScale,
                        tileBounds.Top + 1.3f * scale);
            }
            using (Pen frame = new Pen(BlendColor(Color.FromArgb(109, 143, 197),
                Color.FromArgb(67, 113, 184), _emptyHover), 1.7f * glyphScale))
            {
                frame.StartCap = LineCap.Round;
                frame.EndCap = LineCap.Round;
                frame.LineJoin = LineJoin.Round;
                g.DrawLines(frame, new PointF[] {
                    new PointF(left, glyphTop + corner), new PointF(left, glyphTop),
                    new PointF(left + corner, glyphTop) });
                g.DrawLines(frame, new PointF[] {
                    new PointF(right - corner, glyphTop), new PointF(right, glyphTop),
                    new PointF(right, glyphTop + corner) });
                g.DrawLines(frame, new PointF[] {
                    new PointF(left, bottom - corner), new PointF(left, bottom),
                    new PointF(left + corner, bottom) });
                g.DrawLines(frame, new PointF[] {
                    new PointF(right - corner, bottom), new PointF(right, bottom),
                    new PointF(right, bottom - corner) });
                float middle = glyphTop + 19f * glyphScale;
                g.DrawLine(frame, centerX - 5f * glyphScale, middle,
                    centerX + 5f * glyphScale, middle);
                g.DrawLine(frame, centerX, middle - 5f * glyphScale,
                    centerX, middle + 5f * glyphScale);
            }
            using (Font title = new Font(UiTheme.FamilyName, 16f, FontStyle.Regular))
            using (Font subtitle = new Font(UiTheme.FamilyName, 9.5f))
            using (Font detail = new Font(UiTheme.FamilyName, 9f))
            using (Brush primary = new SolidBrush(Color.FromArgb(39, 60, 96)))
            using (Brush secondary = new SolidBrush(Color.FromArgb(106, 125, 152)))
            using (Brush muted = new SolidBrush(Color.FromArgb(131, 148, 172)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                float textLeft = 16f * scale;
                float textWidth = Math.Max(1, ClientSize.Width - textLeft * 2);
                g.DrawString("开始你的第一张截图", title, primary,
                    new RectangleF(textLeft, top + 65f * scale, textWidth, 38f * scale), format);
                g.DrawString("点击图标开始，或按 " + _captureShortcutText,
                    subtitle, secondary,
                    new RectangleF(textLeft, top + 111f * scale, textWidth, 27f * scale), format);
                g.DrawString("也可以打开图片 / Ctrl+V 粘贴", detail, muted,
                    new RectangleF(textLeft, top + 145f * scale, textWidth, 25f * scale), format);
            }
        }

        private static Color BlendColor(Color from, Color to, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hoverTimer != null)
                {
                    _hoverTimer.Stop();
                    _hoverTimer.Dispose();
                    _hoverTimer = null;
                }
                CancelGesture();
                DisposeAnnotations();
                if (_viewportCache != null) _viewportCache.Dispose();
                _viewportCache = null;
                if (_source != null) _source.Dispose();
                if (_composite != null) _composite.Dispose();
                _source = null;
                _composite = null;
            }
            base.Dispose(disposing);
        }

        private abstract class Annotation : IDisposable
        {
            public abstract void Render(Graphics g);
            public virtual void Dispose() { }

            protected static Pen CreatePen(Color color, float width)
            {
                Pen pen = new Pen(color, width);
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                return pen;
            }
        }

        private sealed class TranslatedAnnotation : Annotation
        {
            private readonly Annotation _annotation;
            public PointF Offset;
            public TranslatedAnnotation(Annotation annotation, PointF offset)
            { _annotation = annotation; Offset = offset; }
            public override void Render(Graphics graphics)
            {
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(Offset.X, Offset.Y);
                    _annotation.Render(graphics);
                }
                finally { graphics.Restore(state); }
            }
            public override void Dispose() { _annotation.Dispose(); }
        }

        private sealed class RectangleAnnotation : Annotation
        {
            private readonly RectangleF _rectangle;
            private readonly Color _color;
            private readonly float _width;

            public RectangleAnnotation(PointF start, PointF end, Color color, float width)
            {
                _rectangle = RectangleF.FromLTRB(Math.Min(start.X, end.X),
                    Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
                _color = color;
                _width = width;
            }

            public override void Render(Graphics g)
            {
                if (_rectangle.Width <= 0 || _rectangle.Height <= 0) return;
                using (Pen pen = CreatePen(_color, _width))
                    g.DrawRectangle(pen, _rectangle.X, _rectangle.Y,
                        _rectangle.Width, _rectangle.Height);
            }
        }

        private sealed class ArrowAnnotation : Annotation
        {
            private readonly PointF _start;
            private readonly PointF _end;
            private readonly Color _color;
            private readonly float _width;

            public ArrowAnnotation(PointF start, PointF end, Color color, float width)
            {
                _start = start;
                _end = end;
                _color = color;
                _width = width;
            }

            public override void Render(Graphics g)
            {
                float dx = _end.X - _start.X;
                float dy = _end.Y - _start.Y;
                float length = (float)Math.Sqrt(dx * dx + dy * dy);
                if (length < 0.5f) return;
                float headLength = Math.Min(length * 0.65f, Math.Max(12f, _width * 4.5f));
                float halfHeadWidth = headLength * 0.46f;
                float ux = dx / length;
                float uy = dy / length;
                PointF basePoint = new PointF(_end.X - ux * headLength,
                    _end.Y - uy * headLength);
                using (Pen pen = CreatePen(_color, _width))
                    g.DrawLine(pen, _start,
                        new PointF(_end.X - ux * headLength * 0.35f,
                            _end.Y - uy * headLength * 0.35f));
                using (Brush brush = new SolidBrush(_color))
                    g.FillPolygon(brush, new PointF[] { _end,
                        new PointF(basePoint.X - uy * halfHeadWidth,
                            basePoint.Y + ux * halfHeadWidth),
                        new PointF(basePoint.X + uy * halfHeadWidth,
                            basePoint.Y - ux * halfHeadWidth) });
            }
        }

        private sealed class StrokeAnnotation : Annotation
        {
            public readonly List<PointF> Points = new List<PointF>();
            private readonly Color _color;
            private readonly float _width;

            public StrokeAnnotation(Color color, float width)
            {
                _color = color;
                _width = width;
            }

            public override void Render(Graphics g)
            {
                using (Pen pen = CreatePen(_color, _width))
                    SmoothStroke.Draw(g, pen, Points);
            }
        }

        private sealed class MosaicAnnotation : Annotation
        {
            private readonly Rectangle _region;
            private readonly Bitmap _patch;

            public MosaicAnnotation(Rectangle region, Bitmap patch)
            {
                _region = region;
                _patch = patch;
            }

            public override void Render(Graphics g)
            {
                g.DrawImageUnscaled(_patch, _region.Location);
            }

            public override void Dispose() { _patch.Dispose(); }
        }

        private sealed class TextAnnotation : Annotation
        {
            private readonly PointF _point;
            private readonly string _text;
            private readonly Color _color;
            private readonly Font _font;

            public TextAnnotation(PointF point, string text, Color color, float pixelSize)
            {
                _point = point;
                _text = text;
                _color = color;
                _font = new Font(UiTheme.FamilyName, pixelSize, FontStyle.Bold,
                    GraphicsUnit.Pixel);
            }

            public override void Render(Graphics g)
            {
                using (Brush brush = new SolidBrush(_color))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    g.DrawString(_text, _font, brush, _point, format);
                }
            }

            public override void Dispose() { _font.Dispose(); }
        }

        private sealed class TextInputDialog : Form
        {
            private readonly TextBox _input;
            private readonly NumericUpDown _size;
            public string AnnotationText { get { return _input.Text; } }
            public float TextPixelSize { get { return (float)_size.Value; } }

            public TextInputDialog(int initialSize)
            {
                SuspendLayout();
                Text = "添加文字";
                Font = new Font(UiTheme.FamilyName, 10f);
                AutoScaleDimensions = new SizeF(96F, 96F);
                AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size(420, 246);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Color.FromArgb(248, 250, 253);
                Label label = new Label();
                label.Text = "输入标注内容";
                label.SetBounds(20, 17, 220, 25);
                _input = new TextBox();
                _input.Multiline = true;
                _input.AcceptsReturn = true;
                _input.ScrollBars = ScrollBars.Vertical;
                _input.MaxLength = 2000;
                _input.SetBounds(20, 47, 380, 108);
                _input.TabIndex = 0;
                Label sizeLabel = new Label();
                sizeLabel.Text = "字号（像素）";
                sizeLabel.SetBounds(20, 170, 110, 26);
                _size = new NumericUpDown();
                _size.Minimum = 10;
                _size.Maximum = 160;
                _size.Value = initialSize;
                _size.SetBounds(132, 166, 74, 28);
                _size.TabIndex = 1;
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(222, 205, 84, 30);
                cancel.TabIndex = 3;
                Button ok = new Button();
                ok.Text = "添加";
                ok.DialogResult = DialogResult.OK;
                ok.BackColor = Color.FromArgb(68, 111, 218);
                ok.ForeColor = Color.White;
                ok.FlatStyle = FlatStyle.Flat;
                ok.FlatAppearance.BorderSize = 0;
                ok.SetBounds(316, 205, 84, 30);
                ok.TabIndex = 2;
                Controls.AddRange(new Control[] { label, _input, sizeLabel, _size, cancel, ok });
                AcceptButton = ok;
                CancelButton = cancel;
                Shown += delegate { _input.Focus(); };
                ResumeLayout(true);
            }
        }
    }
}
