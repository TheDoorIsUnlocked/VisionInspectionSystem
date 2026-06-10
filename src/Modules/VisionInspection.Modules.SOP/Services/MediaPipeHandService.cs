using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// MediaPipe 手部检测服务 — 使用两阶段 ONNX 模型。
/// 采用非阻塞模式：检测在后台运行，调用方始终拿到最近一次完成的检测结果，
/// 避免 ONNX 推理阻塞帧处理管线导致画面卡顿。
/// </summary>
public class MediaPipeHandService : IHandPoseEstimationService
{
    private MediaPipeHandDetector? _detector;
    private HandPoseEstimationConfig _config = new();
    private readonly object _lock = new();
    private volatile bool _isDetecting;

    // 缓存最近一次成功的检测结果
    private HandPoseEstimationResult? _lastResult;
    private DateTime _lastResultTime = DateTime.MinValue;
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromMilliseconds(500);

    public bool IsInitialized => _detector?.IsInitialized ?? false;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;

        await Task.Run(() =>
        {
            lock (_lock)
            {
                var palmPath = config.PalmModelPath;
                var landmarkPath = config.LandmarkModelPath;

                if (string.IsNullOrEmpty(palmPath) || !File.Exists(palmPath))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    palmPath = Path.Combine(baseDir, "models", "palm_detection_full_Nx3x192x192_post.onnx");
                    landmarkPath = Path.Combine(baseDir, "models", "hand_landmark_sparse_Nx3x224x224.onnx");
                }

                if (string.IsNullOrEmpty(landmarkPath) || !File.Exists(landmarkPath))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    landmarkPath = Path.Combine(baseDir, "models", "hand_landmark_sparse_Nx3x224x224.onnx");
                }

                Console.WriteLine($"[MediaPipeHand] Palm: {palmPath} (exists={File.Exists(palmPath)})");
                Console.WriteLine($"[MediaPipeHand] Landmark: {landmarkPath} (exists={File.Exists(landmarkPath)})");
                Console.WriteLine($"[MediaPipeHand] *** 将使用 MediaPipe 手部检测方案 (非阻塞模式) ***");

                _detector = new MediaPipeHandDetector(
                    palmPath,
                    landmarkPath,
                    Math.Max(config.ConfidenceThreshold, 0.2f),
                    config.MaxNumHands);

                _detector.Initialize();
                Console.WriteLine($"[MediaPipeHand] 初始化完成, IsInitialized={_detector.IsInitialized}");
            }
        });
    }

    public Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        if (_detector == null || !_detector.IsInitialized)
            return Task.FromResult(new HandPoseEstimationResult { Hands = new List<HandPose>() });

        // 非阻塞模式：如果当前没有正在进行的检测，启动新的后台检测
        if (!_isDetecting)
        {
            _isDetecting = true;
            var imageCopy = image.Copy();
            _ = Task.Run(() =>
            {
                try
                {
                    var hands = _detector!.DetectHands(imageCopy);
                    var result = new HandPoseEstimationResult
                    {
                        Hands = hands,
                        Timestamp = DateTime.Now
                    };
                    lock (_lock)
                    {
                        _lastResult = result;
                        _lastResultTime = DateTime.Now;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MediaPipeHand] DetectHands error: {ex.Message}");
                }
                finally
                {
                    imageCopy.Dispose();
                    _isDetecting = false;
                }
            });
        }

        // 始终立即返回缓存的结果（不阻塞调用方）
        HandPoseEstimationResult? cached;
        lock (_lock)
        {
            cached = _lastResult;
        }

        if (cached != null && (DateTime.Now - _lastResultTime) < MaxCacheAge)
            return Task.FromResult(cached);

        return Task.FromResult(new HandPoseEstimationResult { Hands = new List<HandPose>(), Timestamp = DateTime.Now });
    }

    public Task ShutdownAsync()
    {
        lock (_lock)
        {
            _detector?.Dispose();
            _detector = null;
            _lastResult = null;
        }
        return Task.CompletedTask;
    }
}
