using Microsoft.Extensions.Configuration;
using SkiaSharp;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;

namespace VisionInspection.Modules.SOP;

/// <summary>
/// SOP检测启动器 - 简化SOP检测的启动流程
/// </summary>
public class SOPDetectionStarter : IDisposable
{
    private SOPModule? _sopModule;
    private RegionConfigLoader? _regionLoader;
    private bool _isRunning;

    /// <summary>
    /// 检测模式
    /// </summary>
    public SOPDetectionMode DetectionMode
    {
        get => _sopModule?.DetectionMode ?? SOPDetectionMode.UnifiedDetection;
        set
        {
            if (_sopModule != null)
            {
                _sopModule.DetectionMode = value;
            }
        }
    }

    /// <summary>
    /// 当前工作流
    /// </summary>
    public SOPWorkflow? CurrentWorkflow => _sopModule?.CurrentWorkflow;

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// 当前工作流名称
    /// </summary>
    public string? CurrentWorkflowName => _sopModule?.CurrentWorkflow?.Name;

    /// <summary>
    /// 当前状态
    /// </summary>
    public ModuleState CurrentState => _sopModule?.State ?? ModuleState.Uninitialized;

    /// <summary>
    /// 日志回调
    /// </summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>
    /// 事件：步骤变化
    /// </summary>
    public event EventHandler<StepChangedEventArgs>? StepChanged
    {
        add
        {
            if (_sopModule != null)
                _sopModule.StepChanged += value;
        }
        remove
        {
            if (_sopModule != null)
                _sopModule.StepChanged -= value;
        }
    }

    /// <summary>
    /// 事件：SOP状态变化
    /// </summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged
    {
        add
        {
            if (_sopModule != null)
                _sopModule.SOPStateChanged += value;
        }
        remove
        {
            if (_sopModule != null)
                _sopModule.SOPStateChanged -= value;
        }
    }

    /// <summary>
    /// 事件：违规检测
    /// </summary>
    public event EventHandler<ViolationEventArgs>? ViolationDetected
    {
        add
        {
            if (_sopModule != null)
                _sopModule.ViolationDetected += value;
        }
        remove
        {
            if (_sopModule != null)
                _sopModule.ViolationDetected -= value;
        }
    }

    /// <summary>
    /// 事件：姿态检测
    /// </summary>
    public event EventHandler<PoseDetectedEventArgs>? PoseDetected
    {
        add
        {
            if (_sopModule != null)
                _sopModule.PoseDetected += value;
        }
        remove
        {
            if (_sopModule != null)
                _sopModule.PoseDetected -= value;
        }
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    public SOPDetectionStarter()
    {
    }

    /// <summary>
    /// 记录日志
    /// </summary>
    private void Log(string message)
    {
        OnLog?.Invoke(message);
    }

    /// <summary>
    /// 初始化SOP检测
    /// </summary>
    public async Task InitializeAsync(
        IConfiguration config,
        ICameraService cameraService,
        string? regionConfigPath = null)
    {
        Log("正在初始化SOP检测...");

        // 1. 加载区域配置（如果提供）
        if (!string.IsNullOrEmpty(regionConfigPath))
        {
            Log($"加载区域配置: {regionConfigPath}");
            _regionLoader = new RegionConfigLoader();
            await _regionLoader.LoadFromJsonAsync(regionConfigPath);
            Log($"区域配置加载完成，共 {_regionLoader.GetAllRegions().Count} 个区域");
        }

        // 2. 创建SOP模块
        Log("创建SOP模块...");
        _sopModule = new SOPModule();

        // 3. 初始化模块
        await _sopModule.InitializeAsync(config, cameraService);
        Log("SOP模块初始化完成");

        Log("SOP检测初始化完成");
    }

    /// <summary>
    /// 加载并启动工作流
    /// </summary>
    public void StartWorkflow(string yamlPath)
    {
        if (_sopModule == null)
        {
            throw new InvalidOperationException("SOP模块未初始化");
        }

        Log($"加载SOP流程: {yamlPath}");
        _sopModule.StartWorkflowFromYaml(yamlPath);
        Log($"SOP流程加载完成: {_sopModule.CurrentWorkflow?.Name}");
        Log($"检测模式: {_sopModule.DetectionMode}");
    }

    /// <summary>
    /// 处理帧
    /// </summary>
    public async Task<SOPModuleResult?> ProcessAsync(Dictionary<string, CaptureFrame> frames)
    {
        if (_sopModule == null)
        {
            return null;
        }

        var result = await _sopModule.ProcessAsync(frames);
        return result as SOPModuleResult;
    }

    /// <summary>
    /// 获取区域坐标
    /// </summary>
    public SKRect? GetRegion(string regionId)
    {
        return _regionLoader?.GetRegion(regionId);
    }

    /// <summary>
    /// 获取所有区域
    /// </summary>
    public Dictionary<string, SKRect> GetAllRegions()
    {
        return _regionLoader?.GetAllRegions() ?? new Dictionary<string, SKRect>();
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _sopModule?.Dispose();
        GC.SuppressFinalize(this);
    }
}
