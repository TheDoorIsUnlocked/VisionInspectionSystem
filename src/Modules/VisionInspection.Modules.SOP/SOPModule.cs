using Microsoft.Extensions.Configuration;
using SkiaSharp;
using System.Diagnostics;
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
/// SOP检测测模式?- 支持物体检测测和姿态估计计双模式式
/// </summary>
public class SOPModule : IDetectionModule
{
    private SOPModuleConfig _config = new();
    private ICameraService? _cameraService;
    private Yolo? _yolo;
    private SOPStateMachine? _stateMachine;
    private SOPWorkflow? _currentWorkflow;
    private readonly object _lockObject = new();

    // 静态锁，确保手部姿态估计服务只初始化一次
    private static readonly object _handPoseInitLock = new();
    private static bool _isHandPoseInitializing = false;

    // 模型动态加载相关
    private string _lastLoadedModelPath = "";

    // 姿态估计相关
    private IPoseEstimationService? _poseService;
    private PoseConditionEvaluator? _poseEvaluator;
    private PoseViolationDetector? _poseViolationDetector;

    // 手部姿态估计相关
    private IHandPoseEstimationService? _handPoseService;
    private HandPoseEstimationResult? _lastHandPoseResult;

    // 手部检测后端选择：Auto=MediaPipe→YOLO-pose 兜底；MediaPipe/YoloPose=强制
    // MediaPipe 为默认后端（握拳/横向手泛化好、无需额外大模型）。
    private HandDetectionBackend _handBackend = HandDetectionBackend.MediaPipe;

    // 文件日志记录器
    private static readonly object _sopLogLock = new();
    private const string _sopDebugLogPath = "sop_module_debug.log";

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [SOPModule] {message}";

        Console.WriteLine(logLine);
        Debug.WriteLine(logLine);

