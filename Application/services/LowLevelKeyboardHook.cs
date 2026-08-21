using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnapEye.Services
{
    /// <summary>
    /// A minimal global low-level keyboard hook (WH_KEYBOARD_LL) used to detect a
    /// multi-key chord that Win32 <c>RegisterHotKey</c> cannot express — specifically
    /// Backtick (`) + Space, which the user chose for mic mute.
    ///
    /// Why a hook (vs RegisterHotKey)? RegisterHotKey only allows Ctrl/Alt/Shift/Win as
    /// modifiers, so a "backtick + space" combo is impossible with it. A low-level hook
    /// can observe raw key up/down globally (even when another app is focused), which is
    /// required for muting during a meeting. The hook is read-only: it does NOT swallow
    /// keystrokes, so normal typing of backtick/space still works.
    /// </summary>
    /// <remarks>
    /// Must be created on a thread with a running message loop (the WPF UI thread). The
    /// callback fires on the UI thread; keep handlers fast. Dispose on window close to
    /// remove the hook.
    /// </remarks>
    public sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        // Virtual-key codes for the watched chord.
        public const int VK_SPACE = 0x20;
        public const int VK_OEM_3 = 0xC0; // backtick / grave accent / tilde key

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // Keep a reference to the delegate so the GC doesn't collect it while the hook
        // is installed (a collected delegate causes a hard crash in the hook callback).
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        private readonly HashSet<int> _watched;
        private readonly HashSet<int> _down = new();
        // Prevents auto-repeat from firing the chord continuously while keys are held.
        private bool _fired;
        private bool _disposed;

        /// <summary>Raised once when ALL watched keys are simultaneously held.</summary>
        public event EventHandler? ChordPressed;

        /// <summary>
        /// Create and install the hook. <paramref name="virtualKeys"/> is the set of VK
        /// codes that must all be held to trigger <see cref="ChordPressed"/>.
        /// </summary>
        public LowLevelKeyboardHook(params int[] virtualKeys)
        {
            _watched = new HashSet<int>(virtualKeys);
            _proc = HookCallback;
            Install();
        }

        private void Install()
        {
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
                if (_hookId == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[KbdHook] Failed to install (Win32 {err}).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KbdHook] Install error: {ex.Message}");
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)data.vkCode;

                    if (_watched.Contains(vk))
                    {
                        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                        {
                            _down.Add(vk);
                            if (!_fired && _down.Count >= _watched.Count && _down.IsSupersetOf(_watched))
                            {
                                _fired = true;
                                ChordPressed?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                        {
                            _down.Remove(vk);
                            // Re-arm once any watched key is released.
                            _fired = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KbdHook] Callback error: {ex.Message}");
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
            catch
            {
                // Disposal must never throw.
            }
        }
    }
}
