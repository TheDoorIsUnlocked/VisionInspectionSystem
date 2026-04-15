using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// LoginWindow.xaml 的交互逻辑
/// </summary>
public partial class LoginWindow : Window
{
    private readonly UserManager _userManager;
    private const string RememberFile = "remember.txt";

    public bool IsLoggedIn { get; private set; }

    public LoginWindow(UserManager userManager)
    {
        InitializeComponent();
        _userManager = userManager;

        Loaded += LoginWindow_Loaded;
        KeyDown += LoginWindow_KeyDown;
    }

    private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 加载记住的用户名
        if (File.Exists(RememberFile))
        {
            try
            {
                var username = File.ReadAllText(RememberFile);
                if (!string.IsNullOrEmpty(username))
                {
                    UsernameTextBox.Text = username;
                    RememberMeCheckBox.IsChecked = true;
                    PasswordBox.Focus();
                }
                else
                {
                    UsernameTextBox.Focus();
                }
            }
            catch
            {
                UsernameTextBox.Focus();
            }
        }
        else
        {
            UsernameTextBox.Focus();
        }
    }

    private async void LoginWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await PerformLoginAsync();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await PerformLoginAsync();
    }

    private async Task PerformLoginAsync()
    {
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;

        // 验证输入
        if (string.IsNullOrEmpty(username))
        {
            ShowMessage("请输入用户名");
            UsernameTextBox.Focus();
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowMessage("请输入密码");
            PasswordBox.Focus();
            return;
        }

        // 禁用登录按钮
        LoginButton.IsEnabled = false;
        LoginButton.Content = "登录中...";
        HideMessage();

        try
        {
            // 执行登录
            var result = await _userManager.LoginAsync(username, password);

            if (result.Success)
            {
                // 保存用户名（如果选择了记住我）
                if (RememberMeCheckBox.IsChecked == true)
                {
                    File.WriteAllText(RememberFile, username);
                }
                else
                {
                    if (File.Exists(RememberFile))
                    {
                        File.Delete(RememberFile);
                    }
                }

                IsLoggedIn = true;
                DialogResult = true;
                Close();
            }
            else
            {
                Debug.WriteLine($"LoginWindow: 显示错误消息: {result.Message}");
                ShowMessage(result.Message);
                PasswordBox.Password = "";
                PasswordBox.Focus();
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"登录失败：{ex.Message}");
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "登 录";
        }
    }

    private void ShowMessage(string message)
    {
        Debug.WriteLine($"LoginWindow.ShowMessage: {message}");
        MessageTextBlock.Text = message;
        MessageBorder.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageBorder.Visibility = Visibility.Collapsed;
    }
}