        lock (_sopLogLock)
        {
            try
            {
                File.AppendAllText(_sopDebugLogPath, logLine + Environment.NewLine);
            }
            catch { }
        }
    }

    private SOPDetectionMode _detectionMode = SOPDetectionMode.UnifiedDetection;
    private const string LOG_FILE = "sop_module_debug.log";

    public string Name => "SOPModule";
    public ModuleState State { get; private set; } = ModuleState.Uninitialized;
    public SOPStateMachine? StateMachine => _stateMachine;
    public SOPWorkflow? CurrentWorkflow => _currentWorkflow;

    /// <summary>
    /// 统一日志输出方法（同时输出到 Console、Debug 和文件）
    /// </summary>
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] [SOP] {message}";

        // 1. 输出到 Console
        Console.WriteLine(logLine);

        // 2. 输出到 Debug（可在 Visual Studio 输出窗口查看）
        Debug.WriteLine(logLine);

        // 3. 写入文件（确保不丢失）
        try
        {
            File.AppendAllText(LOG_FILE, logLine + Environment.NewLine);
        }
        catch
        {
            // 忽略文件写入错误
        }
    }

    /// <summary>
    /// 当前检测测模式式
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
    /// 是否启用了姿态估计计
    /// </summary>
    public bool IsPoseEnabled => false; // 统一检测模式下，姿态估计默认不启用

    public ModuleMetadata Metadata { get; } = new()
    {
        DisplayName = "SOP合规检测测",
        Description = "检测测作业员是否遵守标准作业流程（支持物体检测测和姿态估计计双模式式）",
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

    /// <summary>
    /// 模型加载告警（例如工作流指定的专用模型文件不存在、回退到通用COCO模型时触发）。
    /// 用于把"静默回退"暴露给界面，避免操作员面对"什么都没识别到"却无任何提示。
    /// </summary>
    public event EventHandler<string>? ModelWarning;

    public async Task InitializeAsync(IConfiguration config, ICameraService cameraService)
    {
        State = ModuleState.Initializing;
        _cameraService = cameraService;

        try
        {
            // 手动读取配置，避免配置绑定问题
            var sopSection = config.GetSection("SOPModule");

            // 调试：输出所有配置键值对
            Log("开始加载配置...");
            foreach (var child in sopSection.GetChildren())
            {
                Log($"Config key: {child.Key} = {child.Value}");
            }

            _config = new SOPModuleConfig
            {
                ModelPath = sopSection["ModelPath"] ?? "yolo_models/yolov8s.onnx",
                UseGpu = bool.TryParse(sopSection["UseGpu"], out var useGpu) ? useGpu : true,
                ConfidenceThreshold = float.TryParse(sopSection["ConfidenceThreshold"], out var conf) ? conf : 0.6f,
                IouThreshold = float.TryParse(sopSection["IouThreshold"], out var iou) ? iou : 0.45f
            };

            // 读取姿态估计配置
            var poseSection = sopSection.GetSection("PoseEstimation");
            if (poseSection.Exists())
            {
                _config.PoseEstimation = new PoseEstimationConfig
                {
                    Enabled = bool.TryParse(poseSection["Enabled"], out var poseEnabled) ? poseEnabled : false,
                    ModelPath = poseSection["ModelPath"] ?? "yolo_models/yolov8s-pose.onnx"
                };
            }

            // 读取手部检测后端选择（Auto / MediaPipe / YoloPose）
            var backendStr = sopSection["HandDetectionBackend"];
            if (!string.IsNullOrEmpty(backendStr)
                && System.Enum.TryParse<HandDetectionBackend>(backendStr, true, out var parsedBackend))
            {
                _handBackend = parsedBackend;
            }

            // 读取手部姿态估计配置
            var handPoseSection = sopSection.GetSection("HandPoseEstimation");
            if (handPoseSection.Exists())
            {
                _config.HandPoseEstimation = new HandPoseEstimationConfig
                {
                    ModelPath = handPoseSection["ModelPath"] ?? "models",
                    ConfidenceThreshold = float.TryParse(handPoseSection["ConfidenceThreshold"], out var handConf) ? handConf : 0.5f,
                    MaxNumHands = int.TryParse(handPoseSection["MaxNumHands"], out var maxHands) ? maxHands : 2,
                    UseGpu = bool.TryParse(handPoseSection["UseGpu"], out var handGpu) ? handGpu : true
                };
            }

            Console.WriteLine($"[SOP] 配置加载完成: ModelPath={_config.ModelPath}, UseGpu={_config.UseGpu}");

            // 初始化物体检测
            await InitializeYoloAsync();

            // 初始化姿态估计（如果启用）
            if (_config.PoseEstimation?.Enabled == true)
            {
                await InitializePoseEstimationAsync();
            }

            // 初始化状态机（先初始化状态机，确保核心功能可用）
            _stateMachine = new SOPStateMachine();
            SubscribeToStateMachineEvents();

            State = ModuleState.Ready;

            // 手部姿态估计服务改为延迟初始化，在第一次需要时初始化
            // 避免启动时卡住
            Log("手部姿态估计服务将在需要时延迟初始化");
        }
        catch (KeyNotFoundException knfEx)
        {
            State = ModuleState.Error;
            Console.WriteLine($"[SOP ERROR] KeyNotFoundException: {knfEx.Message}");
            Console.WriteLine($"[SOP ERROR] StackTrace: {knfEx.StackTrace}");
            throw new Exception($"SOP模式块初始化失?- 键未找到: {knfEx.Message}. 堆栈: {knfEx.StackTrace}", knfEx);
        }
        catch (Exception ex)
        {
            State = ModuleState.Error;
            Console.WriteLine($"[SOP ERROR] Exception: {ex.Message}");
            Console.WriteLine($"[SOP ERROR] StackTrace: {ex.StackTrace}");
            throw new Exception($"SOP模式块初始化失? {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 初始?YOLO（启动时调用一次）
    /// </summary>
    private async Task InitializeYoloAsync()
    {
        await LoadYoloAsync(_config.ModelPath, _config.UseGpu, "0");
    }

    /// <summary>
    /// 按指定路径加载?切换 YOLO 模式型
    /// </summary>
    private async Task LoadYoloAsync(string modelPath, bool useGpu, string? gpuDevice = null)
    {
        Console.WriteLine($"[SOP LoadYoloAsync] 开始加载模型: modelPath={modelPath}, useGpu={useGpu}");

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

                    // Resolve the model path by searching multiple possible locations
                    var resolvedPath = ResolveModelPath(modelPath);
                    if (resolvedPath == null)
                    {
                        Console.WriteLine($"[SOP] 警告: 模型文件不存在: {modelPath}");
                        _lastLoadedModelPath = "";
                        return;
                    }

                    Console.WriteLine($"[SOP] 加载模型: {resolvedPath}");
                    Console.WriteLine($"[SOP] 模型文件: {Path.GetFileName(resolvedPath)}");

                    var options = new YoloOptions
                    {
                        ExecutionProvider = useGpu
                            ? new CudaExecutionProvider(resolvedPath, int.Parse(gpuDevice ?? "0"))
                            : new CpuExecutionProvider(resolvedPath),
                        ImageResize = ImageResize.Proportional,
                        SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
                    };

                    _yolo = new Yolo(options);
                    _lastLoadedModelPath = modelPath;
                    Console.WriteLine($"[SOP] 模型加载成功: {Path.GetFileName(resolvedPath)}");
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

    /// <summary>
    /// Resolves a relative model path to an absolute path by searching common locations.
    /// </summary>
    private static string? ResolveModelPath(string modelPath)
    {
        Console.WriteLine($"[SOP ResolveModelPath] 开始始解? {modelPath}");

        // If already rooted and exists, use as-is
        if (Path.IsPathRooted(modelPath))
        {
            var fileExists = File.Exists(modelPath);
            var dirExists = Directory.Exists(modelPath);
            Console.WriteLine($"[SOP ResolveModelPath] 绝对路径: {modelPath}, 文件存在: {fileExists}, 目录存在: {dirExists}");
            return (fileExists || dirExists) ? modelPath : null;
        }

        // 1. Try relative to current working directory
        var cwdPath = Path.GetFullPath(modelPath);
        if (File.Exists(cwdPath) || Directory.Exists(cwdPath))
        {
            Console.WriteLine($"[SOP ResolveModelPath] 当前工作作目录找到: {cwdPath}");
            return cwdPath;
        }

        // 2. Try relative to the assembly directory (bin/Debug/net8.0-windows/)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, modelPath);
        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            Console.WriteLine($"[SOP ResolveModelPath] 程序集目录找? {candidate}");
            return candidate;
        }

        // 3. Walk up from assembly dir to find the project/solution root that has yolo_models/
        var dir = baseDir;
        for (int i = 0; i < 6; i++)
        {
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
            candidate = Path.Combine(dir, modelPath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                Console.WriteLine($"[SOP ResolveModelPath] 向上第{i + 1}层找? {candidate}");
                return candidate;
            }

            // Also try yolo_models/<modelPath> in case modelPath doesn't include the directory
            var fileName = Path.GetFileName(modelPath);
            candidate = Path.Combine(dir, "yolo_models", fileName);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                Console.WriteLine($"[SOP ResolveModelPath] yolo_models目录找到: {candidate}");
                return candidate;
            }

            // In src/yolo_models/
            candidate = Path.Combine(dir, "src", "yolo_models", fileName);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                Console.WriteLine($"[SOP ResolveModelPath] src/yolo_models目录找到: {candidate}");
                return candidate;
            }
        }

        Console.WriteLine($"[SOP ResolveModelPath] 未找到模式型文?目录: {modelPath}");
        return null;
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

        // 注意：手部姿态估计服务改为延迟初始化，在第一次需要时初始化
        // 避免启动时卡住

        // 设置默认区域（实际应从配置加载）
        SetupDefaultRegions();
    }

    /// <summary>
    /// 初始化手部姿态估计服务
    /// </summary>
    private async Task InitializeHandPoseEstimationAsync()
    {
        // 快速检查：如果已初始化，直接返回
        if (_handPoseService != null && _handPoseService.IsInitialized)
        {
            Log("手部姿态估计已初始化，跳过");
            return;
        }

        // 使用简单锁确保只初始化一次
        lock (_handPoseInitLock)
        {
            // 双重检查：锁内再次检查
            if (_handPoseService != null && _handPoseService.IsInitialized)
            {
                Log("手部姿态估计已初始化（锁内检查），跳过");
                return;
            }

            // 如果正在初始化，跳过（由另一个线程完成）
            if (_isHandPoseInitializing)
            {
                Log("手部姿态估计正在初始化中，跳过");
                return;
            }

            // 标记正在初始化
            _isHandPoseInitializing = true;
        }

        try
        {
            var rawModelPath = _config.HandPoseEstimation?.ModelPath ?? "";

            Log($"开始初始化手部姿态估计，原始路径: '{rawModelPath}'");

            // 如果路径为空，尝试使用默认目录
            if (string.IsNullOrEmpty(rawModelPath))
            {
                rawModelPath = "models";
                Log($"路径为空，使用默认目录: {rawModelPath}");
            }

            var resolvedModelDir = ResolveModelPath(rawModelPath);
            var actualModelPath = resolvedModelDir ?? rawModelPath;

            Log($"解析后路径: {actualModelPath}, 是否目录: {Directory.Exists(actualModelPath)}, 是否文件: {File.Exists(actualModelPath)}");

            var handConfig = new HandPoseEstimationConfig
            {
                ModelPath = actualModelPath,
                Backend = _handBackend,
                ConfidenceThreshold = _config.HandPoseEstimation?.ConfidenceThreshold ?? 0.5f,
                MaxNumHands = _config.HandPoseEstimation?.MaxNumHands ?? 2,
                UseGpu = _config.HandPoseEstimation?.UseGpu ?? true
            };

            Log($"PalmModelPath: '{handConfig.PalmModelPath}', LandmarkModelPath: '{handConfig.LandmarkModelPath}'");
            Log($"Palm模型存在: {File.Exists(handConfig.PalmModelPath)}, Landmark模型存在: {File.Exists(handConfig.LandmarkModelPath)}");

            // 先释放旧的服务实例
            if (_handPoseService is IDisposable oldService)
            {
                try { oldService.Dispose(); } catch { }
                _handPoseService = null;
            }

            // 选择手部检测方案
            // Backend=MediaPipe / YoloPose : 按配置强制使用该方案
            // Backend=Auto（默认）          : MediaPipe → YOLO-pose 兜底
            // MediaPipe 为默认后端（握拳/横向手泛化好、无需额外大模型）。
            // YoloPose 使用 yolov8s-pose 检测手腕 + MediaPipe Landmark 精修手指，
            // 对横伸/远离躯干的手识别率通常更高，作为兜底。

            if (handConfig.Backend == HandDetectionBackend.MediaPipe)
            {
                if (File.Exists(handConfig.PalmModelPath) && handConfig.PalmModelPath.Contains("palm_detection") && File.Exists(handConfig.LandmarkModelPath))
                {
                    _handPoseService = new MediaPipeHandService();
                    await _handPoseService.InitializeAsync(handConfig);
                    Log($"MediaPipe 手部姿态初始化完成, IsInitialized={_handPoseService.IsInitialized}");
                }
                else
                {
                    Log("Backend=MediaPipe 但模型文件缺失，手部检测未初始化");
                }
            }
            else if (handConfig.Backend == HandDetectionBackend.YoloPose)
            {
                string yoloPoseModelPath = @"e:\yolo\YoloDotNet-master\yolo_models\yolov8s-pose.onnx";
                if (!File.Exists(yoloPoseModelPath))
                {
                    yoloPoseModelPath = new[]
                    {
                        @"e:\yolo\YoloDotNet-master\yolo_models\yolov11s-pose.onnx",
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models", "yolov8s-pose.onnx"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models", "yolov11s-pose.onnx"),
                    }.FirstOrDefault(File.Exists) ?? "";
                }

                string landmarkModelPath = @"e:\yolo\YoloDotNet-master\VisionInspectionSystem\src\UI\VisionInspection.UI\models\hand_landmark_sparse_Nx3x224x224.onnx";
                if (!File.Exists(landmarkModelPath))
                {
                    landmarkModelPath = new[]
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "hand_landmark_sparse_Nx3x224x224.onnx"),
                    }.FirstOrDefault(File.Exists) ?? "";
                }

                if (!string.IsNullOrEmpty(yoloPoseModelPath) && !string.IsNullOrEmpty(landmarkModelPath))
                {
                    handConfig.PalmModelPath = yoloPoseModelPath;
                    handConfig.LandmarkModelPath = landmarkModelPath;
                    _handPoseService = new YoloPoseHandEstimationService();
                    await _handPoseService.InitializeAsync(handConfig);
                    Log($"手部姿态估计初始化成功（YoloPose 方案）, pose={yoloPoseModelPath}, landmark={landmarkModelPath}");
                }
                else
                {
                    Log($"Backend=YoloPose 但模型文件缺失，手部检测未初始化。poseExists={File.Exists(yoloPoseModelPath)}, landmarkExists={File.Exists(landmarkModelPath)}");
                }
            }
            else // Auto：MediaPipe 优先，失败兜底 YOLO-pose
            {
                bool useMediaPipe = File.Exists(handConfig.PalmModelPath)
                    && handConfig.PalmModelPath.Contains("palm_detection")
                    && File.Exists(handConfig.LandmarkModelPath);

                if (useMediaPipe)
                {
                    Log($"检测到 MediaPipe 模型，使用 MediaPipe 方案（默认后端，握拳/横向手支持更好）");
                    _handPoseService = new MediaPipeHandService();
                    await _handPoseService.InitializeAsync(handConfig);
                    Log($"MediaPipe 手部姿态初始化完成, IsInitialized={_handPoseService.IsInitialized}");
                }
                else
                {
                    string yoloPoseModelPath = @"e:\yolo\YoloDotNet-master\yolo_models\yolov8s-pose.onnx";
                    if (!File.Exists(yoloPoseModelPath))
                    {
                        yoloPoseModelPath = new[]
                        {
                            @"e:\yolo\YoloDotNet-master\yolo_models\yolov11s-pose.onnx",
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models", "yolov8s-pose.onnx"),
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models", "yolov11s-pose.onnx"),
                        }.FirstOrDefault(File.Exists) ?? "";
                    }

                    string landmarkModelPath = @"e:\yolo\YoloDotNet-master\VisionInspectionSystem\src\UI\VisionInspection.UI\models\hand_landmark_sparse_Nx3x224x224.onnx";
                    if (!File.Exists(landmarkModelPath))
                    {
                        landmarkModelPath = new[]
                        {
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "hand_landmark_sparse_Nx3x224x224.onnx"),
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "hand_landmark_sparse_Nx3x224x224.onnx"),
                        }.FirstOrDefault(File.Exists) ?? "";
                    }

                    if (!string.IsNullOrEmpty(yoloPoseModelPath) && !string.IsNullOrEmpty(landmarkModelPath))
                    {
                        handConfig.PalmModelPath = yoloPoseModelPath;
                        handConfig.LandmarkModelPath = landmarkModelPath;
                        _handPoseService = new YoloPoseHandEstimationService();
                        await _handPoseService.InitializeAsync(handConfig);
                        Log($"手部姿态估计初始化成功（YoloPose兜底方案）, pose={yoloPoseModelPath}");
                    }
                    else
                    {
                        Log("Auto 模式：MediaPipe 与 YOLO-pose 模型均缺失，手部检测未初始化");
                    }
                }
            }
            }
        }
        catch (Exception ex)
        {
            Log($"手部姿态估计初始化失败: {ex.Message}");
            if (ex.InnerException != null)
            {
                Log($"内部异常: {ex.InnerException.Message}");
            }
            _handPoseService = null;
        }
        finally
        {
            lock (_handPoseInitLock)
            {
                _isHandPoseInitializing = false;
            }
        }
    }

    private void SetupDefaultRegions()
    {
        // 默认区域域定义，实际应从配置文件加载载
        var regions = new Dictionary<string, SKRect>
        {
            ["part_box"] = new SKRect(50, 200, 250, 400),      // 零件盒区域域
            ["fixture"] = new SKRect(300, 250, 500, 450),       // 夹具区域域
            ["tool_rack"] = new SKRect(550, 200, 750, 400),     // 工作具架区域域
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
                result.ErrorMessage = "SOP未在运行状态态";
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

        // 统一检测模式：同时进行物体检测和手部姿态检测
        var detections = ProcessUnifiedDetection(frame, result, timestamp);

        // 关键：把感知结果（物体 + 手部）喂给状态机驱动步骤推进
        _stateMachine?.ProcessFrame(detections, _lastHandPoseResult, timestamp);

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

    /// <summary>
    /// 统一检测模式处理：物体检测（YOLO）+ 手部姿态（MediaPipe），结果一并喂给状态机
    /// </summary>
    private List<ObjectDetection> ProcessUnifiedDetection(CaptureFrame frame, SOPModuleResult result, DateTime timestamp)
    {
        // ========== 1. 物体检测（YOLO）==========
        var detections = new List<ObjectDetection>();
        if (_yolo != null)
        {
            try
            {
                detections = _yolo.RunObjectDetection(frame.Image, _config.ConfidenceThreshold).ToList();
                result.Detections = detections;
                DebugLog($"YOLO 物体检测: {detections.Count} 个目标");
            }
            catch (Exception ex)
            {
                DebugLog($"YOLO 物体检测失败: {ex.Message}");
            }
        }
        else
        {
            DebugLog("YOLO 未初始化，跳过物体检测");
        }

        // ========== 2. 手部姿态检测 ==========

        // 确保服务已初始化
        if (_handPoseService == null || !_handPoseService.IsInitialized)
        {
            try
            {
                InitializeHandPoseEstimationAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                DebugLog($"初始化手部姿态估计服务失败: {ex.Message}");
            }
        }

        // 进行手部姿态检测
        if (_handPoseService != null && _handPoseService.IsInitialized)
        {
            try
            {
                var handResult = _handPoseService.DetectHandsAsync(frame.Image).Result;
                _lastHandPoseResult = handResult;

                result.HandPoseResult = handResult;

                if (handResult.Hands.Count > 0)
                {
                    OnHandPoseDetected(handResult, timestamp);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"手部姿态估计失败: {ex.Message}");
            }
        }

        return detections;
    }

    public void StartWorkflow(SOPWorkflow workflow)
    {
        if (_stateMachine == null)
            throw new InvalidOperationException("状态机未初始化");

        var previousWorkflow = _currentWorkflow;
        _currentWorkflow = workflow;

        // 注入区域域定义（已有）
        if (workflow.Regions != null && workflow.Regions.Count > 0)
        {
            _stateMachine.UpdateZones(workflow.Regions);
            Log($"已加载载 {workflow.Regions.Count} 个区域域定义");
        }
        else
        {
            Log("警告: 工作作流中没有区域域定义，object_in_zone 条件将无法工作作");
        }

        // 检测查并切换 YOLO 模式型
        if (workflow.Model != null && !string.IsNullOrWhiteSpace(workflow.Model.Path))
        {
            var modelPath = workflow.Model.Path;

            // Resolve relative paths by searching common locations
            var resolvedPath = ResolveModelPath(modelPath);
            if (resolvedPath == null)
            {
                var warn = $"工作流模型文件不存在: {modelPath}。\n" +
                           $"已回退到初始化时加载的通用(COCO)模型，该模型不含本产品专属类别" +
                           $"(phone/case_top/case_bottom/manual/charger/cable 等)，\n" +
                           $"因此物体检测、object_present / object_in_zone / hand_near_object / 漏放校验将全部失效。\n" +
                           $"请先训练专用模型并放到该路径，或修正 YAML 的 model.path。";
                Log("警告: " + warn);
                ModelWarning?.Invoke(this, warn);
            }
            else
            {
                modelPath = resolvedPath;

                if (modelPath != _lastLoadedModelPath)
                {
                    Log($"切换模式型: {_lastLoadedModelPath} → {modelPath}");
                    Log($"模式型配置: confidence={workflow.Model.Confidence}, iou={workflow.Model.Iou}, gpu={workflow.Model.UseGpu}");

                    // 同步更新运行时置信度/IoU（本地生效，非配置文件）
                    _config.ConfidenceThreshold = workflow.Model.Confidence;
                    _config.IouThreshold = workflow.Model.Iou;

                    // 后台加载载新模式型（不影响当前帧处理理）
                    _ = Task.Run(() => LoadYoloAsync(modelPath, workflow.Model.UseGpu, workflow.Model.GpuId.ToString()));
                    Log("模式型正在后台加载载... 当前帧仍使用前一个模式型");
                }
                else
                {
                    Log($"模式型未变化，跳过: {Path.GetFileName(modelPath)}");
                }
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
    /// 从YAML文件加载载并启动工作作流
    /// </summary>
    public void StartWorkflowFromYaml(string yamlPath)
    {
        var workflow = SOPYamlConverter.LoadFromYaml(yamlPath);
        StartWorkflow(workflow);
    }

    /// <summary>
    /// 添加载姿态检测测区域?    /// </summary>
    public void AddPoseRegion(string regionId, SKRect region)
    {
        _poseEvaluator?.AddRegion(regionId, region);
    }

    /// <summary>
    /// 添加载禁区域
    /// </summary>
    public void AddForbiddenZone(string zoneId, SKRect zone)
    {
        _poseViolationDetector?.AddForbiddenZone(zoneId, zone);
    }

    public void Dispose()
    {
        try
        {
            // 先停止工作流
            StopWorkflow();

            // 安全释放手部姿态估计服务（防止 AccessViolation）
            if (_handPoseService is IDisposable disposableHand)
            {
                try { disposableHand.Dispose(); } catch { }
                _handPoseService = null;
            }

            // 释放 YOLO
            _yolo?.Dispose();
            _yolo = null;

            // 释放姿态服务
            _poseService = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOP] Dispose 异常: {ex.Message}");
        }
    }

    public Task ShutdownAsync()
    {
        StopWorkflow();
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 更新检测模式
    /// </summary>
    public void UpdateDetectionMode(SOPDetectionMode mode, bool enableHandPose)
    {
        lock (_lockObject)
        {
            var previousMode = _detectionMode;
            _detectionMode = mode;

            Console.WriteLine($"[SOP] 检测模式变更: {previousMode} -> {mode}");

            // 统一检测模式下，如果启用手部检测，异步初始化手部姿态估计服务
            if (enableHandPose)
            {
                if (_handPoseService == null || !_handPoseService.IsInitialized)
                {
                    // 异步初始化，不阻塞当前线程
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await InitializeHandPoseEstimationAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SOP] 手部姿态估计初始化失败: {ex.Message}");
                        }
                    });
                }
            }

            OnDetectionModeChanged();
        }
    }

    /// <summary>
    /// 确保手部姿态估计服务已准备好（用于启动前检查）
    /// </summary>
    public async Task EnsureHandPoseServiceReadyAsync()
    {
        // 统一检测模式下，如果手部姿态估计服务未初始化，等待初始化完成
        if (_handPoseService == null || !_handPoseService.IsInitialized)
        {
            Console.WriteLine($"[SOP] 等待手部姿态估计服务初始化...");
            await InitializeHandPoseEstimationAsync();
            Console.WriteLine($"[SOP] 手部姿态估计服务准备完成, IsInitialized={_handPoseService?.IsInitialized}");
        }
    }

    /// <summary>
    /// 更新手部姿态估计配置
    /// </summary>
    public void UpdateHandPoseConfig(HandPoseEstimationConfig config)
    {
        lock (_lockObject)
        {
            _config.HandPoseEstimation = config;
            Console.WriteLine($"[SOP] 手部姿态估计配置更新: MaxNumHands={config.MaxNumHands}, UseGpu={config.UseGpu}");

            // 如果服务已初始化，异步重新初始化（不阻塞）
            if (_handPoseService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _handPoseService.ShutdownAsync();
                        await InitializeHandPoseEstimationAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SOP] 手部姿态估计重新初始化失败: {ex.Message}");
                    }
                });
            }
        }
    }

    public VisualOverlay GetVisualOverlay()
    {
        return new VisualOverlay();
    }

}

