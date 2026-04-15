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

        try
        {
            // 初始化用户管理器
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "users.db");
            var dbDir = Path.GetDirectoryName(dbPath)!;
            
            // 确保目录存在
            if (!Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }
            
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
            Console.WriteLine("登录成功，准备创建主窗口...");
            var mainWindow = new MainWindow(_userManager);
            Console.WriteLine("主窗口创建成功，准备显示...");
            mainWindow.Closed += (s, args) => Shutdown();
            mainWindow.Show();
            Console.WriteLine("主窗口已显示");
        }
        catch (Exception ex)
        {
            // 输出到控制台
            Console.WriteLine($"程序启动失败：{ex.Message}");
            Console.WriteLine($"堆栈跟踪：{ex.StackTrace}");
            
            // 确保在主线程上显示错误消息
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"程序启动失败：{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
            Shutdown();
        }
    }
}
