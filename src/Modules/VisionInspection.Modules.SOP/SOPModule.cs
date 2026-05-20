using Microsoft.Extensions.Configuration;
using SkiaSharp;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Modules.Detection;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.ExecutionProvider.Cuda;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

namespace VisionInspection.Modules.SOP;

/// <summary>
/// SOP检测模块 - 支持物体检测和姿态估计双模式
/// </summary>
public class SOPModule : IDetectionModule
{
    private SOPModuleConfig _config = new();
    private ICameraService? _cameraService;
    private Yolo? _yolo;
    private SOPStateMachine? _stateMachine;
    private SOPWorkflow? _currentWorkflow;
    private readonly object _lockObject = new();

    // ⭐ 模型动态加载相关
    private string _lastLoadedModelPath = "";

    // 姿态估计相关
    private IPoseEstimationService? _poseService;
    private PoseConditionEvaluator? _poseEvaluator;
    private PoseViolationDetector? _poseViolationDetector;
    
    // ⭐ 手部姿态估计相关
    private IHandPoseEstimationService? _handPoseService;
    private HandPoseEstimationResult? _lastHandPoseResult;
    
    private SOPDetectionMode _detectionMode = SOPDetectionMode.ObjectBased;

    public string Name => "SOPModule";
    public ModuleState State { get; private set; } = ModuleState.Uninitialized;
    public SOPStateMachine? StateMachine => _stateMachine;
    public SOPWorkflow? CurrentWorkflow => _currentWorkflow;

    /// <summary>
    /// 当前检测模式
    /// </summary>
    public SOPDetectionMode DetectionMode
    {
        get => _detectionMode;
        set
        {
            _detectionMode = value;
            OnDetectionModeChanged();
        }
    }

    /// <summary>
    /// 是否启用了姿态估计
    /// </summary>
    public bool IsPoseEnabled => _detectionMode is SOPDetectionMode.PoseBased or SOPDetectionMode.Hybrid;

    public ModuleMetadata Metadata { get; } = new()
    {
        DisplayName = "SOP合规检测",
        Description = "检测作业员是否遵守标准作业流程（支持物体检测和姿态估计双模式）",
        Version = "2.1.0",
        Author = "Vision Inspection Team",
        RequiredCameras = new List<string> { "main_camera" }
    };

    public event EventHandler<StepChangedEventArgs>? StepChanged;
    public event EventHandler<ViolationEventArgs>? ViolationDetected;
    public event EventHandler<SOPCompletedEventArgs>? WorkflowCompleted;
    public event EventHandler<StateChangedEventArgs>? SOPStateChanged;
    public event EventHandler<DetectionModeChangedEventArgs>? DetectionModeChanged;
    public event EventHandler<PoseDetectedEventArgs>? PoseDetected;

