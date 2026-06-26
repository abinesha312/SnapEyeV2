using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnapEye.Services
{
    /// <summary>
    /// Centralised helper for <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>. Keeps
    /// the P/Invoke + constants in one place and provides utilities to apply the flag to
    /// every visual surface SnapEye renders, including WPF Popups (ContextMenu,
    /// ComboBox dropdown, ToolTip) which are separate top-level HWNDs and otherwise
    /// would still appear in screen recordings while the parent window doesn't.
    /// </summary>
    /// <remarks>
    /// <para>What this provides:</para>
    /// <list type="bullet">
    ///   <item>Hides SnapEye visuals from screen-capture / screen-share APIs (OBS, Teams,
    ///   Zoom, Snipping Tool). Other apps see a black or empty rectangle where SnapEye was.</item>
    /// </list>
    /// <para>What this does NOT provide (honest disclaimer):</para>
    /// <list type="bullet">
    ///   <item>It does not block another process from observing keystrokes via a low-level
    ///   keyboard hook (<c>WH_KEYBOARD_LL</c>). True keylogger resistance requires
    ///   driver/kernel-level mechanisms which are out of scope for this app.</item>
    ///   <item>It does not encrypt anything in memory — the typed text still lives in the
    ///   normal WPF text buffers. Pair with <c>PasswordBox</c> for sensitive fields.</item>
    /// </list>
    /// </remarks>
    public static class CaptureInvisibility
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        public const uint WDA_NONE = 0x00000000;
        public const uint WDA_MONITOR = 0x00000001;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Win 10 2004+ / Win 11

        /// <summary>
        /// Apply <see cref="WDA_EXCLUDEFROMCAPTURE"/> to a window's HWND. Safe to call
        /// before or after the window is shown — we ensure the handle exists first.
        /// </summary>
        public static bool ApplyToWindow(Window window)
        {
            if (window == null) return false;
            try
            {
                var helper = new WindowInteropHelper(window);
                helper.EnsureHandle();
                if (helper.Handle == IntPtr.Zero) return false;
                return SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restore normal capture behaviour for a window (used when the user toggles
        /// "Invisible to capture" off in the overlay menu).
        /// </summary>
        public static bool RestoreWindow(Window window)
        {
            if (window == null) return false;
            try
            {
                var helper = new WindowInteropHelper(window);
                if (helper.Handle == IntPtr.Zero) return false;
                return SetWindowDisplayAffinity(helper.Handle, WDA_NONE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Walks the visual + logical tree under <paramref name="root"/> looking for
        /// <see cref="Popup"/> instances (ContextMenu, ComboBox dropdown, ToolTip) and
        /// hooks their <c>Opened</c> event so the hosting HWND inherits the same
        /// capture-invisibility flag as the main window. Idempotent — calling it
        /// multiple times only attaches one handler per popup.
        /// </summary>
        public static void HookAllPopups(DependencyObject root)
        {
            if (root == null) return;
            foreach (var popup in EnumeratePopups(root))
            {
                HookPopup(popup);
            }
        }

        /// <summary>
        /// Hook a single Popup so its hosting window gets WDA_EXCLUDEFROMCAPTURE on Open.
        /// </summary>
        public static void HookPopup(Popup popup)
        {
            if (popup == null) return;
            // Avoid double-hooking — store a marker on Tag (popups rarely use Tag, but if
            // they do, we layer on top non-destructively via a sentinel object).
            const string MarkerKey = "__SnapEyeCaptureInvisibility";
            if (popup.Resources.Contains(MarkerKey)) return;
            popup.Resources[MarkerKey] = true;

            // Defer to Loaded: on Opened the popup's hosting HWND often does not yet
            // expose an HwndSource (same timing issue as context menus — see HookContextMenu).
            popup.Opened += (_, _) =>
            {
                popup.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (popup.Child == null) return;
                        if (PresentationSource.FromVisual(popup.Child) is HwndSource src
                            && src.Handle != IntPtr.Zero)
                            SetWindowDisplayAffinity(src.Handle, WDA_EXCLUDEFROMCAPTURE);
                        else
                            TryApplyWdaFromVisualSubtree(popup.Child);
                    }
                    catch
                    {
                        // Best-effort: if we can't apply the flag the popup will still work,
                        // it just won't be hidden from capture. Better than crashing.
                    }
                }), DispatcherPriority.Loaded);
            };
        }

        /// <summary>
        /// Ensures the <see cref="ComboBox"/> template's dropdown <see cref="Popup"/> is
        /// hooked. Template-expanded popups are sometimes missing from an early
        /// <see cref="HookAllPopups"/> walk; call this after layout (e.g. ContentRendered).
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
        /// Hook a <see cref="ContextMenu"/> so its popup HWND (and any nested submenu popups)
        /// receive <see cref="WDA_EXCLUDEFROMCAPTURE"/> as soon as they exist.
        /// </summary>
        /// <remarks>
        /// WPF hosts context menus in a separate top-level window. <c>PresentationSource.FromVisual(menu)</c>
        /// is often <c>null</c> on <c>Opened</c>, which is why the naive implementation left the
        /// kebab menu visible during screen capture. We wait until <see cref="DispatcherPriority.Loaded"/>,
        /// then resolve an <see cref="HwndSource"/> from item containers or a visual-tree walk.
        /// </remarks>
        public static void HookContextMenu(ContextMenu menu)
        {
            if (menu == null) return;
            const string MarkerKey = "__SnapEyeCaptureInvisibility";
            if (menu.Resources.Contains(MarkerKey)) return;
            menu.Resources[MarkerKey] = true;

            menu.Opened += ContextMenu_Opened;
            WireMenuItemSubmenus(menu);
        }

        private static void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu cm) return;
            cm.Dispatcher.BeginInvoke(new Action(() => ApplyWdaToContextMenu(cm)), DispatcherPriority.Loaded);
        }

        private static void ApplyWdaToContextMenu(ContextMenu cm)
        {
            try
            {
                for (int i = 0; i < cm.Items.Count; i++)
                {
                    if (cm.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject d
                        && TryApplyWdaFromVisualSubtree(d))
                        return;
                }

                // Fallback: walk from the menu's internal root (template / popup chrome).
                TryApplyWdaFromVisualSubtree(cm);
            }
            catch
            {
                /* best effort */
            }
        }

        /// <summary>Recursively attach <see cref="MenuItem.SubmenuOpened"/> (e.g. Opacity slider).</summary>
        private static void WireMenuItemSubmenus(ItemsControl parent)
        {
            if (parent == null) return;
            foreach (object? obj in parent.Items)
            {
                if (obj is not MenuItem mi) continue;

                mi.SubmenuOpened += (_, _) =>
                {
                    mi.Dispatcher.BeginInvoke(new Action(() => ApplyWdaToMenuItemSubmenu(mi)), DispatcherPriority.Loaded);
                };

                WireMenuItemSubmenus(mi);
            }
        }

        private static void ApplyWdaToMenuItemSubmenu(MenuItem mi)
        {
            try
            {
                for (int i = 0; i < mi.Items.Count; i++)
                {
                    if (mi.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject d
                        && TryApplyWdaFromVisualSubtree(d))
                        return;
                }
                TryApplyWdaFromVisualSubtree(mi);
            }
            catch
            {
                /* best effort */
            }
        }

        /// <summary>
        /// Finds the first HWND in the visual subtree rooted at <paramref name="root"/> and applies WDA.
        /// </summary>
        private static bool TryApplyWdaFromVisualSubtree(DependencyObject root)
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
                    SetWindowDisplayAffinity(hs.Handle, WDA_EXCLUDEFROMCAPTURE);
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

        // Recursively walks both the visual and logical tree because Popups defined in
        // ControlTemplates (e.g. inside ComboBox) are reachable via the visual tree only
        // after templates expand, while top-level Popups in XAML are usually reachable
        // via the logical tree.
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
    }
}
