using System.Windows;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// ChangePasswordDialog.xaml 的交互逻辑
/// </summary>
public partial class ChangePasswordDialog : Window
{
    private readonly UserManager _userManager;

    public ChangePasswordDialog(UserManager userManager)
    {
        InitializeComponent();
        _userManager = userManager;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var oldPassword = OldPasswordBox.Password;
        var newPassword = NewPasswordBox.Password;
        var confirmPassword = ConfirmPasswordBox.Password;

        // 验证输入
        if (string.IsNullOrEmpty(oldPassword))
        {
            MessageBox.Show("请输入原密码", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            OldPasswordBox.Focus();
            return;
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            MessageBox.Show("请输入新密码", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            NewPasswordBox.Focus();
            return;
        }

        if (newPassword != confirmPassword)
        {
            MessageBox.Show("两次输入的新密码不一致", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            ConfirmPasswordBox.Focus();
            return;
        }

        if (newPassword.Length < 6)
        {
            MessageBox.Show("新密码长度不能少于6位", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            NewPasswordBox.Focus();
            return;
        }

        // 修改密码
        var result = await _userManager.ChangePasswordAsync(
            _userManager.CurrentUser!.Id, oldPassword, newPassword);

        if (result.Success)
        {
            MessageBox.Show("密码修改成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show(result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
