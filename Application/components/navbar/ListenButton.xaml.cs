using System;
using System.Windows;
using System.Windows.Controls;

namespace SnapEye.Nav
{
    public partial class ListenButton : UserControl
    {
        public event EventHandler? ButtonClicked;

        public ListenButton()
        {
            InitializeComponent();
        }

        private void Btn_Click(object sender, RoutedEventArgs e)
        {
            ButtonClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
