using System;
using System.Windows;
using SnapEye.Config;
using SnapEye.Services;

namespace SnapEye
{
    public partial class KeyboardShortcutsWindow : Window
    {
        public KeyboardShortcutsWindow()
        {
            InitializeComponent();
            ShortcutsList.ItemsSource = AppKeyboardShortcuts.All;
            PrivacyNote.Text = AppKeyboardShortcuts.ScreenCapturePrivacyNote;
        }

        // Inherit the overlay's screen-share invisibility so this dialog isn't captured
        // either (it's a separate top-level HWND, so it needs the flag applied directly).
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                if (AppConfig.InvisibleToCapture)
                    CaptureInvisibility.ApplyToWindow(this);
            }
            catch
            {
                /* best effort */
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
