using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace QingJie
{
    /// <summary>A persistent board embedded beside the current screenshot.</summary>
    public sealed class BoardWorkspace : UserControl
    {
        private readonly BoardCanvas canvas = new BoardCanvas();
        private readonly ScreenshotQueueControl sourcePreview = new ScreenshotQueueControl();
        private readonly Label sourceTitle = new Label();
        private readonly Label sourceInfo = new Label();
        private readonly Label status = new Label();
        private readonly ResponsiveFlowPanel tools = new ResponsiveFlowPanel();
        private readonly Button undo = new RoundedButton();
        private readonly Button delete = new RoundedButton();
        private readonly Button addSourceButton = new RoundedButton();
        private readonly Button editSourceButton = new RoundedButton();
        private readonly Button removeSourceButton = new RoundedButton();
        private readonly Button panButton = new RoundedButton();
        private readonly ZoomReadout zoomLabel = new ZoomReadout();
        private Button copyBoardButton, saveBoardButton;
        private readonly ComboBox widthSelect = new ComboBox();
        private readonly ToolTip hints = new ToolTip();
        private readonly Color blue = UiTheme.Accent;
        private Bitmap sourceImage;
        private long legacySourceId;
        private bool dirty, dialogOpen, capturing, sourceDragging, sourcePressed;
        private bool sourceDropAccepted;
        private bool viewStateInitialized, displayedPanMode;
        private int sourceGeneration;
        private Point sourceDragStart;

        public event Action<bool> ScreenshotRequested;
        public event EventHandler EditScreenshotRequested;
        public event Action<long, Bitmap> EditQueueImageRequested;
        public event Action<long> SourceRemoved;
        public event EventHandler SourceAddedToBoard;
        public event Action HistoryRequested;
        /// <summary>
        /// Raised synchronously after a successful export or clipboard copy.
        /// The bitmap is borrowed for the callback only; subscribers must clone
        /// synchronously if they need to retain it or archive it asynchronously.
        /// </summary>
        public event Action<Bitmap> CompositionProduced;
        public bool IsBusy { get { return dialogOpen || capturing || sourceDragging; } }
        public long SelectedSourceId { get { return sourcePreview.SelectedId; } }
        public int SourceCount { get { return sourcePreview.Count; } }
        public bool SourceIsRetained { get { return IsSourceRetained(SelectedSourceId); } }
        public bool ContainsSourceId(long sourceId) { return sourcePreview.Find(sourceId) != null; }
        public bool IsSourceSaved(long sourceId)
        {
            ScreenshotQueueEntry entry = sourcePreview.Find(sourceId);
            return entry != null && entry.Saved;
        }
        public bool IsSourceRetained(long sourceId)
        {
            ScreenshotQueueEntry entry = sourcePreview.Find(sourceId);
            if (entry == null) return false;
            if (entry.Saved) return true;
            foreach (long id in entry.BoardImageIds)
                if (canvas.ContainsImageId(id)) return true;
            return false;
        }
        public bool CaptureInProgress
        {
            get { return capturing; }
            set
            {
                capturing = value;
                hints.Active = !value;
                Enabled = !value;
                UpdateInteraction();
            }
        }

        public BoardWorkspace()
        {
            SuspendLayout();
            Font = UiTheme.Font(9F);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = UiTheme.Page;
            Size = new Size(1040, 640);
            BuildInterface();
            canvas.ContentChanged += delegate
            {
                dirty = true;
                UpdateActions();
            };
            canvas.SelectionChanged += delegate { UpdateActions(); };
            canvas.ViewChanged += delegate { UpdateView(); };
            canvas.ImageDropped += delegate
            {
                UpdateTools();
                if (sourceDragging) sourceDropAccepted = true;
            };
            canvas.RequestText = RequestText;
            canvas.RequestFileDrop = delegate(string[] paths)
            {
                if (!IsBusy) ImportFiles(paths);
            };
            sourcePreview.SelectionChanged += delegate
            {
                ReleaseSourcePress();
                ScreenshotQueueEntry entry = sourcePreview.SelectedEntry;
                sourceImage = entry == null ? null : entry.Image;
                sourceGeneration++;
                UpdateInteraction();
            };
            sourcePreview.MouseDown += SourceMouseDown;
            sourcePreview.MouseMove += SourceMouseMove;
            sourcePreview.MouseUp += delegate { ReleaseSourcePress(); };
            sourcePreview.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && sourcePreview.HitTest(e.Location) != 0)
                    AddSourceImage();
            };
            sourcePreview.MouseCaptureChanged += delegate
            {
                if (!sourcePreview.Capture) sourcePressed = false;
            };
            AllowDrop = true;
            DragEnter += WorkspaceDragEnter;
            DragDrop += WorkspaceDragDrop;
            UpdateActions();
            UpdateInteraction();
            ResumeLayout(true);
        }

        private void BuildInterface()
        {
            var root = new BackdropLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
                RowCount = 1, Padding = new Padding(12, 12, 12, 10), Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var sourceCard = new CardLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 8, Padding = new Padding(14, 16, 14, 14),
                BackColor = Color.FromArgb(249, 252, 255), Margin = new Padding(0, 0, 14, 0) };
            sourceCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sourceCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sourceCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 2; i < 8; i++) sourceCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sourceTitle.Text = "截图队列";
            sourceTitle.AutoSize = true;
            sourceTitle.BackColor = Color.Transparent;
            sourceTitle.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
            sourceTitle.ForeColor = Color.FromArgb(39, 60, 96);
            sourceTitle.Margin = new Padding(0, 0, 0, 12);
            sourceCard.Controls.Add(sourceTitle, 0, 0);
            sourcePreview.Dock = DockStyle.Fill;
            sourcePreview.Margin = Padding.Empty;
            sourcePreview.MinimumSize = new Size(80, 130);
            sourceCard.Controls.Add(sourcePreview, 0, 1);
            sourceInfo.Dock = DockStyle.Fill;
            sourceInfo.AutoSize = true;
            sourceInfo.BackColor = Color.Transparent;
            sourceInfo.ForeColor = Color.FromArgb(112, 131, 157);
            sourceInfo.Font = new Font(Font.FontFamily, 8F);
            sourceInfo.Margin = new Padding(0, 10, 0, 6);
            sourceCard.Controls.Add(sourceInfo, 0, 2);
            sourceCard.Controls.Add(new Label { Text = "拖到右侧画布，或双击加入",
                AutoSize = true, BackColor = Color.Transparent, ForeColor = Color.FromArgb(105, 125, 154),
                Font = new Font(Font.FontFamily, 8F), Margin = new Padding(0, 0, 0, 14) }, 0, 3);
            StyleButton(addSourceButton, true);
            addSourceButton.Text = "加入画布";
            ((RoundedButton)addSourceButton).Icon = UiIcon.Plus;
            addSourceButton.Height = 38;
            addSourceButton.Dock = DockStyle.Top;
            addSourceButton.Margin = new Padding(0, 0, 0, 8);
            addSourceButton.Click += delegate { AddSourceImage(); };
            sourceCard.Controls.Add(addSourceButton, 0, 4);
            StyleButton(editSourceButton, false);
            editSourceButton.Text = "编辑截图";
            ((RoundedButton)editSourceButton).Icon = UiIcon.Edit;
            editSourceButton.Height = 36;
            editSourceButton.Dock = DockStyle.Top;
            editSourceButton.Margin = Padding.Empty;
            editSourceButton.Click += delegate { EditSelectedSource(); };
            sourceCard.Controls.Add(editSourceButton, 0, 5);
            StyleButton(removeSourceButton, false);
            removeSourceButton.Text = "从队列删除";
            ((RoundedButton)removeSourceButton).Icon = UiIcon.Delete;
            removeSourceButton.Height = 30;
            removeSourceButton.Dock = DockStyle.Top;
            removeSourceButton.Margin = new Padding(0, 7, 0, 0);
            removeSourceButton.Click += delegate { RemoveSource(SelectedSourceId); };
            sourceCard.Controls.Add(removeSourceButton, 0, 6);
            sourceCard.Controls.Add(new Label { Text = "截图持续积累，可反复拖入",
                AutoSize = true, BackColor = Color.Transparent, ForeColor = Color.FromArgb(132, 148, 171),
                Font = new Font(Font.FontFamily, 8F), Margin = new Padding(0, 12, 0, 0) }, 0, 7);
            hints.SetToolTip(sourcePreview, "单击选择截图，拖到画布或双击加入；滚动查看更早的截图");
            root.Controls.Add(sourceCard, 0, 0);

            var boardArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 5, Margin = Padding.Empty, BackColor = Color.Transparent };
            boardArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            boardArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            boardArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            boardArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            boardArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            boardArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(boardArea, 1, 0);
            var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                WrapContents = false, Margin = new Padding(3, 2, 0, 9),
                BackColor = Color.Transparent };
            heading.Controls.Add(new Label { Text = "自由画布", AutoSize = true,
                Font = UiTheme.Font(14F, FontStyle.Bold),
                ForeColor = UiTheme.Ink, Margin = new Padding(0, 0, 12, 0) });
            heading.Controls.Add(new Label { Text = "拖放 · 拼贴 · 标注", AutoSize = true,
                ForeColor = UiTheme.Muted, Font = UiTheme.Font(8.5F),
                Margin = new Padding(0, 7, 0, 0) });
            boardArea.Controls.Add(heading, 0, 0);
            var imports = new ResponsiveFlowPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            imports.Controls.Add(MakeButton("新截图", true, 88, delegate { RequestCapture(false); }, UiIcon.Capture));
            imports.Controls.Add(MakeButton("导入多图", false, 100, ImportImages, UiIcon.Open));
            imports.Controls.Add(MakeButton("粘贴图片", false, 100, PasteImage, UiIcon.Clipboard));
            imports.Controls.Add(MakeButton("历史图片", false, 100, OpenHistory, UiIcon.History));
            hints.SetToolTip(imports.Controls[0], "框选新截图，自动保留在左侧队列");
            hints.SetToolTip(imports.Controls[3], "浏览本机历史图片，可再次加入画布");
            boardArea.Controls.Add(imports, 0, 1);

            var toolbar = new CardLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(9, 8, 9, 8), Margin = new Padding(0, 0, 0, 8),
                BackColor = Color.FromArgb(251, 253, 255) };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tools.Dock = DockStyle.Fill;
            tools.Margin = Padding.Empty;
            string[] names = { "移动图片", "箭头", "画笔", "画框", "文字" };
            DrawingTool[] values = { DrawingTool.Select, DrawingTool.Arrow, DrawingTool.Pen,
                DrawingTool.Rectangle, DrawingTool.Text };
            for (int i = 0; i < names.Length; i++)
            {
                Button button = MakeButton(names[i], false, i == 0 ? 82 : 54, null);
                button.Height = 30;
                button.Tag = values[i];
                button.Click += delegate(object sender, EventArgs e)
                {
                    if (IsBusy) return;
                    bool wasPanning = canvas.PanMode;
                    canvas.PanMode = false;
                    DrawingTool requested = (DrawingTool)((Button)sender).Tag;
                    canvas.Tool = !wasPanning && canvas.Tool == requested ? DrawingTool.Select : requested;
                    UpdateTools();
                    canvas.Focus();
                };
                tools.Controls.Add(button);
            }
            StyleButton(undo, false);
            undo.Text = "撤销";
            undo.Size = new Size(58, 30);
            undo.Margin = new Padding(0, 0, 5, 5);
            undo.Click += delegate { if (!IsBusy) canvas.Undo(); };
            tools.Controls.Add(undo);
            StyleButton(delete, false);
            delete.Text = "删除图片";
            delete.Size = new Size(82, 30);
            delete.Margin = new Padding(0, 0, 5, 5);
            delete.Click += delegate { if (!IsBusy) canvas.DeleteSelected(); };
            tools.Controls.Add(delete);
            Button clear = MakeButton("清空", false, 58, ResetBoard);
            clear.Height = 30;
            tools.Controls.Add(clear);
            toolbar.Controls.Add(tools, 0, 0);
            var options = new ResponsiveFlowPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var palette = new FlowLayoutPanel { Size = new Size(230, 29), WrapContents = false,
                Margin = new Padding(0, 3, 4, 0), BackColor = Color.Transparent };
            palette.Controls.Add(new Label { Text = "颜色", AutoSize = true,
                BackColor = Color.Transparent, ForeColor = Color.FromArgb(110, 123, 143),
                Margin = new Padding(0, 4, 6, 0) });
            Color[] colors = { Color.FromArgb(244, 77, 86), blue, Color.FromArgb(30, 170, 125),
                Color.FromArgb(245, 172, 43), Color.FromArgb(26, 38, 61), Color.White };
            foreach (Color color in colors)
            {
                var swatch = new Button { BackColor = color, Size = new Size(23, 23),
                    FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 1, 7, 0), Tag = color,
                    AccessibleName = "标注颜色 " + color.Name };
                swatch.FlatAppearance.BorderColor = Color.FromArgb(204, 211, 224);
                swatch.Click += delegate(object sender, EventArgs e)
                {
                    if (IsBusy) return;
                    canvas.StrokeColor = (Color)((Button)sender).Tag;
                    foreach (Control item in palette.Controls)
                    {
                        Button candidate = item as Button;
                        if (candidate != null)
                            candidate.FlatAppearance.BorderSize = candidate == sender ? 3 : 1;
                    }
                };
                palette.Controls.Add(swatch);
            }
            ((Button)palette.Controls[1]).FlatAppearance.BorderSize = 3;
            options.Controls.Add(palette);
            var widths = new FlowLayoutPanel { Size = new Size(110, 29), WrapContents = false,
                Margin = new Padding(0, 3, 0, 0), BackColor = Color.Transparent };
            widths.Controls.Add(new Label { Text = "粗细", AutoSize = true,
                BackColor = Color.Transparent, ForeColor = Color.FromArgb(110, 123, 143),
                Margin = new Padding(0, 4, 6, 0) });
            widthSelect.DropDownStyle = ComboBoxStyle.DropDownList;
            widthSelect.Width = 66;
            widthSelect.Items.AddRange(new object[] { "细", "中", "粗" });
            widthSelect.SelectedIndex = 1;
            widthSelect.SelectedIndexChanged += delegate
            {
                canvas.StrokeWidth = new float[] { 4, 8, 16 }[widthSelect.SelectedIndex];
            };
            widths.Controls.Add(widthSelect);
            options.Controls.Add(widths);
            toolbar.Controls.Add(options, 0, 1);
            boardArea.Controls.Add(toolbar, 0, 2);

            canvas.Dock = DockStyle.Fill;
            canvas.Margin = Padding.Empty;
            canvas.StrokeColor = colors[0];
            canvas.StrokeWidth = 8;
            hints.SetToolTip(canvas, "空白处拖动画布，图片上拖动可移动图片；滚轮缩放。导出按内容范围自动裁切，并保留少量留白。");
            boardArea.Controls.Add(canvas, 0, 3);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
                RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 8, 0, 0), BackColor = Color.Transparent };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            status.Dock = DockStyle.Fill;
            status.AutoSize = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            status.Font = new Font(Font.FontFamily, 8F);
            status.ForeColor = Color.FromArgb(118, 130, 150);
            status.Margin = new Padding(0, 4, 0, 0);
            footer.Controls.Add(status, 0, 1);
            footer.SetColumnSpan(status, 2);
            var viewControls = new ResponsiveFlowPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            viewControls.Controls.Add(MakeButton("−", false, 30, delegate
            { if (!IsBusy) { canvas.ZoomOut(); canvas.Focus(); } }));
            zoomLabel.AutoSize = true;
            zoomLabel.Anchor = AnchorStyles.None;
            zoomLabel.ForeColor = Color.FromArgb(92, 113, 145);
            zoomLabel.Margin = Padding.Empty;
            viewControls.Controls.Add(zoomLabel);
            viewControls.Controls.Add(MakeButton("+", false, 30, delegate
            { if (!IsBusy) { canvas.ZoomIn(); canvas.Focus(); } }));
            viewControls.Controls.Add(MakeButton("适应内容", false, 76, delegate
            { if (!IsBusy) { canvas.FitToWindow(); canvas.Focus(); } }));
            StyleButton(panButton, false);
            panButton.Text = "拖动画布";
            panButton.Size = new Size(82, 34);
            panButton.Margin = new Padding(0, 0, 5, 5);
            panButton.Click += delegate
            { if (!IsBusy) { canvas.PanMode = !canvas.PanMode; canvas.Focus(); } };
            hints.SetToolTip(panButton, "开启后可在任意位置拖动画布；也可以按住空格或鼠标中键拖动");
            viewControls.Controls.Add(panButton);
            footer.Controls.Add(viewControls, 0, 0);
            var exports = new FlowLayoutPanel { AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            copyBoardButton = MakeButton("复制内容", false, 88, CopyBoard);
            saveBoardButton = MakeButton("导出内容", true, 94, SaveBoard);
            exports.Controls.Add(copyBoardButton);
            exports.Controls.Add(saveBoardButton);
            footer.Controls.Add(exports, 1, 0);
            boardArea.Controls.Add(footer, 0, 4);
            UpdateTools();
            UpdateView();
        }

        public void SetSourceImage(Bitmap image)
        {
            SetSourceImage(image, image == null ? 0 : --legacySourceId, false);
        }

        public void SetSourceImage(Bitmap image, long sourceId)
        {
            SetSourceImage(image, sourceId, false);
        }

        public void SetSourceImage(Bitmap image, long sourceId, bool isSaved)
        {
            if (image == null) { sourcePreview.SelectId(0); return; }
            sourcePreview.Upsert(image, sourceId, isSaved);
        }

        public void SetSourceSaved(long sourceId, bool saved)
        {
            ScreenshotQueueEntry entry = sourcePreview.Find(sourceId);
            if (entry == null) return;
            entry.Saved = saved;
            UpdateActions();
        }

        public bool SelectSource(long sourceId)
        {
            if (IsBusy) return false;
            return sourcePreview.SelectId(sourceId);
        }

        public bool RemoveSource(long sourceId)
        {
            if (IsBusy || !ContainsSourceId(sourceId)) return false;
            if (!IsSourceRetained(sourceId))
            {
                SetDialogBusy(true);
                try
                {
                    if (MessageBox.Show(FindForm(),
                        "这张截图尚未保存，也未加入画布。从队列删除后无法恢复，是否删除？",
                        "删除队列截图", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
                }
                finally { SetDialogBusy(false); }
            }
            sourcePreview.Remove(sourceId);
            Action<long> handler = SourceRemoved;
            if (handler != null) handler(sourceId);
            return true;
        }

        private void EditSelectedSource()
        {
            if (IsBusy || sourceImage == null) return;
            Action<long, Bitmap> queueHandler = EditQueueImageRequested;
            if (queueHandler != null) queueHandler(SelectedSourceId, sourceImage);
            else
            {
                EventHandler handler = EditScreenshotRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public void AddImage(Bitmap image)
        {
            canvas.AddImage(image);
            canvas.Tool = DrawingTool.Select;
            UpdateTools();
        }

        private void AddSourceImage()
        {
            if (IsBusy || sourceImage == null) return;
            try
            {
                AddImage(sourceImage);
                MarkSourceRetained();
            }
            catch (Exception error) { ShowError("无法加入画布", error); }
        }

        private void MarkSourceRetained()
        {
            long id = canvas.LastAddedImageId;
            ScreenshotQueueEntry entry = sourcePreview.SelectedEntry;
            if (entry != null && id > 0 && canvas.ContainsImageId(id)) entry.BoardImageIds.Add(id);
            UpdateActions();
            EventHandler handler = SourceAddedToBoard;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void SourceMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || IsBusy || sourceImage == null ||
                sourcePreview.HitTest(e.Location) != SelectedSourceId) return;
            sourceDragStart = e.Location;
            sourcePressed = true;
            sourcePreview.Capture = true;
        }

        private void ReleaseSourcePress()
        {
            sourcePressed = false;
            if (sourcePreview.Capture) sourcePreview.Capture = false;
        }

        private void SourceMouseMove(object sender, MouseEventArgs e)
        {
            if (!sourcePressed || e.Button != MouseButtons.Left || IsBusy || sourceImage == null) return;
            Size dragSize = SystemInformation.DragSize;
            Rectangle threshold = new Rectangle(sourceDragStart.X - dragSize.Width / 2,
                sourceDragStart.Y - dragSize.Height / 2, dragSize.Width, dragSize.Height);
            if (threshold.Contains(e.Location)) return;
            ReleaseSourcePress();
            int generation = sourceGeneration;
            sourceDragging = true;
            sourceDropAccepted = false;
            UpdateInteraction();
            try
            {
                // The drop target makes its own copy before DoDragDrop returns.
                // This transient snapshot remains owned by the source for the whole drag.
                using (Bitmap payload = (Bitmap)sourceImage.Clone())
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, true, payload);
                    sourcePreview.DoDragDrop(data, DragDropEffects.Copy);
                }
            }
            catch (Exception error) { ShowError("无法拖动截图", error); }
            finally
            {
                sourceDragging = false;
                UpdateInteraction();
            }
            if (sourceDropAccepted && generation == sourceGeneration && sourceImage != null)
                MarkSourceRetained();
        }

        private void WorkspaceDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !IsBusy && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void WorkspaceDragDrop(object sender, DragEventArgs e)
        {
            if (!IsBusy && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                ImportFiles((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        private Button MakeButton(string text, bool primary, int width, Action action,
            UiIcon icon = UiIcon.None)
        {
            var button = new RoundedButton { Text = text, Size = new Size(width, 34),
                Margin = new Padding(0, 0, 5, 5), Icon = icon };
            StyleButton(button, primary);
            if (action != null) button.Click += delegate { action(); };
            return button;
        }

        private void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? blue : Color.FromArgb(236, 241, 250);
            button.ForeColor = primary ? Color.White : Color.FromArgb(42, 63, 99);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        private void UpdateTools()
        {
            foreach (Control control in tools.Controls)
            {
                Button button = control as Button;
                if (button == null || !(button.Tag is DrawingTool)) continue;
                bool selected = !canvas.PanMode && (DrawingTool)button.Tag == canvas.Tool;
                SetButtonColors(button,
                    selected ? Color.FromArgb(226, 235, 255) : Color.FromArgb(251, 253, 255),
                    selected ? blue : Color.FromArgb(42, 63, 99));
            }
        }

        private void UpdateView()
        {
            zoomLabel.Text = Math.Round(canvas.ZoomPercent).ToString() + "%";
            bool pan = canvas.PanMode;
            if (viewStateInitialized && displayedPanMode == pan) return;
            viewStateInitialized = true;
            displayedPanMode = pan;
            SetButtonColors(panButton, pan ? Color.FromArgb(226, 235, 255) :
                Color.FromArgb(236, 241, 250), pan ? blue : Color.FromArgb(42, 63, 99));
            UpdateTools();
        }

        private static void SetButtonColors(Button button, Color background, Color foreground)
        {
            // ButtonBase may invalidate even when an identical color is assigned.
            if (button.BackColor.ToArgb() != background.ToArgb()) button.BackColor = background;
            if (button.ForeColor.ToArgb() != foreground.ToArgb()) button.ForeColor = foreground;
        }

        private void UpdateActions()
        {
            undo.Enabled = canvas.CanUndo && !IsBusy;
            delete.Enabled = canvas.HasSelection && !IsBusy;
            addSourceButton.Enabled = editSourceButton.Enabled = removeSourceButton.Enabled =
                sourceImage != null && !IsBusy;
            copyBoardButton.Enabled = saveBoardButton.Enabled = canvas.HasContent && !IsBusy;
            sourceTitle.Text = sourcePreview.Count == 0 ? "截图队列" : "截图队列  " + sourcePreview.Count;
            sourceInfo.Text = sourceImage == null ? "选择一张截图" :
                sourceImage.Width + " × " + sourceImage.Height + " 像素" +
                (SourceIsRetained ? " · 已保留" : "");
            Size exportSize = canvas.ExportSize;
            status.Text = canvas.HasContent
                ? canvas.ImageCount + " 张图片 · 导出 " + exportSize.Width + " × " + exportSize.Height +
                    " 像素" + (dirty ? " · 未导出" : "")
                : "自由画布 · 空白处拖动，滚轮缩放";
        }

        private void UpdateInteraction()
        {
            // Internal drags are busy for the host, but the board must still accept them.
            canvas.ImageDropEnabled = !dialogOpen && !capturing;
            sourcePreview.Cursor = sourceImage != null && !IsBusy ? Cursors.Hand : Cursors.Default;
            UpdateActions();
        }

        private void SetDialogBusy(bool value)
        {
            dialogOpen = value;
            UpdateInteraction();
        }

        private void RequestCapture(bool fullScreen)
        {
            if (IsBusy) return;
            Action<bool> handler = ScreenshotRequested;
            if (handler != null) handler(fullScreen);
        }

        private void OpenHistory()
        {
            if (IsBusy) return;
            Action handler = HistoryRequested;
            if (handler != null) handler();
        }

        public void ImportHistoryImages(string[] paths)
        {
            if (IsBusy || paths == null || paths.Length == 0) return;
            ImportFiles(paths);
        }

        private void ImportImages()
        {
            if (IsBusy) return;
            SetDialogBusy(true);
            try
            {
                using (var picker = new OpenFileDialog { Title = "添加图片到画布", Multiselect = true,
                    Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*" })
                    if (picker.ShowDialog(FindForm()) == DialogResult.OK) ImportFiles(picker.FileNames);
            }
            finally { SetDialogBusy(false); }
        }

        private void ImportFiles(string[] paths)
        {
            int added = 0;
            string failed = null;
            foreach (string path in paths)
            {
                try
                {
                    using (Image original = Image.FromFile(path))
                    {
                        ValidateImage(original);
                        using (Bitmap image = new Bitmap(original)) AddImage(image);
                        added++;
                    }
                }
                catch (Exception error) { failed = Path.GetFileName(path) + "：" + error.Message; }
            }
            if (failed != null)
            {
                bool wasDialogOpen = dialogOpen;
                SetDialogBusy(true);
                try
                {
                    MessageBox.Show(FindForm(), "已加入 " + added + " 张图片。部分文件无法导入。\r\n" + failed,
                        "轻截", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { SetDialogBusy(wasDialogOpen); }
            }
        }

        private static void ValidateImage(Image image)
        {
            if ((long)image.Width * image.Height > 80000000L)
                throw new InvalidOperationException("请选择不超过 8000 万像素的图片。");
        }

        private void PasteImage()
        {
            if (IsBusy) return;
            try
            {
                if (!Clipboard.ContainsImage()) { status.Text = "剪贴板中没有图片"; return; }
                using (Image original = Clipboard.GetImage())
                {
                    if (original == null) return;
                    ValidateImage(original);
                    using (Bitmap image = new Bitmap(original)) AddImage(image);
                }
            }
            catch (Exception error) { ShowError("无法粘贴图片", error); }
        }

        private void ResetBoard()
        {
            if (IsBusy || (!canvas.HasContent && !canvas.CanUndo)) return;
            SetDialogBusy(true);
            try
            {
                if (MessageBox.Show(FindForm(), "清空画布中的全部图片和标注？此操作不能撤销。",
                    "清空画布", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                canvas.ClearBoard();
                dirty = false;
                UpdateActions();
            }
            finally { SetDialogBusy(false); }
        }

        private string RequestText()
        {
            if (IsBusy) return null;
            SetDialogBusy(true);
            try
            {
                using (var dialog = new Form { Text = "添加画布文字", Font = Font,
                    AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                    ClientSize = new Size(420, 240), StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
                    MinimizeBox = false, ShowInTaskbar = false })
                {
                    var input = new TextBox { Multiline = true, AcceptsReturn = true, MaxLength = 2000,
                        ScrollBars = ScrollBars.Vertical, Location = new Point(20, 20), Size = new Size(380, 155) };
                    var ok = new Button { Text = "添加", DialogResult = DialogResult.OK,
                        Location = new Point(310, 190), Size = new Size(90, 32) };
                    var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
                        Location = new Point(210, 190), Size = new Size(90, 32) };
                    dialog.Controls.AddRange(new Control[] { input, cancel, ok });
                    dialog.AcceptButton = ok;
                    dialog.CancelButton = cancel;
                    return dialog.ShowDialog(FindForm()) == DialogResult.OK ? input.Text : null;
                }
            }
            finally { SetDialogBusy(false); }
        }

        private void SaveBoard()
        {
            if (IsBusy || !canvas.HasContent) return;
            SetDialogBusy(true);
            try
            {
                using (var picker = new SaveFileDialog { Title = "导出画布内容",
                    Filter = "PNG 图片|*.png|JPEG 图片|*.jpg", AddExtension = true, DefaultExt = "png",
                    FileName = "轻截画布_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png" })
                {
                    if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
                    using (Bitmap image = canvas.ExportImage())
                    using (var data = new MemoryStream())
                    {
                        image.Save(data, picker.FilterIndex == 2 ? ImageFormat.Jpeg : ImageFormat.Png);
                        File.WriteAllBytes(picker.FileName, data.ToArray());
                        RaiseCompositionProduced(image);
                    }
                    dirty = false;
                    UpdateActions();
                    status.Text = "已导出 · " + Path.GetFileName(picker.FileName);
                }
            }
            catch (Exception error) { ShowError("导出失败", error); }
            finally { SetDialogBusy(false); }
        }

        private void CopyBoard()
        {
            if (IsBusy || !canvas.HasContent) return;
            try
            {
                using (Bitmap image = canvas.ExportImage())
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, true, image);
                    Clipboard.SetDataObject(data, true, 5, 100);
                    RaiseCompositionProduced(image);
                }
                dirty = false;
                UpdateActions();
                status.Text = "已复制画布内容";
            }
            catch (Exception error) { ShowError("复制失败", error); }
        }

        private void RaiseCompositionProduced(Bitmap image)
        {
            Action<Bitmap> handler = CompositionProduced;
            if (handler != null) handler(image);
        }

        public bool ConfirmDiscard()
        {
            if (IsBusy) return false;
            int unretained = 0;
            foreach (ScreenshotQueueEntry entry in sourcePreview.Entries)
                if (!IsSourceRetained(entry.Id)) unretained++;
            if (!dirty && unretained == 0) return true;
            SetDialogBusy(true);
            try
            {
                string message = dirty ? "画布内容尚未导出或复制。" : "";
                if (unretained > 0) message += (message.Length == 0 ? "" : "\r\n") +
                    "截图队列中还有 " + unretained + " 张未保存、未加入画布的截图。";
                message += "\r\n\r\n退出轻截将丢弃这些内容，是否退出？";
                return MessageBox.Show(FindForm(), message,
                    "保留截图与画布？", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            finally { SetDialogBusy(false); }
        }

        public bool HandleShortcut(Keys keyData)
        {
            bool zoomIn = keyData == (Keys.Control | Keys.Add) ||
                keyData == (Keys.Control | Keys.Oemplus) ||
                keyData == (Keys.Control | Keys.Shift | Keys.Oemplus);
            bool zoomOut = keyData == (Keys.Control | Keys.Subtract) ||
                keyData == (Keys.Control | Keys.OemMinus);
            bool fit = keyData == (Keys.Control | Keys.D0) ||
                keyData == (Keys.Control | Keys.NumPad0);
            bool known = keyData == (Keys.Control | Keys.O) || keyData == (Keys.Control | Keys.V) ||
                keyData == (Keys.Control | Keys.S) || keyData == (Keys.Control | Keys.C) ||
                keyData == (Keys.Control | Keys.Z) ||
                keyData == Keys.Delete || keyData == Keys.Escape || zoomIn || zoomOut || fit;
            if (!known) return false;
            if (IsBusy || !Enabled) return true;
            if (zoomIn) canvas.ZoomIn();
            else if (zoomOut) canvas.ZoomOut();
            else if (fit) canvas.FitToWindow();
            else if (keyData == (Keys.Control | Keys.O)) ImportImages();
            else if (keyData == (Keys.Control | Keys.V)) PasteImage();
            else if (keyData == (Keys.Control | Keys.S)) SaveBoard();
            else if (keyData == (Keys.Control | Keys.C)) CopyBoard();
            else if (keyData == (Keys.Control | Keys.Z)) canvas.Undo();
            else if (keyData == Keys.Delete)
            {
                if (widthSelect.ContainsFocus) return false;
                if (sourcePreview.ContainsFocus) RemoveSource(SelectedSourceId);
                else canvas.DeleteSelected();
            }
            else { canvas.PanMode = false; canvas.Tool = DrawingTool.Select; UpdateTools(); }
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowError(string title, Exception error)
        {
            bool wasDialogOpen = dialogOpen;
            SetDialogBusy(true);
            try
            {
                MessageBox.Show(FindForm(), title + "。\r\n\r\n" + error.Message,
                    "轻截", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { SetDialogBusy(wasDialogOpen); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hints.Dispose();
                sourceImage = null;
            }
            base.Dispose(disposing);
        }

        /// <summary>A camera readout whose text never changes its layout bounds.</summary>
        private sealed class ZoomReadout : Control
        {
            private string value = "";

            public ZoomReadout()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                AutoSize = true;
                AccessibleRole = AccessibleRole.StaticText;
            }

            public override string Text
            {
                get { return value; }
                set
                {
                    string next = value ?? "";
                    if (this.value == next) return;
                    this.value = next;
                    AccessibleName = "画布缩放 " + next;
                    // A Label with AutoSize asks its parents to lay out on every
                    // percentage change. Only this small readout needs repainting.
                    Invalidate();
                }
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                Size text = TextRenderer.MeasureText("400%", Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return new Size(text.Width + Math.Max(4, Font.Height / 2),
                    text.Height + Math.Max(2, Font.Height / 4));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                TextRenderer.DrawText(e.Graphics, value, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>Reports wrapped height using the actual width imposed by the host.</summary>
        private sealed class ResponsiveFlowPanel : FlowLayoutPanel
        {
            public ResponsiveFlowPanel()
            {
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                WrapContents = true;
                FlowDirection = FlowDirection.LeftToRight;
                Margin = Padding.Empty;
                BackColor = Color.Transparent;
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                int naturalWidth = Padding.Horizontal;
                foreach (Control control in Controls)
                {
                    Size size = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
                    naturalWidth += size.Width + control.Margin.Horizontal;
                }
                // TableLayoutPanel asks for a 1-pixel width when measuring the
                // intrinsic size. Treat it as unconstrained instead of reporting
                // every button on its own row and reserving that inflated height.
                int outerWidth = proposedSize.Width > 1 && proposedSize.Width < Int32.MaxValue
                    ? proposedSize.Width : naturalWidth;
                int available = Math.Max(1, outerWidth - Padding.Horizontal);
                int x = 0, height = 0, rowHeight = 0;
                foreach (Control control in Controls)
                {
                    Size size = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
                    int itemWidth = size.Width + control.Margin.Horizontal;
                    int itemHeight = size.Height + control.Margin.Vertical;
                    if (x > 0 && x + itemWidth > available)
                    {
                        height += rowHeight;
                        x = 0;
                        rowHeight = 0;
                    }
                    x += itemWidth;
                    rowHeight = Math.Max(rowHeight, itemHeight);
                }
                return new Size(Math.Max(1, outerWidth), height + rowHeight + Padding.Vertical);
            }
        }

    }
}
