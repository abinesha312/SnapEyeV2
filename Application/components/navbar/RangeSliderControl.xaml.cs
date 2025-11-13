using System;
using System.Windows;
using System.Windows.Controls;

namespace SnapEye.Nav
{
    public partial class RangeSliderControl : UserControl
    {
        public event EventHandler<double>? ValueChanged;

        public RangeSliderControl()
        {
            InitializeComponent();
        }

        private void RangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ValueLabel != null)
            {
                ValueLabel.Text = ((int)e.NewValue).ToString();
            }
            ValueChanged?.Invoke(this, e.NewValue);
        }

        public double Value
        {
            get => RangeSlider.Value;
            set => RangeSlider.Value = value;
        }

        public double Minimum
        {
            get => RangeSlider.Minimum;
            set => RangeSlider.Minimum = value;
        }

        public double Maximum
        {
            get => RangeSlider.Maximum;
            set => RangeSlider.Maximum = value;
        }
    }
}
