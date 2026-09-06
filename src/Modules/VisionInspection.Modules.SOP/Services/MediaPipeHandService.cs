using SkiaSharp;
using VisionInspection.Modules.SOP.Models;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// MediaPipe 手部检测服务 — 使用两阶段 ONNX 模型。
/// 后台线程执行检测（避免 ONNX 推理直接占用推理锁），调用方等待本次检测完成
/// 后拿到真实结果（上限 600ms）。不再采用"立即返回缓存"策略：
/// 多相机/高负载下检测变慢时，旧策略大多数调用会落在"检测进行中"窗口而返回空缓存，
/// 表现为手部检测"没有运行"。
/// </summary>
public class MediaPipeHandService : IHandPoseEstimationService
{
    private MediaPipeHandDetector? _detector;
    private HandPoseEstimationConfig _config = new();
    private readonly object _lock = new();
    private volatile bool _isDetecting;

    // ⭐ 检测完成信号：调用方等待本次检测完成后再取结果
    private readonly System.Threading.ManualResetEventSlim _detectDone = new(true);

    // 缓存最近一次成功的检测结果
    private HandPoseEstimationResult? _lastResult;

    public bool IsInitialized => _detector?.IsInitialized ?? false;

    public async Task InitializeAsync(HandPoseEstimationConfig config)
    {
        _config = config;

        await Task.Run(() =>
        {
            lock (_lock)
            {
                Console.WriteLine($"[MediaPipeHand] *** 将使用 MediaPipe 手部检测方案 (后台检测模式) ***");
                Console.WriteLine($"[MediaPipeHand] Palm: {config.PalmModelPath}");
                Console.WriteLine($"[MediaPipeHand] Landmark: {config.LandmarkModelPath}");

                _detector = new MediaPipeHandDetector(config);
                _detector.Initialize();
                Console.WriteLine($"[MediaPipeHand] 初始化完成, IsInitialized={_detector.IsInitialized}, 计算设备={(config.UseGpu ? (_detector.UsingGpu ? "CUDA/GPU" : "CPU(已回退)") : "CPU(配置关闭)")}");
            }
        });
    }

    public Task<HandPoseEstimationResult> DetectHandsAsync(SKBitmap image)
    {
        if (_detector == null || !_detector.IsInitialized)
            return Task.FromResult(new HandPoseEstimationResult { Hands = new List<HandPose>() });

        // 若当前没有正在进行的检测，启动一次后台检测（使用输入帧副本，避免并发修改/释放冲突）
        lock (_lock)
        {
            if (!_isDetecting)
            {
                _isDetecting = true;
                _detectDone.Reset();

                var imageCopy = image.Copy();
                _ = Task.Run(() =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var hands = _detector!.DetectHands(imageCopy);
                        sw.Stop();
                        var result = new HandPoseEstimationResult
                        {
                            Hands = hands,
                            Timestamp = DateTime.Now
                        };
                        lock (_lock)
                        {
                            _lastResult = result;
                        }
                        System.Diagnostics.Debug.WriteLine($"[MediaPipeHand] DetectHands 完成: {hands.Count} 只手, 耗时 {sw.ElapsedMilliseconds}ms");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MediaPipeHand] DetectHands error: {ex.Message}");
                    }
                    finally
                    {
                        imageCopy.Dispose();
                        lock (_lock)
                        {
                            _isDetecting = false;
                        }
                        _detectDone.Set();
                    }
                });
            }
        }

        // ⭐ 等待本次检测完成（上限 600ms），拿到真实结果后再返回；
        // 避免检测进行中时返回空缓存导致手部检测"看起来没运行"。
        if (!_detectDone.IsSet)
            _detectDone.Wait(600);

        lock (_lock)
        {
            if (_lastResult != null)
                return Task.FromResult(_lastResult);
        }

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
