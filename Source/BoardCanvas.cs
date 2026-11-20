using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace QingJie
{
    /// <summary>
    /// An editable composition of independently owned images and board-space annotations.
    /// Images are kept at source resolution; only viewport previews are cached.
    /// </summary>
    public sealed class BoardCanvas : Control
    {
        private readonly List<BoardImage> _images = new List<BoardImage>();
        private readonly List<BoardAnnotation> _annotations = new List<BoardAnnotation>();
        private readonly List<BoardChange> _undo = new List<BoardChange>();
        private long _nextImageId;
        private long _lastAddedImageId;
        // World coordinates have no paper boundary. This is the client-space
        // location of world (0,0), independent of the content's bounding box.
        private PointF _viewOrigin;
        private float _scale = .5f;
        private float _uiScale = 1f;
        private bool _fitToWindow = true;
        private bool _panMode;
        private bool _spacePressed;
        private bool _viewInteracting;
        private readonly Timer _viewRefineTimer;
        private Point _panStart;
        private PointF _panOrigin;
        private bool _panWasFit;
        private MouseButtons _panButton;
        private DrawingTool _tool;
        private Color _strokeColor = Color.FromArgb(235, 76, 82);
        private float _strokeWidth = 4f;
        private BoardImage _selected;
        private Bitmap _viewportCache;
        private Bitmap _viewScratch;
        private bool _viewportInteractive;
        private bool _navigationPending;
        private PointF _cachedViewOrigin;
        private float _cachedViewScale;
        private bool _forcePreviewRendering;
        private long _viewGeneration;
        private bool _refineRequested;
        private RefinementWork _refineWork;
        private int _lastViewInputTick;
        private Bitmap _annotationCache;
        private bool _viewportDirty = true;
        private bool _annotationsDirty = true;
        private Gesture _gesture;
        private PointF _start;
        private PointF _current;
        private RectangleF _originalBounds;
        private Bitmap _dragBackground;
        private Bitmap _dragForeground;
        private Bitmap _dragPreview;
        private Bitmap _penPreview;
        private Graphics _penGraphics;
        private Pen _previewPen;
        private List<PointF> _penPoints;
        private DrawingTool _gestureTool;
        private Color _gestureColor;
        private float _gestureWidth;
        private bool _imageDropEnabled = true;
        private bool _dropPreviewVisible;
        private RectangleF _dropBounds;
        private IDataObject _dropData;
        private Bitmap _dropBitmap;
        private string[] _dropFiles;
        private Size _dropImageSize;
        private readonly Font _dropFont = new Font("Microsoft YaHei UI", 10f);
        private bool _disposed;

        private const int HistoryLimit = 100;
        private const float ExportPadding = 32f;
        private const int MaximumExportSide = 32767;
        private const long MaximumExportPixels = 80000000;

        private enum Gesture { None, Move, Resize, Annotate, Pan }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        public event EventHandler ContentChanged;
        public event EventHandler SelectionChanged;
        /// <summary>Camera or pan-tool changes, independent of content and undo history.</summary>
        public event EventHandler ViewChanged;
        /// <summary>Raised only after a bitmap drag/drop has successfully added an image.</summary>
        public event EventHandler ImageDropped;

        /// <summary>The host supplies its text-input dialog; null/empty cancels.</summary>
        public Func<string> RequestText { get; set; }

        /// <summary>
        /// The host imports image files dropped on the board and handles validation
        /// and error reporting. File drops do not identify the current screenshot.
        /// </summary>
        public Action<string[]> RequestFileDrop { get; set; }

        public BoardCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(240, 244, 252);
            TabStop = true;
            AllowDrop = true;
            AccessibleName = "多图画板";
            AccessibleDescription = "无边界画布；拖动空白处平移，滚轮缩放；直接拖动图片，拖动右下角调整大小。";
            _viewRefineTimer = new Timer();
            _viewRefineTimer.Interval = 70;
            _viewRefineTimer.Tick += OnRefineTimerTick;
            UpdateBoardBounds();
        }

        public int ImageCount { get { return _images.Count; } }
        /// <summary>Identity of the most recently added image; zero before the first add.</summary>
        public long LastAddedImageId { get { return _lastAddedImageId; } }
        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool HasSelection { get { return _selected != null; } }
        public bool HasContent { get { return _images.Count > 0 || _annotations.Count > 0; } }
        /// <summary>Compatibility alias for the content export size, not a world limit.</summary>
        public Size BoardSize { get { return ExportSize; } }
        public RectangleF ViewportBoardBounds
        { get { return HasContent ? ToClient(ContentBounds) : ClientRectangle; } }
        public RectangleF ContentBounds
        {
            get
            {
                RectangleF result = RectangleF.Empty;
                bool found = false;
                foreach (BoardImage image in _images)
                    UnionBounds(ref result, ref found, image.Bounds);
                foreach (BoardAnnotation annotation in _annotations)
                    UnionBounds(ref result, ref found, annotation.Bounds);
                return result;
            }
        }
        /// <summary>World-space export crop, rounded outwards with 32 px padding.</summary>
        public RectangleF ExportBounds
        {
            get
            {
                if (!HasContent) return RectangleF.Empty;
                RectangleF content = ContentBounds;
                return RectangleF.FromLTRB((float)Math.Floor(content.Left - ExportPadding),
                    (float)Math.Floor(content.Top - ExportPadding),
                    (float)Math.Ceiling(content.Right + ExportPadding),
                    (float)Math.Ceiling(content.Bottom + ExportPadding));
            }
        }
        public Size ExportSize
        {
            get
            {
                RectangleF bounds = ExportBounds;
                return new Size(SizeComponent(bounds.Width), SizeComponent(bounds.Height));
            }
        }
        public bool DropPreviewVisible { get { return _dropPreviewVisible; } }
        public float ZoomPercent { get { return _scale * 100f; } }
        public bool IsFitToWindow { get { return _fitToWindow; } }
        internal bool IsRefinementPending
        { get { return _refineRequested || _refineWork != null; } }

        /// <summary>A persistent hand tool; the current annotation tool is retained.</summary>
        public bool PanMode
        {
            get { return _panMode; }
            set
            {
                if (_panMode == value) return;
                CancelGesture();
                _panMode = value;
                UpdateCursor(PointToClient(Cursor.Position));
                RaiseViewChanged();
            }
        }

        /// <summary>
        /// Set an absolute display percentage, keeping the board point under the
        /// client-space anchor fixed. Manual zoom is 10–400%; Fit may be smaller.
        /// Camera changes never create content changes or undo entries.
        /// </summary>
        public void ZoomAt(Point clientAnchor, float zoomPercent)
        {
            if (Single.IsNaN(zoomPercent) || Single.IsInfinity(zoomPercent))
                throw new ArgumentOutOfRangeException("zoomPercent");
            CancelGesture();
            float nextScale = Math.Max(.1f, Math.Min(4f, zoomPercent / 100f));
            PointF anchor = ClientToBoard(clientAnchor, false);
            bool changed = Math.Abs(nextScale - _scale) > .000001f || _fitToWindow;
            if (!changed) return;
            _fitToWindow = false;
            _scale = nextScale;
            _viewOrigin = new PointF(clientAnchor.X - anchor.X * _scale,
                clientAnchor.Y - anchor.Y * _scale);
            BeginViewInteraction();
            RaiseViewChanged();
        }

        public void ZoomIn() { ZoomAt(ViewCenter(), ZoomPercent * 1.2f); }
        public void ZoomOut()
        {
            // Fit can be below the manual minimum; minus must never zoom in.
            if (ZoomPercent > 10f) ZoomAt(ViewCenter(), ZoomPercent / 1.2f);
        }

        public void FitToWindow()
        {
            CancelGesture();
            ClearDropFeedback();
            StopViewInteraction();
            bool wasFit = _fitToWindow;
            PointF before = _viewOrigin;
            float beforeScale = _scale;
            _fitToWindow = true;
            UpdateBoardBounds();
            InvalidateCaches(true);
            if (!wasFit && before == _viewOrigin && beforeScale == _scale) RaiseViewChanged();
        }

        public void ResetView() { FitToWindow(); }

        /// <summary>Pan by client pixels, with no alteration to board-space content.</summary>
        public void PanBy(float clientDx, float clientDy)
        {
            if (Single.IsNaN(clientDx) || Single.IsInfinity(clientDx) ||
                Single.IsNaN(clientDy) || Single.IsInfinity(clientDy))
                throw new ArgumentOutOfRangeException("clientDx");
            if (clientDx == 0 && clientDy == 0) return;
            CancelGesture();
            _fitToWindow = false;
            _viewOrigin = new PointF(_viewOrigin.X + clientDx, _viewOrigin.Y + clientDy);
            BeginViewInteraction();
            RaiseViewChanged();
        }

        public PointF BoardToClient(PointF boardPoint) { return ToClient(boardPoint); }

        private Point ViewCenter() { return new Point(ClientSize.Width / 2, ClientSize.Height / 2); }

        private void RaiseViewChanged()
        {
            EventHandler handler = ViewChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void BeginViewInteraction()
        {
            ClearDropFeedback();
            CancelRefinement();
            _viewInteracting = true;
            _lastViewInputTick = Environment.TickCount;
            bool invalidate = !_navigationPending;
            _navigationPending = true;
            _annotationsDirty = true;
            ScheduleRefinement();
            if (invalidate) Invalidate();
        }

        private void StopViewInteraction()
        {
            if (_viewRefineTimer != null) _viewRefineTimer.Stop();
            _viewInteracting = false;
            CancelRefinement();
            // A zero-distance click is still the end of a gesture. Keep a pending
            // refinement so cancelling an old worker cannot strand a coarse view.
            ScheduleRefinement();
        }

        private void OnRefineTimerTick(object sender, EventArgs e)
        {
            ApplyCompletedRefinement();
            if (_viewInteracting && unchecked(Environment.TickCount - _lastViewInputTick) < 140)
                return;
            FinishViewInteraction(sender, e);
        }

        private void FinishViewInteraction(object sender, EventArgs e)
        {
            ApplyCompletedRefinement();
            if (_gesture == Gesture.Pan) return;
            _viewInteracting = false;
            StartRefinementIfReady();
            if (!IsRefinementPending && _viewRefineTimer != null) _viewRefineTimer.Stop();
        }

        private void ScheduleRefinement()
        {
            _refineRequested = HasContent && (_viewportDirty || _navigationPending ||
                _viewportCache == null || _viewportInteractive);
            if (_viewRefineTimer == null || _disposed) return;
            // Do not recreate a native timer for every high-frequency mouse event.
            // Its tick checks a timestamp; input only updates the camera and time.
            if ((_refineRequested || _refineWork != null) && !_viewRefineTimer.Enabled)
                _viewRefineTimer.Start();
        }

        private void CancelRefinement()
        {
            _viewGeneration++;
            _refineRequested = false;
            if (_refineWork != null) _refineWork.Discard();
        }

        private void StartRefinementIfReady()
        {
            if (_disposed || !_refineRequested || _refineWork != null ||
                _viewInteracting || _gesture != Gesture.None) return;
            if (!HasContent) { _refineRequested = false; return; }
            // First present the inexpensive latest camera. Snapshotting itself
            // retains immutable sources; it neither copies nor samples 4K images.
            if (_viewportDirty || _navigationPending || _viewportCache == null ||
                _viewportCache.Size != ClientSize)
            {
                Invalidate();
                return;
            }
            BoardViewSnapshot snapshot = CreateRefinementSnapshot();
            RefinementWork work = new RefinementWork(_viewGeneration);
            _refineWork = work;
            _refineRequested = false;
            Task.Factory.StartNew(delegate
            {
                Bitmap result = null;
                try { result = snapshot.Render(work.Token); }
                catch (OperationCanceledException) { }
                catch (Exception) { /* Keep the usable preview if refinement fails. */ }
                finally
                {
                    snapshot.Dispose();
                    work.Complete(result);
                }
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        private void ApplyCompletedRefinement()
        {
            RefinementWork work = _refineWork;
            if (work == null || !work.Completed) return;
            _refineWork = null;
            Bitmap result = work.TakeResult();
            bool current = !_disposed && work.Generation == _viewGeneration &&
                !_navigationPending && !_viewportDirty && !_viewInteracting &&
                _gesture == Gesture.None;
            work.Discard();
            if (result != null)
            {
                if (current && result.Size == ClientSize)
                {
                    if (_viewScratch != null) _viewScratch.Dispose();
                    _viewScratch = _viewportCache;
                    _viewportCache = result;
                    _viewportInteractive = false;
                    _cachedViewOrigin = _viewOrigin;
                    _cachedViewScale = _scale;
                    Invalidate();
                }
                else result.Dispose();
            }
        }

        private BoardViewSnapshot CreateRefinementSnapshot()
        {
            BoardViewSnapshot snapshot = new BoardViewSnapshot {
                ViewSize = ClientSize, DpiScale = _uiScale, Origin = _viewOrigin,
                Scale = _scale, Background = BackColor
            };
            try
            {
                RectangleF visible = VisibleBoardArea();
                foreach (BoardImage image in _images)
                    if (image.Bounds.IntersectsWith(visible))
                        snapshot.Images.Add(image.RetainSnapshot());
                foreach (BoardAnnotation annotation in _annotations)
                    if (annotation.Bounds.IntersectsWith(visible))
                        snapshot.Annotations.Add(new BoardSnapshotAnnotation {
                            Bounds = annotation.Bounds, Draw = annotation.Draw
                        });
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
        }

        // Deterministic reference for synthetic pixel regressions; production
        // refinement exclusively invokes Render on the worker above.
        private Bitmap BuildRefinedViewportForValidation()
        {
            using (BoardViewSnapshot snapshot = CreateRefinementSnapshot())
                return snapshot.Render(CancellationToken.None);
        }

        /// <summary>Only images currently on the board count, including a restored deletion.</summary>
        public bool ContainsImageId(long id)
        {
            if (id <= 0) return false;
            foreach (BoardImage image in _images)
                if (image.Id == id) return true;
            return false;
        }

        /// <summary>
        /// The host can reject drops while it is changing the board. A source control
        /// performing its own DoDragDrop should keep this enabled for that operation.
        /// </summary>
        public bool ImageDropEnabled
        {
            get { return _imageDropEnabled; }
            set
            {
                if (_imageDropEnabled == value) return;
                _imageDropEnabled = value;
                if (!value) ClearDropFeedback();
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
                UpdateCursor(PointToClient(Cursor.Position));
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

        public PointF ClientToBoard(Point point, bool clamp)
        {
            // Keep the legacy parameter for callers; an infinite workspace never
            // clamps a pointer to content bounds, including during captured drags.
            float x = (point.X - _viewOrigin.X) / Math.Max(.0000001f, _scale);
            float y = (point.Y - _viewOrigin.Y) / Math.Max(.0000001f, _scale);
            return new PointF(x, y);
        }

        public void AddImage(Bitmap bitmap)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            CancelGesture();
            AddImageCore(bitmap, FindPlacement(bitmap.Size));
        }

        /// <summary>
        /// Add an independent copy centered at any world point, including negative
        /// coordinates. ImageDropped belongs only to completed drag/drop operations.
        /// </summary>
        public void AddImageAt(Bitmap bitmap, PointF boardPoint)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");
            if (Single.IsNaN(boardPoint.X) || Single.IsInfinity(boardPoint.X) ||
                Single.IsNaN(boardPoint.Y) || Single.IsInfinity(boardPoint.Y))
                throw new ArgumentOutOfRangeException("boardPoint");
            CancelGesture();
            AddImageCore(bitmap, PlacementAt(bitmap.Size, boardPoint));
        }

        private void AddImageCore(Bitmap bitmap, RectangleF bounds)
        {
            long id = checked(_nextImageId + 1);
            BoardImage image = new BoardImage(CopyImage(bitmap), bounds, id,
                !Image.IsAlphaPixelFormat(bitmap.PixelFormat) &&
                (bitmap.PixelFormat & PixelFormat.Indexed) == 0);
            BoardImage oldSelection = _selected;
            _images.Add(image);
            // Keep identities monotonic across ClearBoard and Undo. A deleted image
            // retains its identity when the same object is restored by undo.
            _nextImageId = _lastAddedImageId = id;
            PushChange(new AddImageChange(image, oldSelection));
            SetSelection(image);
            InvalidateCaches(true);
            RaiseContentChanged();
        }

        /// <summary>
        /// Export the union of all content plus padding, independent of the camera.
        /// Background is white; navigation UI, grid and selection are excluded.
        /// </summary>
        public Bitmap ExportImage()
        {
            if (!HasContent) throw new InvalidOperationException("画布还是空的，请先添加图片或标注。");
            RectangleF bounds = ExportBounds;
            double width = bounds.Width, height = bounds.Height;
            if (Double.IsNaN(width) || Double.IsInfinity(width) ||
                Double.IsNaN(height) || Double.IsInfinity(height) ||
                width <= 0 || height <= 0 || width > MaximumExportSide ||
                height > MaximumExportSide || width * height > MaximumExportPixels)
                throw new InvalidOperationException("内容范围过大，暂时无法导出。请将内容移近一些或缩小图片；导出限单边 32767 像素、总计 8000 万像素。");
            Bitmap result = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.Clear(Color.White);
                    ConfigureQuality(graphics);
                    graphics.TranslateTransform(-bounds.Left, -bounds.Top);
                    foreach (BoardImage image in _images) image.Draw(graphics);
                    foreach (BoardAnnotation annotation in _annotations) annotation.Draw(graphics);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public void Undo()
        {
            CancelGesture();
            if (_undo.Count == 0) return;
            int last = _undo.Count - 1;
            BoardChange change = _undo[last];
            _undo.RemoveAt(last);
            change.Undo(this);
            change.Dispose();
            InvalidateCaches(true);
            RaiseContentChanged();
        }

        public void DeleteSelected()
        {
            CancelGesture();
            if (_selected == null) return;
            BoardImage image = _selected;
            int index = _images.IndexOf(image);
            _images.RemoveAt(index);
            SetSelection(null);
            // The history command owns the removed image until it is restored or
            // discarded. Active images are always owned by _images.
            PushChange(new DeleteImageChange(image, index));
            InvalidateCaches(false);
            RaiseContentChanged();
        }

        public void ClearBoard()
        {
            CancelGesture();
            ClearDropFeedback();
            bool hadContent = _images.Count > 0 || _annotations.Count > 0 || _undo.Count > 0;
            foreach (BoardChange change in _undo) change.Dispose();
            _undo.Clear();
            foreach (BoardImage image in _images) image.Dispose();
            _images.Clear();
            _annotations.Clear();
            SetSelection(null);
            _fitToWindow = true;
            UpdateBoardBounds();
            InvalidateCaches(true);
            if (hadContent) RaiseContentChanged();
        }

        private RectangleF FindPlacement(Size size)
        {
            RectangleF view = VisibleBoardArea();
            float gap = 24 * _uiScale / _scale;
            float cellWidth = Math.Max(1, (view.Width - gap * 3) / 2);
            SizeF fitted = FittedImageSize(size);
            RectangleF best = RectangleF.Empty;
            // Start a local two-column arrangement in the current view, not at a
            // permanent world origin. Occupied columns continue downwards without
            // changing the camera or moving any existing image.
            for (int column = 0; column < 2; column++)
            {
                float x = view.Left + gap + column * (cellWidth + gap);
                float y = view.Top + gap;
                for (int attempt = 0; attempt <= _images.Count; attempt++)
                {
                    RectangleF candidate = new RectangleF(new PointF(x, y), fitted);
                    float nextY = y;
                    foreach (BoardImage image in _images)
                    {
                        RectangleF padded = image.Bounds;
                        padded.Inflate(gap / 2, gap / 2);
                        if (candidate.IntersectsWith(padded))
                            nextY = Math.Max(nextY, image.Bounds.Bottom + gap);
                    }
                    if (nextY == y) break;
                    y = nextY;
                }
                RectangleF placed = new RectangleF(new PointF(x, y), fitted);
                if (column == 0 || placed.Y < best.Y) best = placed;
            }
            return best;
        }

        private SizeF FittedImageSize(Size size)
        {
            float width = Math.Min(1104, Math.Max(1, ClientSize.Width - 72 * _uiScale) / (2 * _scale));
            float height = Math.Min(900, Math.Max(1, ClientSize.Height - 48 * _uiScale) * .75f / _scale);
            float fit = Math.Min(1f, Math.Min(width / size.Width, height / size.Height));
            return new SizeF(Math.Max(1, size.Width * fit), Math.Max(1, size.Height * fit));
        }

        private RectangleF PlacementAt(Size size, PointF center)
        {
            SizeF fitted = FittedImageSize(size);
            float x = center.X - fitted.Width / 2;
            float y = center.Y - fitted.Height / 2;
            return new RectangleF(new PointF(x, y), fitted);
        }

        private static Bitmap CopyImage(Bitmap source)
        {
            Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(copy))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImage(source, new Rectangle(Point.Empty, source.Size),
                        0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
                }
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }

        private void PushChange(BoardChange change)
        {
            _undo.Add(change);
            if (_undo.Count <= HistoryLimit) return;
            _undo[0].Dispose();
            _undo.RemoveAt(0);
        }

        private void SetSelection(BoardImage image)
        {
            if (image != null && !_images.Contains(image)) image = null;
            if (_selected == image) return;
            Rectangle old = SelectionPaintBounds();
            _selected = image;
            InvalidateArea(old);
            InvalidateArea(SelectionPaintBounds());
            EventHandler handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseContentChanged()
        {
            // Content edits never auto-fit and unexpectedly move the user's view.
            if (HasContent) _fitToWindow = false;
            EventHandler handler = ContentChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static void UnionBounds(ref RectangleF union, ref bool found, RectangleF bounds)
        {
            if (!found) { union = bounds; found = true; }
            else union = RectangleF.Union(union, bounds);
        }

        private static int SizeComponent(float value)
        {
            if (Single.IsNaN(value) || value <= 0) return 0;
            return value >= Int32.MaxValue ? Int32.MaxValue : (int)Math.Ceiling(value);
        }

        private void UpdateBoardBounds()
        {
            PointF before = _viewOrigin;
            float beforeScale = _scale;
            if (_fitToWindow)
            {
                if (!HasContent)
                {
                    _scale = .5f;
                    _viewOrigin = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
                }
                else
                {
                    RectangleF content = ExportBounds;
                    float padding = 24 * _uiScale;
                    _scale = Math.Min(4f, Math.Min(
                        Math.Max(1, ClientSize.Width - padding * 2) / Math.Max(1, content.Width),
                        Math.Max(1, ClientSize.Height - padding * 2) / Math.Max(1, content.Height)));
                    _scale = Math.Max(.0000001f, _scale);
                    _viewOrigin = new PointF(ClientSize.Width / 2f -
                        (content.Left + content.Width / 2) * _scale,
                        ClientSize.Height / 2f - (content.Top + content.Height / 2) * _scale);
                }
            }
            if (before != _viewOrigin || beforeScale != _scale) RaiseViewChanged();
        }

        private RectangleF ToClient(RectangleF rectangle)
        {
            return new RectangleF(_viewOrigin.X + rectangle.X * _scale,
                _viewOrigin.Y + rectangle.Y * _scale, rectangle.Width * _scale,
                rectangle.Height * _scale);
        }

        private PointF ToClient(PointF point)
        {
            return new PointF(_viewOrigin.X + point.X * _scale,
                _viewOrigin.Y + point.Y * _scale);
        }

        private static Rectangle Cover(RectangleF rectangle, float margin)
        {
            rectangle.Inflate(margin, margin);
            return Rectangle.FromLTRB((int)Math.Floor(rectangle.Left), (int)Math.Floor(rectangle.Top),
                (int)Math.Ceiling(rectangle.Right), (int)Math.Ceiling(rectangle.Bottom));
        }

        private void InvalidateArea(Rectangle area)
        {
            area.Intersect(ClientRectangle);
            if (area.Width > 0 && area.Height > 0) Invalidate(area, false);
        }

        private void InvalidateCaches(bool annotations)
        {
            CancelRefinement();
            _viewportDirty = true;
            if (annotations) _annotationsDirty = true;
            ScheduleRefinement();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            CancelGesture();
            ClearDropFeedback();
            StopViewInteraction();
            base.OnResize(e);
            UpdateBoardBounds();
            InvalidateCaches(true);
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            CancelRefinement();
            _viewportDirty = true;
            ScheduleRefinement();
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            ApplyCompletedRefinement();
            float scale = e.Graphics.DpiX / 96f;
            if (Math.Abs(scale - _uiScale) > .01f)
            {
                CancelGesture();
                _uiScale = scale;
                CancelRefinement();
                if (_viewScratch != null) { _viewScratch.Dispose(); _viewScratch = null; }
                UpdateBoardBounds();
                _viewportDirty = _annotationsDirty = true;
                ScheduleRefinement();
            }
            Rectangle clip = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            Graphics graphics = e.Graphics;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(clip, CombineMode.Intersect);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                if ((_gesture == Gesture.Move || _gesture == Gesture.Resize) && _dragBackground != null)
                {
                    CopyViewport(graphics, _dragBackground, clip);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    GraphicsState imageState = graphics.Save();
                    graphics.SetClip(ClientRectangle, CombineMode.Intersect);
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    if (_dragPreview != null && _selected != null)
                    {
                        if (_selected.IsOpaque) graphics.CompositingMode = CompositingMode.SourceCopy;
                        RectangleF destination = ToClient(_selected.Bounds);
                        BoardImage.DrawRegion(graphics, _dragPreview, destination,
                            RectangleF.Intersect(destination, ClientRectangle));
                    }
                    graphics.Restore(imageState);
                    if (_dragForeground != null)
                        CopyViewport(graphics, _dragForeground, clip);
                    else
                    {
                        EnsureAnnotationCache();
                        CopyViewport(graphics, _annotationCache, clip);
                    }
                }
                else
                {
                    EnsureViewportCache();
                    CopyViewport(graphics, _viewportCache, clip);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    if (_gesture == Gesture.Annotate)
                    {
                        if (_gestureTool == DrawingTool.Pen && _penPreview != null)
                        {
                            CopyViewport(graphics, _penPreview, clip);
                            DrawActivePenTail(graphics);
                        }
                        else
                            DrawActiveShape(graphics);
                    }
                }
                graphics.CompositingMode = CompositingMode.SourceOver;
                if (!HasContent && _gesture != Gesture.Annotate) DrawEmpty(graphics);
                DrawSelection(graphics);
                if (_dropPreviewVisible) DrawDropFeedback(graphics);
            }
            finally { graphics.Restore(state); }
            StartRefinementIfReady();
        }

        private static void CopyViewport(Graphics graphics, Bitmap image, Rectangle clip)
        {
            if (image != null)
                graphics.DrawImage(image, clip, clip, GraphicsUnit.Pixel);
        }

        private void EnsureViewportCache()
        {
            if (_navigationPending)
            {
                bool shifted = !_viewportDirty && _cachedViewScale == _scale &&
                    TryShiftViewport(_viewOrigin.X - _cachedViewOrigin.X,
                        _viewOrigin.Y - _cachedViewOrigin.Y);
                _navigationPending = false;
                if (!shifted) _viewportDirty = true;
                else
                {
                    _cachedViewOrigin = _viewOrigin;
                    _cachedViewScale = _scale;
                }
            }
            if (!_viewportDirty && _viewportCache != null && _viewportCache.Size == ClientSize) return;
            if (_viewScratch == null || _viewScratch.Size != ClientSize)
            {
                if (_viewScratch != null) _viewScratch.Dispose();
                _viewScratch = NewViewportBitmap();
            }
            bool previous = _forcePreviewRendering;
            _forcePreviewRendering = true;
            try
            {
                using (Graphics graphics = Graphics.FromImage(_viewScratch))
                    PaintViewport(graphics, null, true);
            }
            finally { _forcePreviewRendering = previous; }
            Bitmap old = _viewportCache;
            _viewportCache = _viewScratch;
            _viewScratch = old;
            _viewportDirty = false;
            _viewportInteractive = true;
            _cachedViewOrigin = _viewOrigin;
            _cachedViewScale = _scale;
        }

        private Bitmap BuildViewport(BoardImage excluded, bool includeAnnotations)
        {
            Bitmap bitmap = NewViewportBitmap();
            bool previous = _forcePreviewRendering;
            if (excluded != null) _forcePreviewRendering = true;
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    PaintViewport(graphics, excluded, includeAnnotations);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
            finally { _forcePreviewRendering = previous; }
        }

        private void PaintViewport(Graphics graphics, BoardImage excluded, bool includeAnnotations)
        {
            // FillRectangle respects a dirty-strip clip; Graphics.Clear does not.
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using (Brush background = new SolidBrush(BackColor))
                graphics.FillRectangle(background, ClientRectangle);
            graphics.CompositingMode = CompositingMode.SourceOver;
            DrawGrid(graphics);
            GraphicsState state = graphics.Save();
            ApplyBoardTransform(graphics);
            foreach (BoardImage image in _images)
            {
                // A drag background ends at the selected image. Later images
                // belong to the transparent foreground, preserving their order.
                if (image == excluded) break;
                DrawImageInViewport(graphics, image);
            }
            if (includeAnnotations)
                DrawVisibleAnnotations(graphics);
            graphics.Restore(state);
        }

        private void DrawGrid(Graphics graphics)
        {
            // World-aligned dots remain stable under integer viewport shifts.
            // Adapt their world spacing to keep drawing work bounded at any zoom.
            double worldStep = 64;
            while (worldStep * _scale < 22 * _uiScale) worldStep *= 2;
            while (worldStep * _scale > 48 * _uiScale) worldStep /= 2;
            double step = worldStep * _scale;
            RectangleF clip = RectangleF.Intersect(ClientRectangle, graphics.ClipBounds);
            double firstX = Math.Floor((clip.Left - _viewOrigin.X) / step) * step + _viewOrigin.X;
            double firstY = Math.Floor((clip.Top - _viewOrigin.Y) / step) * step + _viewOrigin.Y;
            int dot = Math.Max(1, (int)Math.Round(_uiScale));
            using (Brush brush = new SolidBrush(Color.FromArgb(211, 220, 234)))
                for (double y = firstY; y <= clip.Bottom; y += step)
                    for (double x = firstX; x <= clip.Right; x += step)
                        graphics.FillRectangle(brush, (float)Math.Floor(x + .5),
                            (float)Math.Floor(y + .5), dot, dot);
        }

        private bool TryShiftViewport(float dx, float dy)
        {
            int x = (int)Math.Round(dx), y = (int)Math.Round(dy);
            if (Math.Abs(dx - x) > .0001f || Math.Abs(dy - y) > .0001f ||
                _viewportDirty || _viewportCache == null || _viewportCache.Size != ClientSize ||
                !_viewportInteractive || HasContent ||
                Math.Abs(x) >= ClientSize.Width || Math.Abs(y) >= ClientSize.Height)
                return false;
            if (x == 0 && y == 0) return true;
            if (_viewScratch == null || _viewScratch.Size != ClientSize)
            {
                if (_viewScratch != null) _viewScratch.Dispose();
                _viewScratch = NewViewportBitmap();
            }
            // The empty grid can be shifted exactly. Content is repainted once per
            // paint frame: GDI+ nearest-neighbor clipping can change the first
            // sampled row of a thumbnail, even with an integer destination. Reusing
            // that row after a later pan would leave a permanent image seam.
            // Input remains coalesced and both buffers stay client-sized.
            bool interacting = _viewInteracting;
            _viewInteracting = _viewportInteractive;
            try
            {
                using (Graphics graphics = Graphics.FromImage(_viewScratch))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(_viewportCache,
                        new Rectangle(x, y, ClientSize.Width, ClientSize.Height),
                        new Rectangle(Point.Empty, ClientSize), GraphicsUnit.Pixel);
                    Rectangle vertical = x > 0 ? new Rectangle(0, 0, x, ClientSize.Height) :
                        new Rectangle(ClientSize.Width + x, 0, -x, ClientSize.Height);
                    Rectangle horizontal = y > 0 ?
                        new Rectangle(Math.Max(0, x), 0, ClientSize.Width - Math.Abs(x), y) :
                        new Rectangle(Math.Max(0, x), ClientSize.Height + y,
                            ClientSize.Width - Math.Abs(x), -y);
                    PaintViewportStrip(graphics, vertical);
                    PaintViewportStrip(graphics, horizontal);
                }
                Bitmap old = _viewportCache;
                _viewportCache = _viewScratch;
                _viewScratch = old;
                return true;
            }
            finally { _viewInteracting = interacting; }
        }

        private void PaintViewportStrip(Graphics graphics, Rectangle strip)
        {
            if (strip.Width <= 0 || strip.Height <= 0) return;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(strip);
                graphics.CompositingQuality = CompositingQuality.Default;
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Default;
                PaintViewport(graphics, null, true);
            }
            finally { graphics.Restore(state); }
        }

        private void EnsureAnnotationCache()
        {
            if (!_annotationsDirty && _annotationCache != null && _annotationCache.Size == ClientSize) return;
            Bitmap next = NewViewportBitmap();
            try
            {
                using (Graphics graphics = Graphics.FromImage(next))
                {
                    graphics.Clear(Color.Transparent);
                    ApplyBoardTransform(graphics);
                    DrawVisibleAnnotations(graphics);
                }
            }
            catch { next.Dispose(); throw; }
            if (_annotationCache != null) _annotationCache.Dispose();
            _annotationCache = next;
            _annotationsDirty = false;
        }

        private Bitmap BuildDragForeground(int firstImage)
        {
            Bitmap bitmap = NewViewportBitmap();
            bool previous = _forcePreviewRendering;
            _forcePreviewRendering = true;
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    ApplyBoardTransform(graphics);
                    for (int i = firstImage; i < _images.Count; i++)
                        DrawImageInViewport(graphics, _images[i]);
                    DrawVisibleAnnotations(graphics);
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
            finally { _forcePreviewRendering = previous; }
        }

        private Bitmap NewViewportBitmap()
        {
            Bitmap bitmap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height),
                PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(96 * _uiScale, 96 * _uiScale);
            return bitmap;
        }

        private RectangleF VisibleBoardArea()
        {
            PointF first = ClientToBoard(Point.Empty, false);
            PointF last = ClientToBoard(new Point(ClientSize.Width, ClientSize.Height), false);
            return RectangleF.FromLTRB(first.X, first.Y, last.X, last.Y);
        }

        private void DrawImageInViewport(Graphics graphics, BoardImage image)
        {
            RectangleF visible = RectangleF.Intersect(image.Bounds,
                RectangleF.Intersect(VisibleBoardArea(), graphics.ClipBounds));
            if (visible.Width <= 0 || visible.Height <= 0) return;
            // Images outside the viewport do no work. During wheel/pan interaction,
            // reuse a bounded thumbnail; at rest draw only the visible original
            // source region, never a bitmap the size of the enlarged whole board.
            GraphicsState state = graphics.Save();
            try
            {
                // GDI+ may rasterize a large transformed destination before clipping.
                // Map this image's visible rectangle to device pixels explicitly.
                graphics.ResetTransform();
                RectangleF destination = ToClient(image.Bounds);
                // Read the clip after resetting to device coordinates. A world ->
                // client round trip can turn an integer strip boundary into e.g.
                // 570.00006 and accidentally exclude its first row.
                RectangleF clientVisible = RectangleF.Intersect(destination,
                    RectangleF.Intersect(ClientRectangle, graphics.ClipBounds));
                image.DrawPreview(graphics, _scale, destination, clientVisible,
                    _viewInteracting || _viewportInteractive || _forcePreviewRendering);
            }
            finally { graphics.Restore(state); }
        }

        private void DrawVisibleAnnotations(Graphics graphics)
        {
            RectangleF visible = RectangleF.Intersect(VisibleBoardArea(), graphics.ClipBounds);
            foreach (BoardAnnotation annotation in _annotations)
                if (annotation.Bounds.IntersectsWith(visible)) annotation.Draw(graphics);
        }

        private void ApplyBoardTransform(Graphics graphics)
        {
            graphics.SetClip(ClientRectangle, CombineMode.Intersect);
            graphics.TranslateTransform(_viewOrigin.X, _viewOrigin.Y);
            graphics.ScaleTransform(_scale, _scale);
            ConfigureQuality(graphics);
        }

        private static void ConfigureQuality(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        private void DrawEmpty(Graphics graphics)
        {
            // Screen-fixed guidance is drawn over the viewport cache, so panning
            // never copies old text into the background or leaves a ghost.
            Rectangle title = new Rectangle(12,
                (int)(ClientSize.Height / 2 - 32 * _uiScale),
                Math.Max(1, ClientSize.Width - 24), (int)(32 * _uiScale));
            using (Font heading = new Font("Microsoft YaHei UI", 15f))
            using (Font detail = new Font("Microsoft YaHei UI", 9.5f))
            {
                TextRenderer.DrawText(graphics, "从这里自由展开", heading, title,
                    Color.FromArgb(90, 111, 141), TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping);
                title.Y += (int)(38 * _uiScale);
                TextRenderer.DrawText(graphics, "拖入图片 · 拖动空白处平移 · 滚轮缩放", detail, title,
                    Color.FromArgb(139, 153, 173), TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.PreserveGraphicsClipping);
            }
        }

        private RectangleF ResizeHandle()
        {
            if (_selected == null) return RectangleF.Empty;
            RectangleF image = ToClient(_selected.Bounds);
            float size = 12 * _uiScale;
            return new RectangleF(image.Right - size / 2, image.Bottom - size / 2, size, size);
        }

        private Rectangle SelectionPaintBounds()
        {
            return _selected == null ? Rectangle.Empty : Cover(ToClient(_selected.Bounds), 10 * _uiScale);
        }

        private void DrawSelection(Graphics graphics)
        {
            if (_selected == null) return;
            RectangleF bounds = ToClient(_selected.Bounds);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(54, 113, 238), 1.5f * _uiScale))
            {
                graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                RectangleF handle = ResizeHandle();
                graphics.FillRectangle(Brushes.White, handle);
                graphics.DrawRectangle(pen, handle.X, handle.Y, handle.Width, handle.Height);
            }
        }

        private void DrawDropFeedback(Graphics graphics)
        {
            Color blue = Color.FromArgb(47, 102, 241);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF boardEdge = ClientRectangle;
            boardEdge.Inflate(-2 * _uiScale, -2 * _uiScale);
            if (boardEdge.Width <= 0 || boardEdge.Height <= 0) return;
            using (Pen border = new Pen(blue, 3 * _uiScale))
                graphics.DrawRectangle(border, boardEdge.X, boardEdge.Y, boardEdge.Width, boardEdge.Height);
            if (_dropBounds.Width > 0 && _dropBounds.Height > 0)
            {
                RectangleF placement = ToClient(_dropBounds);
                using (Brush tint = new SolidBrush(Color.FromArgb(30, blue)))
                    graphics.FillRectangle(tint, placement);
                using (Pen outline = new Pen(blue, 2 * _uiScale))
                {
                    outline.DashStyle = DashStyle.Dash;
                    graphics.DrawRectangle(outline, placement.X, placement.Y, placement.Width, placement.Height);
                }
            }
            Rectangle label = new Rectangle(
                (int)(boardEdge.Left + (boardEdge.Width - 160 * _uiScale) / 2),
                (int)(boardEdge.Top + 12 * _uiScale),
                (int)(160 * _uiScale), (int)(30 * _uiScale));
            using (Brush fill = new SolidBrush(blue)) graphics.FillRectangle(fill, label);
            TextRenderer.DrawText(graphics, _dropFiles == null ? "松手加入画板" : "松手导入图片",
                _dropFont, label, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            ClearDropFeedback();
            UpdateDropFeedback(drgevent);
        }

        protected override void OnDragOver(DragEventArgs drgevent)
        {
            base.OnDragOver(drgevent);
            UpdateDropFeedback(drgevent);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            ClearDropFeedback();
            base.OnDragLeave(e);
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            drgevent.Effect = DragDropEffects.None;
            try
            {
                Point client = PointToClient(new Point(drgevent.X, drgevent.Y));
                Bitmap bitmap;
                Size size;
                if (!_imageDropEnabled || !Enabled || !ClientRectangle.Contains(client) ||
                    (drgevent.AllowedEffect & DragDropEffects.Copy) == 0) return;
                if (TryGetDropBitmap(drgevent.Data, out bitmap, out size))
                {
                    ClearDropFeedback();
                    AddImageAt(bitmap, ClientToBoard(client, true));
                    Tool = DrawingTool.Select;
                    drgevent.Effect = DragDropEffects.Copy;
                    EventHandler handler = ImageDropped;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                else
                {
                    Action<string[]> import = RequestFileDrop;
                    string[] paths;
                    if (import == null || !TryGetDropFiles(drgevent.Data, out paths)) return;
                    ClearDropFeedback();
                    import(paths);
                    drgevent.Effect = DragDropEffects.Copy;
                }
            }
            finally
            {
                // The data object's bitmap is borrowed from the source control.
                // Only AddImageAt's independent copy belongs to this board.
                ClearDropFeedback();
            }
        }

        private void UpdateDropFeedback(DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            Point client = PointToClient(new Point(e.X, e.Y));
            if (!_imageDropEnabled || !Enabled || !ClientRectangle.Contains(client) ||
                (e.AllowedEffect & DragDropEffects.Copy) == 0)
            {
                ClearDropFeedback();
                return;
            }
            bool wasFileDrop = _dropFiles != null;
            if (!Object.ReferenceEquals(e.Data, _dropData) || (_dropBitmap == null && _dropFiles == null))
            {
                Bitmap bitmap;
                Size size;
                if (!TryGetDropBitmap(e.Data, out bitmap, out size))
                {
                    string[] paths;
                    if (RequestFileDrop == null || !TryGetDropFiles(e.Data, out paths))
                    {
                        ClearDropFeedback();
                        return;
                    }
                    _dropFiles = paths;
                }
                else _dropFiles = null;
                _dropData = e.Data;
                _dropBitmap = bitmap;
                _dropImageSize = size;
            }
            if (_dropFiles != null && RequestFileDrop == null)
            {
                ClearDropFeedback();
                return;
            }
            RectangleF next = _dropFiles == null
                ? PlacementAt(_dropImageSize, ClientToBoard(client, true)) : RectangleF.Empty;
            e.Effect = DragDropEffects.Copy;
            if (_dropPreviewVisible && next == _dropBounds && wasFileDrop == (_dropFiles != null)) return;
            bool wasVisible = _dropPreviewVisible;
            RectangleF old = _dropBounds;
            _dropPreviewVisible = true;
            _dropBounds = next;
            if (!wasVisible || wasFileDrop != (_dropFiles != null))
                Invalidate();
            else
            {
                InvalidateArea(Cover(ToClient(old), 4 * _uiScale));
                InvalidateArea(Cover(ToClient(next), 4 * _uiScale));
            }
        }

        private static bool TryGetDropBitmap(IDataObject data, out Bitmap bitmap, out Size size)
        {
            bitmap = null;
            size = Size.Empty;
            if (data == null) return false;
            try
            {
                if (!data.GetDataPresent(DataFormats.Bitmap, true)) return false;
                Bitmap candidate = data.GetData(DataFormats.Bitmap, true) as Bitmap;
                if (candidate == null) return false;
                size = candidate.Size;
                if (size.Width <= 0 || size.Height <= 0) return false;
                bitmap = candidate;
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool TryGetDropFiles(IDataObject data, out string[] paths)
        {
            paths = null;
            if (data == null) return false;
            try
            {
                if (!data.GetDataPresent(DataFormats.FileDrop, true)) return false;
                string[] candidate = data.GetData(DataFormats.FileDrop, true) as string[];
                if (candidate == null || candidate.Length == 0) return false;
                foreach (string path in candidate)
                    if (String.IsNullOrWhiteSpace(path)) return false;
                paths = (string[])candidate.Clone();
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void ClearDropFeedback()
        {
            if (_dropPreviewVisible) Invalidate();
            _dropPreviewVisible = false;
            _dropBounds = RectangleF.Empty;
            _dropData = null;
            _dropBitmap = null;
            _dropFiles = null;
            _dropImageSize = Size.Empty;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) ClearDropFeedback();
            base.OnEnabledChanged(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) ClearDropFeedback();
            base.OnVisibleChanged(e);
        }

        private BoardImage HitTest(PointF point)
        {
            for (int i = _images.Count - 1; i >= 0; i--)
                if (_images[i].Bounds.Contains(point)) return _images[i];
            return null;
        }

        private void UpdateCursor(Point location)
        {
            if (_gesture == Gesture.Pan || _panMode || _spacePressed)
            { Cursor = Cursors.Hand; return; }
            if (_gesture == Gesture.Move) { Cursor = Cursors.SizeAll; return; }
            if (_gesture == Gesture.Resize) { Cursor = Cursors.SizeNWSE; return; }
            if (_tool == DrawingTool.Select || _tool == DrawingTool.Mosaic)
            {
                if (_selected != null && ResizeHandle().Contains(location)) Cursor = Cursors.SizeNWSE;
                else Cursor = HitTest(ClientToBoard(location, false)) != null ? Cursors.SizeAll : Cursors.Default;
            }
            else Cursor = _tool == DrawingTool.Text ? Cursors.IBeam : Cursors.Cross;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { CancelGesture(); return; }
            bool pan = e.Button == MouseButtons.Middle ||
                (e.Button == MouseButtons.Left &&
                (_panMode || _spacePressed || GetKeyState(0x20) < 0));
            if (e.Button != MouseButtons.Left && !pan) return;
            Focus();
            CancelGesture();
            if (pan)
            {
                StartPan(e);
                return;
            }
            // Editing starts on a stable camera. An idle timer must not replace
            // the image layers while a pen stroke or image drag is in progress.
            StopViewInteraction();
            bool direct = _tool == DrawingTool.Select || _tool == DrawingTool.Mosaic;
            bool resize = direct && _selected != null && ResizeHandle().Contains(e.Location);
            if (!ClientRectangle.Contains(e.Location) && !resize) return;
            PointF point = ClientToBoard(e.Location, true);
            if (direct)
            {
                BoardImage image = resize ? _selected : HitTest(point);
                SetSelection(image);
                if (image == null) { StartPan(e); return; }
                _start = point;
                _originalBounds = image.Bounds;
                _gesture = resize ? Gesture.Resize : Gesture.Move;
                try
                {
                    EnsureViewportCache();
                    EnsureAnnotationCache();
                    _dragBackground = BuildViewport(image, false);
                    int nextImage = _images.IndexOf(image) + 1;
                    if (nextImage < _images.Count) _dragForeground = BuildDragForeground(nextImage);
                    // Borrow the preview already created by the static viewport.
                    // Moving an image changes no scale, so neither mouse-down nor
                    // mouse-up should resample its multi-megapixel source again.
                    _dragPreview = image.GetInteractionPreview(_scale);
                    Capture = true;
                    UpdateCursor(e.Location);
                }
                catch { CancelGesture(); throw; }
                return;
            }

            if (_tool == DrawingTool.Text)
            {
                Func<string> request = RequestText;
                string text = request == null ? null : request();
                if (!String.IsNullOrWhiteSpace(text))
                    CommitAnnotation(new TextAnnotation(point, text, _strokeColor,
                        Math.Max(28, _strokeWidth * 9)));
                return;
            }
            if (_tool != DrawingTool.Pen && _tool != DrawingTool.Arrow && _tool != DrawingTool.Rectangle) return;
            _gesture = Gesture.Annotate;
            _gestureTool = _tool;
            _gestureColor = _strokeColor;
            _gestureWidth = _strokeWidth;
            _start = _current = point;
            if (_gestureTool == DrawingTool.Pen)
            {
                _penPoints = new List<PointF>();
                _penPoints.Add(point);
                _penPreview = NewViewportBitmap();
                _penGraphics = Graphics.FromImage(_penPreview);
                _penGraphics.Clear(Color.Transparent);
                ApplyBoardTransform(_penGraphics);
                _previewPen = MakePen(_gestureColor, _gestureWidth);
                // The first dot and the current tail stay on the temporary layer.
                // Only curve segments that cannot change enter the stroke cache.
            }
            Capture = true;
            if (!HasContent) Invalidate();
            else InvalidateArea(ActivePaintBounds());
        }

        private void StartPan(MouseEventArgs e)
        {
            ClearDropFeedback();
            _panStart = e.Location;
            _panOrigin = _viewOrigin;
            _panWasFit = _fitToWindow;
            _panButton = e.Button;
            _gesture = Gesture.Pan;
            Capture = true;
            UpdateCursor(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_gesture == Gesture.Pan)
            {
                ApplyPan(e.Location);
                return;
            }
            if (_gesture == Gesture.Move || _gesture == Gesture.Resize)
            {
                ApplyImageDrag(ClientToBoard(e.Location, true));
                return;
            }
            if (_gesture == Gesture.Annotate)
            {
                PointF next = ClientToBoard(e.Location, true);
                if (_gestureTool == DrawingTool.Pen)
                {
                    float dx = next.X - _current.X, dy = next.Y - _current.Y;
                    if ((dx * dx + dy * dy) * _scale * _scale < .25f) return;
                    _current = next;
                    _penPoints.Add(next);
                    SmoothStroke.DrawLatestSegment(_penGraphics, _previewPen, _penPoints);
                    // The recent three-point hull covers both the erased old tail
                    // and the new stable curve/tail. Appending remains O(1).
                    RectangleF recent = SmoothStroke.GetRecentBounds(_penPoints);
                    InvalidateArea(Cover(ToClient(recent), _gestureWidth * _scale + 4));
                }
                else
                {
                    Rectangle old = ActivePaintBounds();
                    _current = next;
                    InvalidateArea(old);
                    InvalidateArea(ActivePaintBounds());
                }
                return;
            }
            UpdateCursor(e.Location);
        }

        private void ApplyPan(Point location)
        {
            PointF next = new PointF(_panOrigin.X + location.X - _panStart.X,
                _panOrigin.Y + location.Y - _panStart.Y);
            if (next == _viewOrigin) return;
            _fitToWindow = false;
            _viewOrigin = next;
            BeginViewInteraction();
            RaiseViewChanged();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!ClientRectangle.Contains(e.Location) || e.Delta == 0 || _gesture != Gesture.None) return;
            if (e.Delta < 0 && ZoomPercent <= 10f) return;
            float factor = (float)Math.Pow(1.2, e.Delta / 120.0);
            ZoomAt(e.Location, ZoomPercent * factor);
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }

        private void ApplyImageDrag(PointF point)
        {
            if (_selected == null) return;
            Rectangle old = SelectionPaintBounds();
            if (_gesture == Gesture.Move)
            {
                float x = _originalBounds.X + point.X - _start.X;
                float y = _originalBounds.Y + point.Y - _start.Y;
                _selected.Bounds = new RectangleF(x, y, _originalBounds.Width, _originalBounds.Height);
            }
            else
            {
                float width = _originalBounds.Width, height = _originalBounds.Height;
                float desiredWidth = width + point.X - _start.X;
                float desiredHeight = height + point.Y - _start.Y;
                float factor = (desiredWidth * width + desiredHeight * height) /
                    Math.Max(1, width * width + height * height);
                float minimum = Math.Min(1f, 32f / Math.Min(width, height));
                factor = Math.Max(minimum, factor);
                _selected.Bounds = new RectangleF(_originalBounds.Location,
                    new SizeF(width * factor, height * factor));
            }
            InvalidateArea(old);
            InvalidateArea(SelectionPaintBounds());
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_gesture == Gesture.Pan)
            {
                if (e.Button != _panButton) return;
                ApplyPan(e.Location);
                _gesture = Gesture.None;
                Capture = false;
                UpdateCursor(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left || _gesture == Gesture.None) return;
            Gesture finished = _gesture;
            if (finished == Gesture.Move || finished == Gesture.Resize)
            {
                ApplyImageDrag(ClientToBoard(e.Location, true));
                bool changed = _selected != null && _selected.Bounds != _originalBounds;
                BoardImage image = _selected;
                RectangleF before = _originalBounds;
                _gesture = Gesture.None;
                Capture = false;
                DisposeGestureResources();
                if (changed)
                {
                    PushChange(new TransformImageChange(image, before));
                    InvalidateCaches(false);
                    RaiseContentChanged();
                }
                else InvalidateArea(SelectionPaintBounds());
                UpdateCursor(e.Location);
                return;
            }

            PointF finalPoint = ClientToBoard(e.Location, true);
            BoardAnnotation annotation = null;
            if (_gestureTool == DrawingTool.Pen)
            {
                if (_penPoints[_penPoints.Count - 1] != finalPoint) _penPoints.Add(finalPoint);
                annotation = new PenAnnotation(_penPoints.ToArray(), _gestureColor, _gestureWidth);
            }
            else
            {
                _current = finalPoint;
                float dx = _current.X - _start.X, dy = _current.Y - _start.Y;
                if (_gestureTool == DrawingTool.Arrow && dx * dx + dy * dy >= 4)
                    annotation = new ShapeAnnotation(_gestureTool, _start, _current, _gestureColor, _gestureWidth);
                if (_gestureTool == DrawingTool.Rectangle && Math.Abs(dx) >= 2 && Math.Abs(dy) >= 2)
                    annotation = new ShapeAnnotation(_gestureTool, _start, _current, _gestureColor, _gestureWidth);
            }
            _gesture = Gesture.None;
            Capture = false;
            DisposeGestureResources();
            if (annotation != null) CommitAnnotation(annotation);
            else Invalidate();
        }

        private void CommitAnnotation(BoardAnnotation annotation)
        {
            CancelRefinement();
            bool wasEmpty = _images.Count == 0 && _annotations.Count == 0;
            _annotations.Add(annotation);
            PushChange(new AnnotationChange(annotation));
            try
            {
                // A committed stroke is one new top layer. Append it to valid caches
                // instead of re-sampling every source image when the mouse is released.
                // An empty board needs one rebuild to remove the empty-state caption.
                if (!wasEmpty && !_viewportDirty && !_navigationPending && _viewportCache != null &&
                    _viewportCache.Size == ClientSize)
                    AppendAnnotation(_viewportCache, annotation);
                else _viewportDirty = true;
                if (!_annotationsDirty && _annotationCache != null && _annotationCache.Size == ClientSize)
                    AppendAnnotation(_annotationCache, annotation);
                else _annotationsDirty = true;
            }
            catch
            {
                _viewportDirty = _annotationsDirty = true;
                Invalidate();
                throw;
            }
            Invalidate();
            RaiseContentChanged();
            ScheduleRefinement();
        }

        private void AppendAnnotation(Bitmap cache, BoardAnnotation annotation)
        {
            using (Graphics graphics = Graphics.FromImage(cache))
            {
                ApplyBoardTransform(graphics);
                if (annotation.Bounds.IntersectsWith(VisibleBoardArea())) annotation.Draw(graphics);
            }
        }

        private static RectangleF Normalize(PointF a, PointF b)
        {
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        private Rectangle ActivePaintBounds()
        {
            return Cover(ToClient(Normalize(_start, _current)),
                Math.Max(18, _gestureWidth * 5) * _scale + 4);
        }

        private void DrawActivePenTail(Graphics graphics)
        {
            if (_penPoints == null || _penPoints.Count == 0 || _previewPen == null) return;
            GraphicsState state = graphics.Save();
            try
            {
                ApplyBoardTransform(graphics);
                if (_penPoints.Count == 1)
                    SmoothStroke.Draw(graphics, _previewPen, _penPoints);
                else
                    SmoothStroke.DrawTail(graphics, _previewPen, _penPoints);
            }
            finally { graphics.Restore(state); }
        }

        private void DrawActiveShape(Graphics graphics)
        {
            GraphicsState state = graphics.Save();
            ApplyBoardTransform(graphics);
            new ShapeAnnotation(_gestureTool, _start, _current, _gestureColor, _gestureWidth).Draw(graphics);
            graphics.Restore(state);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _gesture != Gesture.None) CancelGesture();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && !e.Control && !e.Alt)
            {
                _spacePressed = true;
                UpdateCursor(PointToClient(Cursor.Position));
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                _spacePressed = false;
                UpdateCursor(PointToClient(Cursor.Position));
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _spacePressed = false;
            UpdateCursor(PointToClient(Cursor.Position));
            base.OnLostFocus(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // The first Escape cancels an in-progress edit. With no edit underway,
            // let the host switch the tool back to direct image movement.
            if (keyData == Keys.Escape && _gesture != Gesture.None)
            { CancelGesture(); return true; }
            if (keyData == Keys.Delete) { DeleteSelected(); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CancelGesture()
        {
            if (_gesture == Gesture.None) return;
            bool panGesture = _gesture == Gesture.Pan;
            bool imageGesture = _gesture == Gesture.Move || _gesture == Gesture.Resize;
            if (imageGesture && _selected != null) _selected.Bounds = _originalBounds;
            if (panGesture)
            {
                _viewOrigin = _panOrigin;
                _fitToWindow = _panWasFit;
            }
            _gesture = Gesture.None;
            Capture = false;
            DisposeGestureResources();
            if (panGesture)
            {
                StopViewInteraction();
                InvalidateCaches(true);
                RaiseViewChanged();
            }
            Invalidate();
            UpdateCursor(PointToClient(Cursor.Position));
        }

        private void DisposeGestureResources()
        {
            if (_penGraphics != null) { _penGraphics.Dispose(); _penGraphics = null; }
            if (_previewPen != null) { _previewPen.Dispose(); _previewPen = null; }
            if (_penPreview != null) { _penPreview.Dispose(); _penPreview = null; }
            if (_dragBackground != null) { _dragBackground.Dispose(); _dragBackground = null; }
            if (_dragForeground != null) { _dragForeground.Dispose(); _dragForeground = null; }
            _dragPreview = null; // Owned by the selected BoardImage.
            _penPoints = null;
        }

        private static Pen MakePen(Color color, float width)
        {
            Pen pen = new Pen(color, width);
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            return pen;
        }

        private static bool ArrowHead(PointF from, PointF to, float width,
            out PointF left, out PointF right)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            left = right = to;
            if (length < .1f) return false;
            float head = Math.Min(length * .45f, Math.Max(18, width * 4));
            float ux = dx / length, uy = dy / length;
            left = new PointF(to.X - head * ux + head * .5f * uy,
                to.Y - head * uy - head * .5f * ux);
            right = new PointF(to.X - head * ux - head * .5f * uy,
                to.Y - head * uy + head * .5f * ux);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                CancelRefinement();
                if (_refineWork != null) { _refineWork.Discard(); _refineWork = null; }
                if (_viewRefineTimer != null) _viewRefineTimer.Dispose();
                _gesture = Gesture.None;
                DisposeGestureResources();
                foreach (BoardChange change in _undo) change.Dispose();
                _undo.Clear();
                foreach (BoardImage image in _images) image.Dispose();
                _images.Clear();
                _annotations.Clear();
                _selected = null;
                _dropData = null;
                _dropBitmap = null;
                _dropFiles = null;
                _dropFont.Dispose();
                if (_viewportCache != null) _viewportCache.Dispose();
                if (_viewScratch != null) _viewScratch.Dispose();
                if (_annotationCache != null) _annotationCache.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// One worker owns a snapshot until it finishes. Cancellation never waits
        /// for GDI+, and a late result has a single owner even after control disposal.
        /// The UI timer polls completion; no queued BeginInvoke can strand a bitmap.
        /// </summary>
        private sealed class RefinementWork
        {
            private readonly object gate = new object();
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private Bitmap result;
            private bool discarded;
            private bool cancellationDisposed;
            public readonly long Generation;
            public volatile bool Completed;
            public CancellationToken Token { get { return cancellation.Token; } }
            public RefinementWork(long generation) { Generation = generation; }
            public void Complete(Bitmap bitmap)
            {
                lock (gate)
                {
                    if (discarded)
                    {
                        if (bitmap != null) bitmap.Dispose();
                    }
                    else result = bitmap;
                    Completed = true;
                    if (discarded) DisposeCancellation();
                }
            }
            public Bitmap TakeResult()
            {
                lock (gate)
                {
                    Bitmap bitmap = result;
                    result = null;
                    return bitmap;
                }
            }
            public void Discard()
            {
                lock (gate)
                {
                    discarded = true;
                    if (!cancellationDisposed) cancellation.Cancel();
                    if (result != null) { result.Dispose(); result = null; }
                    if (Completed) DisposeCancellation();
                }
            }
            private void DisposeCancellation()
            {
                if (cancellationDisposed) return;
                cancellationDisposed = true;
                cancellation.Dispose();
            }
        }

        private sealed class BoardImage : IDisposable
        {
            public readonly long Id;
            public readonly Bitmap Pixels;
            public readonly bool IsOpaque;
            public RectangleF Bounds;
            private Bitmap preview;
            private bool previewHighQuality;
            private readonly object pixelGate = new object();
            private readonly object lifetimeGate = new object();
            private int pixelReferences = 1;
            private bool ownerDisposed;
            public BoardImage(Bitmap pixels, RectangleF bounds, long id, bool knownOpaque)
            {
                Pixels = pixels; Bounds = bounds; Id = id;
                try { IsOpaque = knownOpaque || HasOnlyOpaquePixels(pixels); }
                catch { pixels.Dispose(); throw; }
            }
            private static bool HasOnlyOpaquePixels(Bitmap pixels)
            {
                BitmapData data = pixels.LockBits(new Rectangle(Point.Empty, pixels.Size),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int[] row = new int[pixels.Width];
                    for (int y = 0; y < pixels.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < row.Length; x++)
                            if ((row[x] & unchecked((int)0xff000000)) != unchecked((int)0xff000000))
                                return false;
                    }
                    return true;
                }
                finally { pixels.UnlockBits(data); }
            }
            public void Draw(Graphics graphics)
            {
                lock (pixelGate) DrawBitmap(graphics, Pixels, Bounds);
            }
            public BoardSnapshotImage RetainSnapshot()
            {
                lock (lifetimeGate)
                {
                    if (ownerDisposed) throw new ObjectDisposedException("BoardImage");
                    pixelReferences++;
                }
                return new BoardSnapshotImage {
                    Pixels = Pixels, Bounds = Bounds, IsOpaque = IsOpaque,
                    SyncRoot = pixelGate, Release = ReleasePixels
                };
            }
            private void ReleasePixels()
            {
                bool release;
                lock (lifetimeGate) release = --pixelReferences == 0;
                if (release) lock (pixelGate) Pixels.Dispose();
            }
            public void DrawPreview(Graphics graphics, float scale, RectangleF destination,
                RectangleF visible, bool interactive)
            {
                CompositingMode previousMode = graphics.CompositingMode;
                CompositingQuality previousQuality = graphics.CompositingQuality;
                if (IsOpaque) graphics.CompositingMode = CompositingMode.SourceCopy;
                try
                {
                    if (interactive)
                    {
                        InterpolationMode previous = graphics.InterpolationMode;
                        PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
                        // Bilinear GDI+ stretching can dominate a wheel frame even
                        // with a tiny source. Show the existing thumbnail immediately;
                        // the idle refinement restores high-quality source sampling.
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        if (IsOpaque) graphics.CompositingQuality = CompositingQuality.HighSpeed;
                        DrawRegionFast(graphics, GetInteractionPreview(scale), destination, visible);
                        graphics.InterpolationMode = previous;
                        graphics.PixelOffsetMode = previousOffset;
                    }
                    else if (Bounds.Width * scale <= 1024 && Bounds.Height * scale <= 1024)
                        DrawRegion(graphics, GetPreview(scale), destination, visible);
                    else
                        lock (pixelGate) DrawRegion(graphics, Pixels, destination, visible);
                }
                finally
                {
                    graphics.CompositingMode = previousMode;
                    graphics.CompositingQuality = previousQuality;
                }
            }
            public Bitmap GetInteractionPreview(float scale)
            {
                // Warm previews are reused at every zoom/pan tick. A newly exposed
                // image gets one small bilinear thumbnail, not a full-size resample.
                if (preview != null) return preview;
                float quickScale = Math.Min(scale, 320f / Math.Max(Bounds.Width, Bounds.Height));
                return CreatePreview(PreviewSize(quickScale), false);
            }
            public Bitmap GetPreview(float scale)
            {
                Size size = PreviewSize(scale);
                if (preview != null && preview.Size == size && previewHighQuality) return preview;
                return CreatePreview(size, true);
            }
            private Size PreviewSize(float scale)
            {
                // A single image preview is at most 1024 x 1024, irrespective of
                // board height or zoom. Enlarged views sample only visible original
                // pixels directly into the client-sized viewport cache.
                float boundedScale = Math.Min(scale, 1024f / Math.Max(Bounds.Width, Bounds.Height));
                return new Size(Math.Max(1, (int)Math.Ceiling(Bounds.Width * boundedScale)),
                    Math.Max(1, (int)Math.Ceiling(Bounds.Height * boundedScale)));
            }
            private Bitmap CreatePreview(Size size, bool highQuality)
            {
                Bitmap next = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                try
                {
                    using (Graphics graphics = Graphics.FromImage(next))
                    {
                        ConfigureQuality(graphics);
                        if (!highQuality) graphics.InterpolationMode = InterpolationMode.Bilinear;
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        lock (pixelGate)
                            DrawBitmap(graphics, Pixels, new RectangleF(PointF.Empty, size));
                    }
                }
                catch { next.Dispose(); throw; }
                if (preview != null) preview.Dispose();
                preview = next;
                previewHighQuality = highQuality;
                return preview;
            }
            public static void DrawRegion(Graphics graphics, Bitmap bitmap, RectangleF bounds,
                RectangleF visible)
            {
                if (visible.Width <= 0 || visible.Height <= 0) return;
                RectangleF source = new RectangleF((visible.Left - bounds.Left) * bitmap.Width / bounds.Width,
                    (visible.Top - bounds.Top) * bitmap.Height / bounds.Height,
                    visible.Width * bitmap.Width / bounds.Width,
                    visible.Height * bitmap.Height / bounds.Height);
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    PointF[] destination = { new PointF(visible.Left, visible.Top),
                        new PointF(visible.Right, visible.Top), new PointF(visible.Left, visible.Bottom) };
                    graphics.DrawImage(bitmap, destination, source, GraphicsUnit.Pixel, attributes);
                }
            }
            private static void DrawRegionFast(Graphics graphics, Bitmap bitmap, RectangleF bounds,
                RectangleF visible)
            {
                if (visible.Width <= 0 || visible.Height <= 0) return;
                GraphicsState state = graphics.Save();
                try
                {
                    // Draw the whole mapping through the viewport clip, avoiding
                    // a source-crop rescale for every change of camera.
                    graphics.SetClip(visible, CombineMode.Intersect);
                    graphics.DrawImage(bitmap, bounds, new RectangleF(0, 0, bitmap.Width, bitmap.Height),
                        GraphicsUnit.Pixel);
                }
                finally { graphics.Restore(state); }
            }
            private static void DrawBitmap(Graphics graphics, Bitmap bitmap, RectangleF bounds)
            {
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    PointF[] destination = { new PointF(bounds.Left, bounds.Top),
                        new PointF(bounds.Right, bounds.Top), new PointF(bounds.Left, bounds.Bottom) };
                    graphics.DrawImage(bitmap, destination,
                        new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
                }
            }
            public void Dispose()
            {
                lock (lifetimeGate)
                {
                    if (ownerDisposed) return;
                    ownerDisposed = true;
                }
                if (preview != null) { preview.Dispose(); preview = null; }
                ReleasePixels();
            }
        }

        private abstract class BoardAnnotation
        {
            public abstract RectangleF Bounds { get; }
            public abstract void Draw(Graphics graphics);
        }

        private sealed class ShapeAnnotation : BoardAnnotation
        {
            private readonly DrawingTool tool;
            private readonly PointF from, to;
            private readonly Color color;
            private readonly float width;
            private readonly RectangleF bounds;
            public override RectangleF Bounds { get { return bounds; } }
            public ShapeAnnotation(DrawingTool tool, PointF from, PointF to, Color color, float width)
            {
                this.tool = tool; this.from = from; this.to = to; this.color = color; this.width = width;
                RectangleF extent = Normalize(from, to);
                if (tool == DrawingTool.Arrow)
                {
                    PointF left, right;
                    if (ArrowHead(from, to, width, out left, out right))
                    {
                        extent = RectangleF.Union(extent, Normalize(left, right));
                        extent = RectangleF.Union(extent, Normalize(left, to));
                    }
                }
                extent.Inflate(width / 2 + 2, width / 2 + 2);
                bounds = extent;
            }
            public override void Draw(Graphics graphics)
            {
                using (Pen pen = MakePen(color, width))
                {
                    if (tool == DrawingTool.Rectangle)
                    {
                        RectangleF rectangle = Normalize(from, to);
                        if (rectangle.Width > 0 && rectangle.Height > 0)
                            graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                        return;
                    }
                    if (tool != DrawingTool.Arrow) return;
                    float dx = to.X - from.X, dy = to.Y - from.Y;
                    float length = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (length < .1f) return;
                    graphics.DrawLine(pen, from, to);
                    PointF left, right;
                    ArrowHead(from, to, width, out left, out right);
                    using (Brush brush = new SolidBrush(color))
                        graphics.FillPolygon(brush, new PointF[] { to, left, right });
                }
            }
        }

        private sealed class PenAnnotation : BoardAnnotation
        {
            private readonly PointF[] points;
            private readonly Color color;
            private readonly float width;
            private readonly RectangleF bounds;
            public override RectangleF Bounds { get { return bounds; } }
            public PenAnnotation(PointF[] points, Color color, float width)
            {
                this.points = points; this.color = color; this.width = width;
                RectangleF extent = RectangleF.Empty;
                if (points.Length > 0)
                {
                    float left = points[0].X, right = left, top = points[0].Y, bottom = top;
                    foreach (PointF point in points)
                    {
                        left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                        top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
                    }
                    extent = RectangleF.FromLTRB(left, top, right, bottom);
                    extent.Inflate(width / 2 + 2, width / 2 + 2);
                }
                bounds = extent;
            }
            public override void Draw(Graphics graphics)
            {
                if (points.Length == 0) return;
                using (Pen pen = MakePen(color, width))
                    SmoothStroke.Draw(graphics, pen, points);
            }
        }

        private sealed class TextAnnotation : BoardAnnotation
        {
            private readonly PointF position;
            private readonly string text;
            private readonly Color color;
            private readonly float size;
            private readonly RectangleF bounds;
            public override RectangleF Bounds { get { return bounds; } }
            public TextAnnotation(PointF position, string text, Color color, float size)
            {
                this.position = position; this.text = text; this.color = color; this.size = size;
                using (Bitmap measure = new Bitmap(1, 1))
                using (Graphics graphics = Graphics.FromImage(measure))
                using (Font font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    ConfigureQuality(graphics);
                    SizeF measured = graphics.MeasureString(text, font, PointF.Empty, format);
                    RectangleF extent = new RectangleF(position, measured);
                    extent.Inflate(2, 2);
                    bounds = extent;
                }
            }
            public override void Draw(Graphics graphics)
            {
                using (Font font = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush brush = new SolidBrush(color))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                    graphics.DrawString(text, font, brush, position, format);
            }
        }

        private abstract class BoardChange : IDisposable
        {
            public abstract void Undo(BoardCanvas board);
            public virtual void Dispose() { }
        }

        private sealed class AddImageChange : BoardChange
        {
            private readonly BoardImage image;
            private readonly BoardImage previousSelection;
            public AddImageChange(BoardImage image, BoardImage previousSelection)
            { this.image = image; this.previousSelection = previousSelection; }
            public override void Undo(BoardCanvas board)
            {
                board._images.Remove(image);
                board.SetSelection(previousSelection);
                image.Dispose();
            }
        }

        private sealed class TransformImageChange : BoardChange
        {
            private readonly BoardImage image;
            private readonly RectangleF before;
            public TransformImageChange(BoardImage image, RectangleF before)
            { this.image = image; this.before = before; }
            public override void Undo(BoardCanvas board)
            {
                image.Bounds = before;
                board.SetSelection(image);
            }
        }

        private sealed class DeleteImageChange : BoardChange
        {
            private BoardImage image;
            private readonly int index;
            public DeleteImageChange(BoardImage image, int index) { this.image = image; this.index = index; }
            public override void Undo(BoardCanvas board)
            {
                BoardImage restored = image;
                image = null;
                board._images.Insert(Math.Min(index, board._images.Count), restored);
                board.SetSelection(restored);
            }
            public override void Dispose()
            {
                if (image != null) { image.Dispose(); image = null; }
            }
        }

        private sealed class AnnotationChange : BoardChange
        {
            private readonly BoardAnnotation annotation;
            public AnnotationChange(BoardAnnotation annotation) { this.annotation = annotation; }
            public override void Undo(BoardCanvas board) { board._annotations.Remove(annotation); }
        }
    }
}
