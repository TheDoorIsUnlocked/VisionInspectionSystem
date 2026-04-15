using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Security.Cryptography;
using System.Text;

namespace VisionInspection.Core.Models;

/// <summary>
/// 用户角色枚举
/// </summary>
public enum UserRole
{
    Guest = 0,      // 访客 - 仅查看
    Operator = 1,   // 操作员 - 运行检测、查看结果
    Admin = 2       // 管理员 - 所有权限包括用户管理
}

/// <summary>
/// 用户实体
/// </summary>
public partial class User : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _passwordHash = string.Empty;

    [ObservableProperty]
    private UserRole _role = UserRole.Operator;

    [ObservableProperty]
    private bool _isActive = true;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;

    [ObservableProperty]
    private DateTime _lastLoginAt;

    [ObservableProperty]
    private string _lastLoginIp = string.Empty;

    /// <summary>
    /// 验证密码
    /// </summary>
    public bool VerifyPassword(string password)
    {
        return PasswordHash == HashPassword(password);
    }

    /// <summary>
    /// 设置密码
    /// </summary>
    public void SetPassword(string password)
    {
        PasswordHash = HashPassword(password);
    }

    /// <summary>
    /// SHA256密码哈希
    /// </summary>
    private static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// 角色显示名称
    /// </summary>
    public string RoleDisplayName => Role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Operator => "操作员",
        UserRole.Guest => "访客",
        _ => "未知"
    };

    /// <summary>
    /// 状态显示
    /// </summary>
    public string StatusDisplay => IsActive ? "启用" : "禁用";
}

/// <summary>
/// 登录记录
/// </summary>
public class LoginRecord
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime LoginTime { get; set; }
    public DateTime? LogoutTime { get; set; }
    public string LoginIp { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string FailReason { get; set; } = string.Empty;
}

/// <summary>
/// 权限定义
/// </summary>
public static class Permissions
{
    // 系统管理
    public const string UserManagement = "UserManagement";      // 用户管理
    public const string SystemConfig = "SystemConfig";          // 系统配置
    public const string ViewLogs = "ViewLogs";                  // 查看日志

    // 检测功能
    public const string RunDetection = "RunDetection";          // 运行检测
    public const string StopDetection = "StopDetection";        // 停止检测
    public const string ViewResults = "ViewResults";            // 查看结果
    public const string ExportResults = "ExportResults";        // 导出结果

    // 配置功能
    public const string EditROI = "EditROI";                    // 编辑ROI
    public const string EditModel = "EditModel";                // 编辑模型
    public const string CameraConfig = "CameraConfig";          // 相机配置
    public const string PLCConfig = "PLCConfig";                // PLC配置

    /// <summary>
    /// 检查角色是否有权限
    /// </summary>
    public static bool HasPermission(UserRole role, string permission)
    {
        return role switch
        {
            UserRole.Admin => true, // 管理员拥有所有权限
            UserRole.Operator => permission switch
            {
                RunDetection => true,
                StopDetection => true,
                ViewResults => true,
                ExportResults => true,
                EditROI => true,
                _ => false
            },
            UserRole.Guest => permission switch
            {
                ViewResults => true,
                _ => false
            },
            _ => false
        };
    }

    /// <summary>
    /// 获取角色的所有权限
    /// </summary>
    public static string[] GetRolePermissions(UserRole role)
    {
        return role switch
        {
            UserRole.Admin => new[]
            {
                UserManagement, SystemConfig, ViewLogs,
                RunDetection, StopDetection, ViewResults, ExportResults,
                EditROI, EditModel, CameraConfig, PLCConfig
            },
            UserRole.Operator => new[]
            {
                RunDetection, StopDetection, ViewResults, ExportResults, EditROI
            },
            UserRole.Guest => new[]
            {
                ViewResults
            },
            _ => Array.Empty<string>()
        };
    }
}
