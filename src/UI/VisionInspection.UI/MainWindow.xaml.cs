using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.UI.ViewModels;
using VisionInspection.UI.Views;

namespace VisionInspection.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserManager _userManager;

    public MainWindow(UserManager userManager)
    {
        InitializeComponent();
        _userManager = userManager;

        // 设置DataContext
        DataContext = new MainViewModel();

        // 设置窗口标题显示当前用户
        Title = $"视觉检测系统 - [{_userManager.CurrentUser?.DisplayName} ({_userManager.CurrentUser?.RoleDisplayName})]";

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        // 主窗口关闭时退出应用程序
        Application.Current.Shutdown();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 根据权限控制菜单显示
        UpdateMenuByPermission();
    }

    private void UpdateMenuByPermission()
    {
        // 只有管理员能看到用户管理菜单
        if (!_userManager.HasPermission(Permissions.UserManagement))
        {
            // 隐藏用户管理菜单（需要在XAML中命名菜单项）
            if (FindName("UserManagementMenuItem") is UIElement userMenu)
            {
                userMenu.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            var viewModel = DataContext as MainViewModel;
            if (viewModel != null)
            {
                var shapeType = item.Content.ToString() == "矩形" ? ROIShapeType.Rectangle : ROIShapeType.Circle;
                viewModel.RoiEditorViewModel.CurrentShapeType = shapeType;
            }
        }
    }

    /// <summary>
    /// 用户管理菜单点击
    /// </summary>
    private void UserManagementMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_userManager.HasPermission(Permissions.UserManagement))
        {
            MessageBox.Show("您没有权限访问用户管理功能", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var userManagementWindow = new UserManagementWindow(_userManager);
        userManagementWindow.Owner = this;
        userManagementWindow.ShowDialog();
    }

    /// <summary>
    /// 修改密码菜单点击
    /// </summary>
    private async void ChangePasswordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChangePasswordDialog(_userManager);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    /// <summary>
    /// 退出登录菜单点击
    /// </summary>
    private void LogoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定要退出登录吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _userManager.Logout();

            // 重新显示登录窗口
            var loginWindow = new LoginWindow(_userManager);
            if (loginWindow.ShowDialog() == true)
            {
                // 登录成功，更新标题
                Title = $"视觉检测系统 - [{_userManager.CurrentUser?.DisplayName} ({_userManager.CurrentUser?.RoleDisplayName})]";
                UpdateMenuByPermission();
            }
            else
            {
                // 登录失败，关闭程序
                Application.Current.Shutdown();
            }
        }
    }

    /// <summary>
    /// 退出程序菜单点击
    /// </summary>
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定要退出程序吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _userManager.Logout();
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// 关于菜单点击
    /// </summary>
    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "视觉检测系统 v1.0\n\n基于 YOLO 深度学习框架\n支持 ROI 区域检测和 SOP 合规检查",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// 相机配置菜单点击
    /// </summary>
    private void CameraConfigMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var cameraConfigWindow = new CameraConfigWindow();
        cameraConfigWindow.Owner = this;
        cameraConfigWindow.ShowDialog();
    }

    /// <summary>
    /// 模型管理菜单点击
    /// </summary>
    private void ModelManagementMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var modelManagerWindow = new ModelManagerWindow();
        modelManagerWindow.Owner = this;
        modelManagerWindow.ShowDialog();
    }

    /// <summary>
    /// 通信设置菜单点击
    /// </summary>
    private void CommunicationConfigMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("通信设置功能开发中...", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}