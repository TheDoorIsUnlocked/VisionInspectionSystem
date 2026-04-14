using Microsoft.Extensions.Configuration;

namespace VisionInspection.Core.Interfaces;

/// <summary>
/// 检测模块接口 - 所有模块必须实现
/// </summary>
public interface IDetectionModule : IDisposable
{
    /// <summary>
    /// 模块唯一标识（英文，无空格）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 当前状态
    /// </summary>
    ModuleState State { get; }

    /// <summary>
    /// 模块元数据（用于UI展示）
    /// </summary>
    ModuleMetadata Metadata { get; }

    /// <summary>
    /// 初始化模块
    /// </summary>
    Task InitializeAsync(IConfiguration config, ICameraManager cameraManager);

    /// <summary>
    /// 执行检测
    /// </summary>
    Task<ModuleResult> ProcessAsync(Dictionary<string, CaptureFrame> frames);

    /// <summary>
    /// 关闭模块
    /// </summary>
    Task ShutdownAsync();

    /// <summary>
    /// 获取可视化结果（用于UI叠加显示）
    /// </summary>
    VisualOverlay GetVisualOverlay();
}

/// <summary>
/// 模块状态
/// </summary>
public enum ModuleState
{
    Uninitialized,
    Initializing,
    Ready,
    Processing,
    Error
}

/// <summary>
/// 模块元数据
/// </summary>
public class ModuleMetadata
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public List<string> RequiredCameras { get; set; } = new();
    public Dictionary<string, string> ConfigSchema { get; set; } = new();
}
