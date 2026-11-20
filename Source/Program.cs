using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace QingJie
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }

            bool created;
            using (var instance = new Mutex(true, @"Local\QingJie.Screenshot.1", out created))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (!created)
                {
                    MessageBox.Show("轻截已经在运行。请从任务栏或系统托盘打开。", "轻截",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("操作未完成，请重试。\r\n\r\n" + e.Exception.Message, "轻截",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                Application.Run(new MainForm());
                instance.ReleaseMutex();
            }
        }
    }
}
