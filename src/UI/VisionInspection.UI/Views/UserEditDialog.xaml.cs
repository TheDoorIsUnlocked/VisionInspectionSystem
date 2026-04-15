using System;
using System.Windows;
using System.Windows.Controls;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// UserEditDialog.xaml 的交互逻辑
/// </summary>
public partial class UserEditDialog : Window
{
    private readonly UserManager _userManager;
    private readonly User? _existingUser;

    public UserEditDialog(UserManager userManager, User? user = null)
    {
        InitializeComponent();
        _userManager = userManager;
        _existingUser = user;

        Loaded += UserEditDialog_Loaded;
    }

    private void UserEditDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_existingUser != null)
        {
            // 编辑模式
            TitleTextBlock.Text = "编辑用户";
            Title = "编辑用户";
            UsernameTextBox.Text = _existingUser.Username;
            UsernameTextBox.IsEnabled = false; // 用户名不可修改
            DisplayNameTextBox.Text = _existingUser.DisplayName;
            PasswordLabel.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Collapsed;
            IsActiveCheckBox.IsChecked = _existingUser.IsActive;

            // 设置角色
            foreach (ComboBoxItem item in RoleComboBox.Items)
            {
                if (item.Tag?.ToString() == ((int)_existingUser.Role).ToString())
                {
                    RoleComboBox.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            // 添加模式
            TitleTextBlock.Text = "添加用户";
            Title = "添加用户";
            RoleComboBox.SelectedIndex = 1; // 默认操作员
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameTextBox.Text.Trim();
        var displayName = DisplayNameTextBox.Text.Trim();
        var password = PasswordBox.Password;
        var role = (UserRole)int.Parse(((ComboBoxItem)RoleComboBox.SelectedItem).Tag?.ToString() ?? "1");
        var isActive = IsActiveCheckBox.IsChecked == true;

        // 验证输入
        if (string.IsNullOrEmpty(username))
        {
            MessageBox.Show("请输入用户名", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            UsernameTextBox.Focus();
            return;
        }

        if (string.IsNullOrEmpty(displayName))
        {
            MessageBox.Show("请输入显示名称", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayNameTextBox.Focus();
            return;
        }

        if (_existingUser == null && string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入密码", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            PasswordBox.Focus();
            return;
        }

        if (_existingUser != null)
        {
            // 更新用户
            _existingUser.DisplayName = displayName;
            _existingUser.Role = role;
            _existingUser.IsActive = isActive;

            var result = await _userManager.UpdateUserAsync(_existingUser);
            if (result.Success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show(result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            // 添加用户
            var newUser = new User
            {
                Username = username,
                DisplayName = displayName,
                Role = role,
                IsActive = isActive
            };

            var result = await _userManager.AddUserAsync(newUser, password);
            if (result.Success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show(result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
