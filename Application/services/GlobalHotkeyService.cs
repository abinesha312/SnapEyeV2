using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SnapEye.Services
{
    /// <summary>
    /// Wraps the Win32 RegisterHotKey API so SnapEye can react to keyboard shortcuts even
    /// when another application (Notepad, Chrome, Zoom, ...) has the foreground focus.
    /// The hidden message pump is provided by the owner window's HwndSource, so we don't
    /// need a separate top-level window.
    /// </summary>
    /// <remarks>
    /// Lifetime: create once after the owner window has a HWND (Window_Loaded or later),
    /// register hotkeys, then call Dispose() on Window_Closed to release the system slot
    /// so other apps can reuse the combo.
    /// </remarks>
    public sealed class GlobalHotkeyService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        // Modifier flag constants (see Win32 RegisterHotKey docs).
        public const uint MOD_ALT       = 0x0001;
        public const uint MOD_CONTROL   = 0x0002;
        public const uint MOD_SHIFT     = 0x0004;
        public const uint MOD_WIN       = 0x0008;
        // MOD_NOREPEAT prevents auto-repeat firing while the key is held down. Always on
        // for SnapEye's hotkeys — a held Ctrl+Shift+S shouldn't trigger 50 captures.
        public const uint MOD_NOREPEAT  = 0x4000;

        private readonly Window _owner;
        private readonly HwndSource _src;
        private readonly Dictionary<int, Action> _handlers = new();
        private int _nextId = 0xB000; // arbitrary base; Win32 hotkey ids must be unique per HWND.
        private bool _disposed;

        public event EventHandler<string>? HotkeyRegistrationFailed;

        public GlobalHotkeyService(Window owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));

            var helper = new WindowInteropHelper(_owner);
            helper.EnsureHandle(); // guarantees the HWND exists even if called pre-Loaded
            var src = HwndSource.FromHwnd(helper.Handle)
                      ?? throw new InvalidOperationException("Owner window has no HwndSource.");
            _src = src;
            _src.AddHook(WndProc);
        }

        /// <summary>
        /// Register a hotkey. Modifiers must be a bitwise OR of <c>MOD_*</c> constants
        /// (MOD_NOREPEAT is added automatically). Returns <c>true</c> on success; on
        /// failure raises <see cref="HotkeyRegistrationFailed"/> with a description so the
        /// caller can surface an inline error.
        /// </summary>
        public bool Register(uint modifiers, Key wpfKey, Action onPress)
        {
            ThrowIfDisposed();
            if (onPress == null) throw new ArgumentNullException(nameof(onPress));

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
            int id = _nextId++;
            uint flags = modifiers | MOD_NOREPEAT;

            bool ok = RegisterHotKey(_src.Handle, id, flags, vk);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                HotkeyRegistrationFailed?.Invoke(
                    this,
                    $"Hotkey {DescribeCombo(modifiers, wpfKey)} could not be registered (Win32 error {err}). It may already be in use by another application.");
                return false;
            }

            _handlers[id] = onPress;
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_handlers.TryGetValue(id, out var action))
                {
                    handled = true;
                    // Marshal back onto the dispatcher async — the WndProc is on the UI
                    // thread already, but BeginInvoke decouples handler exceptions from
                    // the message pump so a bad handler can't tear down the hook.
                    _owner.Dispatcher.BeginInvoke(action);
                }
            }
            return IntPtr.Zero;
        }

        private static string DescribeCombo(uint modifiers, Key key)
        {
            var parts = new List<string>(4);
            if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & MOD_SHIFT)   != 0) parts.Add("Shift");
            if ((modifiers & MOD_ALT)     != 0) parts.Add("Alt");
            if ((modifiers & MOD_WIN)     != 0) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                foreach (var id in _handlers.Keys)
                {
                    try { UnregisterHotKey(_src.Handle, id); } catch { /* best effort */ }
                }
                _handlers.Clear();
                _src.RemoveHook(WndProc);
            }
            catch
            {
                // Disposal must never throw — owner window may already be torn down.
            }
        }
    }
}
