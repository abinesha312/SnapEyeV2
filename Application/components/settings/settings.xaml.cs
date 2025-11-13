using System;
using System.Windows;
using System.Windows.Controls;

namespace SnapEye.Settings
{
    public partial class SettingsControl : UserControl
    {
        // Events
        public event EventHandler? VisibilityToggled;
        public event EventHandler? QuitRequested;

        public SettingsControl()
        {
            InitializeComponent();
        }

        // Visibility toggle handlers
        private void VisibilityToggle_Checked(object sender, RoutedEventArgs e)
        {
            VisibilityToggled?.Invoke(this, EventArgs.Empty);
        }

        private void VisibilityToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            VisibilityToggled?.Invoke(this, EventArgs.Empty);
        }

        // Quit button handler
        private void QuitBtn_Click(object sender, RoutedEventArgs e)
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
        }

        // Property to get/set visibility toggle state
        public bool IsRegionVisible
        {
            get => VisibilityToggle.IsChecked ?? true;
            set => VisibilityToggle.IsChecked = value;
        }
    }
}

