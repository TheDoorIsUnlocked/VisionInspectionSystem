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
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// 窗口关闭前事件 - 异步关闭相机资源
    /// </summary>
    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 取消关闭事件，先执行清理
        e.Cancel = true;
        
        // 释放ViewModel资源（异步执行，避免阻塞UI线程）
        if (DataContext is MainViewModel viewModel)
        {
            await Task.Run(() => viewModel.Dispose());
        }
        
        // 清理完成后关闭窗口
        Closing -= MainWindow_Closing;
        Close();
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

    private CameraConfigWindow? _cameraConfigWindow;
    
    /// <summary>
    /// 相机配置菜单点击
    /// </summary>
    private void CameraConfigMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // 复用相机配置窗口实例，避免多次创建导致的事件订阅混乱
        if (_cameraConfigWindow == null)
        {
            _cameraConfigWindow = new CameraConfigWindow();
            _cameraConfigWindow.Owner = this;
            _cameraConfigWindow.Closed += (s, args) => _cameraConfigWindow = null;
        }
        
        if (_cameraConfigWindow.IsVisible)
        {
            _cameraConfigWindow.Activate();
        }
        else
        {
            _cameraConfigWindow.Show();
        }
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

    /// <summary>
    /// ROI形状 - 矩形菜单点击
    /// </summary>
    private void ShapeRectangleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && DataContext is MainViewModel viewModel)
        {
            viewModel.RoiEditorViewModel.CurrentShapeType = ROIShapeType.Rectangle;
            // 取消圆形选中状态
            if (FindName("ShapeCircleMenuItem") is MenuItem circleMenu)
            {
                circleMenu.IsChecked = false;
            }
        }
    }

    /// <summary>
    /// ROI形状 - 圆形菜单点击
    /// </summary>
    private void ShapeCircleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && DataContext is MainViewModel viewModel)
        {
            viewModel.RoiEditorViewModel.CurrentShapeType = ROIShapeType.Circle;
            // 取消矩形选中状态
            if (FindName("ShapeRectangleMenuItem") is MenuItem rectMenu)
            {
                rectMenu.IsChecked = false;
            }
        }
    }

    /// <summary>
    /// 显示/隐藏ROI列表面板
    /// </summary>
    private void ShowROIPanelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            if (ROIPanel != null && ROISplitter != null)
            {
                bool isVisible = menuItem.IsChecked;
                
                // 显示/隐藏ROI面板
                ROIPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                
                // 显示/隐藏分隔条
                ROISplitter.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// 向左旋转90度
    /// </summary>
    private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.RoiEditorViewModel.ImageRotationAngle -= 90;
            // 归一化到0-360度
            viewModel.RoiEditorViewModel.ImageRotationAngle = NormalizeAngle(viewModel.RoiEditorViewModel.ImageRotationAngle);
        }
    }

    /// <summary>
    /// 向右旋转90度
    /// </summary>
    private void RotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.RoiEditorViewModel.ImageRotationAngle += 90;
            // 归一化到0-360度
            viewModel.RoiEditorViewModel.ImageRotationAngle = NormalizeAngle(viewModel.RoiEditorViewModel.ImageRotationAngle);
        }
    }

    /// <summary>
    /// 将角度归一化到0-360度范围
    /// </summary>
    private float NormalizeAngle(float angle)
    {
        while (angle >= 360) angle -= 360;
        while (angle < 0) angle += 360;
        return angle;
    }
}