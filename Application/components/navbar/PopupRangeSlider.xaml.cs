using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SnapEye.Nav
{
    public partial class PopupRangeSlider : UserControl
    {
        public event EventHandler<double>? ValueChanged;

        public PopupRangeSlider()
        {
            InitializeComponent();
        }

        // Toggle popup when button is clicked
        private void IconButton_Click(object sender, RoutedEventArgs e)
        {
            SliderPopup.IsOpen = !SliderPopup.IsOpen;
        }

        // Handle slider value changes
        private void VerticalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ValueDisplay != null)
            {
                ValueDisplay.Text = ((int)e.NewValue).ToString();
            }

            // Update icon based on value
            UpdateIcon(e.NewValue);

            // Raise event for parent to handle
            ValueChanged?.Invoke(this, e.NewValue);
        }

        // Update icon based on volume level (like YouTube)
        private void UpdateIcon(double value)
        {
            if (IconButton?.Content is TextBlock iconText)
            {
                if (value == 0)
                {
                    iconText.Text = "🔇"; // Muted
                }
                else if (value < 33)
                {
                    iconText.Text = "🔈"; // Low
                }
                else if (value < 66)
                {
                    iconText.Text = "🔉"; // Medium
                }
                else
                {
                    iconText.Text = "🔊"; // High
                }
            }
        }

        private void SliderPopup_Closed(object? sender, EventArgs e)
        {
            // Optional: Handle popup close event
        }

        // Public properties for external access
        public double Value
        {
            get => VerticalSlider.Value;
            set => VerticalSlider.Value = value;
        }

        public double Minimum
        {
            get => VerticalSlider.Minimum;
            set => VerticalSlider.Minimum = value;
        }

        public double Maximum
        {
            get => VerticalSlider.Maximum;
            set => VerticalSlider.Maximum = value;
        }
    }
}