    public async Task InitializeAsync(IConfiguration config, ICameraService cameraService)
    {
        State = ModuleState.Initializing;
        _cameraService = cameraService;

        _config = config.GetSection("SOPModule").Get<SOPModuleConfig>() ?? new SOPModuleConfig();

        try
        {
            // 初始化物体检测
            await InitializeYoloAsync();

            // 初始化姿态估计（如果启用）
            if (_config.PoseEstimation?.Enabled == true)
            {
                await InitializePoseEstimationAsync();
            }

            // 初始化状态机
            _stateMachine = new SOPStateMachine();
            SubscribeToStateMachineEvents();

            State = ModuleState.Ready;
        }
        catch (Exception ex)
        {
            State = ModuleState.Error;
            throw new Exception($"SOP模块初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 初始化 YOLO（启动时调用一次）
    /// </summary>
    private async Task InitializeYoloAsync()
    {
        await LoadYoloAsync(_config.ModelPath, _config.UseGpu, "0");
    }

    /// <summary>
    /// 按指定路径加载/切换 YOLO 模型
    /// </summary>
    private async Task LoadYoloAsync(string modelPath, bool useGpu, string? gpuDevice = null)
    {
        // 如果路径没变且模型已加载，跳过
        if (_yolo != null && modelPath == _lastLoadedModelPath)
        {
            Console.WriteLine($"[SOP] 模型未变化，跳过重载: {modelPath}");
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                lock (_lockObject)
                {
                    // 释放旧模型
                    _yolo?.Dispose();
                    _yolo = null;

                    if (!File.Exists(modelPath))
                    {
                        Console.WriteLine($"[SOP] 警告: 模型文件不存在: {modelPath}");
                        _lastLoadedModelPath = "";
                        return;
                    }

                    var options = new YoloOptions
                    {
                        ExecutionProvider = useGpu
                            ? new CudaExecutionProvider(modelPath, int.Parse(gpuDevice ?? "0"))
                            : new CpuExecutionProvider(modelPath),
                        ImageResize = ImageResize.Proportional,
                        SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
                    };

                    _yolo = new Yolo(options);
                    _lastLoadedModelPath = modelPath;
                    Console.WriteLine($"[SOP] 模型加载成功: {Path.GetFileName(modelPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SOP] 模型加载失败: {ex.Message}");
                _yolo = null;
                _lastLoadedModelPath = "";
            }
        });
    }

    private async Task InitializePoseEstimationAsync()
    {
        // 使用模拟服务进行测试，实际使用时替换为真实服务
        _poseService = new MockPoseEstimationService();
        // _poseService = new YoloPoseEstimationService();

        if (_config.PoseEstimation != null)
        {
            await _poseService.InitializeAsync(_config.PoseEstimation);
        }

        _poseEvaluator = new PoseConditionEvaluator();
        _poseViolationDetector = new PoseViolationDetector();

        // ⭐ 初始化手部姿态估计服务
        await InitializeHandPoseEstimationAsync();

        // 设置默认区域（实际应从配置加载）
        SetupDefaultRegions();
    }

    /// <summary>
    /// 初始化手部姿态估计服务
    /// </summary>
    private async Task InitializeHandPoseEstimationAsync()
    {
        // 根据配置选择使用哪种手部检测方案
        var handConfig = new HandPoseEstimationConfig
        {
            ModelPath = _config.HandPoseEstimation?.ModelPath ?? "",
            ConfidenceThreshold = _config.HandPoseEstimation?.ConfidenceThreshold ?? 0.5f,
            MaxNumHands = _config.HandPoseEstimation?.MaxNumHands ?? 2,
            UseGpu = _config.HandPoseEstimation?.UseGpu ?? true
        };

        if (!string.IsNullOrEmpty(handConfig.ModelPath) && File.Exists(handConfig.ModelPath))
        {
            // 使用YOLO方案（如果有模型）
            _handPoseService = new YoloHandPoseEstimationService();
            await _handPoseService.InitializeAsync(handConfig);
            Console.WriteLine("[SOP] 手部姿态估计服务初始化完成（YOLO方案）");
        }
        else
        {
            // 使用MediaPipe方案（默认）
            _handPoseService = new MediaPipeHandPoseEstimationService();
            await _handPoseService.InitializeAsync(handConfig);
            Console.WriteLine("[SOP] 手部姿态估计服务初始化完成（MediaPipe方案）");
        }
    }

    private void SetupDefaultRegions()
    {
        // 默认区域定义，实际应从配置文件加载
        var regions = new Dictionary<string, SKRect>
        {
            ["part_box"] = new SKRect(50, 200, 250, 400),      // 零件盒区域
            ["fixture"] = new SKRect(300, 250, 500, 450),       // 夹具区域
            ["tool_rack"] = new SKRect(550, 200, 750, 400),     // 工具架区域
            ["release_channel"] = new SKRect(400, 500, 600, 600) // 放行通道
        };

        foreach (var (id, rect) in regions)
        {
            _poseEvaluator?.AddRegion(id, rect);
        }
    }

    private void SubscribeToStateMachineEvents()
    {
        if (_stateMachine == null) return;

        _stateMachine.StepChanged += (s, e) =>
        {
            StepChanged?.Invoke(this, e);
        };

        _stateMachine.ViolationDetected += (s, e) =>
        {
            ViolationDetected?.Invoke(this, e);
        };

        _stateMachine.WorkflowCompleted += (s, e) =>
        {
            WorkflowCompleted?.Invoke(this, e);
        };

        _stateMachine.StateChanged += (s, e) =>
        {
            SOPStateChanged?.Invoke(this, e);
        };
    }

    private void OnDetectionModeChanged()
    {
        DetectionModeChanged?.Invoke(this, new DetectionModeChangedEventArgs(_detectionMode));
    }

    public Task<ModuleResult> ProcessAsync(Dictionary<string, CaptureFrame> frames)
    {
        var result = new SOPModuleResult { ModuleName = Name };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            State = ModuleState.Processing;

            if (_stateMachine?.CurrentState != SOPExecutionState.Running)
            {
                result.Success = true;
                result.ErrorMessage = "SOP未在运行状态";
                result.Level = DefectLevel.Good;
                return Task.FromResult<ModuleResult>(result);
            }

            if (frames.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "无图像帧";
                result.Level = DefectLevel.Critical;
                return Task.FromResult<ModuleResult>(result);
            }

            var mainFrame = frames.FirstOrDefault(f => f.Key == "main_camera").Value;
            if (mainFrame == null)
            {
                result.Success = false;
                result.ErrorMessage = "未找到主相机帧";
                result.Level = DefectLevel.Critical;
                return Task.FromResult<ModuleResult>(result);
            }

            lock (_lockObject)
            {
                ProcessFrameInternal(mainFrame, result);
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Level = DefectLevel.Critical;
        }
        finally
        {
            State = ModuleState.Ready;
        }

        return Task.FromResult<ModuleResult>(result);
    }

    private void ProcessFrameInternal(CaptureFrame frame, SOPModuleResult result)
    {
        var timestamp = DateTime.Now;

        // 根据检测模式处理
        switch (_detectionMode)
        {
            case SOPDetectionMode.ObjectBased:
                ProcessObjectDetection(frame, result, timestamp);
                break;

            case SOPDetectionMode.PoseBased:
                ProcessPoseDetection(frame, result, timestamp);
                break;

            case SOPDetectionMode.Hybrid:
                ProcessHybridDetection(frame, result, timestamp);
                break;
        }

        // 更新结果
        if (_stateMachine != null)
        {
            result.CurrentStepId = _stateMachine.CurrentStepId;
            result.CurrentState = _stateMachine.CurrentState;
            result.StepHistory = _stateMachine.StepHistory.ToList();
            result.Violations = _stateMachine.Violations.ToList();
            result.IsPass = !_stateMachine.Violations.Any();
            result.Level = result.IsPass ? DefectLevel.Good : DefectLevel.Critical;

            // 更新StepResults（用于UI显示）
            var currentStep = _currentWorkflow?.Steps.FirstOrDefault(s => s.StepId == _stateMachine.CurrentStepId);
            result.StepResults = new StepResults
            {
                Message = currentStep?.StepName ?? "等待开始",
                CurrentStep = _stateMachine.CurrentStepId,
                TotalSteps = _currentWorkflow?.Steps.Count ?? 0
            };
        }
    }

    /// <summary>
    /// 物体检测模式处理
    /// </summary>
    private void ProcessObjectDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
    {
        if (_stateMachine == null) return;

        // 如果 YOLO 还没加载好（刚切换工作流，模型在后台加载中），用空结果
        if (_yolo == null)
        {
            Console.WriteLine("[SOP] 模型加载中，本帧跳过检测");
            return;
        }

        // 使用当前工作流覆盖的阈值，否则用全局配置
        float confidence = _currentWorkflow?.Model?.Confidence ?? _config.ConfidenceThreshold;
        float iou = _currentWorkflow?.Model?.Iou ?? _config.IouThreshold;

        var detections = _yolo.RunObjectDetection(
            frame.Image,
            confidence: confidence,
            iou: iou);

        _stateMachine.ProcessFrame(detections.ToList(), timestamp);

        result.Detections = detections.ToList();
    }

    /// <summary>
    /// 姿态检测模式处理
    /// </summary>
    private void ProcessPoseDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
    {
        if (_poseService == null || _poseEvaluator == null || _stateMachine == null) return;

        // 检测人体姿态
        var poses = _poseService.DetectPosesAsync(frame.Image).Result;

        // 触发姿态检测事件
        if (poses.Count > 0)
        {
            PoseDetected?.Invoke(this, new PoseDetectedEventArgs(poses, timestamp));
        }

        // 获取当前步骤
        var currentStep = _stateMachine.CurrentWorkflow?.Steps
            .FirstOrDefault(s => s.StepId == _stateMachine.CurrentStepId);

        if (currentStep != null)
        {
            // 评估每个通过条件
            foreach (var condition in currentStep.PassConditions)
            {
                var checkResult = _poseEvaluator.EvaluateCondition(condition, poses, currentStep);

                if (checkResult.IsMet)
                {
                    // 条件满足，尝试推进步骤
                    _stateMachine.TryCompleteCurrentStep(timestamp);
                    break;
                }
            }

            // 检测违规
            if (_poseViolationDetector != null)
            {
                var violations = _poseViolationDetector.DetectViolations(poses, currentStep, timestamp);
                foreach (var violation in violations)
                {
                    _stateMachine.ReportViolation(violation);
                }
            }
        }

        result.PoseCount = poses.Count;
    }

    /// <summary>
    /// 混合检测模式处理
    /// </summary>
    private void ProcessHybridDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
    {
        if (_yolo == null || _stateMachine == null) return;

        // 同时进行物体检测和姿态检测
        var detections = _yolo.RunObjectDetection(
            frame.Image,
            confidence: _config.ConfidenceThreshold,
            iou: _config.IouThreshold);

        result.Detections = detections.ToList();

        // ⭐ 如果启用了手部姿态估计，进行手部检测
        if (_handPoseService != null && _handPoseService.IsInitialized)
        {
            try
            {
                var handResult = _handPoseService.DetectHandsAsync(frame.Image).Result;
                _lastHandPoseResult = handResult;
                
                if (handResult.Hands.Count > 0)
                {
                    Console.WriteLine($"[SOP] 检测到手部: {handResult.Hands.Count}只, 耗时: {handResult.ProcessingTimeMs}ms");
                    
                    // 触发手部检测事件
                    OnHandPoseDetected(handResult, timestamp);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SOP] 手部姿态估计失败: {ex.Message}");
            }
        }

        // 如果启用了姿态估计，也进行姿态检测
        if (_poseService != null && _poseEvaluator != null)
        {
            var poses = _poseService.DetectPosesAsync(frame.Image).Result;
            result.PoseCount = poses.Count;

            if (poses.Count > 0)
            {
                PoseDetected?.Invoke(this, new PoseDetectedEventArgs(poses, timestamp));
            }

            // 优先使用姿态检测进行步骤判断
            var currentStep = _stateMachine.CurrentWorkflow?.Steps
                .FirstOrDefault(s => s.StepId == _stateMachine.CurrentStepId);

            if (currentStep != null)
            {
                bool poseConditionMet = false;

                foreach (var condition in currentStep.PassConditions)
                {
                    var checkResult = _poseEvaluator.EvaluateCondition(condition, poses, currentStep);
                    if (checkResult.IsMet)
                    {
                        poseConditionMet = true;
                        break;
                    }
                }

                // 如果姿态条件不满足，回退到物体检测
                if (!poseConditionMet)
                {
                    _stateMachine.ProcessFrame(detections.ToList(), timestamp);
                }
                else
                {
                    _stateMachine.TryCompleteCurrentStep(timestamp);
                }

                // 检测姿态违规
                if (_poseViolationDetector != null)
                {
                    var violations = _poseViolationDetector.DetectViolations(poses, currentStep, timestamp);
                    foreach (var violation in violations)
                    {
                        _stateMachine.ReportViolation(violation);
                    }
                }
            }
            else
            {
                _stateMachine.ProcessFrame(detections.ToList(), timestamp);
            }
        }
        else
        {
            // 姿态服务未初始化，仅使用物体检测
            _stateMachine.ProcessFrame(detections.ToList(), timestamp);
        }
    }

    /// <summary>
    /// 手部姿态检测事件
    /// </summary>
    public event EventHandler<HandPoseDetectedEventArgs>? HandPoseDetected;

    /// <summary>
    /// 触发手部姿态检测事件
    /// </summary>
    private void OnHandPoseDetected(HandPoseEstimationResult result, DateTime timestamp)
    {
        HandPoseDetected?.Invoke(this, new HandPoseDetectedEventArgs(result, timestamp));
    }

    public void StartWorkflow(SOPWorkflow workflow)
    {
        if (_stateMachine == null)
            throw new InvalidOperationException("状态机未初始化");

        var previousWorkflow = _currentWorkflow;
        _currentWorkflow = workflow;

        // ⭐ 注入区域定义（已有）
        if (workflow.Regions != null && workflow.Regions.Count > 0)
        {
            _stateMachine.UpdateZones(workflow.Regions);
            Console.WriteLine($"[SOP] 已加载 {workflow.Regions.Count} 个区域定义");
        }
        else
        {
            Console.WriteLine("[SOP] 警告: 工作流中没有区域定义，object_in_zone 条件将无法工作");
        }

        // ⭐ 检查并切换 YOLO 模型
        if (workflow.Model != null && !string.IsNullOrWhiteSpace(workflow.Model.Path))
        {
            var modelPath = workflow.Model.Path;

            // 支持相对路径：相对于项目根目录下 yolo_models/ 目录
            if (!Path.IsPathRooted(modelPath))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
                var candidate1 = Path.Combine(projectRoot, "yolo_models", modelPath);
                var candidate2 = Path.Combine(projectRoot, modelPath);

                if (File.Exists(candidate1))
                    modelPath = candidate1;
                else if (File.Exists(modelPath))
                { /* 相对路径指的就是当前模型路径，保持不变 */ }
                else if (File.Exists(candidate2))
                    modelPath = candidate2;
            }

            if (modelPath != _lastLoadedModelPath)
            {
                Console.WriteLine($"[SOP] 切换模型: {_lastLoadedModelPath} → {modelPath}");
                Console.WriteLine($"[SOP] 模型配置: confidence={workflow.Model.Confidence}, iou={workflow.Model.Iou}, gpu={workflow.Model.UseGpu}");

                // 同步更新运行时置信度/IoU（本地生效，非配置文件）
                _config.ConfidenceThreshold = workflow.Model.Confidence;
                _config.IouThreshold = workflow.Model.Iou;

                // 后台加载新模型（不影响当前帧处理）
                _ = Task.Run(() => LoadYoloAsync(modelPath, workflow.Model.UseGpu, workflow.Model.GpuId.ToString()));
                Console.WriteLine($"[SOP] 模型正在后台加载... 当前帧仍使用前一个模型");
            }
            else
            {
                Console.WriteLine($"[SOP] 模型未变化，跳过: {Path.GetFileName(modelPath)}");
            }
        }

        _stateMachine.Start(workflow);
    }

    public void PauseWorkflow()
    {
        _stateMachine?.Pause();
    }

    public void ResumeWorkflow()
    {
        _stateMachine?.Resume();
    }

    public void StopWorkflow()
    {
        _stateMachine?.Stop();
        _currentWorkflow = null;
    }

    public void ResetWorkflow()
    {
        if (_currentWorkflow != null)
        {
            _stateMachine?.Start(_currentWorkflow);
        }
    }

    /// <summary>
    /// 从YAML文件加载并启动工作流
    /// </summary>
    public void StartWorkflowFromYaml(string yamlPath)
    {
        var workflow = SOPYamlConverter.LoadFromYaml(yamlPath);
        StartWorkflow(workflow);
    }

    /// <summary>
    /// 添加姿态检测区域
    /// </summary>
    public void AddPoseRegion(string regionId, SKRect region)
    {
        _poseEvaluator?.AddRegion(regionId, region);
    }

    /// <summary>
    /// 添加禁区
    /// </summary>
    public void AddForbiddenZone(string zoneId, SKRect zone)
    {
        _poseViolationDetector?.AddForbiddenZone(zoneId, zone);
    }

    public void Dispose()
    {
        _yolo?.Dispose();
        _poseService = null;
    }

    public Task ShutdownAsync()
    {
        StopWorkflow();
        Dispose();
        return Task.CompletedTask;
    }

    public VisualOverlay GetVisualOverlay()
    {
        var overlay = new VisualOverlay();

        // 添加当前步骤信息
        if (_stateMachine != null)
        {
            var currentStep = _stateMachine.CurrentWorkflow?.Steps
                .FirstOrDefault(s => s.StepId == _stateMachine.CurrentStepId);

            if (currentStep != null)
            {
                overlay.Items.Add(new OverlayItem
                {
                    Type = OverlayType.Text,
                    Bounds = new SKRect(10, 10, 400, 40),
                    Color = SKColors.Yellow,
                    Label = $"步骤: {currentStep.StepName}"
                });
            }
        }

        return overlay;
    }
}

/// <summary>
/// SOP模块配置
/// </summary>
public class SOPModuleConfig
{
    public string ModelPath { get; set; } = "models/sop_yolov8.onnx";
    public bool UseGpu { get; set; } = true;
    public float ConfidenceThreshold { get; set; } = 0.6f;
    public float IouThreshold { get; set; } = 0.45f;
    public PoseEstimationConfig? PoseEstimation { get; set; }
    
