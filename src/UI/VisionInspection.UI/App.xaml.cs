using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using VisionInspection.Core.Services;
using VisionInspection.UI.Views;

namespace VisionInspection.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private UserManager? _userManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化用户管理器
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "users.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _userManager = new UserManager(dbPath);

        // 显示登录窗口
        var loginWindow = new LoginWindow(_userManager);
        if (loginWindow.ShowDialog() != true)
        {
            // 登录失败或取消，退出程序
            Shutdown();
            return;
        }

        // 登录成功，显示主窗口
        var mainWindow = new MainWindow(_userManager);
        mainWindow.Show();
    }
}
