using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 手部姿态估计服务接口
/// </summary>
public interface IHandPoseEstimationService
{
    Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image);
    bool IsInitialized { get; }
    Task InitializeAsync(HandPoseEstimationConfig config);
    Task ShutdownAsync();
}

/// <summary>
/// 基于DWPose的手部姿态估计服务（备用方案）
/// 使用DWPose进行全身姿态检测，提取手部关键点
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
                // DWPose 使用两个模型：人体检测(yolox_l.onnx) + 全身姿态(dw-ll_ucoco_384.onnx)
                // 优先使用显式配置路径，否则回退到模型目录下的默认文件名
                string detModelPath = config.DWPoseDetModelPath;
                string poseModelPath = config.DWPosePoseModelPath;

                if (string.IsNullOrEmpty(detModelPath) || string.IsNullOrEmpty(poseModelPath))
                {
                    string modelDir = !string.IsNullOrEmpty(config.DWPoseModelDir)
                        ? config.DWPoseModelDir
                        : config.PalmModelPath; // 兼容旧逻辑：把 PalmModelPath 当目录用

                    if (Directory.Exists(modelDir))
                    {
                        if (string.IsNullOrEmpty(detModelPath))
                            detModelPath = Path.Combine(modelDir, "yolox_l.onnx");
                        if (string.IsNullOrEmpty(poseModelPath))
                            poseModelPath = Path.Combine(modelDir, "dw-ll_ucoco_384.onnx");
                    }
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
