using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QingJie
{
    /// <summary>
    /// Edits a frozen region at its original screen location. The image stays fully
    /// opaque; only the separately owned toolbar uses window opacity.
    /// </summary>
    public sealed class QuickCaptureForm : Form
    {
        private readonly AnnotationCanvas _canvas;
        private readonly CaptureToolbarForm _toolbar;
        private readonly Dictionary<DrawingTool, Button> _tools =
            new Dictionary<DrawingTool, Button>();
        private readonly Button _undo;
        private readonly Button _redo;
        private readonly CaptureToolbarButton _scrollCapture;
        private readonly ContextMenuStrip _widthMenu;
        private int _revision;
        private int _acceptedRevision = -1;
        private bool _closing;
        private readonly Bitmap _desktopSnapshot;
        private readonly bool _ownsDesktopSnapshot;
        private readonly Rectangle _desktopBounds;
        private readonly SelectionFrame _selectionFrame;
        private Rectangle _selectionBounds;
        private Rectangle _resizeStartBounds;
        private Point _resizeStartPointer;
        private SelectionEdges _resizeEdges;
        private Bitmap _resizeComposite;
        private bool _resizing;
        private readonly Timer _resizeTimer;
        private Rectangle _pendingResizeBounds;
        private bool _resizeBoundsPending;
        private bool _resizePreviewStarted;
        private bool _applyingResizeBounds;

        // Each event transfers a fresh independent bitmap to its subscriber.
        public event Action<Bitmap> ImageAccepted;
        public event Action<Bitmap, Point> PinRequested;
        public event Action<Bitmap> EditRequested;
        /// <summary>Raised after this window closes, with the selected physical screen rectangle.</summary>
        public event Action<Rectangle> ScrollRequested;
        internal bool ContinuingWithScroll { get; private set; }

        public AnnotationCanvas Canvas { get { return _canvas; } }
        public Rectangle ToolbarBounds { get { return _toolbar.Bounds; } }
        public Rectangle SelectionBounds { get { return _selectionBounds; } }
        public Rectangle SnapshotBounds { get { return _desktopBounds; } }

        public QuickCaptureForm(Bitmap image, Rectangle screenBounds)
            : this(image, screenBounds, image, screenBounds) { }

        public QuickCaptureForm(Bitmap image, Rectangle screenBounds,
            Bitmap desktopSnapshot, Rectangle desktopBounds)
            : this(image, screenBounds, desktopSnapshot, desktopBounds, false) { }

        // The capture coordinator keeps the original frozen desktop alive until this
        // window closes. Borrowing avoids copying an entire multi-monitor desktop.
        internal QuickCaptureForm(Bitmap image, Rectangle screenBounds,
            Bitmap desktopSnapshot, Rectangle desktopBounds, bool borrowDesktopSnapshot)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (desktopSnapshot == null) throw new ArgumentNullException("desktopSnapshot");
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
                throw new ArgumentOutOfRangeException("screenBounds");
            if (desktopBounds.Size != desktopSnapshot.Size || !desktopBounds.Contains(screenBounds) ||
                screenBounds.Size != image.Size)
                throw new ArgumentException("截图尺寸或选区与原始屏幕范围不匹配。");
            _ownsDesktopSnapshot = !borrowDesktopSnapshot;
            _desktopSnapshot = borrowDesktopSnapshot ? desktopSnapshot : CaptureImageActions.Clone(desktopSnapshot);
            _desktopBounds = desktopBounds;
            _selectionBounds = screenBounds;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "轻截 · 快速标注";
            BackColor = Color.White;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Bounds = screenBounds;
            _canvas = new AnnotationCanvas { Dock = DockStyle.Fill, CompactMode = true,
                BackColor = Color.White, Tool = DrawingTool.Select };
            _canvas.SetImage(image);
            _canvas.ContentChanged += delegate { _revision++; UpdateActions(); };
            _canvas.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (_canvas.Tool == DrawingTool.Select && e.Button == MouseButtons.Left)
                    BeginSelectionResize(SelectionEdges.Move, _canvas.PointToScreen(e.Location));
            };
            _canvas.MouseEnter += delegate { UpdateCanvasCursor(); };
            _canvas.MouseMove += delegate { UpdateCanvasCursor(); };
            Controls.Add(_canvas);
            _selectionFrame = new SelectionFrame(this) { Dock = DockStyle.Fill };
            Controls.Add(_selectionFrame);
            _selectionFrame.BringToFront();
            _resizeTimer = new Timer { Interval = 8 };
            _resizeTimer.Tick += delegate { ApplyPendingResizeBounds(); };

            _toolbar = new CaptureToolbarForm("截图标注工具");
            _toolbar.Shortcut = HandleShortcut;
            AddTool("调整 · 拖动内部移动选区，拖动边缘调整大小", DrawingTool.Select);
            _scrollCapture = _toolbar.AddButton("滚动长截图 · 向下滚动，Ctrl+Enter 完成", RequestScrollCapture);
            AddTool("画笔", DrawingTool.Pen);
            AddTool("箭头", DrawingTool.Arrow);
            AddTool("矩形", DrawingTool.Rectangle);
            AddTool("马赛克", DrawingTool.Mosaic);
            AddTool("文字", DrawingTool.Text);
            _toolbar.StartGroup();
            CaptureToolbarButton color = null;
            color = _toolbar.AddButton("颜色", delegate
            {
                using (ColorDialog picker = new ColorDialog { Color = _canvas.StrokeColor,
                    FullOpen = true })
                    if (picker.ShowDialog(this) == DialogResult.OK)
                    {
                        _canvas.StrokeColor = picker.Color;
                        color.AccentColor = picker.Color;
                        color.Invalidate();
                    }
            });
            color.AccentColor = _canvas.StrokeColor;
            color.AccessibleDescription = "选择标注颜色";
            _widthMenu = new ContextMenuStrip { Font = UiTheme.Font(9f) };
            Button width = null;
            width = _toolbar.AddButton("画笔粗细 · 3 px", delegate
            {
                Control anchor = width.Visible ? (Control)width : _toolbar;
                _widthMenu.Show(anchor, new Point(0, anchor.Height));
            });
            width.AccessibleName = "画笔粗细";
            foreach (int value in new int[] { 1, 2, 3, 5, 8, 12, 20, 32 })
            {
                int pixels = value;
                _widthMenu.Items.Add(pixels + " px", null, delegate
                {
                    _canvas.StrokeWidth = pixels;
                    width.Text = "画笔粗细 · " + pixels + " px";
                });
            }
            _toolbar.StartGroup();
            _undo = _toolbar.AddButton("撤回 · Ctrl+Z", delegate { _canvas.Undo(); });
            _redo = _toolbar.AddButton("还原 · Ctrl+Y", delegate { _canvas.Redo(); });
            _toolbar.AddButton("清除", delegate { _canvas.ResetAnnotations(); });
            _toolbar.StartGroup();
            _toolbar.AddButton("复制 · Ctrl+C / Enter", CopyToClipboard);
            _toolbar.AddButton("保存 · Ctrl+S", SaveImage);
            _toolbar.AddButton("贴屏", PinCurrentImage);
            _toolbar.AddButton("编辑器", SendToEditor);
            _toolbar.StartGroup();
            _toolbar.AddButton("退出 · Esc", Close);
            UpdateActions();
        }

        private void AddTool(string label, DrawingTool tool)
        {
            Button button = _toolbar.AddButton(label, delegate
            {
                _canvas.Tool = tool;
                UpdateActions();
                _canvas.Focus();
            });
            _tools.Add(tool, button);
        }

        private void UpdateActions()
        {
            if (_closing || IsDisposed || Disposing) return;
            if (_undo != null) _undo.Enabled = _canvas.CanUndo;
            if (_redo != null) _redo.Enabled = _canvas.CanRedo;
            if (_scrollCapture != null)
            {
                bool annotated = _canvas.CanUndo || _canvas.CanRedo;
                _scrollCapture.Enabled = !annotated;
                _scrollCapture.Text = annotated
                    ? "滚动长截图 · 请先清除标注与还原记录"
                    : "滚动长截图 · 向下滚动，Ctrl+Enter 完成";
                _scrollCapture.AccessibleDescription = annotated
                    ? "为保留当前编辑，请先使用清除按钮移除全部标注和还原记录。"
                    : "以当前选区继续滚动长截图；在原页面向下滚动，完成后可编辑并保存到历史图片。";
            }
            foreach (KeyValuePair<DrawingTool, Button> tool in _tools)
                tool.Value.BackColor = tool.Key == _canvas.Tool
                    ? Color.FromArgb(49, 116, 220) : CaptureToolbarForm.ButtonColor;
            UpdateCanvasCursor();
            if (_selectionFrame != null)
            {
                _selectionFrame.Visible = _canvas.Tool == DrawingTool.Select;
                if (_selectionFrame.Visible) _selectionFrame.BringToFront();
            }
        }

        private void UpdateCanvasCursor()
        {
            if (_closing || _canvas == null || _canvas.IsDisposed) return;
            if (_canvas.Tool == DrawingTool.Select && _canvas.Cursor != Cursors.SizeAll)
                _canvas.Cursor = Cursors.SizeAll;
        }

        private void RequestScrollCapture()
        {
            Action<Rectangle> handler = ScrollRequested;
            if (handler == null || _closing || IsDisposed || Disposing || !Enabled ||
                _canvas.CanUndo || _canvas.CanRedo) return;
            if (_resizing) FinishSelectionResize(false);
            if (_canvas.CanUndo || _canvas.CanRedo) return;
            Rectangle selection = _selectionBounds;
            // The original overlay and borrowed desktop are owned by the quick
            // session. Finish closing it before the host starts a live session.
            ContinuingWithScroll = true;
            Close();
            if (IsDisposed) handler(selection);
            else ContinuingWithScroll = false;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PositionToolbar();
            _toolbar.Show(this);
            _canvas.Focus();
            UpdateCanvasCursor();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            CaptureWindowPlacement.SetMinimumTrackSize(ref message, new Size(2, 2));
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_selectionBounds.Width > 0 && _selectionBounds.Height > 0)
                SetBoundsCore(_selectionBounds.X, _selectionBounds.Y,
                    _selectionBounds.Width, _selectionBounds.Height, BoundsSpecified.All);
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

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_toolbar != null && !_applyingResizeBounds) PositionToolbar();
        }

        private void PositionToolbar()
        {
            if (_closing || IsDisposed || Disposing || _toolbar == null ||
                _toolbar.IsDisposed || _toolbar.Disposing) return;
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            if (!_toolbar.IsHandleCreated)
                _toolbar.Location = CaptureWindowPlacement.Beside(Bounds, _toolbar.Size, work);
            _toolbar.FitWithin(work);
            _toolbar.Location = CaptureWindowPlacement.Beside(Bounds, _toolbar.Size, work);
        }

        /// <summary>Re-crops original screen pixels; this does not scale the screenshot.</summary>
        public void SetSelectionBounds(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new ArgumentOutOfRangeException("bounds");
            Rectangle next = ClampSelection(bounds, _desktopBounds);
            if (next == _selectionBounds) return;
            Rectangle source = new Rectangle(next.X - _desktopBounds.X,
                next.Y - _desktopBounds.Y, next.Width, next.Height);
            using (Bitmap crop = _desktopSnapshot.Clone(source, PixelFormat.Format32bppArgb))
            {
                PointF offset = new PointF(_selectionBounds.X - next.X, _selectionBounds.Y - next.Y);
                _selectionBounds = next;
                Bounds = next;
                _canvas.ReplaceCaptureImage(crop, offset);
            }
            PositionToolbar();
            Invalidate();
        }

        internal static Rectangle ClampSelection(Rectangle bounds, Rectangle desktop)
        {
            int minimumWidth = Math.Min(2, desktop.Width), minimumHeight = Math.Min(2, desktop.Height);
            int left = Math.Max(desktop.Left, Math.Min(bounds.Left, desktop.Right - minimumWidth));
            int top = Math.Max(desktop.Top, Math.Min(bounds.Top, desktop.Bottom - minimumHeight));
            int right = (int)Math.Max((long)left + minimumWidth,
                Math.Min((long)desktop.Right, (long)bounds.Left + bounds.Width));
            int bottom = (int)Math.Max((long)top + minimumHeight,
                Math.Min((long)desktop.Bottom, (long)bounds.Top + bounds.Height));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        [Flags]
        private enum SelectionEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

        private void BeginSelectionResize(SelectionEdges edges, Point screenPoint)
        {
            if (_resizing || _closing || _canvas.Tool != DrawingTool.Select || edges == SelectionEdges.None) return;
            _resizeStartBounds = _selectionBounds;
            _resizeStartPointer = screenPoint;
            _resizeEdges = edges;
            _resizeBoundsPending = false;
            _resizePreviewStarted = false;
            // A click alone must not copy a large screenshot. Start the preview
            // only when a move changes the rectangle, and copy only annotations.
            _resizing = true;
            _selectionFrame.Cursor = edges == SelectionEdges.Move ? Cursors.SizeAll : _selectionFrame.Cursor;
            _selectionFrame.Capture = true;
        }

        private void UpdateSelectionResize(Point screenPoint)
        {
            if (!_resizing) return;
            int dx = screenPoint.X - _resizeStartPointer.X;
            int dy = screenPoint.Y - _resizeStartPointer.Y;
            int left = _resizeStartBounds.Left, top = _resizeStartBounds.Top;
            int right = _resizeStartBounds.Right, bottom = _resizeStartBounds.Bottom;
            if (_resizeEdges == SelectionEdges.Move)
            {
                left = Math.Max(_desktopBounds.Left,
                    Math.Min(_desktopBounds.Right - _resizeStartBounds.Width, left + dx));
                top = Math.Max(_desktopBounds.Top,
                    Math.Min(_desktopBounds.Bottom - _resizeStartBounds.Height, top + dy));
                right = left + _resizeStartBounds.Width;
                bottom = top + _resizeStartBounds.Height;
            }
            else
            {
                if ((_resizeEdges & SelectionEdges.Left) != 0)
                    left = Math.Max(_desktopBounds.Left, Math.Min(right - 2, left + dx));
                if ((_resizeEdges & SelectionEdges.Right) != 0)
                    right = Math.Min(_desktopBounds.Right, Math.Max(left + 2, right + dx));
                if ((_resizeEdges & SelectionEdges.Top) != 0)
                    top = Math.Max(_desktopBounds.Top, Math.Min(bottom - 2, top + dy));
                if ((_resizeEdges & SelectionEdges.Bottom) != 0)
                    bottom = Math.Min(_desktopBounds.Bottom, Math.Max(top + 2, bottom + dy));
            }
            Rectangle next = Rectangle.FromLTRB(left, top, right, bottom);
            if ((_resizeBoundsPending ? _pendingResizeBounds : Bounds) == next) return;
            if (!_resizePreviewStarted)
            {
                if (_canvas.CanUndo) _resizeComposite = _canvas.ExportImage();
                _resizePreviewStarted = true;
                _canvas.Visible = false;
                _resizeTimer.Start();
            }
            // Native SetBounds rebuilds the window surface and resizes its docked
            // children. High-frequency mouse events therefore only replace this
            // pending rectangle; one UI timer tick applies the newest rectangle.
            _pendingResizeBounds = next;
            _resizeBoundsPending = true;
        }

        private void ApplyPendingResizeBounds()
        {
            if (_closing || IsDisposed || Disposing) return;
            if (!_resizing || !_resizeBoundsPending) return;
            Rectangle next = _pendingResizeBounds;
            _resizeBoundsPending = false;
            if (Bounds == next) return;
            _applyingResizeBounds = true;
            try { Bounds = next; }
            finally { _applyingResizeBounds = false; }
            // Right/bottom resizing does not raise LocationChanged, but the
            // toolbar must still follow the selection after each merged frame.
            PositionToolbar();
            Invalidate();
        }

        private void FinishSelectionResize(bool cancel)
        {
            if (!_resizing) return;
            _resizeTimer.Stop();
            if (!cancel) ApplyPendingResizeBounds();
            _resizeBoundsPending = false;
            Rectangle candidate = Bounds;
            _resizing = false;
            _selectionFrame.Capture = false;
            try
            {
                if (cancel) Bounds = _resizeStartBounds;
                else SetSelectionBounds(candidate);
            }
            finally
            {
                if (_resizeComposite != null) { _resizeComposite.Dispose(); _resizeComposite = null; }
                _resizePreviewStarted = false;
                _canvas.Visible = true;
                UpdateActions();
                PositionToolbar();
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_desktopSnapshot == null) return;
            PaintFrozenSelection(e.Graphics, e.ClipRectangle);
        }

        private void PaintFrozenSelection(Graphics graphics, Rectangle clip)
        {
            clip.Intersect(ClientRectangle);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            if ((!_resizing || !_resizePreviewStarted) && _canvas != null && _canvas.ImageSize == ClientSize)
            {
                _canvas.PaintViewportRegion(graphics, clip);
                return;
            }
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            Rectangle source = new Rectangle(Left - _desktopBounds.Left + clip.Left,
                Top - _desktopBounds.Top + clip.Top, clip.Width, clip.Height);
            graphics.DrawImage(_desktopSnapshot, clip, source, GraphicsUnit.Pixel);
            if (_resizing && _resizeComposite != null)
            {
                Rectangle original = new Rectangle(_resizeStartBounds.Left - Left,
                    _resizeStartBounds.Top - Top, _resizeComposite.Width, _resizeComposite.Height);
                Rectangle visible = Rectangle.Intersect(clip, original);
                if (visible.Width > 0 && visible.Height > 0)
                    graphics.DrawImage(_resizeComposite, visible,
                        new Rectangle(visible.X - original.X, visible.Y - original.Y,
                            visible.Width, visible.Height), GraphicsUnit.Pixel);
            }
        }

        /// <summary>Publishes at most once per edit revision; no clipboard or file side effects.</summary>
        public void AcceptCurrentImage()
        {
            if (_resizing) FinishSelectionResize(false);
            Action<Bitmap> handler = ImageAccepted;
            if (_acceptedRevision == _revision || handler == null) return;
            Bitmap copy = _canvas.ExportImage();
            try { handler(copy); }
            catch { copy.Dispose(); throw; }
            _acceptedRevision = _revision;
        }

        public void PinCurrentImage()
        {
            if (_closing) return;
            RunAction(delegate
            {
                AcceptCurrentImage();
                Action<Bitmap, Point> handler = PinRequested;
                using (Bitmap result = _canvas.ExportImage())
                {
                    if (handler != null)
                    {
                        Bitmap transfer = CaptureImageActions.Clone(result);
                        try { handler(transfer, Location); }
                        catch { transfer.Dispose(); throw; }
                    }
                    else
                    {
                        PinnedImageForm pinned = new PinnedImageForm(result, Location);
                        pinned.Show();
                    }
                }
                Close();
            });
        }

        public void SendToEditor()
        {
            if (_closing) return;
            RunAction(delegate
            {
                Action<Bitmap> handler = EditRequested;
                if (handler == null) return;
                AcceptCurrentImage();
                Bitmap result = _canvas.ExportImage();
                try { handler(result); }
                catch { result.Dispose(); throw; }
                Close();
            });
        }

        public void CopyToClipboard()
        {
            RunAction(delegate
            {
                using (Bitmap result = _canvas.ExportImage()) CaptureImageActions.Copy(result);
                AcceptCurrentImage();
                Close();
            });
        }

        public void SaveImage()
        {
            RunAction(delegate
            {
                using (Bitmap result = _canvas.ExportImage())
                    if (!CaptureImageActions.Save(this, result)) return;
                AcceptCurrentImage();
            });
        }

        private void RunAction(Action action)
        {
            if (_closing) return;
            try
            {
                if (_resizing) FinishSelectionResize(false);
                action();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "轻截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool HandleShortcut(Keys keys)
        {
            if (keys == Keys.Escape && _resizing) { FinishSelectionResize(true); return true; }
            if (_resizing && (keys == (Keys.Control | Keys.Z) || keys == (Keys.Control | Keys.Y) ||
                keys == (Keys.Control | Keys.Shift | Keys.Z)))
                FinishSelectionResize(false);
            if (keys == Keys.Escape) { Close(); return true; }
            if (keys == (Keys.Control | Keys.C) || keys == Keys.Enter)
            { CopyToClipboard(); return true; }
            if (keys == (Keys.Control | Keys.S)) { SaveImage(); return true; }
            if (keys == (Keys.Control | Keys.Z)) { _canvas.Undo(); return true; }
            if (keys == (Keys.Control | Keys.Y) || keys == (Keys.Control | Keys.Shift | Keys.Z))
            { _canvas.Redo(); return true; }
            return false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            if (_toolbar != null && !_toolbar.IsDisposed) _toolbar.Close();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Child disposal and owner callbacks can synchronously raise layout
                // events. Mark closing before releasing any dependent window.
                _closing = true;
                _resizing = false;
                _resizeBoundsPending = false;
                if (_toolbar != null) _toolbar.Dispose();
                if (_resizeTimer != null) _resizeTimer.Dispose();
                if (_widthMenu != null) _widthMenu.Dispose();
                if (_resizeComposite != null) { _resizeComposite.Dispose(); _resizeComposite = null; }
                if (_ownsDesktopSnapshot && _desktopSnapshot != null) _desktopSnapshot.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class SelectionFrame : Control
        {
            private readonly QuickCaptureForm _owner;
            private SelectionEdges _hoverEdges;
            internal SelectionFrame(QuickCaptureForm owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AccessibleName = "拖动边缘或角点调整选区，拖动内部移动选区";
                TabStop = false;
            }
            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                Region next = new Region(ClientRectangle);
                if (Width > 16 && Height > 16)
                    next.Exclude(new Rectangle(8, 8, Width - 16, Height - 16));
                Region previous = Region;
                Region = next;
                if (previous != null) previous.Dispose();
            }
            private SelectionEdges HitEdges(Point point)
            {
                SelectionEdges edges = SelectionEdges.None;
                if (point.X < 8) edges |= SelectionEdges.Left;
                else if (point.X >= Width - 8) edges |= SelectionEdges.Right;
                if (point.Y < 8) edges |= SelectionEdges.Top;
                else if (point.Y >= Height - 8) edges |= SelectionEdges.Bottom;
                return edges;
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                    _owner.BeginSelectionResize(HitEdges(e.Location), PointToScreen(e.Location));
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_owner._resizing) { _owner.UpdateSelectionResize(PointToScreen(e.Location)); return; }
                SelectionEdges edges = HitEdges(e.Location);
                if (_hoverEdges != edges) { _hoverEdges = edges; Invalidate(); }
                if (edges == (SelectionEdges.Left | SelectionEdges.Top) ||
                    edges == (SelectionEdges.Right | SelectionEdges.Bottom)) Cursor = Cursors.SizeNWSE;
                else if (edges == (SelectionEdges.Right | SelectionEdges.Top) ||
                    edges == (SelectionEdges.Left | SelectionEdges.Bottom)) Cursor = Cursors.SizeNESW;
                else if ((edges & (SelectionEdges.Left | SelectionEdges.Right)) != 0) Cursor = Cursors.SizeWE;
                else Cursor = Cursors.SizeNS;
            }
            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_owner._resizing || _hoverEdges == SelectionEdges.None) return;
                _hoverEdges = SelectionEdges.None;
                Invalidate();
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left)
                {
                    _owner.UpdateSelectionResize(PointToScreen(e.Location));
                    _owner.FinishSelectionResize(false);
                }
            }
            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (!Capture && _owner._resizing) _owner.FinishSelectionResize(true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                _owner.PaintFrozenSelection(e.Graphics, e.ClipRectangle);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                SelectionEdges emphasized = _owner._resizing ? _owner._resizeEdges : _hoverEdges;
                using (Pen line = new Pen(Color.FromArgb(57, 132, 242), 1))
                    e.Graphics.DrawRectangle(line, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
                using (Pen active = new Pen(Color.FromArgb(101, 190, 255), 3))
                {
                    if ((emphasized & SelectionEdges.Left) != 0) e.Graphics.DrawLine(active, 1, 1, 1, Height - 2);
                    if ((emphasized & SelectionEdges.Right) != 0) e.Graphics.DrawLine(active, Width - 2, 1, Width - 2, Height - 2);
                    if ((emphasized & SelectionEdges.Top) != 0) e.Graphics.DrawLine(active, 1, 1, Width - 2, 1);
                    if ((emphasized & SelectionEdges.Bottom) != 0) e.Graphics.DrawLine(active, 1, Height - 2, Width - 2, Height - 2);
                }
                int[] xs = { 1, Width / 2 - 3, Math.Max(1, Width - 7) };
                int[] ys = { 1, Height / 2 - 3, Math.Max(1, Height - 7) };
                using (Pen outline = new Pen(Color.FromArgb(40, 111, 221)))
                    for (int y = 0; y < 3; y++)
                        for (int x = 0; x < 3; x++)
                        {
                            if (x == 1 && y == 1) continue;
                            Rectangle handle = new Rectangle(xs[x], ys[y], 5, 5);
                            SelectionEdges edges = (x == 0 ? SelectionEdges.Left : x == 2 ? SelectionEdges.Right : 0) |
                                (y == 0 ? SelectionEdges.Top : y == 2 ? SelectionEdges.Bottom : 0);
                            e.Graphics.FillRectangle((emphasized & edges) == edges ? Brushes.LightSkyBlue : Brushes.White, handle);
                            e.Graphics.DrawRectangle(outline, handle);
                        }
            }
        }
    }

    internal static class CaptureWindowPlacement
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
            int width, int height, uint flags);

        internal static void SetPhysicalBounds(IntPtr window, int x, int y, int width, int height)
        {
            // Form.SetBoundsCore adds a separate primary-monitor maximum even when
            // WM_GETMINMAXINFO allows larger sizes. WM_WINDOWPOSCHANGED synchronizes
            // the requested native rectangle back into the control's managed Bounds.
            if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010))
                throw new System.ComponentModel.Win32Exception();
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Point Reserved;
            public Point MaximumSize;
            public Point MaximumPosition;
            public Point MinimumTrackSize;
            public Point MaximumTrackSize;
        }

        internal static void SetMinimumTrackSize(ref Message message, Size size)
        {
            if (message.Msg != 0x24 || message.LParam == IntPtr.Zero) return;
            MinMaxInfo info = (MinMaxInfo)Marshal.PtrToStructure(message.LParam, typeof(MinMaxInfo));
            info.MinimumTrackSize = new Point(size.Width, size.Height);
            // Screen-spanning selections and enlarged pins can exceed one monitor.
            // Windows otherwise silently limits SetBounds to monitor width + chrome.
            info.MaximumTrackSize = new Point(32767, 32767);
            Marshal.StructureToPtr(info, message.LParam, false);
        }

        internal static Point Beside(Rectangle anchor, Size size, Rectangle work)
        {
            int y = anchor.Bottom + 8;
            if ((long)y + size.Height > work.Bottom) y = anchor.Top - size.Height - 8;
            return Clamp(new Point(anchor.Right - size.Width, y), size, work);
        }

        internal static Point Clamp(Point location, Size size, Rectangle work)
        {
            return new Point(Math.Max(work.Left, Math.Min(location.X, work.Right - size.Width)),
                Math.Max(work.Top, Math.Min(location.Y, work.Bottom - size.Height)));
        }
    }

    /// <summary>A small owned window, so opacity never affects screenshot pixels.</summary>
    internal sealed class CaptureToolbarForm : Form
    {
        internal static readonly Color ButtonColor = Color.FromArgb(43, 53, 69);
        private readonly Panel _flow;
        private readonly List<CaptureToolbarButton> _buttons = new List<CaptureToolbarButton>();
        private readonly CaptureToolbarButton _overflowButton;
        private readonly ContextMenuStrip _overflowMenu;
        private readonly ToolTip _tips;
        private int _shownCount;
        private int _fittedWidth;
        private int _fittedControls;
        private float _fittedScale;
        private bool _nextGroup;
        private CaptureToolbarButton _disabledTipButton;
        internal Func<Keys, bool> Shortcut;

        internal CaptureToolbarForm(string name)
        {
            Text = name;
            AccessibleName = name;
            Font = UiTheme.Font(9f);
            // Layout explicitly uses device pixels so even a very narrow monitor
            // has one row, with remaining commands reachable through "more".
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(29, 37, 51);
            Opacity = .94;
            ClientSize = new Size(100, 44);
            _flow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = BackColor };
            _flow.Paint += PaintGroupSeparators;
            Controls.Add(_flow);
            _tips = new ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 7000,
                ShowAlways = true };
            // Disabled icon buttons do not receive their own hover messages.
            // Resolve those from the parent so the reason remains discoverable.
            _flow.MouseMove += ShowDisabledButtonTip;
            _flow.MouseLeave += HideDisabledButtonTip;
            _overflowMenu = new ContextMenuStrip { Font = UiTheme.Font(9f) };
            _overflowButton = MakeButton("更多操作", ShowOverflow);
            _overflowButton.Visible = false;
            _flow.Controls.Add(_overflowButton);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ClassStyle |= 0x20000; return value; }
        }
        private CaptureToolbarButton MakeButton(string text, Action action)
        {
            CaptureToolbarButton button = new CaptureToolbarButton {
                Text = text, AccessibleName = text, Width = 34, Height = 34,
                Margin = Padding.Empty, BackColor = ButtonColor, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, TabStop = true };
            button.FlatAppearance.BorderSize = 0;
            button.Action = action;
            button.Click += delegate { button.ActivateAction(); };
            _tips.SetToolTip(button, text);
            button.TextChanged += delegate
            {
                button.AccessibleName = button.Text;
                _tips.SetToolTip(button, button.Text);
                button.Invalidate();
            };
            return button;
        }
        internal CaptureToolbarButton AddButton(string text, Action action)
        {
            CaptureToolbarButton button = MakeButton(text, action);
            button.StartsGroup = _nextGroup;
            _nextGroup = false;
            _buttons.Add(button);
            _flow.Controls.Add(button);
            return button;
        }
        internal void StartGroup() { _nextGroup = _buttons.Count != 0; }
        private void ShowDisabledButtonTip(object sender, MouseEventArgs e)
        {
            if (!Visible || IsDisposed || Disposing) return;
            CaptureToolbarButton target = null;
            for (int i = 0; i < _shownCount; i++)
                if (!_buttons[i].Enabled && _buttons[i].Bounds.Contains(e.Location))
                { target = _buttons[i]; break; }
            if (target == _disabledTipButton) return;
            _tips.Hide(_flow);
            _disabledTipButton = target;
            if (target != null)
                _tips.Show(target.Text, _flow, target.Left + target.Width / 2,
                    target.Bottom + 6, 7000);
        }
        private void HideDisabledButtonTip(object sender, EventArgs e)
        {
            _tips.Hide(_flow);
            _disabledTipButton = null;
        }
        private int GroupSpacing(int shown, int spacing)
        {
            int total = 0;
            for (int i = 1; i < shown; i++) if (_buttons[i].StartsGroup) total += spacing;
            return total;
        }
        private void PaintGroupSeparators(object sender, PaintEventArgs e)
        {
            using (Pen line = new Pen(Color.FromArgb(67, 82, 104)))
                for (int i = 1; i < _shownCount; i++)
                {
                    if (!_buttons[i].StartsGroup) continue;
                    int x = (_buttons[i - 1].Right + _buttons[i].Left) / 2;
                    e.Graphics.DrawLine(line, x, _buttons[i].Top + _buttons[i].Height / 4,
                        x, _buttons[i].Bottom - _buttons[i].Height / 4);
                }
        }
        private void ShowOverflow()
        {
            PopulateOverflowMenu();
            if (_overflowMenu.Items.Count != 0)
                _overflowMenu.Show(_overflowButton, new Point(0, _overflowButton.Height));
        }
        private void PopulateOverflowMenu()
        {
            _overflowMenu.Items.Clear();
            for (int index = _shownCount; index < _buttons.Count; index++)
            {
                CaptureToolbarButton button = _buttons[index];
                ToolStripMenuItem item = new ToolStripMenuItem(button.Text) {
                    Enabled = button.Enabled,
                    Checked = button.BackColor == Color.FromArgb(49, 116, 220) };
                item.Click += delegate { button.ActivateAction(); };
                _overflowMenu.Items.Add(item);
            }
        }
        internal void FitWithin(Rectangle work)
        {
            if (IsDisposed || Disposing) return;
            float scale;
            using (Graphics graphics = CreateGraphics()) scale = Math.Max(1, graphics.DpiX / 96f);
            FitWithin(work, scale);
        }
        // Explicit scale also supports offscreen layout tests without changing the
        // computer's DPI or displaying a window.
        internal void FitWithin(Rectangle work, float scale)
        {
            if (IsDisposed || Disposing) return;
            scale = Math.Max(1, Math.Min(4, scale));
            int available = Math.Max(1, work.Width - Math.Min(8, Math.Max(0, work.Width - 1)));
            // Moving a pinned image can generate hundreds of location events.
            // Reposition the toolbar without repeatedly laying out all its buttons.
            if (_fittedWidth == available && _fittedControls == _buttons.Count &&
                Math.Abs(_fittedScale - scale) < .01f) return;
            int padding = Math.Min((int)Math.Round(5 * scale), Math.Max(0, (available - 1) / 2));
            int gap = Math.Max(1, (int)Math.Round(2 * scale));
            int normal = Math.Max(1, (int)Math.Round(34 * scale));
            int minimum = Math.Max(1, (int)Math.Round(26 * scale));
            int inner = Math.Max(1, available - padding * 2);
            int count = Math.Max(1, _buttons.Count);
            int groupGap = Math.Max(1, (int)Math.Round(8 * scale));
            int side = Math.Min(normal, Math.Max(1,
                (inner - gap * (count - 1) - GroupSpacing(count, groupGap)) / count));
            bool overflow = side < minimum;
            int slots;
            if (overflow)
            {
                _shownCount = Math.Max(0, count - 1);
                while (_shownCount > 0 && (_shownCount + 1) * minimum + _shownCount * gap +
                    GroupSpacing(_shownCount, groupGap) > inner) _shownCount--;
                slots = _shownCount + 1;
                side = Math.Min(normal, Math.Max(1,
                    (inner - gap * (slots - 1) - GroupSpacing(_shownCount, groupGap)) / slots));
            }
            else { slots = count; _shownCount = _buttons.Count; }
            int width = Math.Min(available, padding * 2 + side * slots + gap * (slots - 1) +
                GroupSpacing(_shownCount, groupGap));
            int height = Math.Min(Math.Max(1, work.Height), side + padding * 2);
            int top = Math.Min(padding, Math.Max(0, height - side));
            int buttonHeight = Math.Min(side, Math.Max(1, height - top * 2));
            SuspendLayout();
            _flow.SuspendLayout();
            ClientSize = new Size(width, height);
            int left = padding;
            for (int index = 0; index < _buttons.Count; index++)
            {
                CaptureToolbarButton button = _buttons[index];
                button.Visible = index < _shownCount;
                if (index < _shownCount)
                {
                    if (index > 0 && button.StartsGroup) left += groupGap;
                    button.SetBounds(left, top, side, buttonHeight);
                    left += side + gap;
                }
            }
            _overflowButton.Visible = overflow;
            if (overflow)
                _overflowButton.SetBounds(left, top, side, buttonHeight);
            _flow.ResumeLayout(false);
            ResumeLayout(false);
            _flow.Invalidate();
            _fittedWidth = available;
            _fittedControls = _buttons.Count;
            _fittedScale = scale;
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            CaptureWindowPlacement.SetMinimumTrackSize(ref message, new Size(1, 1));
            if (message.Msg == 0x02E0 /* WM_DPICHANGED */ && !IsDisposed && !Disposing &&
                _flow != null && _overflowButton != null)
            {
                int dpi = (int)((long)message.WParam & 0xffff);
                _fittedScale = 0;
                Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
                FitWithin(work, Math.Max(1, dpi / 96f));
                Location = Owner != null && !Owner.IsDisposed
                    ? CaptureWindowPlacement.Beside(Owner.Bounds, Size, work)
                    : CaptureWindowPlacement.Clamp(Location, Size, work);
            }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.F4) && Owner != null)
            {
                Owner.Close();
                return true;
            }
            return (Shortcut != null && Shortcut(keyData)) || base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(Color.FromArgb(93, 110, 139)))
                e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_flow != null)
                {
                    _flow.MouseMove -= ShowDisabledButtonTip;
                    _flow.MouseLeave -= HideDisabledButtonTip;
                }
                if (_tips != null) _tips.Dispose();
                if (_overflowMenu != null) _overflowMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class CaptureToolbarButton : Button
    {
        private bool _hovered;
        private bool _pressed;
        internal Action Action;
        internal bool StartsGroup;
        internal Color AccentColor = Color.FromArgb(252, 89, 101);
        internal void ActivateAction()
        {
            if (!IsDisposed && !Disposing && Enabled && Action != null) Action();
        }
        internal CaptureToolbarButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e)
        { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)
        { _hovered = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e)
        { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color fill = BackColor;
            bool selected = fill != CaptureToolbarForm.ButtonColor;
            if (Enabled && _pressed) fill = Color.FromArgb(37, 92, 171);
            else if (Enabled && _hovered && fill == CaptureToolbarForm.ButtonColor)
                fill = Color.FromArgb(62, 77, 99);
            e.Graphics.Clear(Parent == null ? CaptureToolbarForm.ButtonColor : Parent.BackColor);
            float inset = Math.Max(1, Math.Min(Width, Height) / 34f);
            RectangleF surface = new RectangleF(inset, inset,
                Math.Max(1, Width - inset * 2), Math.Max(1, Height - inset * 2));
            using (GraphicsPath path = RoundedSurface(surface, Math.Min(6 * inset, surface.Height / 3)))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (Enabled && (selected || _hovered || Focused))
                    using (Pen outline = new Pen(selected || Focused ? Color.FromArgb(121, 183, 255) :
                        Color.FromArgb(93, 113, 140), inset)) e.Graphics.DrawPath(outline, path);
            }
            DrawIcon(e.Graphics);
        }
        private static GraphicsPath RoundedSurface(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        private void DrawIcon(Graphics graphics)
        {
            GraphicsState saved = graphics.Save();
            float side = Math.Min(Width, Height) * .62f;
            graphics.TranslateTransform((Width - side) / 2, (Height - side) / 2);
            graphics.ScaleTransform(side / 24, side / 24);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color color = Enabled ? ForeColor : Color.FromArgb(120, 137, 160);
            using (Pen pen = new Pen(color, 1.8f) { StartCap = LineCap.Round,
                EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (Brush brush = new SolidBrush(color))
            {
                string label = Text;
                if (label.StartsWith("调整"))
                {
                    graphics.DrawRectangle(pen, 4, 4, 16, 16);
                    foreach (Point p in new Point[] { new Point(2, 2), new Point(18, 2),
                        new Point(2, 18), new Point(18, 18) })
                        graphics.FillRectangle(brush, p.X, p.Y, 4, 4);
                }
                else if (label.StartsWith("滚动长截图"))
                {
                    graphics.DrawLines(pen, new PointF[] { new PointF(7, 3), new PointF(3, 3),
                        new PointF(3, 21), new PointF(7, 21) });
                    graphics.DrawLines(pen, new PointF[] { new PointF(17, 3), new PointF(21, 3),
                        new PointF(21, 21), new PointF(17, 21) });
                    graphics.DrawLine(pen, 12, 5, 12, 18);
                    graphics.DrawLines(pen, new PointF[] { new PointF(8, 14),
                        new PointF(12, 18), new PointF(16, 14) });
                }
                else if (label.StartsWith("画笔粗细"))
                {
                    graphics.DrawLine(pen, 3, 5, 21, 5);
                    pen.Width = 3;
                    graphics.DrawLine(pen, 3, 12, 21, 12);
                    pen.Width = 5;
                    graphics.DrawLine(pen, 3, 20, 21, 20);
                }
                else if (label.StartsWith("画笔"))
                {
                    graphics.DrawPolygon(pen, new PointF[] { new PointF(4, 16), new PointF(16, 4),
                        new PointF(20, 8), new PointF(8, 20), new PointF(3, 21) });
                    graphics.DrawLine(pen, 13, 7, 17, 11);
                }
                else if (label.StartsWith("箭头"))
                {
                    graphics.DrawLine(pen, 3, 21, 20, 4);
                    graphics.DrawLines(pen, new PointF[] { new PointF(11, 4), new PointF(20, 4), new PointF(20, 13) });
                }
                else if (label.StartsWith("矩形")) graphics.DrawRectangle(pen, 3, 5, 18, 14);
                else if (label.StartsWith("马赛克"))
                {
                    for (int y = 0; y < 3; y++)
                        for (int x = 0; x < 3; x++)
                            if ((x + y) % 2 == 0) graphics.FillRectangle(brush, 3 + x * 7, 3 + y * 7, 5, 5);
                            else graphics.DrawRectangle(pen, 3 + x * 7, 3 + y * 7, 5, 5);
                }
                else if (label.StartsWith("文字"))
                {
                    graphics.DrawLine(pen, 4, 4, 20, 4);
                    graphics.DrawLine(pen, 12, 4, 12, 21);
                    graphics.DrawLine(pen, 8, 21, 16, 21);
                }
                else if (label.StartsWith("颜色"))
                {
                    using (Brush accent = new SolidBrush(AccentColor)) graphics.FillEllipse(accent, 4, 4, 16, 16);
                    graphics.DrawEllipse(pen, 3, 3, 18, 18);
                }
                else if (label.StartsWith("撤回") || label.StartsWith("还原"))
                {
                    if (label.StartsWith("还原")) { graphics.TranslateTransform(24, 0); graphics.ScaleTransform(-1, 1); }
                    graphics.DrawBezier(pen, new PointF(4, 9), new PointF(18, 1),
                        new PointF(26, 19), new PointF(12, 21));
                    graphics.DrawLines(pen, new PointF[] { new PointF(4, 3), new PointF(4, 10), new PointF(11, 10) });
                }
                else if (label.StartsWith("清除"))
                {
                    graphics.DrawLines(pen, new PointF[] { new PointF(5, 7), new PointF(6, 21),
                        new PointF(18, 21), new PointF(19, 7) });
                    graphics.DrawLine(pen, 3, 6, 21, 6);
                    graphics.DrawLines(pen, new PointF[] { new PointF(9, 5), new PointF(9, 2),
                        new PointF(15, 2), new PointF(15, 5) });
                    graphics.DrawLine(pen, 10, 10, 10, 17);
                    graphics.DrawLine(pen, 14, 10, 14, 17);
                }
                else if (label.StartsWith("复制"))
                {
                    graphics.DrawRectangle(pen, 8, 8, 13, 13);
                    graphics.DrawLines(pen, new PointF[] { new PointF(4, 16), new PointF(3, 16),
                        new PointF(3, 3), new PointF(16, 3), new PointF(16, 4) });
                }
                else if (label.StartsWith("保存"))
                {
                    graphics.DrawPolygon(pen, new PointF[] { new PointF(3, 3), new PointF(18, 3),
                        new PointF(21, 6), new PointF(21, 21), new PointF(3, 21) });
                    graphics.DrawRectangle(pen, 7, 3, 9, 6);
                    graphics.DrawRectangle(pen, 7, 14, 10, 7);
                }
                else if (label.StartsWith("贴屏"))
                {
                    graphics.DrawPolygon(pen, new PointF[] { new PointF(8, 3), new PointF(17, 3),
                        new PointF(16, 10), new PointF(20, 14), new PointF(5, 14), new PointF(9, 10) });
                    graphics.DrawLine(pen, 12, 14, 12, 22);
                }
                else if (label.StartsWith("编辑器"))
                {
                    graphics.DrawRectangle(pen, 2, 4, 20, 16);
                    graphics.DrawLine(pen, 2, 9, 22, 9);
                    graphics.DrawLine(pen, 8, 9, 8, 20);
                }
                else if (label.StartsWith("放大") || label.StartsWith("缩小"))
                {
                    graphics.DrawEllipse(pen, 2, 2, 14, 14);
                    graphics.DrawLine(pen, 14, 14, 22, 22);
                    graphics.DrawLine(pen, 5, 9, 13, 9);
                    if (label.StartsWith("放大")) graphics.DrawLine(pen, 9, 5, 9, 13);
                }
                else if (label.StartsWith("原始尺寸"))
                {
                    graphics.DrawRectangle(pen, 2, 3, 20, 18);
                    graphics.DrawLines(pen, new PointF[] { new PointF(5, 10), new PointF(7, 8), new PointF(7, 16) });
                    graphics.DrawLines(pen, new PointF[] { new PointF(15, 10), new PointF(17, 8), new PointF(17, 16) });
                    graphics.FillEllipse(brush, 10.5f, 9, 2, 2);
                    graphics.FillEllipse(brush, 10.5f, 14, 2, 2);
                }
                else if (label.StartsWith("更多"))
                    for (int index = 0; index < 3; index++) graphics.FillEllipse(brush, 3 + index * 7, 10, 4, 4);
                else
                {
                    graphics.DrawLine(pen, 5, 5, 19, 19);
                    graphics.DrawLine(pen, 19, 5, 5, 19);
                }
            }
            graphics.Restore(saved);
        }
    }

    internal static class CaptureImageActions
    {
        internal static Bitmap Clone(Bitmap image)
        {
            return image.Clone(new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppArgb);
        }
        internal static void Copy(Bitmap image)
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, image);
            Clipboard.SetDataObject(data, true, 5, 100);
        }
        internal static bool Save(IWin32Window owner, Bitmap image)
        {
            using (SaveFileDialog dialog = new SaveFileDialog {
                Title = "保存截图", Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
                DefaultExt = "png", AddExtension = true,
                FileName = "轻截_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                using (MemoryStream encoded = new MemoryStream())
                {
                    image.Save(encoded, dialog.FilterIndex == 2 ? ImageFormat.Jpeg : ImageFormat.Png);
                    File.WriteAllBytes(dialog.FileName, encoded.ToArray());
                }
                return true;
            }
        }
    }
}
