using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingJie
{
    /// <summary>
    /// A modeless, manually scrolled capture. Call RunAsync on the WinForms UI thread
    /// after hiding the selection overlay. Input is never injected into the target.
    /// </summary>
    public sealed class ScrollCaptureSession : IDisposable
    {
        private readonly Rectangle _screenArea;
        private readonly Func<Rectangle, Bitmap> _capture;
        private readonly bool _showControls;
        private readonly int _interval;
        private readonly bool _adaptiveSampling;
        private int _idleFrames;
        private readonly ScrollStitcher _stitcher;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private ScrollCaptureControl _control;
        private readonly List<Form> _selectionHints = new List<Form>();
        private Bitmap _preview;
        private IntPtr _excludedPreviewHandle;
        private ScrollCaptureHotkeys _hotkeys;
        private bool _running;
        private bool _started;
        private bool _disposed;
        private bool _resourcesReleased;
        private volatile bool _finishRequested;

        public int Height { get; private set; }
        public string StatusText { get; private set; }
        internal ScrollFrameResult LastFrameResult { get; private set; }
        public event EventHandler ProgressChanged;

        public ScrollCaptureSession(Rectangle screenArea, Bitmap firstFrame)
            : this(screenArea, firstFrame, ScreenCapture.CaptureArea, true, 120, true) { }

        // Synthetic tests inject frame ownership and suppress every system surface:
        // no real capture, native hotkeys, visible form, tray icon or input events.
        internal ScrollCaptureSession(Rectangle screenArea, Bitmap firstFrame,
            Func<Rectangle, Bitmap> capture, bool showControls, int interval)
            : this(screenArea, firstFrame, capture, showControls, interval, false) { }

        private ScrollCaptureSession(Rectangle screenArea, Bitmap firstFrame,
            Func<Rectangle, Bitmap> capture, bool showControls, int interval, bool adaptiveSampling)
        {
            if (firstFrame == null) throw new ArgumentNullException("firstFrame");
            if (capture == null) throw new ArgumentNullException("capture");
            if (screenArea.Width <= 0 || screenArea.Height <= 0 || screenArea.Size != firstFrame.Size)
                throw new ArgumentException("滚动截图区域与首帧尺寸不匹配。", "screenArea");
            _screenArea = screenArea;
            _capture = capture;
            _showControls = showControls;
            _interval = Math.Max(1, interval);
            _adaptiveSampling = adaptiveSampling;
            _stitcher = new ScrollStitcher(firstFrame);
            Height = _stitcher.Height;
            StatusText = "在选区内慢慢向下滚动";
        }

        /// <summary>Returns an independent image, or null for either cancellation path.</summary>
        public async Task<Bitmap> RunAsync(CancellationToken token)
        {
            if (_disposed) throw new ObjectDisposedException("ScrollCaptureSession");
            if (_started) throw new InvalidOperationException("滚动截图会话只能启动一次。");
            if (_showControls && !Application.MessageLoop)
                throw new InvalidOperationException("请从应用窗口启动滚动截图。");
            _started = true;
            _running = true;
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token))
            {
                CancellationToken cancellation = linked.Token;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    await RefreshPreviewAsync(cancellation);
                    if (_showControls) await CreateControlsAsync(cancellation);
                    // Give the closed selection window time to leave the desktop.
                    await _wake.WaitAsync(_interval, cancellation);
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        bool finalSample = _finishRequested;
                        ScrollFrameResult result;
                        using (Bitmap frame = await CaptureSampleAsync(cancellation))
                        {
                            if (frame == null || frame.Size != _screenArea.Size)
                                throw new InvalidOperationException("屏幕尺寸发生变化，请重新选择滚动截图区域。");
                            cancellation.ThrowIfCancellationRequested();
                            // Exactly one capture/match is in flight. Neither the UI
                            // nor Dispose touches stitcher pixels during this task.
                            result = await Task.Run(delegate { return _stitcher.AddFrame(frame); });
                        }
                        cancellation.ThrowIfCancellationRequested();
                        Height = _stitcher.Height;
                        if (result == ScrollFrameResult.Appended) await RefreshPreviewAsync(cancellation);
                        UpdateProgress(result);
                        if (finalSample || result == ScrollFrameResult.LimitReached) break;
                        // A finish request received during matching needs one newer
                        // sample, so the final scroll is never lost to a stale frame.
                        if (!_finishRequested) await _wake.WaitAsync(NextSampleDelay(), cancellation);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    Bitmap export = await Task.Run(delegate { return _stitcher.ExportImage(); });
                    if (cancellation.IsCancellationRequested || _disposed)
                    {
                        export.Dispose();
                        return null;
                    }
                    return export;
                }
                catch (OperationCanceledException) { return null; }
                finally
                {
                    _running = false;
                    CloseControls();
                    ReleaseResources();
                }
            }
        }

        private async Task RefreshPreviewAsync(CancellationToken token)
        {
            Bitmap next = await Task.Run(delegate { return _stitcher.CreatePreview(320, 2048); });
            if (token.IsCancellationRequested || _disposed)
            {
                next.Dispose();
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            }
            Bitmap old = _preview;
            _preview = next;
            if (_control != null && !_control.IsDisposed)
                _control.SetPreview(_preview, _screenArea.Width, Height, _screenArea.Height);
            if (old != null) old.Dispose();
        }

        internal Bitmap CopyPreview()
        { return _preview == null ? null : (Bitmap)_preview.Clone(); }

        private int NextSampleDelay()
        {
            if (!_adaptiveSampling) return _interval;
            int delay = _idleFrames >= 3 ? 280 : _interval;
            if (_control != null && !_control.IsDisposed && _control.Bounds.IntersectsWith(_screenArea) &&
                !ScrollCaptureNative.IsExcluded(_control.Handle, _excludedPreviewHandle)) delay = Math.Max(300, delay);
            return delay;
        }

        private async Task<Bitmap> CaptureSampleAsync(CancellationToken token)
        {
            bool temporarilyHidden = false;
            if (_control != null && !_control.IsDisposed && _control.Visible &&
                _control.Bounds.IntersectsWith(_screenArea) &&
                !ScrollCaptureNative.IsExcluded(_control.Handle, _excludedPreviewHandle))
            {
                _control.Hide();
                temporarilyHidden = true;
                await ScrollCaptureNative.WaitForCompositionAsync(token);
            }
            try
            {
                token.ThrowIfCancellationRequested();
                return _capture(_screenArea);
            }
            finally
            {
                // Restore before background matching/thumbnail work. The fallback
                // only hides for the compositor + readback, not the full interval.
                if (temporarilyHidden && !_disposed && !token.IsCancellationRequested &&
                    _control != null && !_control.IsDisposed) _control.Show();
            }
        }

        internal void RequestFinish()
        {
            if (_disposed || _resourcesReleased) return;
            _finishRequested = true;
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        internal void RequestCancel()
        {
            try { if (!_resourcesReleased) _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void UpdateProgress(ScrollFrameResult result)
        {
            LastFrameResult = result;
            _idleFrames = result == ScrollFrameResult.Unchanged ? Math.Min(3, _idleFrames + 1) : 0;
            Height = _stitcher.Height;
            if (result == ScrollFrameResult.NoMatch)
                StatusText = "未接上：向上回滚一点，保留画面重叠";
            else if (result == ScrollFrameResult.LimitReached)
                StatusText = "已达到长度上限，正在完成";
            else if (result == ScrollFrameResult.Appended)
                StatusText = "已拼接 " + Height + " px · 继续向下滚动";
            else StatusText = "当前 " + Height + " px · 等待向下滚动";
            if (_control != null && !_control.IsDisposed) _control.SetStatus(StatusText);
            EventHandler changed = ProgressChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private async Task CreateControlsAsync(CancellationToken token)
        {
            _hotkeys = new ScrollCaptureHotkeys(RequestFinish, RequestCancel);
            _control = new ScrollCaptureControl(RequestFinish, RequestCancel);
            _control.SetPreview(_preview, _screenArea.Width, Height, _screenArea.Height);
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle monitor = screens[i].Bounds;
                if (monitor.IntersectsWith(_screenArea) && !_screenArea.Contains(monitor))
                {
                    ScrollCaptureHint shade = new ScrollCaptureHint(monitor, _screenArea, false);
                    _selectionHints.Add(shade);
                    shade.Show();
                }
            }
            Rectangle frameBounds = _screenArea;
            frameBounds.Inflate(2, 2);
            ScrollCaptureHint border = new ScrollCaptureHint(frameBounds, _screenArea, true);
            _selectionHints.Add(border);
            border.Show();
            Rectangle preferredWork = Screen.FromPoint(new Point(_screenArea.Right - 1,
                _screenArea.Bottom - 1)).WorkingArea;
            _control.Location = preferredWork.Location;
            float scale = 1;
            using (Graphics graphics = _control.CreateGraphics()) scale = Math.Max(1, graphics.DpiX / 96f);
            _control.SetScaleToFit(scale, preferredWork.Size);
            Rectangle placement = PlacePreview(_screenArea, _control.Size, preferredWork);
            _control.Bounds = placement;
            _control.SetShortcuts(_hotkeys.FinishRegistered, _hotkeys.CancelRegistered);
            _control.SetStatus("在选区内慢慢向下滚动");
            if (placement.IntersectsWith(_screenArea)) await VerifyExclusionAsync(token);
            _control.Show();
        }

        internal static Rectangle PlacePreview(Rectangle sample, Size size, Rectangle preferredWork)
        {
            Rectangle placement;
            if (TryPlaceControl(sample, size, new Rectangle[] { preferredWork }, out placement)) return placement;
            Point anchor = new Point(sample.Right - size.Width - 12, sample.Bottom - size.Height - 12);
            return new Rectangle(CaptureWindowPlacement.Clamp(anchor, size, preferredWork), size);
        }

        private async Task VerifyExclusionAsync(CancellationToken token)
        {
            // API success alone is insufficient: older systems can silently return
            // a black window. Prove this exact capture pipeline leaves pixels intact.
            if (!ScrollCaptureNative.TryExclude(_control.Handle)) return;
            Rectangle strip = _control.RectangleToScreen(new Rectangle(6, 3,
                Math.Min(64, _control.ClientSize.Width - 12), 3));
            if (!_screenArea.Contains(strip)) { ScrollCaptureNative.ClearExclusion(_control.Handle); return; }
            using (Bitmap before = _capture(strip))
            {
                if (before == null || before.Size != strip.Size) return;
                _control.Show();
                _control.Update(); // Paint the probe strip before flushing its composition.
                await ScrollCaptureNative.WaitForCompositionAsync(token);
                using (Bitmap after = _capture(strip))
                    if (ScrollCaptureNative.IsWindowCovering(_control.Handle,
                        new Point(strip.Left + strip.Width / 2, strip.Top + 1)) &&
                        ScrollCaptureNative.ProvesExclusion(before, after, _control.BackColor))
                        _excludedPreviewHandle = _control.Handle;
            }
            if (_excludedPreviewHandle == IntPtr.Zero)
            {
                _control.Hide();
                ScrollCaptureNative.ClearExclusion(_control.Handle);
            }
        }

        internal static bool TryPlaceControl(Rectangle sample, Size size,
            Rectangle[] workAreas, out Rectangle placement)
        {
            const int gap = 8;
            List<Rectangle> screens = new List<Rectangle>(workAreas);
            screens.Sort(delegate(Rectangle first, Rectangle second)
            {
                Rectangle a = Rectangle.Intersect(sample, first), b = Rectangle.Intersect(sample, second);
                return ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height);
            });
            foreach (Rectangle work in screens)
            {
                if (size.Width <= 0 || size.Height <= 0 || size.Width > work.Width || size.Height > work.Height) continue;
                List<Point> candidates = new List<Point>();
                candidates.Add(new Point(sample.Right + gap, sample.Bottom - size.Height));
                candidates.Add(new Point(sample.Right - size.Width, sample.Bottom + gap));
                candidates.Add(new Point(sample.Right - size.Width, sample.Top - size.Height - gap));
                candidates.Add(new Point(sample.Right + gap, sample.Top));
                candidates.Add(new Point(sample.Left - size.Width - gap, sample.Top));
                candidates.Add(work.Location);
                candidates.Add(new Point(work.Right - size.Width, work.Top));
                candidates.Add(new Point(work.Left, work.Bottom - size.Height));
                candidates.Add(new Point(work.Right - size.Width, work.Bottom - size.Height));
                foreach (Point candidate in candidates)
                {
                    Point point = new Point(Math.Max(work.Left, Math.Min(candidate.X, work.Right - size.Width)),
                        Math.Max(work.Top, Math.Min(candidate.Y, work.Bottom - size.Height)));
                    Rectangle bounds = new Rectangle(point, size);
                    if (!bounds.IntersectsWith(sample)) { placement = bounds; return true; }
                }
            }
            placement = Rectangle.Empty;
            return false;
        }

        private void CloseControls()
        {
            if (_control != null) { _control.Dispose(); _control = null; }
            foreach (Form hint in _selectionHints) hint.Dispose();
            _selectionHints.Clear();
            if (_hotkeys != null) { _hotkeys.Dispose(); _hotkeys = null; }
        }
        private void ReleaseResources()
        {
            if (_resourcesReleased) return;
            _resourcesReleased = true;
            _stitcher.Dispose();
            if (_preview != null) { _preview.Dispose(); _preview = null; }
            _stop.Dispose();
            _wake.Dispose();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_resourcesReleased) _stop.Cancel();
            // No UI-thread Wait/Join: the one background match releases its owned
            // bitmap, then RunAsync's UI continuation disposes all session resources.
            if (!_running) { CloseControls(); ReleaseResources(); }
        }
    }

    internal sealed class ScrollCaptureControl : Form
    {
        private readonly Label _heading;
        private readonly Label _dimensions;
        private readonly ScrollPreviewCanvas _previewCanvas;
        private readonly Label _status;
        private readonly Button _finish;
        private readonly Button _cancel;
        internal ScrollCaptureControl(Action finish, Action cancel)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(29, 37, 51);
            Font = UiTheme.Font(9);
            _heading = new Label { ForeColor = Color.White, Text = "长截图预览", TextAlign = ContentAlignment.MiddleLeft };
            _dimensions = new Label { ForeColor = Color.FromArgb(163, 188, 223), TextAlign = ContentAlignment.MiddleLeft };
            _previewCanvas = new ScrollPreviewCanvas();
            _status = new Label { ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true };
            _finish = MakeButton("完成 · Ctrl+Enter", finish);
            _cancel = MakeButton("取消 · Esc", cancel);
            Controls.Add(_heading); Controls.Add(_dimensions); Controls.Add(_previewCanvas);
            Controls.Add(_status); Controls.Add(_finish); Controls.Add(_cancel);
            SetScale(1);
        }
        private Button MakeButton(string label, Action action)
        {
            Button button = new Button { Text = label, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 72, 111), ForeColor = Color.White, TabStop = false };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { action(); };
            return button;
        }
        internal void SetScale(float scale)
        {
            ClientSize = new Size((int)(246 * scale), (int)(244 * scale));
            _heading.SetBounds((int)(10 * scale), (int)(6 * scale), (int)(106 * scale), (int)(18 * scale));
            _dimensions.SetBounds((int)(119 * scale), (int)(6 * scale), (int)(117 * scale), (int)(18 * scale));
            _previewCanvas.SetBounds((int)(8 * scale), (int)(30 * scale), (int)(230 * scale), (int)(140 * scale));
            _status.SetBounds((int)(10 * scale), (int)(177 * scale), (int)(226 * scale), (int)(27 * scale));
            _finish.SetBounds((int)(10 * scale), (int)(213 * scale), (int)(136 * scale), (int)(24 * scale));
            _cancel.SetBounds((int)(154 * scale), (int)(213 * scale), (int)(82 * scale), (int)(24 * scale));
        }
        internal void SetScaleToFit(float scale, Size available)
        {
            SetScale(Math.Max(.5f, Math.Min(scale, Math.Min((available.Width - 16) / 246f,
                (available.Height - 16) / 244f))));
        }
        internal void SetPreview(Bitmap image, int width, int height, int viewportHeight)
        {
            _dimensions.Text = width + " × " + height + " px";
            _previewCanvas.SetPreview(image, height, viewportHeight);
        }
        internal void SetStatus(string text)
        {
            _status.Text = text;
            _status.ForeColor = text.Contains("未接上") ? Color.FromArgb(255, 194, 113) : Color.FromArgb(206, 219, 239);
        }
        internal void SetShortcuts(bool finish, bool cancel)
        {
            _finish.Text = finish ? "完成 · Ctrl+Enter" : "完成";
            _cancel.Text = cancel ? "取消 · Esc" : "取消";
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */;
                return value;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021 /* WM_MOUSEACTIVATE */) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(Color.FromArgb(81, 111, 153)))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class ScrollCaptureHotkeys : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        private readonly Action _finish;
        private readonly Action _cancel;
        internal bool FinishRegistered { get; private set; }
        internal bool CancelRegistered { get; private set; }
        internal ScrollCaptureHotkeys(Action finish, Action cancel)
        {
            _finish = finish; _cancel = cancel;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            FinishRegistered = RegisterHotKey(Handle, 1, 0x4002 /* CTRL | NOREPEAT */, (uint)Keys.Enter);
            CancelRegistered = RegisterHotKey(Handle, 2, 0x4000 /* NOREPEAT */, (uint)Keys.Escape);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
            {
                if (message.WParam.ToInt32() == 1) _finish();
                else if (message.WParam.ToInt32() == 2) _cancel();
                return;
            }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            if (FinishRegistered) UnregisterHotKey(Handle, 1);
            if (CancelRegistered) UnregisterHotKey(Handle, 2);
            DestroyHandle();
        }
    }
}
