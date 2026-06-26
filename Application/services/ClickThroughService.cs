using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SnapEye.Services
{
    /// <summary>
    /// Implements the "Island Bar" click-through (incognito) mode for the overlay.
    /// <para>
    /// When click-through is enabled, the overlay stays fully visible but mouse input
    /// passes straight through to whatever application is behind it (via the
    /// <c>WS_EX_TRANSPARENT</c> extended window style) — EXCEPT while the cursor is over a
    /// designated interactive region (the Island Bar button), so the user can always toggle
    /// the mode back off.
    /// </para>
    /// <para>
    /// We achieve the "button is still clickable" carve-out with a lightweight dispatcher
    /// timer that polls the cursor position and flips <c>WS_EX_TRANSPARENT</c> on/off as the
    /// cursor enters/leaves the interactive rect. This avoids the lifetime and crash risks
    /// of a global low-level mouse hook and is plenty responsive at ~40ms.
    /// </para>
    /// </summary>
    public sealed class ClickThroughService : IDisposable
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private readonly Window window;
        private readonly Func<Rect?> getInteractiveScreenRect;
        private readonly DispatcherTimer timer;

        private bool clickThrough;
        private bool currentlyTransparent;
        private IntPtr hwnd = IntPtr.Zero;

        /// <summary>True when the overlay is currently in click-through (incognito) mode.</summary>
        public bool IsClickThrough => clickThrough;

        /// <param name="window">The overlay window to make click-through.</param>
        /// <param name="getInteractiveScreenRect">
        /// Returns the screen-pixel rectangle that must remain clickable while click-through
        /// is on (the Island Bar button). Return null to make the whole window pass through.
        /// </param>
        public ClickThroughService(Window window, Func<Rect?> getInteractiveScreenRect)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            this.getInteractiveScreenRect = getInteractiveScreenRect;
            timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) => Poll();
        }

        /// <summary>Enable or disable click-through (pass-through) mode.</summary>
        public void SetClickThrough(bool enabled)
        {
            try
            {
                clickThrough = enabled;
                EnsureHwnd();
                if (enabled)
                {
                    timer.Start();
                    Poll();
                }
                else
                {
                    timer.Stop();
                    SetTransparent(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClickThrough] SetClickThrough error: {ex.Message}");
            }
        }

        private void EnsureHwnd()
        {
            if (hwnd == IntPtr.Zero)
            {
                try { hwnd = new WindowInteropHelper(window).Handle; }
                catch { hwnd = IntPtr.Zero; }
            }
        }

        private void Poll()
        {
            try
            {
                if (!clickThrough) return;
                EnsureHwnd();
                if (hwnd == IntPtr.Zero) return;

                bool overInteractive = false;
                Rect? rect = getInteractiveScreenRect?.Invoke();
                if (rect.HasValue && GetCursorPos(out POINT p))
                    overInteractive = rect.Value.Contains(p.X, p.Y);

                // Stay interactive only while hovering the carve-out region; otherwise
                // let clicks fall through to the app behind the overlay.
                SetTransparent(!overInteractive);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClickThrough] Poll error: {ex.Message}");
            }
        }

        private void SetTransparent(bool transparent)
        {
            if (hwnd == IntPtr.Zero) return;
            if (transparent == currentlyTransparent) return;
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (transparent)
                    ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
                else
                    ex &= ~WS_EX_TRANSPARENT; // keep WS_EX_LAYERED (AllowsTransparency needs it)
                SetWindowLong(hwnd, GWL_EXSTYLE, ex);
                currentlyTransparent = transparent;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClickThrough] SetTransparent error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try { timer.Stop(); } catch { /* ignore */ }
            try { SetTransparent(false); } catch { /* ignore */ }
        }
    }
}
