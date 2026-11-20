using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QingJie
{
    // WinForms visibility changes before the compositor has necessarily removed
    // a window's last presented surface. Keep it cloaked throughout capture.
    internal sealed class CaptureWindowVisibility : IDisposable
    {
        private const int TransitionsForceDisabled = 3;
        private const int Cloak = 13;
        private const int Cloaked = 14;
        private readonly List<Form> hidden = new List<Form>();
        private readonly List<Action> restoreAttributes = new List<Action>();

        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle,
            int attribute, ref int value, int bytes);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle,
            int attribute, out int value, int bytes);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        private delegate bool WindowVisitor(IntPtr handle, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowVisitor visitor, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);

        internal void Hide(Form window)
        {
            if (window == null || window.IsDisposed || !window.Visible) return;
            IntPtr handle = window.Handle;
            hidden.Add(window);
            try
            {
                int oldCloak, enabled = 1;
                if (DwmGetWindowAttribute(handle, Cloaked, out oldCloak, 4) == 0 &&
                    (oldCloak & 1) == 0 && DwmSetWindowAttribute(handle, Cloak, ref enabled, 4) == 0)
                    RememberReset(window, handle, Cloak);
                else if (DwmSetWindowAttribute(handle, TransitionsForceDisabled, ref enabled, 4) == 0)
                    RememberReset(window, handle, TransitionsForceDisabled);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            window.Hide();
        }

        private void RememberReset(Form window, IntPtr handle, int attribute)
        {
            restoreAttributes.Add(delegate {
                if (!window.IsHandleCreated || window.Handle != handle) return;
                int off = 0;
                DwmSetWindowAttribute(handle, attribute, ref off, 4);
            });
        }

        internal async Task WaitForCleanFrameAsync(CancellationToken token)
        {
            // Return to the message loop so the initiating click, popup menus and
            // tooltips can finish closing, then wait for two compositor updates.
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            // ToolTip.Active cancels future hovers; an already displayed native
            // tracking tip can remain visible even after its owner was hidden.
            EnumThreadWindows(GetCurrentThreadId(), DismissTooltip, IntPtr.Zero);
            foreach (Form form in hidden)
                if (form.IsHandleCreated && IsWindowVisible(form.Handle))
                    throw new InvalidOperationException("轻截窗口仍在显示，请重新开始截图。");
            await WaitForPresentationAsync(token);
        }

        private static bool DismissTooltip(IntPtr handle, IntPtr ignored)
        {
            if (!IsWindowVisible(handle)) return true;
            var name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            string type = name.ToString();
            if (type.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                type.StartsWith("WindowsForms10.tooltips_class32.", StringComparison.OrdinalIgnoreCase))
            {
                SendMessage(handle, 0x041C /* TTM_POP */, IntPtr.Zero, IntPtr.Zero);
                if (IsWindowVisible(handle)) ShowWindow(handle, 0 /* SW_HIDE */);
            }
            return true;
        }

        internal static async Task WaitForPresentationAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool composed = Flush();
            await Task.Delay(composed ? 16 : 140, token);
            if (composed && !Flush()) await Task.Delay(120, token);
        }

        private static bool Flush()
        {
            try { return DwmFlush() == 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public void Dispose()
        {
            foreach (Action restore in restoreAttributes) restore();
            restoreAttributes.Clear();
        }
    }
}
