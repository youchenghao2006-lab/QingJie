using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingJie
{
    public sealed class MainForm : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(
            IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        private readonly Color blue = Color.FromArgb(47, 102, 241);
        private readonly Color ink = Color.FromArgb(26, 38, 61);
        private readonly AnnotationCanvas canvas = new AnnotationCanvas();
        private readonly ComboBox delay = new ComboBox();
        private readonly Label status = new Label();
        private readonly Label shortcut = new Label();
        private readonly Button save = new RoundedButton();
        private readonly Button copy = new RoundedButton();
        private readonly Button undo = new RoundedButton();
        private readonly Button redo = new RoundedButton();
        private readonly Button deleteImageButton = new RoundedButton();
        private readonly Button addToBoardButton = new RoundedButton();
        private FlowLayoutPanel normalOptions, mosaicOptions;
        private BoardWorkspace board;
        private bool boardMode;
        private Panel workspaceHost;
        private Control editorWorkspace;
        private Button boardToggleButton;
        private Button editorNavigationButton;
        private readonly Label editorInfo = new Label();
        private long imageRevision;
        private long boardSourceRevision = -1;
        private long currentSourceId;
        private long nextSourceId;
        private readonly Dictionary<long, long> sourceVersions = new Dictionary<long, long>();
        private readonly FlowLayoutPanel tools = new FlowLayoutPanel();
        private readonly ToolTip hints = new ToolTip { InitialDelay = 450, ReshowDelay = 80, AutoPopDelay = 4500 };
        private NotifyIcon tray;
        private ContextMenuStrip trayMenu;
        private CancellationTokenSource pendingCapture;
        private RegionOverlay activeCaptureOverlay;
        private bool closeAfterCapture;
        private bool capturing, dialogOpen, dirty, hotkeyRegistered, settingImage;
        private bool backgroundMode, exitRequested, initialFramePrepared, exitApproved, waitingForArchives;
        private readonly AppSettings settings;
        private readonly HistoryStore history;
        private HistoryBrowserForm historyWindow;
        private QuickCaptureForm quickCapture;
        private readonly List<PinnedImageForm> pinnedImages = new List<PinnedImageForm>();
        private ToolStripMenuItem trayCaptureItem;
        private Button regionButton;
        private Button scrollCaptureButton;
        private const int HotkeyId = 1001;

        public MainForm()
        {
            SuspendLayout();
            settings = AppSettings.Load(Path.Combine(Application.StartupPath, "settings.ini"));
            history = new HistoryStore(Path.Combine(Application.StartupPath, "历史记录"));
            Text = "轻截 · 截图与标注";
            Font = UiTheme.Font(9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1280, 800);
            MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(240, 244, 252);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildInterface();
            BuildTray();
            RefreshShortcutLabels();
            canvas.CaptureRequested += delegate { BeginCapture(false); };
            canvas.ContentChanged += delegate
            {
                imageRevision++;
                if (!settingImage)
                {
                    dirty = true;
                    if (currentSourceId != 0)
                        sourceVersions[currentSourceId] = CurrentSourceVersion + 1;
                    if (board != null && currentSourceId != 0)
                        board.SetSourceSaved(currentSourceId, false);
                }
                UpdateActions();
            };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                    EnterBackground();
            };
            Load += delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, area.Width - 32), Math.Min(Height, area.Height - 32));
                Location = new Point(area.Left + (area.Width - Width) / 2,
                    area.Top + (area.Height - Height) / 2);
            };
            Shown += delegate { UpdateActions(); };
            ResumeLayout(true);
        }

        protected override void OnLoad(EventArgs e)
        {
            // Size/DPI and child layout settle before the first visible frame.
            // Warm the same static surfaces once; no whole-window compositing
            // flag or animation remains active during drawing and navigation.
            base.OnLoad(e);
            if (initialFramePrepared || DesignMode) return;
            initialFramePrepared = true;
            PerformLayout();
            using (Graphics graphics = CreateGraphics())
            using (Bitmap initial = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                PixelFormat.Format32bppPArgb))
            {
                initial.SetResolution(graphics.DpiX, graphics.DpiY);
                DrawToBitmap(initial, new Rectangle(Point.Empty, initial.Size));
            }
        }

        private void BuildInterface()
        {
            var root = new BackdropLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var sidebar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = Color.FromArgb(234, 240, 251), Padding = new Padding(16, 20, 16, 16) };
            root.Controls.Add(sidebar, 0, 0);
            var rail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 14,
                Margin = Padding.Empty, BackColor = Color.Transparent };
            rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int[] heights = { 42, 10, 14, 46, 40, 40, 40, 14, 22, 42, 42, 42 };
            foreach (int height in heights) rail.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            sidebar.Controls.Add(rail);
            rail.Controls.Add(new Label { Text = "轻截", Dock = DockStyle.Fill,
                Padding = new Padding(9, 0, 0, 0),
                ForeColor = Color.FromArgb(47, 85, 157), Font = new Font(Font.FontFamily, 23F, FontStyle.Bold) }, 0, 0);
            var region = MakeButton("框选截图", true);
            regionButton = region;
            ((RoundedButton)region).Icon = UiIcon.Capture;
            region.Dock = DockStyle.Fill;
            region.Margin = new Padding(0, 0, 0, 2);
            region.Click += delegate { BeginCapture(false); };
            hints.SetToolTip(region, "框选屏幕区域 · " + settings.ShortcutText);
            rail.Controls.Add(region, 0, 3);
            var full = MakeRailButton("截取当前屏幕", delegate { BeginCapture(true); });
            ((RoundedButton)full).Icon = UiIcon.Screen;
            hints.SetToolTip(full, "只截点击时鼠标所在的屏幕\n延时期间移动鼠标不会切换目标屏幕");
            rail.Controls.Add(full, 0, 4);
            scrollCaptureButton = MakeRailButton("滚动长截图", delegate { BeginScrollCapture(); });
            ((RoundedButton)scrollCaptureButton).Icon = UiIcon.ScrollCapture;
            hints.SetToolTip(scrollCaptureButton,
                "框选滚动区域后，在原页面向下滚动\n完成后可编辑、复制或保存，并保留到历史图片");
            rail.Controls.Add(scrollCaptureButton, 0, 5);
            Button open = MakeRailButton("打开图片", delegate { OpenImage(); });
            ((RoundedButton)open).Icon = UiIcon.Open;
            rail.Controls.Add(open, 0, 6);
            rail.Controls.Add(new Label { Text = "工作区", Dock = DockStyle.Fill,
                Padding = new Padding(12, 0, 0, 0), ForeColor = UiTheme.Muted,
                Font = new Font(Font.FontFamily, 8F) }, 0, 8);
            editorNavigationButton = MakeRailButton("截图编辑", ShowScreenshotEditor);
            ((RoundedButton)editorNavigationButton).Icon = UiIcon.Edit;
            rail.Controls.Add(editorNavigationButton, 0, 9);
            boardToggleButton = MakeRailButton("自由画布", delegate { OpenBoard(false); });
            rail.Controls.Add(boardToggleButton, 0, 10);
            ((RoundedButton)boardToggleButton).Icon = UiIcon.Board;
            Button browseHistory = MakeRailButton("历史图片", ShowHistory);
            ((RoundedButton)browseHistory).Icon = UiIcon.History;
            rail.Controls.Add(browseHistory, 0, 11);
            Button preferences = MakeRailButton("设置", ShowSettings);
            ((RoundedButton)preferences).Icon = UiIcon.Settings;
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Margin = Padding.Empty, Padding = new Padding(8, 0, 8, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottom.Controls.Add(new Label { Text = "截图延时", Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(128, 139, 156), Font = new Font(Font.FontFamily, 8F) }, 0, 0);
            delay.DropDownStyle = ComboBoxStyle.DropDownList;
            delay.Items.AddRange(new object[] { "立即截图", "3 秒后", "5 秒后", "10 秒后" });
            delay.SelectedIndex = 0;
            delay.Dock = DockStyle.Top;
            bottom.Controls.Add(delay, 0, 1);
            shortcut.Text = settings.ShortcutText + "\n随时开始截图";
            shortcut.Dock = DockStyle.Fill;
            shortcut.ForeColor = Color.FromArgb(143, 153, 169);
            shortcut.Font = new Font(Font.FontFamily, 8F);
            bottom.Controls.Add(shortcut, 0, 2);
            preferences.Margin = Padding.Empty;
            bottom.Controls.Add(preferences, 0, 3);
            rail.Controls.Add(bottom, 0, 13);
            UpdateWorkspaceNavigation();

            var workspace = new BackdropLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                Padding = new Padding(24, 20, 24, 16), ColumnCount = 1, RowCount = 4 };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            workspaceHost = new BufferedWorkspacePanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = BackColor };
            root.Controls.Add(workspaceHost, 1, 0);
            editorWorkspace = workspace;
            workspaceHost.Controls.Add(workspace);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent,
                ColumnCount = 2, RowCount = 1, Margin = new Padding(4, 0, 0, 0) };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var headingText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 2, Margin = Padding.Empty, BackColor = Color.Transparent };
            headingText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headingText.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            headingText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            headingText.Controls.Add(new Label { Text = "截图编辑", Dock = DockStyle.Fill,
                Margin = Padding.Empty, ForeColor = ink, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold) }, 0, 0);
            editorInfo.Text = String.Empty;
            editorInfo.Dock = DockStyle.Fill;
            editorInfo.Margin = new Padding(1, 3, 0, 0);
            editorInfo.ForeColor = UiTheme.Muted;
            editorInfo.Font = new Font(Font.FontFamily, 8.5F);
            editorInfo.AutoEllipsis = true;
            headingText.Controls.Add(editorInfo, 0, 1);
            heading.Controls.Add(headingText, 0, 0);
            var headingActions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44,
                ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Color.Transparent };
            headingActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            headingActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headingActions.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            StyleButton(deleteImageButton, false);
            deleteImageButton.Text = "删除";
            ((RoundedButton)deleteImageButton).Icon = UiIcon.Delete;
            deleteImageButton.BackColor = Color.FromArgb(246, 248, 253);
            deleteImageButton.ForeColor = Color.FromArgb(129, 101, 118);
            deleteImageButton.Dock = DockStyle.Fill;
            deleteImageButton.Margin = Padding.Empty;
            deleteImageButton.Enabled = false;
            deleteImageButton.Click += delegate { DeleteCurrentImage(); };
            hints.SetToolTip(deleteImageButton, "清空当前截图和标注 · Delete\n已保存的图片文件会保留");
            headingActions.Controls.Add(deleteImageButton, 1, 0);
            StyleButton(addToBoardButton, false);
            addToBoardButton.Text = "前往画布";
            ((RoundedButton)addToBoardButton).Icon = UiIcon.Board;
            addToBoardButton.Dock = DockStyle.Fill;
            addToBoardButton.Margin = new Padding(0, 0, 6, 0);
            addToBoardButton.Enabled = false;
            addToBoardButton.Click += delegate { OpenBoard(false); };
            hints.SetToolTip(addToBoardButton, "在同一窗口打开自由画布，把左侧截图拖到右侧任意位置");
            headingActions.Controls.Add(addToBoardButton, 0, 0);
            heading.Controls.Add(headingActions, 1, 0);
            workspace.Controls.Add(heading, 0, 0);

            var toolbar = new CardLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Color.FromArgb(251, 253, 255), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 0, 0, 12) };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            tools.Dock = DockStyle.Fill;
            tools.WrapContents = false;
            tools.Margin = Padding.Empty;
            string[] names = { "画框", "箭头", "画笔", "马赛克", "文字" };
            string[] tips = { "拖动绘制矩形", "拖动绘制箭头", "按住鼠标自由绘制",
                "选择样式后拖动框选区域", "点击图片添加文字" };
            DrawingTool[] values = { DrawingTool.Rectangle, DrawingTool.Arrow,
                DrawingTool.Pen, DrawingTool.Mosaic, DrawingTool.Text };
            for (int i = 0; i < names.Length; i++)
            {
                var button = MakeButton(names[i], false);
                button.Size = new Size(82, 36);
                ((RoundedButton)button).Icon = new UiIcon[] { UiIcon.Rectangle, UiIcon.Arrow,
                    UiIcon.Pen, UiIcon.Mosaic, UiIcon.Text }[i];
                button.Margin = new Padding(2, 0, 3, 0);
                button.Tag = values[i];
                hints.SetToolTip(button, tips[i] + "\n再次点击或按 Esc 退出标注");
                button.Click += delegate(object sender, EventArgs args)
                {
                    DrawingTool requested = (DrawingTool)((Button)sender).Tag;
                    canvas.Tool = canvas.Tool == requested ? DrawingTool.Select : requested;
                    UpdateToolButtons();
                    if (canvas.HasImage)
                        status.Text = canvas.Tool == DrawingTool.Select
                            ? "已退出标注"
                            : ((Button)sender).Text + " · " + hints.GetToolTip((Button)sender).Split('\n')[0];
                    canvas.Focus();
                };
                tools.Controls.Add(button);
            }
            StyleButton(undo, false);
            undo.Text = "撤销";
            undo.Size = new Size(72, 36);
            ((RoundedButton)undo).Icon = UiIcon.Undo;
            undo.Margin = new Padding(16, 0, 0, 0);
            undo.Click += delegate { canvas.Undo(); };
            hints.SetToolTip(undo, "撤销上一步 · Ctrl + Z");
            tools.Controls.Add(undo);
            StyleButton(redo, false);
            redo.Text = "重做";
            redo.Size = new Size(72, 36);
            ((RoundedButton)redo).Icon = UiIcon.Redo;
            redo.Margin = new Padding(6, 0, 0, 0);
            redo.Click += delegate { canvas.Redo(); };
            hints.SetToolTip(redo, "还原撤销的标注 · Ctrl + Y");
            tools.Controls.Add(redo);
            toolbar.Controls.Add(tools, 0, 0);

            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
                Margin = Padding.Empty, Padding = new Padding(4, 5, 0, 0) };
            options.Controls.Add(new Label { Text = "颜色", AutoSize = true, ForeColor = Color.FromArgb(110, 123, 143),
                Margin = new Padding(0, 4, 8, 0) });
            Color[] colors = { Color.FromArgb(244, 77, 86), blue, Color.FromArgb(30, 170, 125),
                Color.FromArgb(245, 172, 43), ink, Color.White };
            foreach (Color color in colors)
            {
                var swatch = new Button { BackColor = color, Size = new Size(23, 23), FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(0, 1, 7, 0), Tag = color, AccessibleName = "标注颜色 " + color.Name };
                swatch.FlatAppearance.BorderColor = Color.FromArgb(204, 211, 224);
                swatch.FlatAppearance.BorderSize = 1;
                swatch.Click += delegate(object sender, EventArgs args)
                {
                    canvas.StrokeColor = (Color)((Button)sender).Tag;
                    foreach (Control item in options.Controls)
                    {
                        var candidate = item as Button;
                        if (candidate != null && candidate.Tag is Color)
                            candidate.FlatAppearance.BorderSize = candidate == sender ? 3 : 1;
                    }
                };
                options.Controls.Add(swatch);
            }
            ((Button)options.Controls[1]).FlatAppearance.BorderSize = 3;
            options.Controls.Add(new Label { Text = "粗细", AutoSize = true, ForeColor = Color.FromArgb(110, 123, 143),
                Margin = new Padding(16, 4, 8, 0) });
            var width = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 65 };
            width.Items.AddRange(new object[] { "细", "中", "粗" });
            width.SelectedIndex = 1;
            width.SelectedIndexChanged += delegate { canvas.StrokeWidth = new float[] { 2, 4, 8 }[width.SelectedIndex]; };
            options.Controls.Add(width);
            normalOptions = options;
            var optionHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = Color.Transparent };
            optionHost.Controls.Add(options);
            mosaicOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
                Margin = Padding.Empty, Padding = new Padding(4, 5, 0, 0), Visible = false };
            mosaicOptions.Controls.Add(new Label { Text = "样式", AutoSize = true,
                ForeColor = Color.FromArgb(110, 123, 143), Margin = new Padding(0, 4, 8, 0) });
            var mosaicStyle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            mosaicStyle.Items.AddRange(new object[] { "像素方块", "柔化模糊", "圆点", "斜纹", "纯色遮挡" });
            mosaicStyle.SelectedIndex = 0;
            mosaicStyle.SelectedIndexChanged += delegate { canvas.MosaicStyle = (MosaicStyle)mosaicStyle.SelectedIndex; };
            mosaicOptions.Controls.Add(mosaicStyle);
            mosaicOptions.Controls.Add(new Label { Text = "强度", AutoSize = true,
                ForeColor = Color.FromArgb(110, 123, 143), Margin = new Padding(14, 4, 8, 0) });
            var mosaicSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 66 };
            mosaicSize.Items.AddRange(new object[] { "轻", "中", "强", "很强" });
            mosaicSize.SelectedIndex = 1;
            mosaicSize.SelectedIndexChanged += delegate { canvas.MosaicSize = new int[] { 12, 24, 48, 72 }[mosaicSize.SelectedIndex]; };
            hints.SetToolTip(mosaicSize, "调整方块、模糊或图案的尺寸；纯色遮挡始终完全覆盖");
            mosaicOptions.Controls.Add(mosaicSize);
            var coverColor = MakeButton("遮挡颜色", false);
            coverColor.Size = new Size(96, 28);
            coverColor.Margin = new Padding(12, 0, 0, 0);
            coverColor.Enabled = false;
            hints.SetToolTip(coverColor, "选择「纯色遮挡」时使用的覆盖颜色");
            mosaicStyle.SelectedIndexChanged += delegate
            {
                bool solid = canvas.MosaicStyle == MosaicStyle.Solid;
                coverColor.Enabled = solid;
                mosaicSize.Enabled = !solid;
            };
            coverColor.Click += delegate
            {
                using (var picker = new ColorDialog { Color = canvas.StrokeColor, FullOpen = true })
                {
                    dialogOpen = true;
                    try { if (picker.ShowDialog(this) == DialogResult.OK) canvas.StrokeColor = picker.Color; }
                    finally { dialogOpen = false; }
                }
            };
            mosaicOptions.Controls.Add(coverColor);
            optionHost.Controls.Add(mosaicOptions);
            toolbar.Controls.Add(optionHost, 0, 1);
            workspace.Controls.Add(toolbar, 0, 1);

            canvas.Dock = DockStyle.Fill;
            canvas.Margin = Padding.Empty;
            canvas.StrokeColor = colors[0];
            canvas.StrokeWidth = 4;
            canvas.Tool = DrawingTool.Select;
            workspace.Controls.Add(canvas, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent,
                Padding = new Padding(0, 18, 0, 0), Margin = Padding.Empty };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
            status.Dock = DockStyle.Fill;
            status.ForeColor = Color.FromArgb(118, 130, 150);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            footer.Controls.Add(status, 0, 0);
            StyleButton(copy, false);
            copy.Text = "复制图片";
            ((RoundedButton)copy).Icon = UiIcon.Copy;
            copy.Dock = DockStyle.Fill;
            copy.Margin = new Padding(0, 0, 10, 0);
            copy.Click += delegate { CopyImage(); };
            hints.SetToolTip(copy, "复制到剪贴板 · Ctrl + C");
            footer.Controls.Add(copy, 1, 0);
            StyleButton(save, true);
            save.Text = "保存图片";
            ((RoundedButton)save).Icon = UiIcon.Save;
            save.Dock = DockStyle.Fill;
            save.Margin = Padding.Empty;
            save.Click += delegate { SaveImage(); };
            hints.SetToolTip(save, "保存 PNG 或 JPEG · Ctrl + S");
            footer.Controls.Add(save, 2, 0);
            workspace.Controls.Add(footer, 0, 3);
            UpdateToolButtons();
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new RoundedButton { Text = text };
            StyleButton(button, primary);
            return button;
        }

        private void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? blue : Color.FromArgb(236, 240, 247);
            button.ForeColor = primary ? Color.White : ink;
            button.Font = new Font(Font.FontFamily, 9F);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        private Button MakeRailButton(string text, Action action)
        {
            var button = MakeButton(text, false);
            button.BackColor = Color.FromArgb(234, 240, 251);
            button.ForeColor = Color.FromArgb(73, 98, 143);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 0, 0, 2);
            ((RoundedButton)button).Navigation = true;
            button.Click += delegate { action(); };
            return button;
        }

        private void UpdateWorkspaceNavigation()
        {
            if (editorNavigationButton != null)
                ((RoundedButton)editorNavigationButton).Selected = !boardMode;
            if (boardToggleButton != null)
                ((RoundedButton)boardToggleButton).Selected = boardMode;
        }

        private void UpdateToolButtons()
        {
            if (normalOptions != null)
            {
                normalOptions.Visible = canvas.Tool != DrawingTool.Mosaic;
                mosaicOptions.Visible = canvas.Tool == DrawingTool.Mosaic;
            }
            foreach (Control control in tools.Controls)
            {
                var button = control as Button;
                if (button == null || !(button.Tag is DrawingTool)) continue;
                bool selected = (DrawingTool)button.Tag == canvas.Tool;
                button.BackColor = selected ? Color.FromArgb(226, 235, 255) : Color.FromArgb(251, 253, 255);
                button.ForeColor = selected ? blue : ink;
            }
        }

        private void BuildTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Font = UiTheme.Font(9f);
            trayCaptureItem = (ToolStripMenuItem)trayMenu.Items.Add("框选截图", null, delegate { BeginCapture(false); });
            trayMenu.Items.Add("截取当前屏幕", null, delegate { BeginCapture(true); });
            trayMenu.Items.Add("滚动长截图", null, delegate { BeginScrollCapture(); });
            trayMenu.Items.Add("打开轻截", null, delegate { RestoreWindow(); });
            trayMenu.Items.Add("历史图片", null, delegate { ShowHistory(); });
            trayMenu.Items.Add("设置", null, delegate { RestoreWindow(); ShowSettings(); });
            trayMenu.Items.Add("取消本次截图", null, delegate
            {
                if (pendingCapture != null) pendingCapture.Cancel();
            });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出轻截", null, delegate { exitRequested = true; Close(); });
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "轻截",
                Visible = true, ContextMenuStrip = trayMenu };
            tray.DoubleClick += delegate { RestoreWindow(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, settings.NativeModifiers,
                (uint)(settings.CaptureShortcut & Keys.KeyCode));
            RefreshShortcutLabels();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId)
            {
                BeginCaptureCore(false, true);
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == settings.CaptureShortcut)
            {
                if (!capturing && !dialogOpen) BeginCaptureCore(false, true);
                return true;
            }
            if (boardMode && board != null && !capturing && !dialogOpen)
            {
                if (board.HandleShortcut(keyData)) return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (!capturing && !dialogOpen)
            {
                if (keyData == Keys.Escape && canvas.HasImage && canvas.Tool != DrawingTool.Select)
                {
                    canvas.Tool = DrawingTool.Select;
                    UpdateToolButtons();
                    status.Text = "已退出标注";
                    return true;
                }
                if (keyData == (Keys.Control | Keys.S)) { SaveImage(); return true; }
                if (keyData == (Keys.Control | Keys.C)) { CopyImage(); return true; }
                if (keyData == (Keys.Control | Keys.Z)) { canvas.Undo(); return true; }
                if (keyData == (Keys.Control | Keys.Y)) { canvas.Redo(); return true; }
                if (keyData == (Keys.Control | Keys.O)) { OpenImage(); return true; }
                if (keyData == (Keys.Control | Keys.V)) { PasteImage(); return true; }
                if (keyData == Keys.Delete && !(ActiveControl is TextBoxBase) &&
                    !(ActiveControl is ComboBox) && !(ActiveControl is NumericUpDown))
                { DeleteCurrentImage(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void RestoreWindow()
        {
            if (IsDisposed || capturing || waitingForArchives) return;
            backgroundMode = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void EnterBackground()
        {
            backgroundMode = true;
            Hide();
            if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Hide();
            RefreshShortcutLabels();
        }

        private void RefreshShortcutLabels()
        {
            string label = settings.ShortcutText;
            canvas.CaptureShortcutText = label;
            shortcut.Text = label + (IsHandleCreated && !hotkeyRegistered
                ? " 被占用\n请在设置里更换" : "\n随时开始截图");
            if (regionButton != null) hints.SetToolTip(regionButton, "框选屏幕区域 · " + label);
            if (trayCaptureItem != null) trayCaptureItem.Text = "框选截图   " + label;
            if (tray != null) tray.Text = "轻截 · " + label + " 截图";
        }

        private void ShowSettings()
        {
            if (capturing || dialogOpen || waitingForArchives || (board != null && board.IsBusy)) return;
            dialogOpen = true;
            if (hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            try
            {
                using (SettingsDialog dialog = new SettingsDialog(settings, ApplySettings))
                    dialog.ShowDialog(this);
            }
            finally
            {
                dialogOpen = false;
                if (!hotkeyRegistered && !IsDisposed)
                    hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, settings.NativeModifiers,
                        (uint)(settings.CaptureShortcut & Keys.KeyCode));
                RefreshShortcutLabels();
            }
        }

        private string ApplySettings(Keys shortcutKey, CloseBehavior closing)
        {
            if (!AppSettings.IsValidShortcut(shortcutKey)) return "快捷键无效，请重新选择。";
            Keys previous = settings.CaptureShortcut;
            CloseBehavior previousClosing = settings.Closing;
            bool changed = previous != shortcutKey;
            if (changed || !hotkeyRegistered)
            {
                if (hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
                settings.CaptureShortcut = shortcutKey;
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, settings.NativeModifiers,
                    (uint)(shortcutKey & Keys.KeyCode));
                if (!hotkeyRegistered)
                {
                    settings.CaptureShortcut = previous;
                    hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, settings.NativeModifiers,
                        (uint)(previous & Keys.KeyCode));
                    RefreshShortcutLabels();
                    return "该快捷键已被其他程序使用，请换一个。";
                }
            }
            settings.Closing = closing;
            try { settings.Save(); }
            catch (Exception error)
            {
                if (changed)
                {
                    UnregisterHotKey(Handle, HotkeyId);
                    settings.CaptureShortcut = previous;
                    hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, settings.NativeModifiers,
                        (uint)(previous & Keys.KeyCode));
                }
                settings.Closing = previousClosing;
                RefreshShortcutLabels();
                return "设置未能保存：" + error.Message;
            }
            RefreshShortcutLabels();
            return null;
        }

        private bool CanReplaceImage()
        {
            if (!canvas.HasImage) return true;
            try
            {
                // The queue owns a full-resolution snapshot before the editor is
                // replaced. Capture several images without discarding the previous one.
                RefreshBoardSource();
                return board != null && boardSourceRevision == imageRevision &&
                    board.ContainsSourceId(currentSourceId);
            }
            catch (Exception error)
            {
                ShowError("无法保留当前截图", error);
                return false;
            }
        }

        private void BeginCapture(bool fullScreen)
        {
            BeginCaptureCore(fullScreen, false);
        }

        private enum CaptureMode { Standard, Quick, Scroll }

        private void BeginCaptureCore(bool fullScreen, bool preferQuick)
        {
            StartCapture(fullScreen, preferQuick ? CaptureMode.Quick : CaptureMode.Standard);
        }

        private void BeginScrollCapture()
        {
            StartCapture(false, CaptureMode.Scroll);
        }

        private async void StartCapture(bool fullScreen, CaptureMode mode, Rectangle? chosenArea = null)
        {
            if (capturing || dialogOpen || waitingForArchives || !Enabled || IsDisposed ||
                (board != null && board.IsBusy)) return;
            if (quickCapture != null && !quickCapture.IsDisposed)
            {
                quickCapture.Activate();
                return;
            }
            bool returnToMain = !backgroundMode && Visible && WindowState != FormWindowState.Minimized;
            bool preferQuick = mode == CaptureMode.Quick;
            bool scrolling = mode == CaptureMode.Scroll;
            // A visible window on the other monitor may not be the user's active app.
            bool focusMainAfterCapture = returnToMain && (!preferQuick || ContainsFocus);
            bool quickMode = !scrolling && (preferQuick || !returnToMain);
            bool historyWasVisible = historyWindow != null && !historyWindow.IsDisposed && historyWindow.Visible;
            bool succeeded = false;
            // Choose before a confirmation dialog, hiding, or delay can move focus.
            Rectangle targetMonitorBounds = fullScreen
                ? Screen.FromPoint(Cursor.Position).Bounds : Rectangle.Empty;
            if (!quickMode && !CanReplaceImage()) return;
            capturing = true;
            var cancellation = new CancellationTokenSource();
            pendingCapture = cancellation;
            var visibility = preferQuick ? null : new CaptureWindowVisibility();
            try
            {
                // An F2 selection has already honored the user's capture delay.
                int seconds = chosenArea.HasValue ? 0 : new int[] { 0, 3, 5, 10 }[delay.SelectedIndex];
                if (board != null && !preferQuick) board.CaptureInProgress = true;
                // F2 works on exactly what the user is looking at, including
                // this app. Explicit sidebar capture still hides its own UI.
                if (visibility != null)
                {
                    hints.Active = false;
                    if (trayMenu.Visible) trayMenu.Close();
                    visibility.Hide(historyWindow);
                    visibility.Hide(this);
                }
                tray.Text = seconds > 0 ? "轻截 · 等待 " + seconds + " 秒后截图" : "轻截 · 正在截图";
                if (seconds > 0) await Task.Delay(seconds * 1000, cancellation.Token);
                if (visibility != null)
                {
                    // The delay can still be canceled from the tray. Once sampling
                    // starts, its menu must stay out of the selection and live frames.
                    tray.ContextMenuStrip = null;
                    if (trayMenu.Visible) trayMenu.Close();
                    await visibility.WaitForCleanFrameAsync(cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (fullScreen)
                {
                    using (Bitmap monitor = ScreenCapture.CaptureArea(targetMonitorBounds))
                    {
                        if (quickMode) AcceptQuickCapture(monitor, targetMonitorBounds, monitor, targetMonitorBounds, focusMainAfterCapture);
                        else AcceptCapture(monitor);
                        succeeded = true;
                    }
                }
                else if (scrolling)
                {
                    succeeded = await CaptureScrollingRegionAsync(chosenArea, cancellation.Token);
                }
                else if (quickMode)
                {
                    succeeded = await BeginQuickSelectionAsync(focusMainAfterCapture, cancellation.Token);
                }
                else
                {
                    Rectangle area;
                    Bitmap selected;
                    if (!TrySelectCaptureRegion(false, cancellation.Token, out area, out selected)) return;
                    using (selected)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        AcceptCapture(selected);
                        succeeded = true;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsDisposed)
                    MessageBox.Show("截图未完成。\r\n\r\n" + error.Message, "轻截",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (visibility != null) visibility.Dispose();
                pendingCapture = null;
                cancellation.Dispose();
                capturing = false;
                if (!IsDisposed)
                {
                    RefreshShortcutLabels();
                    if (visibility != null)
                    {
                        tray.ContextMenuStrip = trayMenu;
                        hints.Active = true;
                    }
                    if (!preferQuick && board != null && !board.IsDisposed)
                        board.CaptureInProgress = false;
                    if (scrolling && succeeded) ShowScreenshotEditor();
                    if (scrolling ? succeeded || returnToMain :
                        !quickMode || (!succeeded && focusMainAfterCapture)) RestoreWindow();
                    if (!preferQuick && returnToMain && !succeeded && historyWasVisible &&
                        historyWindow != null && !historyWindow.IsDisposed)
                        historyWindow.Show(this);
                    if (closeAfterCapture)
                    {
                        closeAfterCapture = false;
                        BeginInvoke(new Action(Close));
                    }
                }
            }
        }

        private async Task<bool> CaptureScrollingRegionAsync(Rectangle? chosenArea, CancellationToken token)
        {
            Rectangle area = chosenArea.GetValueOrDefault();
            if (chosenArea.HasValue && !SystemInformation.VirtualScreen.Contains(area))
                throw new InvalidOperationException("显示器范围已改变，请重新框选要滚动的内容。");
            if (!chosenArea.HasValue)
            {
                Bitmap unused;
                if (!TrySelectCaptureRegion(true, token, out area, out unused)) return false;
            }
            if (area.Width < 32 || area.Height < 64)
                throw new InvalidOperationException("请框选至少 32 × 64 像素的滚动区域。");
            // Anchor the sequence to the live page after the overlay closes.
            // A frozen image taken before the user selects may be seconds old.
            await CaptureWindowVisibility.WaitForPresentationAsync(token);
            ScrollCaptureSession session;
            using (Bitmap first = ScreenCapture.CaptureArea(area))
            {
                token.ThrowIfCancellationRequested();
                tray.Text = "轻截 · 正在拼接长截图";
                session = new ScrollCaptureSession(area, first);
            }
            using (session)
            using (Bitmap result = await session.RunAsync(token))
            {
                if (result == null || IsDisposed || Disposing) return false;
                token.ThrowIfCancellationRequested();
                string note = "长截图已完成";
                if (result.Height <= area.Height)
                    note = session.LastFrameResult == ScrollFrameResult.NoMatch ?
                        "页面未能对齐，仅保留首屏" : "未检测到可拼接的滚动内容，仅保留首屏";
                else if (session.LastFrameResult == ScrollFrameResult.NoMatch)
                    note = "最后一屏未能对齐，已保留拼接成功的部分";
                else if (session.LastFrameResult == ScrollFrameResult.LimitReached)
                    note = "已达到尺寸上限，已保留拼接成功的部分";
                OpenAndArchiveCapture(result, note + " · " + result.Width + " × " + result.Height + " 像素");
                return true;
            }
        }

        private bool TrySelectCaptureRegion(bool scrolling, CancellationToken token, out Rectangle area, out Bitmap image)
        {
            area = Rectangle.Empty;
            image = null;
            Rectangle desktopBounds;
            using (Bitmap desktop = ScreenCapture.CaptureDesktop(out desktopBounds))
            using (var overlay = new RegionOverlay(desktop, desktopBounds, scrolling))
            {
                activeCaptureOverlay = overlay;
                try
                {
                    using (token.Register(delegate { if (!overlay.IsDisposed) overlay.Close(); }, true))
                    {
                        token.ThrowIfCancellationRequested();
                        if (overlay.ShowDialog() != DialogResult.OK) return false;
                        area = overlay.SelectedRegion;
                        area.Offset(desktopBounds.Location);
                        image = overlay.Result;
                        return true;
                    }
                }
                finally { activeCaptureOverlay = null; }
            }
        }

        private void AcceptCapture(Bitmap image)
        {
            OpenAndArchiveCapture(image, null);
        }

        private void OpenAndArchiveCapture(Bitmap image, string message)
        {
            SetCurrentImage(image);
            if (message != null) status.Text = message;
            ArchiveImage(image, HistoryCategory.Screenshot, currentSourceId, CurrentSourceVersion);
        }

        private async Task<bool> BeginQuickSelectionAsync(bool returnToMain, CancellationToken cancellation)
        {
            Rectangle bounds;
            Bitmap desktop = ScreenCapture.CaptureDesktop(out bounds);
            RegionOverlay overlay = null;
            bool transferred = false;
            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            try
            {
                overlay = new RegionOverlay(desktop, bounds) { KeepOpenAfterSelection = true };
                RegionOverlay backdrop = overlay;
                activeCaptureOverlay = overlay;
                overlay.SelectionCompleted += delegate(Bitmap selected, Rectangle relativeBounds)
                {
                    using (selected)
                    {
                        try
                        {
                            Rectangle screenBounds = relativeBounds;
                            screenBounds.Offset(bounds.Location);
                            QuickCaptureForm window = CreateQuickCapture(selected, screenBounds,
                                desktop, bounds, returnToMain, backdrop, true);
                            // The same screen pixels and dim backdrop survive the
                            // selection-to-edit transition. No second desktop copy.
                            window.Disposed += delegate
                            {
                                backdrop.Dispose();
                                desktop.Dispose();
                            };
                            transferred = true;
                            completion.TrySetResult(true);
                        }
                        catch (Exception error) { completion.TrySetException(error); }
                    }
                };
                overlay.FormClosed += delegate
                {
                    if (!transferred) completion.TrySetResult(false);
                };
                using (cancellation.Register(delegate
                {
                    if (!backdrop.IsDisposed) backdrop.Close();
                }, true))
                {
                    cancellation.ThrowIfCancellationRequested();
                    overlay.Show();
                    return await completion.Task;
                }
            }
            finally
            {
                if (ReferenceEquals(activeCaptureOverlay, overlay)) activeCaptureOverlay = null;
                if (!transferred)
                {
                    if (overlay != null) overlay.Dispose();
                    desktop.Dispose();
                }
            }
        }

        private void AcceptQuickCapture(Bitmap image, Rectangle screenBounds,
            Bitmap desktopSnapshot, Rectangle desktopBounds, bool returnToMain)
        {
            CreateQuickCapture(image, screenBounds, desktopSnapshot, desktopBounds, returnToMain, null, false);
        }

        private QuickCaptureForm CreateQuickCapture(Bitmap image, Rectangle screenBounds,
            Bitmap desktopSnapshot, Rectangle desktopBounds, bool returnToMain, Form backdrop,
            bool borrowDesktopSnapshot)
        {
            quickCapture = new QuickCaptureForm(image, screenBounds, desktopSnapshot, desktopBounds,
                borrowDesktopSnapshot);
            QuickCaptureForm window = quickCapture;
            window.ScrollRequested += delegate(Rectangle area) { StartCapture(false, CaptureMode.Scroll, area); };
            Task<bool> lastArchive = null;
            window.ImageAccepted += delegate(Bitmap result)
            {
                using (result) lastArchive = ArchiveImageAsync(result, HistoryCategory.Screenshot, 0, -1);
            };
            window.PinRequested += delegate(Bitmap result, Point position)
            {
                using (result)
                {
                    PinnedImageForm pin = new PinnedImageForm(result, position);
                    pinnedImages.Add(pin);
                    pin.FormClosed += delegate { pinnedImages.Remove(pin); pin.Dispose(); };
                    pin.Show();
                }
            };
            window.EditRequested += delegate(Bitmap result)
            {
                using (result) SetCurrentImage(result);
                MarkSourceAfterArchive(lastArchive, currentSourceId, CurrentSourceVersion);
                // This callback can arrive from a button after capture has finished.
                RestoreWindow();
                ShowScreenshotEditor();
            };
            window.FormClosed += delegate
            {
                if (ReferenceEquals(quickCapture, window)) quickCapture = null;
                window.Dispose();
                if (!window.ContinuingWithScroll && returnToMain && !backgroundMode && !IsDisposed && !Disposing &&
                    !exitRequested && !waitingForArchives)
                    RestoreWindow();
            };
            try
            {
                if (backdrop == null) window.Show();
                else window.Show(backdrop);
                return window;
            }
            catch
            {
                if (ReferenceEquals(quickCapture, window)) quickCapture = null;
                window.Dispose();
                throw;
            }
        }

        private async void ArchiveImage(Bitmap image, HistoryCategory category, long sourceId, long revision)
        {
            await ArchiveImageAsync(image, category, sourceId, revision);
        }

        private async Task<bool> ArchiveImageAsync(Bitmap image, HistoryCategory category, long sourceId, long revision)
        {
            try
            {
                Task<string> pending = history.SaveAsync(image, category);
                await pending;
                MarkSourceSaved(sourceId, revision);
                return true;
            }
            catch (Exception error)
            {
                if (IsDisposed || Disposing) return false;
                status.Text = "历史保存失败，请复制或另存图片：" + error.Message;
                if (backgroundMode && tray != null)
                    tray.ShowBalloonTip(4000, "历史图片未能保存", "截图仍可复制或另存，请检查历史文件夹权限与磁盘空间。",
                        ToolTipIcon.Warning);
                return false;
            }
        }

        private async void MarkSourceAfterArchive(Task<bool> archive, long sourceId, long revision)
        {
            if (archive != null && await archive) MarkSourceSaved(sourceId, revision);
        }

        private void MarkSourceSaved(long sourceId, long revision)
        {
            if (IsDisposed || Disposing) return;
            long savedVersion;
            if (sourceId != 0 && sourceVersions.TryGetValue(sourceId, out savedVersion) &&
                savedVersion == revision)
            {
                if (currentSourceId == sourceId) dirty = false;
                if (board != null && !board.IsDisposed) board.SetSourceSaved(sourceId, true);
            }
        }

        private void ShowHistory()
        {
            if (capturing || dialogOpen || waitingForArchives || (board != null && board.IsBusy)) return;
            RestoreWindow();
            if (historyWindow != null && !historyWindow.IsDisposed)
            {
                historyWindow.Show();
                historyWindow.RefreshHistory();
                historyWindow.Activate();
                return;
            }
            historyWindow = new HistoryBrowserForm(history);
            HistoryBrowserForm window = historyWindow;
            window.EditRequested += delegate(string path)
            {
                if (waitingForArchives || capturing || dialogOpen || (board != null && board.IsBusy)) return;
                try
                {
                    if (!CanReplaceImage()) return;
                    using (Bitmap image = history.LoadImage(path)) SetCurrentImage(image);
                    ShowScreenshotEditor();
                    RestoreWindow();
                    window.Hide();
                }
                catch (Exception error) { ShowError("无法导入历史图片", error); }
            };
            window.AddToBoardRequested += delegate(string[] paths)
            {
                if (waitingForArchives || capturing || dialogOpen || (board != null && board.IsBusy)) return;
                OpenBoard(false);
                board.ImportHistoryImages(paths);
                window.Hide();
                RestoreWindow();
            };
            window.FormClosed += delegate
            {
                if (ReferenceEquals(historyWindow, window)) historyWindow = null;
                window.Dispose();
            };
            window.Show(this);
        }

        private void EnsureBoard()
        {
            if (board != null && !board.IsDisposed) return;
            boardSourceRevision = -1;
            board = new BoardWorkspace { Dock = DockStyle.Fill, Visible = false,
                Bounds = workspaceHost.ClientRectangle };
            board.ScreenshotRequested += delegate(bool fullScreen) { BeginCapture(fullScreen); };
            board.HistoryRequested += ShowHistory;
            board.CompositionProduced += delegate(Bitmap image) { ArchiveImage(image, HistoryCategory.Board, 0, -1); };
            board.EditQueueImageRequested += EditQueuedScreenshot;
            board.SourceRemoved += delegate(long sourceId)
            {
                sourceVersions.Remove(sourceId);
                if (sourceId != currentSourceId) return;
                ClearCurrentEditor();
            };
            board.SourceAddedToBoard += delegate
            {
                status.Text = "截图已加入画板";
            };
            workspaceHost.Controls.Add(board);
        }

        private void OpenBoard(bool addCurrent)
        {
            if (capturing || dialogOpen || (board != null && board.IsBusy)) return;
            EnsureBoard();
            RefreshBoardSource();
            if (addCurrent && canvas.HasImage)
                using (Bitmap image = canvas.ExportImage()) board.AddImage(image);
            boardMode = true;
            SwitchWorkspace(board, editorWorkspace);
            UpdateWorkspaceNavigation();
            Text = "轻截 · 截图与自由画布";
            board.Focus();
        }

        private void ShowScreenshotEditor()
        {
            if (capturing || dialogOpen || (board != null && board.IsBusy)) return;
            boardMode = false;
            SwitchWorkspace(editorWorkspace, board);
            UpdateWorkspaceNavigation();
            Text = "轻截 · 截图与标注";
            canvas.Focus();
        }

        private void SwitchWorkspace(Control next, Control previous)
        {
            // Keep both pages at their final viewport size. Hiding/showing a page
            // must not resize its drawing surface and discard a warm render cache.
            // SuspendLayout alone still permits synchronous paints between the
            // visibility and z-order changes; coalesce those into one final paint.
            IntPtr redrawHost = workspaceHost.IsHandleCreated && workspaceHost.Visible
                ? workspaceHost.Handle : IntPtr.Zero;
            if (redrawHost != IntPtr.Zero)
                SendMessage(redrawHost, 0x000B /* WM_SETREDRAW */, IntPtr.Zero, IntPtr.Zero);
            workspaceHost.SuspendLayout();
            try
            {
                if (next.Bounds != workspaceHost.ClientRectangle)
                    next.Bounds = workspaceHost.ClientRectangle;
                next.BringToFront();
                next.Visible = true;
                if (previous != null && previous != next) previous.Visible = false;
            }
            finally
            {
                try { workspaceHost.ResumeLayout(true); }
                finally
                {
                    if (redrawHost != IntPtr.Zero && !workspaceHost.IsDisposed)
                    {
                        SendMessage(redrawHost, 0x000B /* WM_SETREDRAW */, new IntPtr(1), IntPtr.Zero);
                        workspaceHost.Invalidate(true);
                    }
                }
            }
        }

        private void EditQueuedScreenshot(long sourceId, Bitmap image)
        {
            if (capturing || dialogOpen || image == null || board.IsBusy) return;
            if (sourceId != currentSourceId)
            {
                if (!CanReplaceImage()) return;
                bool saved = board.IsSourceSaved(sourceId);
                settingImage = true;
                try { canvas.SetImage(image); }
                finally { settingImage = false; }
                currentSourceId = sourceId;
                boardSourceRevision = imageRevision;
                dirty = !saved;
                UpdateActions();
            }
            board.SelectSource(sourceId);
            ShowScreenshotEditor();
        }

        private void RefreshBoardSource()
        {
            if (!canvas.HasImage || currentSourceId == 0) return;
            EnsureBoard();
            if (boardSourceRevision == imageRevision) return;
            using (Bitmap source = canvas.ExportImage())
                board.SetSourceImage(source, currentSourceId, !dirty);
            boardSourceRevision = imageRevision;
        }

        private void SetCurrentImage(Bitmap bitmap)
        {
            if (bitmap == null) { ClearCurrentEditor(); return; }
            RefreshBoardSource();
            EnsureBoard();
            long sourceId = checked(nextSourceId + 1);
            settingImage = true;
            try { canvas.SetImage(bitmap); }
            finally { settingImage = false; }
            nextSourceId = currentSourceId = sourceId;
            sourceVersions[sourceId] = 0;
            dirty = true;
            // A fresh capture has no annotations: enqueue its pixels directly,
            // avoiding a redundant full-resolution ExportImage allocation.
            board.SetSourceImage(bitmap, sourceId, false);
            boardSourceRevision = imageRevision;
            UpdateActions();
        }

        private void UpdateActions()
        {
            bool hasImage = canvas.HasImage;
            copy.Enabled = save.Enabled = hasImage;
            deleteImageButton.Enabled = hasImage;
            addToBoardButton.Enabled = hasImage;
            undo.Enabled = canvas.CanUndo;
            redo.Enabled = canvas.CanRedo;
            foreach (Control control in tools.Controls)
                if (control.Tag is DrawingTool) control.Enabled = hasImage;
            if (normalOptions != null) normalOptions.Enabled = hasImage;
            if (mosaicOptions != null) mosaicOptions.Enabled = hasImage;
            editorInfo.Text = hasImage
                ? canvas.ImageSize.Width + " × " + canvas.ImageSize.Height + " 像素 · 原始分辨率"
                : String.Empty;
            status.Text = hasImage
                ? "选择工具标注，完成后复制或保存"
                : "图片仅在本地处理";
        }

        private long CurrentSourceVersion
        {
            get
            {
                long value;
                return sourceVersions.TryGetValue(currentSourceId, out value) ? value : 0;
            }
        }

        private void DeleteCurrentImage()
        {
            if (!canvas.HasImage || capturing || dialogOpen || !Enabled ||
                (board != null && board.IsBusy)) return;
            try
            {
                RefreshBoardSource();
                if (board != null && board.ContainsSourceId(currentSourceId))
                {
                    board.RemoveSource(currentSourceId);
                    return;
                }
                ClearCurrentEditor();
            }
            catch (Exception error) { ShowError("无法删除截图", error); }
        }

        private void ClearCurrentEditor()
        {
            settingImage = true;
            try
            {
                canvas.SetImage(null);
                canvas.Tool = DrawingTool.Select;
                dirty = false;
            }
            finally { settingImage = false; }
            currentSourceId = 0;
            boardSourceRevision = imageRevision;
            UpdateToolButtons();
            UpdateActions();
            status.Text = "已删除当前截图 · 可以重新截图";
            if (!boardMode) canvas.Focus();
        }

        private void OpenImage()
        {
            if (capturing || dialogOpen || !CanReplaceImage()) return;
            RestoreWindow();
            dialogOpen = true;
            try
            {
                using (var picker = new OpenFileDialog { Title = "打开图片", Filter =
                    "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*" })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    using (Image original = Image.FromFile(picker.FileName))
                    {
                        if ((long)original.Width * original.Height > 80000000L)
                            throw new InvalidOperationException("图片过大，请选择不超过 8000 万像素的图片。");
                        using (var bitmap = new Bitmap(original)) SetCurrentImage(bitmap);
                    }
                }
            }
            catch (Exception error) { ShowError("无法打开图片", error); }
            finally { dialogOpen = false; }
        }

        private void PasteImage()
        {
            if (capturing || dialogOpen || !Enabled) return;
            try
            {
                if (!Clipboard.ContainsImage()) { status.Text = "剪贴板中没有图片"; return; }
                if (!CanReplaceImage()) return;
                using (Image original = Clipboard.GetImage())
                {
                    if (original == null) return;
                    if ((long)original.Width * original.Height > 80000000L)
                        throw new InvalidOperationException("图片过大，请选择不超过 8000 万像素的图片。");
                    using (var bitmap = new Bitmap(original)) SetCurrentImage(bitmap);
                }
            }
            catch (Exception error) { ShowError("无法粘贴图片", error); }
        }

        private void SaveImage()
        {
            if (!canvas.HasImage || capturing || dialogOpen) return;
            dialogOpen = true;
            try
            {
                using (var picker = new SaveFileDialog { Title = "保存截图",
                    Filter = "PNG 图片（无损）|*.png|JPEG 图片|*.jpg", AddExtension = true,
                    DefaultExt = "png", FileName = "轻截_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    using (Bitmap result = canvas.ExportImage())
                    {
                        ImageFormat format = picker.FilterIndex == 2 ? ImageFormat.Jpeg : ImageFormat.Png;
                        // Finish encoding before opening the destination.
                        using (var data = new MemoryStream())
                        {
                            result.Save(data, format);
                            File.WriteAllBytes(picker.FileName, data.ToArray());
                        }
                        ArchiveImage(result, HistoryCategory.Screenshot, currentSourceId, CurrentSourceVersion);
                    }
                    dirty = false;
                    if (board != null) board.SetSourceSaved(currentSourceId, true);
                    status.Text = "已保存 · " + Path.GetFileName(picker.FileName);
                }
            }
            catch (Exception error) { ShowError("保存失败", error); }
            finally { dialogOpen = false; }
        }

        private void CopyImage()
        {
            if (!canvas.HasImage || capturing || dialogOpen) return;
            try
            {
                using (Bitmap result = canvas.ExportImage())
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, true, result);
                    Clipboard.SetDataObject(data, true, 5, 100);
                    ArchiveImage(result, HistoryCategory.Screenshot, currentSourceId, CurrentSourceVersion);
                }
                dirty = false;
                if (board != null) board.SetSourceSaved(currentSourceId, true);
                status.Text = "已复制 · 可粘贴到聊天、文档或邮件";
            }
            catch (Exception error) { ShowError("复制失败，请稍后重试", error); }
        }

        private void ShowError(string message, Exception error)
        {
            bool wasDialogOpen = dialogOpen;
            dialogOpen = true;
            try
            {
                MessageBox.Show(this, message + "。\r\n\r\n" + error.Message, "轻截",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { dialogOpen = wasDialogOpen; }
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (capturing && e.CloseReason == CloseReason.UserClosing)
            {
                closeAfterCapture = true;
                if (pendingCapture != null) pendingCapture.Cancel();
                if (activeCaptureOverlay != null && !activeCaptureOverlay.IsDisposed)
                {
                    activeCaptureOverlay.DialogResult = DialogResult.Cancel;
                    activeCaptureOverlay.Close();
                }
                e.Cancel = true;
                return;
            }
            if ((dialogOpen || (board != null && board.IsBusy)) &&
                e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            if (e.CloseReason == CloseReason.UserClosing && waitingForArchives)
            {
                e.Cancel = true;
                return;
            }
            if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
            {
                DialogResult choice = settings.Closing == CloseBehavior.Background ? DialogResult.Yes :
                    settings.Closing == CloseBehavior.Exit ? DialogResult.No : DialogResult.None;
                if (choice == DialogResult.None)
                {
                    dialogOpen = true;
                    try
                    {
                        using (CloseChoiceDialog dialog = new CloseChoiceDialog(settings.ShortcutText))
                            choice = dialog.ShowDialog(this);
                    }
                    finally { dialogOpen = false; }
                }
                if (choice != DialogResult.No)
                {
                    e.Cancel = true;
                    if (choice == DialogResult.Yes) EnterBackground();
                    return;
                }
                exitRequested = true;
            }
            if (e.CloseReason == CloseReason.UserClosing && !exitApproved)
            {
                if (!CanReplaceImage() || (board != null && !board.ConfirmDiscard()))
                {
                    exitRequested = false;
                    e.Cancel = true;
                    return;
                }
                exitApproved = true;
            }
            Task pendingHistory = history.FlushAsync();
            if (e.CloseReason == CloseReason.UserClosing && !pendingHistory.IsCompleted)
            {
                e.Cancel = true;
                waitingForArchives = true;
                SetExitInteractionEnabled(false);
                status.Text = "正在完成历史图片保存…";
                try { await pendingHistory; }
                catch (Exception error) { if (!IsDisposed) ShowError("部分历史图片未能保存", error); }
                if (!IsDisposed) BeginInvoke(new Action(delegate
                {
                    waitingForArchives = false;
                    SetExitInteractionEnabled(true);
                    Close();
                }));
                return;
            }
            if (pendingCapture != null) pendingCapture.Cancel();
            base.OnFormClosing(e);
        }

        private void SetExitInteractionEnabled(bool value)
        {
            Enabled = value;
            if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Enabled = value;
            if (quickCapture != null && !quickCapture.IsDisposed)
            {
                quickCapture.Enabled = value;
                foreach (Form owned in quickCapture.OwnedForms) owned.Enabled = value;
            }
            foreach (PinnedImageForm pin in pinnedImages)
            {
                if (pin.IsDisposed) continue;
                pin.Enabled = value;
                foreach (Form owned in pin.OwnedForms) owned.Enabled = value;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (pendingCapture != null) pendingCapture.Cancel();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayMenu != null) trayMenu.Dispose();
                if (quickCapture != null) quickCapture.Dispose();
                foreach (PinnedImageForm pin in pinnedImages.ToArray()) pin.Dispose();
                pinnedImages.Clear();
                if (historyWindow != null) historyWindow.Dispose();
                history.Dispose();
                hints.Dispose();
                if (board != null) board.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