    /// <summary>
    /// 手部姿态估计配置
    /// </summary>
    public HandPoseEstimationConfig? HandPoseEstimation { get; set; }
}

/// <summary>
/// SOP模块结果
/// </summary>
public class SOPModuleResult : ModuleResult
{
    public int CurrentStepId { get; set; }
    public SOPExecutionState CurrentState { get; set; }
    public List<StepExecutionRecord> StepHistory { get; set; } = new();
    public List<ViolationRecord> Violations { get; set; } = new();
    public bool IsPass { get; set; }

    /// <summary>
    /// 检测结果（物体检测模式）
    /// </summary>
    public List<ObjectDetection> Detections { get; set; } = new();

    /// <summary>
    /// 检测到的人体姿态数量（姿态模式）
    /// </summary>
    public int PoseCount { get; set; }

    /// <summary>
    /// 步骤结果信息（用于UI显示）
    /// </summary>
    public StepResults StepResults { get; set; } = new();
}

/// <summary>
/// 步骤结果显示信息
/// </summary>
public class StepResults
{
    /// <summary>
    /// 状态消息
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// 当前步骤
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    /// 总步骤数
    /// </summary>
    public int TotalSteps { get; set; }
}

/// <summary>
/// 检测模式变更事件参数
/// </summary>
public class DetectionModeChangedEventArgs : EventArgs
{
    public SOPDetectionMode NewMode { get; }

    public DetectionModeChangedEventArgs(SOPDetectionMode newMode)
    {
        NewMode = newMode;
    }
}

/// <summary>
/// 姿态检测事件参数
/// </summary>
public class PoseDetectedEventArgs : EventArgs
{
    public List<HumanPose> Poses { get; }
    public DateTime Timestamp { get; }

    public PoseDetectedEventArgs(List<HumanPose> poses, DateTime timestamp)
    {
        Poses = poses;
        Timestamp = timestamp;
    }
}
