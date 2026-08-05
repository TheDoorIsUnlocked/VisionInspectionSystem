using System.Windows;
using System.Windows.Controls;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// 通用单行文本输入弹窗（用于重命名配方等）
    /// </summary>
    public partial class InputDialog : Window
    {
        /// <summary>用户输入的结果文本</summary>
        public string ResultText { get; private set; } = "";

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            InputBox.Text = defaultValue;

            InputBox.TextChanged += (_, _) =>
                OkButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResultText = (InputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(ResultText)) return;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
