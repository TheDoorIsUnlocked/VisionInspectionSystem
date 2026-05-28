using SkiaSharp;
using VisionInspection.Modules.SOP.Models;
using YoloOptions = YoloDotNet.Models.YoloOptions;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 手部姿态估计服务接口
/// </summary>
public interface IHandPoseEstimationService
{
    /// <summary>
    /// 检测图像中的所有手部并估计姿态
    /// </summary>
    Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image);

    /// <summary>
    /// 是否已初始化
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// 初始化服务
    /// </summary>
    Task InitializeAsync(HandPoseEstimationConfig config);

    /// <summary>
    /// 关闭服务
    /// </summary>
    Task ShutdownAsync();
}

/// <summary>
/// 基于MediaPipe的手部姿态估计服务
/// </summary>
public class MediaPipeHandPoseEstimationService : IHandPoseEstimationService
{
    private HandPoseEstimationConfig _config = new();
    private MediaPipeHandDetector? _detector;
    private readonly object _lockObject = new();

    public bool IsInitialized => _detector?.IsInitialized ?? false;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;
        
        await Task.Run(() =>
        {
            lock (_lockObject)
            {
                // 创建检测器（支持ONNX模型或模拟模式）
                _detector = new MediaPipeHandDetector(
                    config.PalmModelPath,
                    config.LandmarkModelPath,
                    config.ConfidenceThreshold,
                    config.MaxNumHands
                );
                
                _detector.Initialize();
                
                Console.WriteLine($"[HandPose] 初始化手部姿态估计服务");
                Console.WriteLine($"[HandPose] 手掌检测模型: {config.PalmModelPath}");
                Console.WriteLine($"[HandPose] 关键点检测模型: {config.LandmarkModelPath}");
                Console.WriteLine($"[HandPose] 置信度阈值: {config.ConfidenceThreshold}");
                Console.WriteLine($"[HandPose] 最大手数: {config.MaxNumHands}");
                Console.WriteLine($"[HandPose] 使用GPU: {config.UseGpu}");
            }
        });
    }

    public async Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        if (_detector == null)
        {
            throw new InvalidOperationException("手部姿态估计服务未初始化");
        }

        var result = new HandPoseEstimationResult
        {
            Timestamp = DateTime.Now
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 执行手部检测
        var hands = await Task.Run(() => _detector.DetectHands(image));
        result.Hands.AddRange(hands);

        stopwatch.Stop();
        result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        if (hands.Count > 0)
        {
            Console.WriteLine($"[HandPose] 检测到 {hands.Count} 只手，处理时间: {result.ProcessingTimeMs}ms");
        }

        return result;
    }

    public Task ShutdownAsync()
    {
        lock (_lockObject)
        {
            _detector?.Dispose();
            _detector = null;
            Console.WriteLine("[HandPose] 手部姿态估计服务已关闭");
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// 基于YOLO的手部检测 + 简单关键点估计（备用方案）
/// </summary>
public class YoloHandPoseEstimationService : IHandPoseEstimationService
{
    private YoloDotNet.Yolo? _yolo;
    private HandPoseEstimationConfig _config = new();

    public bool IsInitialized => _yolo != null;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;
        
        if (!File.Exists(config.ModelPath))
        {
            throw new FileNotFoundException($"手部检测模型文件不存在: {config.ModelPath}");
        }

        await Task.Run(() =>
        {
            var options = new YoloOptions
            {
                ExecutionProvider = config.UseGpu 
                    ? new YoloDotNet.ExecutionProvider.Cuda.CudaExecutionProvider(config.ModelPath, 0)
                    : new YoloDotNet.ExecutionProvider.Cpu.CpuExecutionProvider(config.ModelPath),
                ImageResize = YoloDotNet.Enums.ImageResize.Proportional,
                SamplingOptions = new(SkiaSharp.SKFilterMode.Nearest, SkiaSharp.SKMipmapMode.None)
            };

            _yolo = new YoloDotNet.Yolo(options);
            Console.WriteLine($"[HandPose] YOLO手部检测模型加载成功: {config.ModelPath}");
        });
    }

    public async Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        if (_yolo == null)
        {
            throw new InvalidOperationException("YOLO手部检测模型未初始化");
        }

        var result = new HandPoseEstimationResult
        {
            Timestamp = DateTime.Now
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 使用YOLO进行姿态检测（检测人体，然后提取手部关键点）
        var poses = await Task.Run(() => _yolo.RunPoseEstimation(image));

        foreach (var pose in poses)
        {
            // 从人体姿态中提取手部关键点
            var handPose = ExtractHandPoseFromBodyPose(pose);
            if (handPose != null)
            {
                result.Hands.Add(handPose);
            }
        }

        stopwatch.Stop();
        result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        return result;
    }

    private HandPose? ExtractHandPoseFromBodyPose(YoloDotNet.Models.PoseEstimation pose)
    {
        // YOLO Pose检测17个关键点，其中包括手腕
        // 这里提取手腕作为手部位置的近似
        var keypoints = pose.KeyPoints;
        
        // YOLO Pose的KeyPoint是record类型，包含X, Y, Confidence
        // COCO格式索引：9=左手腕，10=右手腕
        // 注意：YOLO输出可能不包含ID，这里简化处理
        if (keypoints.Length < 11)
        {
            return null;
        }

        // 创建一个简化的手部姿态（仅包含手腕位置）
        var handPose = new HandPose
        {
            Timestamp = DateTime.Now,
            HandType = HandType.Right  // 默认右手
        };

        // 使用第10个点作为手腕（简化处理）
        var wrist = keypoints[9];
        if (wrist.Confidence >= _config.ConfidenceThreshold)
        {
            handPose.Keypoints.Add(new HandKeypoint(
                HandKeypointType.Wrist,
                wrist.X,
                wrist.Y,
                0,  // Z坐标未知
                (float)wrist.Confidence
            ));
            return handPose;
        }

        return null;
    }

    public Task ShutdownAsync()
    {
        _yolo?.Dispose();
        _yolo = null;
        Console.WriteLine("[HandPose] YOLO手部检测服务已关闭");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 基于DWPose的手部姿态估计服务
/// 使用DWPose进行全身姿态检测，提取手部关键点
/// 支持双手同时检测，检测更稳定
/// </summary>
public class DWPoseHandEstimationService : IHandPoseEstimationService
{
    private HandPoseEstimationConfig _config = new();
    private DWPoseHandDetector? _detector;
    private readonly object _lockObject = new();

    public bool IsInitialized => _detector?.IsInitialized ?? false;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;

        await Task.Run(() =>
        {
            lock (_lockObject)
            {
                // DWPose使用两个模型：检测模型和姿态模型
                string modelDir = config.PalmModelPath;
                string detModelPath = config.PalmModelPath;
                string poseModelPath = config.LandmarkModelPath;

                // 如果路径是DWPose模型目录，使用默认模型名称
                if (Directory.Exists(modelDir))
                {
                    detModelPath = Path.Combine(modelDir, "yolox_l.onnx");
                    poseModelPath = Path.Combine(modelDir, "dw-ll_ucoco_384.onnx");
                }

                Console.WriteLine($"[HandPose] 正在初始化DWPose手部姿态估计服务...");
                Console.WriteLine($"[HandPose] 检测模型路径: {detModelPath}");
                Console.WriteLine($"[HandPose] 姿态模型路径: {poseModelPath}");

                _detector = new DWPoseHandDetector(
                    detModelPath,
                    poseModelPath,
                    config.ConfidenceThreshold,
                    config.MaxNumHands
                );

                _detector.Initialize();

                if (!_detector.IsInitialized)
                {
                    throw new InvalidOperationException("DWPose检测器初始化失败，请检查模型文件是否存在");
                }

                Console.WriteLine($"[HandPose] DWPose手部姿态估计服务初始化成功");
                Console.WriteLine($"[HandPose] 检测模型: {detModelPath}");
                Console.WriteLine($"[HandPose] 姿态模型: {poseModelPath}");
                Console.WriteLine($"[HandPose] 置信度阈值: {config.ConfidenceThreshold}");
                Console.WriteLine($"[HandPose] 最大手数: {config.MaxNumHands}");
            }
        });
    }

    public async Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        if (_detector == null)
        {
            throw new InvalidOperationException("DWPose手部姿态估计服务未初始化");
        }

        var result = new HandPoseEstimationResult
        {
            Timestamp = DateTime.Now
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 执行手部检测
        var hands = await Task.Run(() => _detector.DetectHands(image));
        result.Hands.AddRange(hands);

        stopwatch.Stop();
        result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        if (hands.Count > 0)
        {
            Console.WriteLine($"[HandPose] DWPose检测到 {hands.Count} 只手，处理时间: {result.ProcessingTimeMs}ms");
        }

        return result;
    }

    public Task ShutdownAsync()
    {
        lock (_lockObject)
        {
            _detector?.Dispose();
            _detector = null;
            Console.WriteLine("[HandPose] DWPose手部姿态估计服务已关闭");
        }
        return Task.CompletedTask;
    }
}