/// <summary>
/// SOP模式块配置
/// </summary>
public class SOPModuleConfig
{
    public string ModelPath { get; set; } = "models/sop_yolov8.onnx";
    public bool UseGpu { get; set; } = true;
    public float ConfidenceThreshold { get; set; } = 0.6f;
    public float IouThreshold { get; set; } = 0.45f;
    public PoseEstimationConfig? PoseEstimation { get; set; }

    /// <summary>
    /// 手部姿态估计计配?    /// </summary>
    public HandPoseEstimationConfig? HandPoseEstimation { get; set; }
}

/// <summary>
/// SOP模式块结果果
/// </summary>
public class SOPModuleResult : ModuleResult
{
    public int CurrentStepId { get; set; }
    public SOPExecutionState CurrentState { get; set; }
    public List<StepExecutionRecord> StepHistory { get; set; } = new();
    public List<ViolationRecord> Violations { get; set; } = new();
    public bool IsPass { get; set; }

    /// <summary>
    /// 检测测结果果（物体检测测模式式）
    /// </summary>
    public List<ObjectDetection> Detections { get; set; } = new();

    /// <summary>
    /// 检测测到的人体姿态数量（姿态模式式）
    /// </summary>
    public int PoseCount { get; set; }

    /// <summary>
    /// 手部姿态检测测结果?    /// </summary>
    public HandPoseEstimationResult? HandPoseResult { get; set; }

    /// <summary>
    /// 步骤结果果信息（用于UI显示?    /// </summary>
    public StepResults StepResults { get; set; } = new();
}

/// <summary>
/// 步骤结果果显示信息
/// </summary>
public class StepResults
{
    /// <summary>
    /// 状态态消?    /// </summary>
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
/// 检测测模式式变更事件参?/// </summary>
public class DetectionModeChangedEventArgs : EventArgs
{
    public SOPDetectionMode NewMode { get; }

    public DetectionModeChangedEventArgs(SOPDetectionMode newMode)
    {
        NewMode = newMode;
    }
}

/// <summary>
/// 姿态检测测事件参?/// </summary>
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
