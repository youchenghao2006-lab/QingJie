using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingJie
{
    public sealed class HistoryBrowserForm : Form
    {
        private readonly HistoryStore store;
        private readonly HistoryGrid grid;
        private readonly Button allButton, screenshotButton, boardButton;
        private readonly Button editButton, addButton;
        private readonly Label status;
        private HistoryCategory? category;
        private int refreshRevision;

        public event Action<string> EditRequested;
        public event Action<string[]> AddToBoardRequested;

        public HistoryBrowserForm(HistoryStore historyStore)
        {
            if (historyStore == null) throw new ArgumentNullException("historyStore");
            SuspendLayout();
            store = historyStore;
            Text = "历史记录 · 轻截";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiTheme.Font(9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 680);
            MinimumSize = new Size(760, 520);
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(240, 244, 252);
            Icon = SystemIcons.Application;

            TableLayoutPanel root = new BackdropLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 4
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);

            Label title = new Label {
                Text = "历史记录", Dock = DockStyle.Top, AutoSize = true,
                Font = UiTheme.Font(20F, FontStyle.Bold), ForeColor = Color.FromArgb(31, 48, 79)
            };
            Label subtitle = new Label {
                Text = "截图与画布分开保存，关闭轻截后仍可继续使用",
                AutoSize = true, Dock = DockStyle.Bottom, ForeColor = Color.FromArgb(101, 117, 144)
            };
            Panel heading = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            heading.Controls.Add(title); heading.Controls.Add(subtitle);
            root.Controls.Add(heading, 0, 0);

            TableLayoutPanel tabs = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent
            };
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 226));
            tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel categories = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty
            };
            allButton = MakeButton("全部", 80);
            screenshotButton = MakeButton("截图", 90);
            boardButton = MakeButton("画布", 90);
            categories.Controls.AddRange(new Control[] { allButton, screenshotButton, boardButton });
            allButton.Click += delegate { ChangeCategory(null); };
            screenshotButton.Click += delegate { ChangeCategory(HistoryCategory.Screenshot); };
            boardButton.Click += delegate { ChangeCategory(HistoryCategory.Board); };
            FlowLayoutPanel utilities = new FlowLayoutPanel {
                Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft,
                BackColor = Color.Transparent, Margin = Padding.Empty
            };
            Button refresh = MakeButton("刷新", 74);
            Button folder = MakeButton("打开文件夹", 132);
            refresh.Click += delegate { RefreshHistory(); };
            folder.Click += delegate { OpenFolder(); };
            utilities.Controls.Add(refresh); utilities.Controls.Add(folder);
            tabs.Controls.Add(categories, 0, 0); tabs.Controls.Add(utilities, 1, 0);
            root.Controls.Add(tabs, 0, 1);

            grid = new HistoryGrid(store) { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            grid.SelectionChanged += delegate { UpdateActions(); };
            grid.OpenRequested += delegate { EditSelection(); };
            root.Controls.Add(grid, 0, 2);

            TableLayoutPanel footer = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                BackColor = Color.Transparent, Margin = Padding.Empty
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            status = new Label {
                Text = "正在读取历史记录…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(101, 117, 144), AutoEllipsis = true
            };
            editButton = MakeButton("在编辑器打开", 150);
            addButton = MakeButton("加入画布", 144);
            editButton.Dock = addButton.Dock = DockStyle.Fill;
            editButton.Click += delegate { EditSelection(); };
            addButton.Click += delegate { AddSelection(); };
            footer.Controls.Add(status, 0, 0); footer.Controls.Add(editButton, 1, 0);
            footer.Controls.Add(addButton, 2, 0);
            root.Controls.Add(footer, 0, 3);
            SetTabColors();
            UpdateActions();
            Load += delegate { RefreshHistory(); };
            ResumeLayout(true);
        }

        public async void RefreshHistory()
        {
            int revision = ++refreshRevision;
            HistoryCategory? requestedCategory = category;
            status.Text = "正在读取历史记录…";
            try
            {
                List<HistoryEntry> entries = await Task.Run(delegate { return store.List(requestedCategory); });
                if (IsDisposed || Disposing || revision != refreshRevision) return;
                grid.SetEntries(entries);
                UpdateActions();
            }
            catch (Exception error)
            {
                if (IsDisposed || Disposing || revision != refreshRevision) return;
                status.Text = "读取失败：" + error.Message;
            }
        }

        private static Button MakeButton(string text, int width)
        {
            return new RoundedButton {
                Text = text, Width = width, Height = 42, Margin = new Padding(0, 0, 6, 0),
                BackColor = Color.White, ForeColor = Color.FromArgb(52, 70, 103),
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
            };
        }

        private void ChangeCategory(HistoryCategory? next)
        {
            if (category == next) return;
            category = next;
            SetTabColors();
            RefreshHistory();
        }

        private void SetTabColors()
        {
            SetTabColor(allButton, !category.HasValue);
            SetTabColor(screenshotButton, category == HistoryCategory.Screenshot);
            SetTabColor(boardButton, category == HistoryCategory.Board);
        }

        private static void SetTabColor(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(47, 102, 241) : Color.White;
            button.ForeColor = selected ? Color.White : Color.FromArgb(52, 70, 103);
        }

        private void UpdateActions()
        {
            int count = grid.SelectedEntries.Count;
            editButton.Enabled = count == 1;
            addButton.Enabled = count > 0;
            status.Text = grid.Count == 0 ? "还没有记录。截图和画布导出后会出现在这里。" :
                count > 0 ? "已选择 " + count + " 张 · Ctrl / Shift 可多选" :
                grid.Count + " 张记录 · 双击编辑，Ctrl / Shift 可多选";
        }

        private void EditSelection()
        {
            List<HistoryEntry> selected = grid.SelectedEntries;
            if (selected.Count != 1 || EditRequested == null) return;
            EditRequested(selected[0].Path);
        }

        private void AddSelection()
        {
            List<HistoryEntry> selected = grid.SelectedEntries;
            if (selected.Count == 0 || AddToBoardRequested == null) return;
            string[] paths = new string[selected.Count];
            for (int i = 0; i < paths.Length; i++) paths[i] = selected[i].Path;
            AddToBoardRequested(paths);
        }

        private void OpenFolder()
        {
            string directory = category == HistoryCategory.Screenshot ? store.ScreenshotDirectory :
                category == HistoryCategory.Board ? store.BoardDirectory : store.RootDirectory;
            List<HistoryEntry> selected = grid.SelectedEntries;
            if (selected.Count == 1) directory = Path.GetDirectoryName(selected[0].Path);
            try
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "无法打开历史目录。\r\n\r\n" + error.Message,
                    "历史记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.F5) { RefreshHistory(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>
    /// Only visible tiles request thumbnails. Two workers decode in the background;
    /// the bounded cache owns finished bitmaps and a UI timer applies results.
    /// Workers never access controls or keep a source file open after decoding.
    /// </summary>
    internal sealed class HistoryGrid : ScrollableControl
    {
        private sealed class ThumbnailResult
        {
            internal string Path;
            internal Bitmap Image;
            internal int Revision;
        }

        private readonly HistoryStore store;
        private readonly List<HistoryEntry> entries = new List<HistoryEntry>();
        private readonly HashSet<int> selection = new HashSet<int>();
        private readonly Dictionary<string, Bitmap> thumbnails = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> recent = new LinkedList<string>();
        private readonly HashSet<string> requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<HistoryEntry> waiting = new Queue<HistoryEntry>();
        private readonly Queue<ThumbnailResult> completed = new Queue<ThumbnailResult>();
        private readonly object resultGate = new object();
        private readonly Timer resultsTimer = new Timer { Interval = 60 };
        private int activeWorkers, revision, anchor = -1, focused = -1;
        private bool released;
        private float unit = 1F;

        public event EventHandler SelectionChanged;
        public event EventHandler OpenRequested;
        internal int Count { get { return entries.Count; } }
        internal List<HistoryEntry> SelectedEntries
        {
            get
            {
                List<HistoryEntry> result = new List<HistoryEntry>();
                for (int i = 0; i < entries.Count; i++) if (selection.Contains(i)) result.Add(entries[i]);
                return result;
            }
        }

        internal HistoryGrid(HistoryStore historyStore)
        {
            store = historyStore;
            AutoScroll = true;
            TabStop = true;
            BackColor = Color.FromArgb(240, 244, 252);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            resultsTimer.Tick += delegate { DrainThumbnails(); };
        }

        internal void SetEntries(List<HistoryEntry> next)
        {
            revision++;
            entries.Clear(); entries.AddRange(next);
            selection.Clear(); anchor = focused = -1;
            waiting.Clear(); requested.Clear();
            ClearThumbnails();
            AutoScrollPosition = Point.Empty;
            UpdateScrollArea();
            Invalidate();
            RaiseSelectionChanged();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics graphics = CreateGraphics()) unit = graphics.DpiX / 96F;
            UpdateScrollArea();
            resultsTimer.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated) resultsTimer.Start();
            else resultsTimer.Stop();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollArea();
            Invalidate();
        }

        private int CellWidth { get { return Math.Max(1, (int)(210 * unit)); } }
        private int CellHeight { get { return Math.Max(1, (int)(180 * unit)); } }
        private int Columns { get { return Math.Max(1, ClientSize.Width / CellWidth); } }
        private Rectangle TileBounds(int index)
        {
            int columns = Columns;
            int available = Math.Max(CellWidth, ClientSize.Width / columns);
            int gap = (int)(6 * unit);
            return new Rectangle(index % columns * available + gap,
                index / columns * CellHeight + AutoScrollPosition.Y + gap,
                available - gap * 2, CellHeight - gap * 2);
        }

        private void UpdateScrollArea()
        {
            int rows = (entries.Count + Columns - 1) / Columns;
            AutoScrollMinSize = new Size(0, rows * CellHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Math.Abs(unit - e.Graphics.DpiX / 96F) > .01F)
            {
                unit = e.Graphics.DpiX / 96F;
                revision++; waiting.Clear(); requested.Clear(); ClearThumbnails();
                UpdateScrollArea();
            }
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            if (entries.Count == 0)
            {
                TextRenderer.DrawText(graphics, "历史记录保存在本机\n截图与画布会各自归档",
                    Font, ClientRectangle, Color.FromArgb(125, 141, 167),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.PreserveGraphicsClipping);
                return;
            }
            int firstRow = Math.Max(0, -AutoScrollPosition.Y / CellHeight);
            int lastRow = (-AutoScrollPosition.Y + ClientSize.Height) / CellHeight + 1;
            int end = Math.Min(entries.Count, lastRow * Columns);
            for (int i = firstRow * Columns; i < end; i++)
            {
                Rectangle tile = TileBounds(i);
                if (!tile.IntersectsWith(e.ClipRectangle)) continue;
                DrawTile(graphics, i, tile);
            }
            StartLoads();
        }

        private void DrawTile(Graphics graphics, int index, Rectangle tile)
        {
            HistoryEntry entry = entries[index];
            bool selected = selection.Contains(index);
            using (Brush fill = new SolidBrush(selected ? Color.FromArgb(231, 239, 255) : Color.White))
                graphics.FillRectangle(fill, tile);
            using (Pen outline = new Pen(selected ? Color.FromArgb(47, 102, 241) : Color.FromArgb(221, 230, 244),
                selected ? 2 * unit : unit))
                graphics.DrawRectangle(outline, tile.X, tile.Y, tile.Width - 1, tile.Height - 1);
            int padding = (int)(10 * unit);
            Rectangle imageArea = new Rectangle(tile.X + padding, tile.Y + padding,
                tile.Width - padding * 2, (int)(112 * unit));
            Bitmap image;
            if (thumbnails.TryGetValue(entry.Path, out image))
            {
                recent.Remove(entry.Path); recent.AddLast(entry.Path);
                if (image != null)
                    graphics.DrawImageUnscaled(image, imageArea.X + (imageArea.Width - image.Width) / 2,
                        imageArea.Y + (imageArea.Height - image.Height) / 2);
                else DrawPlaceholder(graphics, imageArea, "图片无法读取");
            }
            else
            {
                DrawPlaceholder(graphics, imageArea, "加载预览…");
                if (requested.Add(entry.Path)) waiting.Enqueue(entry);
            }
            Rectangle label = new Rectangle(imageArea.X, imageArea.Bottom + (int)(7 * unit),
                imageArea.Width, (int)(20 * unit));
            string time = entry.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss");
            TextRenderer.DrawText(graphics, time, Font, label, Color.FromArgb(43, 61, 92),
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
            label.Y += (int)(20 * unit);
            string detail = (entry.Category == HistoryCategory.Screenshot ? "截图" : "画布") +
                (selected ? "  ·  已选择" : "");
            TextRenderer.DrawText(graphics, detail, Font, label, Color.FromArgb(105, 123, 152),
                TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }

        private void DrawPlaceholder(Graphics graphics, Rectangle rectangle, string text)
        {
            using (Brush brush = new SolidBrush(Color.FromArgb(244, 247, 252)))
                graphics.FillRectangle(brush, rectangle);
            TextRenderer.DrawText(graphics, text, Font, rectangle, Color.FromArgb(135, 150, 173),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.PreserveGraphicsClipping);
        }

        private void StartLoads()
        {
            while (waiting.Count > 0)
            {
                lock (resultGate)
                {
                    if (released || activeWorkers >= 2) return;
                    activeWorkers++;
                }
                HistoryEntry entry = waiting.Dequeue();
                int requestRevision = revision;
                Size size = new Size((int)(186 * unit), (int)(112 * unit));
                Task.Run(delegate
                {
                    Bitmap thumbnail = null;
                    try
                    {
                        using (Bitmap source = store.LoadImage(entry.Path))
                        {
                            float scale = Math.Min((float)size.Width / source.Width, (float)size.Height / source.Height);
                            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                            thumbnail = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                            using (Graphics graphics = Graphics.FromImage(thumbnail))
                            {
                                graphics.Clear(Color.FromArgb(244, 247, 252));
                                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
                            }
                        }
                    }
                    catch { if (thumbnail != null) thumbnail.Dispose(); thumbnail = null; }
                    lock (resultGate)
                    {
                        activeWorkers--;
                        if (released) { if (thumbnail != null) thumbnail.Dispose(); }
                        else completed.Enqueue(new ThumbnailResult {
                            Path = entry.Path, Image = thumbnail, Revision = requestRevision
                        });
                    }
                });
            }
        }

        private void DrainThumbnails()
        {
            bool changed = false;
            lock (resultGate)
            {
                while (completed.Count > 0)
                {
                    ThumbnailResult result = completed.Dequeue();
                    if (result.Revision != revision)
                    {
                        if (result.Image != null) result.Image.Dispose();
                        continue;
                    }
                    Bitmap prior;
                    if (thumbnails.TryGetValue(result.Path, out prior) && prior != null) prior.Dispose();
                    thumbnails[result.Path] = result.Image;
                    recent.Remove(result.Path); recent.AddLast(result.Path);
                    changed = true;
                }
            }
            while (recent.Count > 80)
            {
                string oldest = recent.First.Value;
                recent.RemoveFirst();
                Bitmap image = thumbnails[oldest];
                if (image != null) image.Dispose();
                thumbnails.Remove(oldest);
                requested.Remove(oldest);
            }
            if (changed) Invalidate();
            StartLoads();
        }

        private void ClearThumbnails()
        {
            foreach (Bitmap image in thumbnails.Values) if (image != null) image.Dispose();
            thumbnails.Clear(); recent.Clear();
        }

        private int HitIndex(Point point)
        {
            if (!ClientRectangle.Contains(point)) return -1;
            int row = (point.Y - AutoScrollPosition.Y) / CellHeight;
            int width = Math.Max(CellWidth, ClientSize.Width / Columns);
            int column = Math.Min(Columns - 1, point.X / width);
            int index = row * Columns + column;
            return index < entries.Count && TileBounds(index).Contains(point) ? index : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            SelectIndex(HitIndex(e.Location), (ModifierKeys & Keys.Control) != 0,
                (ModifierKeys & Keys.Shift) != 0);
        }

        private void SelectIndex(int index, bool control, bool shift)
        {
            if (!control) selection.Clear();
            if (index >= 0)
            {
                if (shift && anchor >= 0)
                    for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++) selection.Add(i);
                else if (control && selection.Contains(index)) selection.Remove(index);
                else selection.Add(index);
                if (!shift) anchor = index;
                focused = index;
            }
            Invalidate();
            RaiseSelectionChanged();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && HitIndex(e.Location) >= 0 && selection.Count == 1 &&
                OpenRequested != null) OpenRequested(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.A)
            {
                selection.Clear();
                for (int i = 0; i < entries.Count; i++) selection.Add(i);
                Invalidate(); RaiseSelectionChanged(); e.Handled = true; return;
            }
            if (e.KeyCode == Keys.Enter && selection.Count == 1)
            {
                if (OpenRequested != null) OpenRequested(this, EventArgs.Empty);
                e.Handled = true; return;
            }
            int step = e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Left ? -1 :
                e.KeyCode == Keys.Down ? Columns : e.KeyCode == Keys.Up ? -Columns : 0;
            if (step == 0 || entries.Count == 0) return;
            int next = Math.Max(0, Math.Min(entries.Count - 1, focused < 0 ? 0 : focused + step));
            SelectIndex(next, false, e.Shift);
            Rectangle tile = TileBounds(next);
            if (tile.Top < 0) AutoScrollPosition = new Point(0, next / Columns * CellHeight);
            else if (tile.Bottom > ClientSize.Height)
                AutoScrollPosition = new Point(0, (next / Columns + 1) * CellHeight - ClientSize.Height);
            e.Handled = true;
        }

        private void RaiseSelectionChanged()
        {
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                resultsTimer.Stop(); resultsTimer.Dispose();
                lock (resultGate)
                {
                    released = true;
                    while (completed.Count > 0)
                    {
                        Bitmap image = completed.Dequeue().Image;
                        if (image != null) image.Dispose();
                    }
                }
                waiting.Clear(); ClearThumbnails();
            }
            base.Dispose(disposing);
        }
    }
}
