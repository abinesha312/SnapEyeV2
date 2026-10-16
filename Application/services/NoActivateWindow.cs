using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnapEye.Services
{
    /// <summary>
    /// Keeps a top-level window (and its popups) from becoming the foreground window.
    /// Mouse input is still delivered — buttons, menus, scrolling, and dragging work —
    /// but keyboard focus stays with whichever application was already in front.
    /// </summary>
    public static class NoActivateWindow
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_SETFOCUS = 0x0007;
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_NCACTIVATE = 0x0086;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int MA_NOACTIVATE = 3;
        private const int WA_INACTIVE = 0;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint GA_ROOT = 2;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private const string MarkerKey = "__SnapEyeNoActivate";
        private const string OwnedProp = "SnapEyeNoActivate";

        private static readonly HwndSourceHook Hook = WndProc;
        private static readonly Dictionary<Window, DragSession> Drags = new();
        private static IntPtr lastExternalForeground = IntPtr.Zero;
        private static bool bouncingFocus;
        private static long lastBounceTickMs;

        // Tracks the real foreground app even when SnapEye is idle (WndProc alone only
        // updates while our HWND is getting messages — which is why bounce used to fail
        // after long gaps and leave SnapEye stuck as foreground for seconds).
        private static WinEventDelegate? foregroundProc;
        private static IntPtr foregroundHook = IntPtr.Zero;
        private static Dispatcher? uiDispatcher;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private sealed class DragSession
        {
            public required Window Window { get; init; }
            public required IntPtr Hwnd { get; init; }
            public required POINT StartCursor { get; init; }
            public required RECT StartRect { get; init; }
            public required MouseEventHandler Move { get; init; }
            public required MouseButtonEventHandler Up { get; init; }
            public required MouseEventHandler CaptureLost { get; init; }
            public required EventHandler Closed { get; init; }
        }

        /// <summary>
        /// Apply <c>WS_EX_NOACTIVATE</c> and the focus-refusal hook. Call once the HWND exists
        /// (<see cref="Window.OnSourceInitialized"/>).
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null) return;
            try
            {
                uiDispatcher ??= window.Dispatcher;
                EnsureForegroundWatcher();
                RememberExternalForeground();
                var helper = new WindowInteropHelper(window);
                helper.EnsureHandle();
                if (helper.Handle == IntPtr.Zero) return;
                if (HwndSource.FromHwnd(helper.Handle) is HwndSource source)
                    ApplyToSource(source);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoActivate] Apply error: {ex.Message}");
            }
        }

        /// <summary>
        /// Install a process-wide foreground watcher once. Needed because <see cref="WndProc"/>
        /// only sees messages delivered to our HWND — after a quiet stretch the cached previous
        /// app can be stale/destroyed and bounce silently no-ops, leaving SnapEye in front.
        /// </summary>
        private static void EnsureForegroundWatcher()
        {
            if (foregroundHook != IntPtr.Zero) return;
            try
            {
                foregroundProc = OnSystemForegroundChanged;
                foregroundHook = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                if (foregroundHook == IntPtr.Zero)
                    Console.WriteLine($"[NoActivate] SetWinEventHook failed (Win32 {Marshal.GetLastWin32Error()}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoActivate] Foreground watcher error: {ex.Message}");
            }
        }

        private static void OnSystemForegroundChanged(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */) return;

                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                if (root == IntPtr.Zero) root = hwnd;

                if (IsExternal(root))
                {
                    lastExternalForeground = root;
                    return;
                }

                // One of our no-activate windows became foreground — push focus back.
                if (bouncingFocus) return;
                ScheduleBounce();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoActivate] Foreground callback error: {ex.Message}");
            }
        }

        private static void ScheduleBounce()
        {
            IntPtr target = PickBounceTarget(IntPtr.Zero, IntPtr.Zero);
            if (target == IntPtr.Zero) return;

            void Go()
            {
                if (bouncingFocus) return;
                // Re-check: user may already have moved on.
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && IsExternal(fg))
                {
                    IntPtr root = GetAncestor(fg, GA_ROOT);
                    lastExternalForeground = root != IntPtr.Zero ? root : fg;
                    return;
                }
                IntPtr t = PickBounceTarget(IntPtr.Zero, IntPtr.Zero);
                if (t != IntPtr.Zero)
                    BounceFocus(t);
            }

            try
            {
                if (uiDispatcher != null && !uiDispatcher.CheckAccess())
                    uiDispatcher.BeginInvoke(Go, DispatcherPriority.Send);
                else
                    Go();
            }
            catch
            {
                try { Go(); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Move <paramref name="window"/> with the cursor until the left button is released,
        /// without activating it. Replaces <see cref="Window.DragMove"/>.
        /// </summary>
        public static void BeginDrag(Window window)
        {
            if (window == null || Drags.ContainsKey(window)) return;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                if (!GetCursorPos(out POINT cursor) || !GetWindowRect(hwnd, out RECT rect)) return;

                var session = new DragSession
                {
                    Window = window,
                    Hwnd = hwnd,
                    StartCursor = cursor,
                    StartRect = rect,
                    Move = OnDragMove,
                    Up = OnDragUp,
                    CaptureLost = OnDragCaptureLost,
                    Closed = OnDragClosed
                };

                window.PreviewMouseMove += session.Move;
                window.PreviewMouseLeftButtonUp += session.Up;
                window.LostMouseCapture += session.CaptureLost;
                window.Closed += session.Closed;
                Drags[window] = session;

                if (!Mouse.Capture(window))
                    EndDrag(window);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoActivate] BeginDrag error: {ex.Message}");
                EndDrag(window);
            }
        }

        /// <summary>Hook every <see cref="Popup"/> currently in the tree. Idempotent.</summary>
        public static void HookAllPopups(DependencyObject root)
        {
            if (root == null) return;
            foreach (var popup in EnumeratePopups(root))
                HookPopup(popup);
        }

        /// <summary>
        /// When the popup opens, its separate top-level HWND gets the same no-activate treatment
        /// as the overlay. The HWND often does not exist until after <c>Opened</c>, so the work
        /// is deferred to <see cref="DispatcherPriority.Loaded"/>.
        /// </summary>
        public static void HookPopup(Popup popup)
        {
            if (popup == null) return;
            if (popup.Resources.Contains(MarkerKey)) return;
            popup.Resources[MarkerKey] = true;

            popup.Opened += (_, _) =>
            {
                popup.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (popup.Child == null) return;
                        if (PresentationSource.FromVisual(popup.Child) is HwndSource src
                            && src.Handle != IntPtr.Zero)
                            ApplyToSource(src);
                        else
                            TryApplyFromVisualSubtree(popup.Child);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NoActivate] Popup hook error: {ex.Message}");
                    }
                }), DispatcherPriority.Loaded);
            };
        }

        /// <summary>
        /// ComboBox dropdowns live in a template <see cref="Popup"/> that an early tree walk can miss.
        /// </summary>
        public static void HookComboBoxPopup(ComboBox? combo)
        {
            if (combo == null) return;
            try
            {
                combo.ApplyTemplate();
                var p1 = combo.Template?.FindName("Popup", combo) as Popup;
                var p2 = combo.Template?.FindName("PART_Popup", combo) as Popup;
                if (p1 != null) HookPopup(p1);
                if (p2 != null && !ReferenceEquals(p1, p2)) HookPopup(p2);
            }
            catch
            {
                /* best effort */
            }
        }

        /// <summary>
        /// Context menus (and nested submenus such as the opacity slider) are their own HWNDs.
        /// </summary>
        public static void HookContextMenu(ContextMenu menu)
        {
            if (menu == null) return;
            if (menu.Resources.Contains(MarkerKey)) return;
            menu.Resources[MarkerKey] = true;

            menu.Opened += ContextMenu_Opened;
            WireMenuItemSubmenus(menu);
        }

        private static void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu cm) return;
            cm.Dispatcher.BeginInvoke(new Action(() => ApplyToContextMenu(cm)), DispatcherPriority.Loaded);
        }

        private static void ApplyToContextMenu(ContextMenu cm)
        {
            try
            {
                for (int i = 0; i < cm.Items.Count; i++)
                {
                    if (cm.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject d
                        && TryApplyFromVisualSubtree(d))
                        return;
                }

                TryApplyFromVisualSubtree(cm);
            }
            catch
            {
                /* best effort */
            }
        }

        private static void WireMenuItemSubmenus(ItemsControl parent)
        {
            if (parent == null) return;
            foreach (object? obj in parent.Items)
            {
                if (obj is not MenuItem mi) continue;

                mi.SubmenuOpened += (_, _) =>
                {
                    mi.Dispatcher.BeginInvoke(new Action(() => ApplyToMenuItemSubmenu(mi)), DispatcherPriority.Loaded);
                };

                WireMenuItemSubmenus(mi);
            }
        }

        private static void ApplyToMenuItemSubmenu(MenuItem mi)
        {
            try
            {
                for (int i = 0; i < mi.Items.Count; i++)
                {
                    if (mi.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject d
                        && TryApplyFromVisualSubtree(d))
                        return;
                }

                TryApplyFromVisualSubtree(mi);
            }
            catch
            {
                /* best effort */
            }
        }

        private static void ApplyToSource(HwndSource source)
        {
            IntPtr hwnd = source.Handle;
            if (hwnd == IntPtr.Zero) return;

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
            SetProp(hwnd, OwnedProp, new IntPtr(1));

            // Remove-then-add keeps a reopened popup from stacking duplicate hooks
            // if Windows recycles the HWND.
            source.RemoveHook(Hook);
            source.AddHook(Hook);
        }

        private static bool TryApplyFromVisualSubtree(DependencyObject root)
        {
            var q = new Queue<DependencyObject>();
            q.Enqueue(root);
            var seen = new HashSet<DependencyObject>();

            while (q.Count > 0)
            {
                var n = q.Dequeue();
                if (!seen.Add(n)) continue;

                if (n is Visual vis && PresentationSource.FromVisual(vis) is HwndSource hs && hs.Handle != IntPtr.Zero)
                {
                    ApplyToSource(hs);
                    return true;
                }

                int count = 0;
                try { count = VisualTreeHelper.GetChildrenCount(n); } catch { count = 0; }
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var ch = VisualTreeHelper.GetChild(n, i);
                        if (ch != null) q.Enqueue(ch);
                    }
                    catch { /* ignore */ }
                }
            }

            return false;
        }

        private static IEnumerable<Popup> EnumeratePopups(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            var seen = new HashSet<DependencyObject>();

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!seen.Add(node)) continue;

                if (node is Popup popup)
                    yield return popup;

                int visualCount = 0;
                try { visualCount = VisualTreeHelper.GetChildrenCount(node); } catch { visualCount = 0; }
                for (int i = 0; i < visualCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child != null) stack.Push(child);
                }

                foreach (var lc in LogicalTreeHelper.GetChildren(node))
                {
                    if (lc is DependencyObject d) stack.Push(d);
                }
            }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            RememberExternalForeground();

            if (msg == WM_MOUSEACTIVATE)
            {
                // Deliver the click, but do not activate this window.
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            if (msg == WM_NCACTIVATE)
            {
                // Refuse the "active" non-client state. Still bounce if Windows is activating us.
                if (wParam != IntPtr.Zero && !bouncingFocus)
                    ScheduleBounce();
                handled = true;
                return IntPtr.Zero; // FALSE → keep inactive look
            }

            if (msg == WM_ACTIVATE)
            {
                int state = wParam.ToInt32() & 0xFFFF;
                if (state != WA_INACTIVE && !bouncingFocus)
                {
                    ScheduleBounce();
                    handled = true;
                }
                return IntPtr.Zero;
            }

            if (msg == WM_WINDOWPOSCHANGING)
            {
                // Show, move, and WPF layout all come through here. Force the no-activate
                // flag so a position update cannot become a foreground change.
                try
                {
                    var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    pos.flags |= SWP_NOACTIVATE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                catch
                {
                    /* best effort */
                }
                return IntPtr.Zero;
            }

            if (msg == WM_SETFOCUS && !bouncingFocus)
            {
                IntPtr target = PickBounceTarget(hwnd, wParam);
                if (target != IntPtr.Zero)
                    BounceFocus(target);
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private static void RememberExternalForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (IsExternal(fg))
                {
                    IntPtr root = GetAncestor(fg, GA_ROOT);
                    lastExternalForeground = root != IntPtr.Zero ? root : fg;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        private static bool IsExternal(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            try
            {
                if (GetProp(hwnd, OwnedProp) != IntPtr.Zero) return false;
                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                if (root != IntPtr.Zero && root != hwnd)
                {
                    if (!IsWindow(root)) return false;
                    if (GetProp(root, OwnedProp) != IntPtr.Zero) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Prefer the last real foreground app. <paramref name="wParam"/> is the HWND losing
        /// focus, which may itself be another SnapEye popup — never bounce between our own windows.
        /// </summary>
        private static IntPtr PickBounceTarget(IntPtr ourHwnd, IntPtr wParam)
        {
            if (IsExternal(lastExternalForeground))
                return lastExternalForeground;
            if (wParam != IntPtr.Zero && wParam != ourHwnd && IsExternal(wParam))
                return wParam;

            // Last resort: whatever is currently foreground if it isn't us (race during bounce).
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != ourHwnd && IsExternal(fg))
                    return fg;
            }
            catch
            {
                /* ignore */
            }
            return IntPtr.Zero;
        }

        private static void BounceFocus(IntPtr target)
        {
            long now = Environment.TickCount64;
            if (now - lastBounceTickMs < 50) return;
            lastBounceTickMs = now;

            bouncingFocus = true;
            try
            {
                if (!IsWindow(target)) return;
                IntPtr root = GetAncestor(target, GA_ROOT);
                if (root == IntPtr.Zero || !IsWindow(root)) root = target;

                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
                uint targetThread = GetWindowThreadProcessId(root, out _);
                uint thisThread = GetCurrentThreadId();
                bool attachedFg = false;
                bool attachedTarget = false;

                // AttachThreadInput lets SetForegroundWindow succeed when Windows' focus-lock
                // would otherwise leave SnapEye stuck as the foreground app.
                if (fg != IntPtr.Zero && fgThread != 0 && fgThread != thisThread)
                    attachedFg = AttachThreadInput(thisThread, fgThread, true);
                if (targetThread != 0 && targetThread != thisThread && targetThread != fgThread)
                    attachedTarget = AttachThreadInput(thisThread, targetThread, true);

                try
                {
                    SetForegroundWindow(root);
                    SetFocus(target);
                }
                finally
                {
                    if (attachedTarget) AttachThreadInput(thisThread, targetThread, false);
                    if (attachedFg) AttachThreadInput(thisThread, fgThread, false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoActivate] BounceFocus error: {ex.Message}");
            }
            finally
            {
                bouncingFocus = false;
            }
        }

        private static void OnDragMove(object sender, MouseEventArgs e)
        {
            if (sender is not Window window || !Drags.TryGetValue(window, out DragSession? session))
                return;
            try
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    EndDrag(window);
                    return;
                }

                if (!GetCursorPos(out POINT now)) return;
                int x = session.StartRect.Left + (now.X - session.StartCursor.X);
                int y = session.StartRect.Top + (now.Y - session.StartCursor.Y);
                SetWindowPos(session.Hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                SyncDipPosition(window, x, y);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoActivate] Drag move error: {ex.Message}");
            }
        }

        private static void OnDragUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Window window)
                EndDrag(window);
        }

        private static void OnDragCaptureLost(object sender, MouseEventArgs e)
        {
            if (sender is Window window)
                EndDrag(window);
        }

        private static void OnDragClosed(object? sender, EventArgs e)
        {
            if (sender is Window window)
                EndDrag(window);
        }

        private static void EndDrag(Window window)
        {
            if (!Drags.Remove(window, out DragSession? session)) return;
            try
            {
                window.PreviewMouseMove -= session.Move;
                window.PreviewMouseLeftButtonUp -= session.Up;
                window.LostMouseCapture -= session.CaptureLost;
                window.Closed -= session.Closed;
                if (Mouse.Captured == window)
                    Mouse.Capture(null);
            }
            catch
            {
                /* ignore */
            }
        }

        /// <summary>
        /// Keep WPF's DIP <see cref="Window.Left"/> / <see cref="Window.Top"/> aligned with the
        /// pixel position. <see cref="Window.SizeToContent"/> would otherwise snap the window
        /// back to the stale dependency-property location. <c>WM_WINDOWPOSCHANGING</c> forces
        /// <c>SWP_NOACTIVATE</c> on the follow-up move WPF issues.
        /// </summary>
        private static void SyncDipPosition(Window window, int pixelX, int pixelY)
        {
            try
            {
                if (PresentationSource.FromVisual(window)?.CompositionTarget is not CompositionTarget target)
                    return;
                Point dip = target.TransformFromDevice.Transform(new Point(pixelX, pixelY));
                window.Left = dip.X;
                window.Top = dip.Y;
            }
            catch
            {
                /* best effort */
            }
        }
    }
}
