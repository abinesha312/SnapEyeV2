using System;
using System.Windows;
using System.Windows.Controls;

namespace SnapEye.Nav
{
    public partial class CameraButton : UserControl
    {
        public event EventHandler? ButtonClicked;

        public CameraButton()
        {
            InitializeComponent();
        }

        private void Btn_Click(object sender, RoutedEventArgs e)
        {
            ButtonClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}

