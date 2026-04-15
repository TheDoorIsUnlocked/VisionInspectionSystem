using System;
using System.Windows;
using System.Windows.Controls;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// UserManagementWindow.xaml 的交互逻辑
/// </summary>
public partial class UserManagementWindow : Window
{
    private readonly UserManager _userManager;

    public UserManagementWindow(UserManager userManager)
    {
        InitializeComponent();
        _userManager = userManager;
        Loaded += UserManagementWindow_Loaded;
    }

    private async void UserManagementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        var users = await _userManager.GetAllUsersAsync();
        UsersDataGrid.ItemsSource = users;
    }

    private async void AddUserButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UserEditDialog(_userManager);
        if (dialog.ShowDialog() == true)
        {
            await LoadUsersAsync();
        }
    }

    private async void EditUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is User user)
        {
            var dialog = new UserEditDialog(_userManager, user);
            if (dialog.ShowDialog() == true)
            {
                await LoadUsersAsync();
            }
        }
    }

    private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is User user)
        {
            var result = MessageBox.Show(
                $"确定要重置用户 \"{user.DisplayName}\" 的密码吗？\n新密码将设为：123456",
                "确认重置密码",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var resetResult = await _userManager.ResetPasswordAsync(user.Id, "123456");
                if (resetResult.Success)
                {
                    MessageBox.Show("密码重置成功！新密码为：123456", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(resetResult.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is User user)
        {
            var result = MessageBox.Show(
                $"确定要删除用户 \"{user.DisplayName}\" 吗？",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var deleteResult = await _userManager.DeleteUserAsync(user.Id);
                if (deleteResult.Success)
                {
                    await LoadUsersAsync();
                }
                else
                {
                    MessageBox.Show(deleteResult.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadUsersAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
