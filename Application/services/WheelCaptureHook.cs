using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnapEye.Services
{
    /// <summary>
    /// Global low-level mouse hook (WH_MOUSE_LL) that lets the overlay keep scrolling its own
    /// content while click-through (WS_EX_TRANSPARENT) sends everything else to the app behind.
    /// Handles both vertical (<c>WM_MOUSEWHEEL</c>) and horizontal (<c>WM_MOUSEHWHEEL</c> —
    /// trackpad two-finger sideways swipe) wheel messages. Clicks and mouse movement are never
    /// touched.
    /// </summary>
    /// <remarks>
    /// Installed on the WPF UI thread, so the handler runs on the UI thread and may touch
    /// WPF elements. Keep it fast: Windows drops hooks that stall.
    /// </remarks>
    public sealed class WheelCaptureHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // Keep the delegate referenced so the GC can't collect it while the hook is live.
        private readonly LowLevelMouseProc _proc;
        private readonly Func<int, int, int, bool, bool> _handler;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _disposed;

        /// <param name="handler">
        /// (screenX, screenY, wheelDelta, isHorizontal) -> true to swallow the wheel event.
        /// Delta is positive when scrolling up / left (away from user / tilt left), in units
        /// of 120 per notch. <paramref name="isHorizontal"/> is true for trackpad sideways
        /// swipes (<c>WM_MOUSEHWHEEL</c>).
        /// </param>
        public WheelCaptureHook(Func<int, int, int, bool, bool> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _proc = HookCallback;
            try
            {
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                IntPtr hMod = module != null ? GetModuleHandle(module.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
                if (_hookId == IntPtr.Zero)
                    Console.WriteLine($"[WheelHook] Failed to install (Win32 {Marshal.GetLastWin32Error()}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WheelHook] Install error: {ex.Message}");
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    bool horizontal = msg == WM_MOUSEHWHEEL;
                    if (msg == WM_MOUSEWHEEL || horizontal)
                    {
                        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                        if (delta != 0 && _handler(data.pt.X, data.pt.Y, delta, horizontal))
                            return (IntPtr)1; // swallow
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WheelHook] Callback error: {ex.Message}");
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }
            }
            catch { /* disposal must never throw */ }
        }
    }
}
