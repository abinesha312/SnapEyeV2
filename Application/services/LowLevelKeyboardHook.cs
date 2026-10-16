using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace SnapEye.Services
{
    /// <summary>
    /// A minimal global low-level keyboard hook (WH_KEYBOARD_LL) used to detect a
    /// multi-key chord that Win32 <c>RegisterHotKey</c> cannot express — for example
    /// Backtick (`) + Space (mic mute), or Ctrl + Space + greater/less (opacity).
    ///
    /// Why a hook (vs RegisterHotKey)? RegisterHotKey only allows Ctrl/Alt/Shift/Win as
    /// modifiers, so chords that include Space as a held key (or a non-modifier like
    /// backtick) are impossible with it. A low-level hook can observe raw key up/down
    /// globally (even when another app is focused). The hook is read-only: it does NOT
    /// swallow keystrokes, so normal typing still works.
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

        // Virtual-key codes for the watched chords.
        public const int VK_CONTROL = 0x11;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;
        public const int VK_SPACE = 0x20;
        public const int VK_OEM_COMMA = 0xBC;  // , / < key
        public const int VK_OEM_PERIOD = 0xBE; // . / > key
        public const int VK_OEM_3 = 0xC0;      // backtick / grave accent / tilde key

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

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Keep a reference to the delegate so the GC doesn't collect it while the hook
        // is installed (a collected delegate causes a hard crash in the hook callback).
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        // Each group is an OR-set: any one key in the group counts as that slot held.
        // All groups must be satisfied to fire. Example for opacity-up:
        //   { SPACE }, { OEM_PERIOD }
        private readonly List<HashSet<int>> _groups;
        private readonly HashSet<int> _watched;
        private readonly bool _requireControl;
        private readonly HashSet<int> _down = new();
        // Prevents auto-repeat from firing the chord continuously while keys are held.
        private bool _fired;
        private bool _disposed;

        /// <summary>Raised once when ALL watched key groups are simultaneously held.</summary>
        public event EventHandler? ChordPressed;

        /// <summary>
        /// Create and install the hook. <paramref name="virtualKeys"/> is the set of VK
        /// codes that must all be held to trigger <see cref="ChordPressed"/>.
        /// </summary>
        public LowLevelKeyboardHook(params int[] virtualKeys)
            : this(requireControl: false, virtualKeys.Select(vk => new[] { vk }).ToArray())
        {
        }

        /// <summary>
        /// Create and install the hook with optional Ctrl requirement and OR-groups of keys.
        /// Every group must have at least one of its keys held; when
        /// <paramref name="requireControl"/> is true, Ctrl (left or right) must also be down.
        /// </summary>
        public LowLevelKeyboardHook(bool requireControl, params int[][] keyGroups)
        {
            if (keyGroups == null || keyGroups.Length == 0)
                throw new ArgumentException("At least one key group is required.", nameof(keyGroups));

            _requireControl = requireControl;
            _groups = keyGroups.Select(g => new HashSet<int>(g)).ToList();
            _watched = new HashSet<int>(_groups.SelectMany(g => g));
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

        private bool KeyPhysicallyDown(int vk)
            => _down.Contains(vk) || (GetAsyncKeyState(vk) & 0x8000) != 0;

        private bool AllGroupsHeld()
        {
            foreach (var group in _groups)
            {
                bool any = false;
                foreach (int vk in group)
                {
                    // Prefer live GetAsyncKeyState so a missed KEYDOWN (IME / focus race)
                    // does not permanently break multi-key chords (e.g. backtick+Space).
                    if (KeyPhysicallyDown(vk))
                    {
                        any = true;
                        break;
                    }
                }
                if (!any) return false;
            }
            return true;
        }

        private bool ControlHeld()
            => !_requireControl
               || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
               || (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
               || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

        private static bool IsControlVk(int vk)
            => vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)data.vkCode;
                    bool keyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool keyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (_watched.Contains(vk))
                    {
                        if (keyDown) _down.Add(vk);
                        else if (keyUp)
                        {
                            _down.Remove(vk);
                            // Re-arm once any watched key is released (so holding the
                            // modifiers and tapping the trigger key fires once per tap).
                            _fired = false;
                        }
                    }
                    else if (keyUp && IsControlVk(vk))
                    {
                        _fired = false;
                    }

                    // Evaluate on every relevant key-down, including Ctrl pressed last
                    // (previously Ctrl wasn't watched, so Space+. then Ctrl never fired).
                    if (keyDown && !_fired && AllGroupsHeld() && ControlHeld()
                        && (_watched.Contains(vk) || IsControlVk(vk)))
                    {
                        _fired = true;
                        ChordPressed?.Invoke(this, EventArgs.Empty);
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
