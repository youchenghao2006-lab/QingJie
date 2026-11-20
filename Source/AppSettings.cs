using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace QingJie
{
    internal enum CloseBehavior { Ask, Background, Exit }

    internal sealed class AppSettings
    {
        internal Keys CaptureShortcut = Keys.F2;
        internal CloseBehavior Closing = CloseBehavior.Ask;
        internal string FilePath { get; private set; }
        internal string ShortcutText { get { return FormatShortcut(CaptureShortcut); } }
        internal uint NativeModifiers
        {
            get
            {
                uint value = 0x4000; // MOD_NOREPEAT
                if ((CaptureShortcut & Keys.Control) != 0) value |= 2;
                if ((CaptureShortcut & Keys.Shift) != 0) value |= 4;
                if ((CaptureShortcut & Keys.Alt) != 0) value |= 1;
                return value;
            }
        }

        internal AppSettings(string path) { FilePath = Path.GetFullPath(path); }

        internal static AppSettings Load(string path)
        {
            AppSettings settings = new AppSettings(path);
            try
            {
                if (!File.Exists(settings.FilePath) || new FileInfo(settings.FilePath).Length > 8192) return settings;
                foreach (string line in File.ReadAllLines(settings.FilePath, Encoding.UTF8))
                {
                    int split = line.IndexOf('='), value;
                    if (split <= 0 || !Int32.TryParse(line.Substring(split + 1), out value)) continue;
                    if (line.Substring(0, split) == "CaptureShortcut" && IsValidShortcut((Keys)value))
                        settings.CaptureShortcut = (Keys)value;
                    if (line.Substring(0, split) == "CloseBehavior" && Enum.IsDefined(typeof(CloseBehavior), value))
                        settings.Closing = (CloseBehavior)value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        internal void Save()
        {
            string directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, "CaptureShortcut=" + (int)CaptureShortcut +
                    Environment.NewLine + "CloseBehavior=" + (int)Closing + Environment.NewLine, Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static bool IsValidShortcut(Keys keys)
        {
            if ((keys & ~(Keys.KeyCode | Keys.Control | Keys.Shift | Keys.Alt)) != 0) return false;
            Keys key = keys & Keys.KeyCode;
            if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey ||
                key == Keys.Menu || key == Keys.LWin || key == Keys.RWin ||
                key == Keys.Escape || key == Keys.Tab || key == Keys.Return) return false;
            return (key >= Keys.F1 && key <= Keys.F24) ||
                ((keys & (Keys.Control | Keys.Alt)) != 0 &&
                ((key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                key == Keys.Space || key == Keys.PrintScreen));
        }

        internal static string FormatShortcut(Keys keys)
        {
            string text = "";
            if ((keys & Keys.Control) != 0) text += "Ctrl + ";
            if ((keys & Keys.Alt) != 0) text += "Alt + ";
            if ((keys & Keys.Shift) != 0) text += "Shift + ";
            Keys key = keys & Keys.KeyCode;
            return text + (key >= Keys.D0 && key <= Keys.D9
                ? ((int)key - (int)Keys.D0).ToString() : key.ToString());
        }
    }

    internal sealed class SettingsDialog : Form
    {
        private Keys selected;
        private readonly TextBox recorder;
        private readonly Label feedback;
        private readonly ComboBox closeChoice;
        internal SettingsDialog(AppSettings settings, Func<Keys, CloseBehavior, string> apply)
        {
            // Keep the 96-DPI design baseline until every child is present.
            // Early layout at high DPI otherwise consumes the scale factor
            // before the later, manually positioned controls are added.
            SuspendLayout();
            Text = "设置 · 轻截";
            Font = UiTheme.Font(10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            BackColor = Color.FromArgb(246, 249, 255);
            selected = settings.CaptureShortcut;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 8, Padding = new Padding(24), Margin = Padding.Empty,
                BackColor = Color.Transparent };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "截图快捷键", AutoSize = true, Dock = DockStyle.Fill,
                Font = UiTheme.Font(13f, FontStyle.Bold), ForeColor = UiTheme.Ink,
                Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            layout.Controls.Add(new Label { Text = "点击输入框后，按下 F 键或 Ctrl / Alt 组合键。",
                AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.SlateGray,
                Margin = new Padding(0, 0, 0, 12) }, 0, 1);

            var shortcutRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
                RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 40, Margin = new Padding(0, 0, 0, 16), BackColor = Color.Transparent };
            shortcutRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shortcutRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            shortcutRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            recorder = new TextBox { ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Font = UiTheme.Font(12f), Text = settings.ShortcutText,
                Margin = new Padding(0, 4, 10, 4) };
            recorder.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (AppSettings.IsValidShortcut(e.KeyData))
                {
                    selected = e.KeyData;
                    recorder.Text = AppSettings.FormatShortcut(selected);
                    feedback.Text = "";
                }
                else feedback.Text = "请选择 F1–F24，或带 Ctrl / Alt 的字母、数字组合。";
            };
            shortcutRow.Controls.Add(recorder, 0, 0);
            Button reset = MakeButton("恢复 F2", new Rectangle(0, 0, 108, 40));
            reset.Dock = DockStyle.Fill;
            reset.Margin = Padding.Empty;
            reset.Click += delegate { selected = Keys.F2; recorder.Text = "F2"; feedback.Text = ""; };
            shortcutRow.Controls.Add(reset, 1, 0);
            layout.Controls.Add(shortcutRow, 0, 2);

            layout.Controls.Add(new Label { Text = "关闭主窗口时", AutoSize = true,
                Dock = DockStyle.Fill, ForeColor = UiTheme.Ink,
                Margin = new Padding(0, 0, 0, 8) }, 0, 3);
            closeChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 8) };
            closeChoice.Items.AddRange(new object[] { "询问：后台运行 / 退出", "直接进入后台", "直接退出程序" });
            closeChoice.SelectedIndex = (int)settings.Closing;
            layout.Controls.Add(closeChoice, 0, 4);
            feedback = new Label { Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(0, 40),
                Margin = new Padding(0, 0, 0, 12), ForeColor = Color.FromArgb(181, 69, 89) };
            layout.Controls.Add(feedback, 0, 5);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 42, Margin = Padding.Empty, BackColor = Color.Transparent };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Button save = MakeButton("保存设置", new Rectangle(0, 0, 144, 42));
            save.Dock = DockStyle.Fill;
            save.Margin = Padding.Empty;
            save.BackColor = Color.FromArgb(66, 105, 232);
            save.ForeColor = Color.White;
            save.Click += delegate
            {
                string error = apply(selected, (CloseBehavior)closeChoice.SelectedIndex);
                if (!String.IsNullOrEmpty(error)) { feedback.Text = error; return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            actions.Controls.Add(save, 2, 0);
            Button cancel = MakeButton("取消", new Rectangle(0, 0, 98, 42));
            cancel.Dock = DockStyle.Fill;
            cancel.Margin = new Padding(0, 0, 10, 0);
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(cancel, 1, 0);
            layout.Controls.Add(actions, 0, 7);
            CancelButton = cancel;
            ResumeLayout(false);
            PerformLayout();
        }
        internal static Button MakeButton(string text, Rectangle bounds)
        {
            return new RoundedButton { Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(231, 238, 252), ForeColor = Color.FromArgb(47, 66, 103),
                Font = UiTheme.Font(10f), Cursor = Cursors.Hand };
        }
    }

    internal sealed class CloseChoiceDialog : Form
    {
        internal CloseChoiceDialog(string shortcut)
        {
            SuspendLayout();
            Text = "关闭窗口 · 轻截";
            Font = UiTheme.Font(10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, 232);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            BackColor = Color.FromArgb(246, 249, 255);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Padding = new Padding(24), Margin = Padding.Empty, BackColor = Color.Transparent };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "要在后台继续运行吗？",
                AutoSize = true, Dock = DockStyle.Fill, ForeColor = UiTheme.Ink,
                Font = UiTheme.Font(14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) }, 0, 0);
            layout.Controls.Add(new Label { Text = "后台时按 " + shortcut + " 可直接框选截图。\r\n截图队列和自由画布会继续保留。",
                AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.SlateGray,
                Margin = new Padding(0, 0, 0, 12) }, 0, 1);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 42, Margin = Padding.Empty, BackColor = Color.Transparent };
            for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Button background = SettingsDialog.MakeButton("后台运行", new Rectangle(0, 0, 144, 42));
            background.Dock = DockStyle.Fill;
            background.Margin = new Padding(0, 0, 8, 0);
            background.BackColor = Color.FromArgb(66, 105, 232);
            background.ForeColor = Color.White;
            background.DialogResult = DialogResult.Yes;
            Button exit = SettingsDialog.MakeButton("退出程序", new Rectangle(0, 0, 144, 42));
            exit.Dock = DockStyle.Fill;
            exit.Margin = new Padding(0, 0, 8, 0);
            exit.DialogResult = DialogResult.No;
            Button cancel = SettingsDialog.MakeButton("取消", new Rectangle(0, 0, 144, 42));
            cancel.Dock = DockStyle.Fill;
            cancel.Margin = Padding.Empty;
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(background, 0, 0);
            actions.Controls.Add(exit, 1, 0);
            actions.Controls.Add(cancel, 2, 0);
            layout.Controls.Add(actions, 0, 3);
            AcceptButton = background;
            CancelButton = cancel;
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
