using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CrownControllerHUD
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; set; } = string.Empty;

        public InputDialog(string title, string message, string defaultText = "")
        {
            InitializeComponent();
            Title = title;
            DataContext = this;

            var messageText = (TextBox)FindName("InputTextBox");
            if (messageText != null)
            {
                ResponseText = defaultText;
                Loaded += (s, e) =>
                {
                    messageText.Focus();
                    messageText.SelectAll();
                };
            }
        }

        public string Message { get; set; } = string.Empty;

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                Close();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }
    }
}