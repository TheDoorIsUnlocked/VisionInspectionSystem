using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using SkiaSharp;
using System.IO;
using NAudio.Wave;
using System.Media;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Windows;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;
using VisionInspection.Modules.Detection;
using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.UI.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisionInspection.UI.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ROIManager _roiManager;
    private readonly CameraManager _cameraManager;
    private readonly YoloDetectionService _detectionService;
    private readonly ModelManager _modelManager;
    private SOPModule? _sopModule;
    private ModelInfo? _loadedModel;
    private bool _isDisposed = false;

    /// <summary>主检测当前是否运行在 GPU 上（界面切换开关的数据源）</summary>
    [ObservableProperty]
    private bool _useGpu;

    /// <summary>
    /// 保存的SOP检测模式配置（从SOP配置界面获取）
    /// </summary>
    private (string DetectionMode, bool EnableHandPose, int MaxNumHands,
        bool EnableFaceFilter, float FaceFilterUpperRatio,
        bool EnableHandStructureCheck, float HandStructureWristTipRatio,
        float DetectionConfidenceThreshold, float MinBoxAreaRatio,
        bool RotationAugmentation)? _savedSOPDetectionConfig;

    private static readonly object _logLock = new();
    private const string _debugLogPath = "sop_frame_debug.log";

    private void DebugLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}";

        Console.WriteLine(logLine);
        System.Diagnostics.Debug.WriteLine(logLine);

        lock (_logLock)
        {
            try
            {
                File.AppendAllText(_debugLogPath, logLine + Environment.NewLine);
            }
            catch { }
        }
    }

    public bool IsCameraConnected => _cameraManager.IsConnected;

    /// <summary>
    /// SOP模块实例（供视图订阅事件）
    /// </summary>
    public SOPModule? SOPModuleInstance => _sopModule;

    [ObservableProperty]
    private ROIEditorViewModel _roiEditorViewModel = null!;

    [ObservableProperty]
    private SKBitmap? _currentImage;

    [ObservableProperty]
    private SKBitmap? _detectionResultImage;

    [ObservableProperty]
    private string _status = "就绪";

    /// <summary>底部状态栏显示的当前连接相机型号（未连接时为空）</summary>
    [ObservableProperty]
    private string _cameraModelText = "";

    [ObservableProperty]
    private string _sopStatus = "SOP模块未初始化";

    [ObservableProperty]
    private int _currentStep = 0;

    [ObservableProperty]
    private int _totalSteps = 0;

    [ObservableProperty]
    private string _loadedModelName = "未加载模型";

    [ObservableProperty]
    private bool _isModelLoaded = false;

    [ObservableProperty]
    private List<DetectedObject> _detectionResults = new();

    [ObservableProperty]
    private DetectedObject? _selectedDetectionResult;

    [ObservableProperty]
    private int _highConfidenceCount = 0;

    [ObservableProperty]
    private string _videoPath = "";

    [ObservableProperty]
    private bool _isVideoPlaying = false;

    [ObservableProperty]
    private long _currentFrameIndex = 0;

    [ObservableProperty]
    private long _totalFrames = 0;

    [ObservableProperty]
    private double _fps = 0;

    [ObservableProperty]
    private bool _isCameraGrabbing = false;

    [ObservableProperty]
    private bool _isRealTimeDetecting = false;

    [ObservableProperty]
    private double _inferenceFps = 0;

    // ⭐ SOP实时检测字段
    [ObservableProperty]
    private bool _isSOPDetecting = false;

    [ObservableProperty]
    private string _sopWorkflowPath = "";

    private readonly SemaphoreSlim _sopInferenceLock = new(1, 1);

    private string _lastDetectionError = "";
    private DateTime _lastFrameTime = DateTime.Now;
    private int _frameCount = 0;
    private DateTime _lastInferenceTime = DateTime.Now;
    private int _inferenceFrameCount = 0;
    private bool _isProcessingFrame = false;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    // 缓存最后一次手部骨架结果，在原始帧上绘制，消除"原始帧→骨架帧"交替闪烁
    private HandPoseEstimationResult? _lastHandPoseResult;
    // 连续无手部检测帧计数器，用于清除残留骨架
    private int _noHandsFrameCount = 0;
    private const int MaxNoHandsFrames = 8;

    // 缓存最后一次 SOP 检测结果，让最新相机帧到达时再叠加渲染。
    // 关键：避免"推理完成 → 把旧帧+检测框覆盖到 CurrentImage → 画面退回上一帧"的卡顿感。
    private SOPModuleResult? _lastSopResult;
    private int _noSopResultFrameCount = 0;

    // ---- 区域监控（在标定区域内检测"未戴安全帽的人"）状态 ----
    private DateTime _regionAlertLastTime = DateTime.MinValue;
    private bool _regionAlertActive = false;
    private const int MaxNoSopResultFrames = 15;  // ~0.5s @ 30fps，超过后清除残留检测框

    // 检测框时序平滑器：IOU 跟踪 + EMA 位置平滑 + 确认/保持机制，消除检测框闪烁与跳动。
    // 仅在推理产生新结果时 Update，渲染时读取 GetActiveBoxes。
    private readonly DetectionTrackSmoother _detectionSmoother = new();

    // 实时检测专用平滑器（与 SOP 的 _detectionSmoother 隔离，避免状态互相污染）
    // 参数从当前加载的 ModelInfo 读取，模型管理对话框可调。
    private DetectionTrackSmoother _realtimeSmoother = new(ema: 0.2f, maxMissed: 5);

    // 实时检测结果缓存：最近一次平滑后的检测对象，由 OnCameraImageGrabbed 在最新相机帧上叠加渲染。
    // 与 SOP 的 _lastSopResult 同理，避免「无框原始帧」与「有框帧」交替造成的闪烁。
    private List<DetectedObject>? _lastRealtimeObjects;
    private int _noRealtimeResultFrameCount;
    private const int MaxNoRealtimeResultFrames = 15; // 连续多少帧无新实时结果则清空缓存
    
    // 修复：添加异步推理队列 - 改为可重新创建
    private Channel<SKBitmap> _inferenceQueue;
    private CancellationTokenSource? _inferenceCts;
    private Task? _inferenceWorkerTask;
    
    /// <summary>
    /// 创建新的推理队列
    /// </summary>
    private void CreateInferenceQueue()
    {
        _inferenceQueue = Channel.CreateBounded<SKBitmap>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public MainViewModel()
    {
        _roiManager = new ROIManager();
        RoiEditorViewModel = new ROIEditorViewModel(_roiManager);
        _cameraManager = CameraManager.Instance;
        _detectionService = new YoloDetectionService();
        _modelManager = new ModelManager();

        // 订阅检测错误事件
        _detectionService.DetectionError += OnDetectionError;
        
        // 订阅相机图像事件
        _cameraManager.ImageGrabbed += OnCameraImageGrabbed;
        _cameraManager.ConnectionStatusChanged += OnCameraConnectionStatusChanged;
    }
    
    /// <summary>
    /// 相机图像采集回调 - 修复：使用异步队列避免阻塞UI线程
    /// 关键修复（卡顿/显示上一帧）：
    ///   1. 用 BeginInvoke 异步投递到 UI 线程，不再阻塞相机采集线程
    ///   2. 检测结果统一在「最新相机帧」上叠加，不再用"推理旧帧+检测框"覆盖画面
    /// </summary>
    private void OnCameraImageGrabbed(object? sender, CameraImageData e)
    {
        // 检查应用程序是否仍在运行
        if (System.Windows.Application.Current == null || _isDisposed)
            return;

        // 将相机图像转换为 SKBitmap 并显示
        var skBitmap = ConvertCameraImageToSKBitmap(e);
        if (skBitmap != null)
        {
            // 先克隆用于推理（在绘制任何 overlay 前复制，确保推理用原始图像）
            SKBitmap? inferenceBitmap = null;
            if (IsSOPDetecting && _sopModule != null)
            {
                inferenceBitmap = skBitmap.Copy();
            }

            // 在最新相机帧上叠加最后一次 SOP 检测结果（检测框 + 手部骨架 + 违规）
            // 这是消除"画面退回上一帧"卡顿感的关键：
            //   - 之前是"推理完成后用旧帧+检测框覆盖 CurrentImage"，造成画面回退
            //   - 现在是"每收到一帧新画面，用最新的检测结果叠加渲染"，画面永远是最新帧
            var cachedSopResult = _lastSopResult;
            if (IsSOPDetecting && cachedSopResult != null)
            {
                try
                {
                    using var canvas = new SKCanvas(skBitmap);
                    DrawSOPDetectionOverlay(canvas, skBitmap.Info, cachedSopResult);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OnCameraImageGrabbed] 叠加 SOP 结果异常: {ex.Message}");
                }

                // 老化计数：如果连续 N 帧没新检测结果，清空缓存避免残留
                _noSopResultFrameCount++;
                if (_noSopResultFrameCount >= MaxNoSopResultFrames)
                {
                    _lastSopResult = null;
                    _noSopResultFrameCount = 0;
                }
            }
            else if (IsSOPDetecting)
            {
                // 还没收到第一次检测结果时，至少画上缓存的手部骨架（保留原行为）
                var cachedHand = _lastHandPoseResult;
                if (cachedHand != null && cachedHand.Hands.Count > 0)
                {
                    using var canvas = new SKCanvas(skBitmap);
                    DrawHandPoses(canvas, cachedHand);
                }
            }

            // 实时检测：在最新相机帧上叠加缓存的实时检测结果（与 SOP 同一架构，保证每帧都有框、丝滑不闪）
            if (IsRealTimeDetecting && _lastRealtimeObjects != null)
            {
                try
                {
                    using var canvas = new SKCanvas(skBitmap);
                    DrawDetectedObjects(canvas, _lastRealtimeObjects, 32f);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OnCameraImageGrabbed] 叠加实时结果异常: {ex.Message}");
                }

                // 老化计数：连续 N 帧没新检测结果则清空缓存，避免残留旧框
                _noRealtimeResultFrameCount++;
                if (_noRealtimeResultFrameCount >= MaxNoRealtimeResultFrames)
                {
                    _lastRealtimeObjects = null;
                    _noRealtimeResultFrameCount = 0;
                }
            }

            // 在UI线程更新图像（用 BeginInvoke 避免阻塞相机采集线程）
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isDisposed && RoiEditorViewModel != null)
                {
                    RoiEditorViewModel.CurrentImage = skBitmap;
                }
            }));

            // 将推理图像放入队列
            if (inferenceBitmap != null)
            {
                if (!_inferenceQueue.Writer.TryWrite(inferenceBitmap))
                {
                    inferenceBitmap.Dispose(); // 队列满了，丢弃旧帧
                }
            }
            else if (IsRealTimeDetecting && _detectionService.IsInitialized && !_isProcessingFrame)
            {
                _ = PerformRealTimeDetectionAsync(skBitmap);
            }
        }
    }
    
    /// <summary>
    /// 启动推理工作线程
    /// </summary>
    private void StartInferenceWorker()
    {
        // 修复：创建新的队列
        CreateInferenceQueue();
        
        _inferenceCts = new CancellationTokenSource();
        _inferenceWorkerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var bitmap in _inferenceQueue.Reader.ReadAllAsync(_inferenceCts.Token))
                {
                    try
                    {
                        await PerformSOPDetectionAsync(bitmap);
                    }
                    finally
                    {
                        bitmap.Dispose(); // 确保释放克隆的图像
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消（包括TaskCanceledException），不输出异常
                System.Diagnostics.Debug.WriteLine("[InferenceWorker] 工作线程正常取消");
            }
            catch (ChannelClosedException)
            {
                // Channel关闭，不输出异常
                System.Diagnostics.Debug.WriteLine("[InferenceWorker] Channel已关闭");
            }
            catch (Exception ex)
            {
                // 其他异常记录
                System.Diagnostics.Debug.WriteLine($"[InferenceWorker] 异常: {ex.GetType().Name}: {ex.Message}");
            }
        }, _inferenceCts.Token);
    }
    
    /// <summary>
    /// 停止推理工作线程
    /// </summary>
    private async Task StopInferenceWorkerAsync()
    {
        try
        {
            _inferenceCts?.Cancel();
            _inferenceQueue?.Writer.TryComplete();
        }
        catch (Exception)
        {
            // 忽略关闭时的异常
        }
        
        if (_inferenceWorkerTask != null)
        {
            try
            {
                await _inferenceWorkerTask;
            }
            catch (OperationCanceledException)
            {
                // 正常取消（包括TaskCanceledException），不输出异常
            }
            catch (ChannelClosedException)
            {
                // Channel已关闭，忽略
            }
            catch (Exception ex)
            {
                // 其他异常记录但不抛出
                System.Diagnostics.Debug.WriteLine($"[StopInference] 异常: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _inferenceWorkerTask = null;
            }
        }
    }
    
    /// <summary>
    /// 执行实时检测
    /// </summary>
    private async Task PerformRealTimeDetectionAsync(SKBitmap bitmap)
    {
        // 使用信号量防止并发处理（支持异步）
        if (!await _inferenceLock.WaitAsync(0))
            return;

        try
        {
            _isProcessingFrame = true;

            // 首帧诊断：输出当前推理使用的模型信息
            if (_inferenceFrameCount == 0)
            {
                var classes = _detectionService.GetClasses();
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] ====== 首帧推理诊断 ======");
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 已加载模型: {LoadedModelName}");
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 检测服务已初始化: {_detectionService.IsInitialized}");
                System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 实际类别数: {classes.Count}");
                if (classes.Count > 0)
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 前5个类别: {string.Join(", ", classes.Take(5))}");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 执行检测：有ROI则检测ROI区域，无ROI则检测全图
            DetectionResult result;
            if (RoiEditorViewModel.ROIs.Count > 0)
            {
                // 有ROI，检测ROI区域
                var rois = RoiEditorViewModel.ROIs.Select(r => new ROIInfo
                {
                    Name = r.ROIName,
                    X = r.GetBoundingBox().Left,
                    Y = r.GetBoundingBox().Top,
                    Width = r.GetBoundingBox().Width,
                    Height = r.GetBoundingBox().Height,
                    ShapeType = (Core.Services.ShapeType)(int)r.ShapeType
                }).ToList();
                
                // 调试输出ROI信息
                foreach (var roi in rois)
                {
                    System.Diagnostics.Debug.WriteLine($"ROI: {roi.Name}, X={roi.X}, Y={roi.Y}, W={roi.Width}, H={roi.Height}");
                }
                
                result = await _detectionService.DetectAsync(bitmap, rois);
            }
            else
            {
                // 无ROI，检测全图
                result = await _detectionService.DetectAsync(bitmap);
            }

            // 首帧诊断：输出关键点是否被提取（确认 pose 绘制有数据可画）
            if (_inferenceFrameCount == 0)
            {
                var kpObj = result.Objects.FirstOrDefault(o => o.KeyPoints != null && o.KeyPoints.Count > 0);
                if (kpObj != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 带关键点的对象: {kpObj.ClassName}, 关键点数: {kpObj.KeyPoints!.Count}");
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 首个关键点: X={kpObj.KeyPoints[0].X:F1}, Y={kpObj.KeyPoints[0].Y:F1}, Conf={kpObj.KeyPoints[0].Confidence:F2}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[RealTimeDetection] 注意: 本帧未检测到带关键点的对象（pose 骨骼不会显示）。对象总数={result.Objects.Count}");
                }
            }

            stopwatch.Stop();

            // 更新UI
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (!_isDisposed)
                {
                    // 时序平滑：消除逐帧 bbox 闪烁/抖动（pose 关键点与 seg 掩膜一并保留，关键点也做 EMA 平滑）
                    _realtimeSmoother.Update(result.Objects);
                    var smoothed = _realtimeSmoother.GetActiveObjects();

                    DetectionResults = smoothed;
                    HighConfidenceCount = smoothed.Count(o => o.Confidence >= 0.5);

                    // 缓存最近一次平滑结果，交由 OnCameraImageGrabbed 在最新相机帧上叠加渲染
                    // （与 SOP 保持同一架构，避免「无框原始帧」与「有框帧」交替造成的闪烁）
                    _lastRealtimeObjects = smoothed;
                    _noRealtimeResultFrameCount = 0;

                    // 计算推理FPS
                    _inferenceFrameCount++;
                    var elapsed = DateTime.Now - _lastInferenceTime;
                    if (elapsed.TotalSeconds >= 1)
                    {
                        InferenceFps = _inferenceFrameCount / elapsed.TotalSeconds;
                        _inferenceFrameCount = 0;
                        _lastInferenceTime = DateTime.Now;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"实时检测异常: {ex.Message}");
        }
        finally
        {
            _isProcessingFrame = false;
            _inferenceLock.Release();
        }
    }
    
    /// <summary>
    /// 绘制检测结果到图像
    /// </summary>
    private SKBitmap? DrawDetectionResults(SKBitmap sourceBitmap, DetectionResult result)
    {
        try
        {
            // 创建可变的bitmap副本
            var resultBitmap = sourceBitmap.Copy();
            
            using (var canvas = new SKCanvas(resultBitmap))
            {
                var paint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = SKColors.Red,
                    StrokeWidth = 4,
                    IsAntialias = true
                };
                
                var textPaint = new SKPaint
                {
                    Color = SKColors.Yellow,
                    TextSize = 64,
                    IsAntialias = true,
                    FakeBoldText = true
                };

                var bgPaint = new SKPaint
                {
                    Color = new SKColor(0, 0, 0, 180),
                    Style = SKPaintStyle.Fill
                };
                
                foreach (var obj in result.Objects)
                {
                    // 绘制边界框
                    var rect = new SKRect(
                        obj.BoundingBox[0],
                        obj.BoundingBox[1],
                        obj.BoundingBox[0] + obj.BoundingBox[2],
                        obj.BoundingBox[1] + obj.BoundingBox[3]);
                    canvas.DrawRect(rect, paint);
                    
                    // 绘制标签（带背景）
                    var label = $"{obj.ClassName} {(obj.Confidence * 100):F1}%";
                    
                    // 计算文字尺寸
                    var textBounds = new SKRect();
                    textPaint.MeasureText(label, ref textBounds);
                    
                    // 标签位置（在边界框上方，如果空间不足则在框内）
                    float labelX = obj.BoundingBox[0];
                    float labelY = obj.BoundingBox[1] - 5;
                    
                    // 如果标签会超出图像顶部，则放在框内
                    if (labelY - textBounds.Height < 0)
                    {
                        labelY = obj.BoundingBox[1] + textBounds.Height + 5;
                    }
                    
                    // 绘制标签背景
                    var bgRect = new SKRect(
                        labelX - 2,
                        labelY - textBounds.Height - 2,
                        labelX + textBounds.Width + 4,
                        labelY + 2);
                    canvas.DrawRect(bgRect, bgPaint);
                    
                    // 绘制标签文字
                    canvas.DrawText(label, labelX, labelY, textPaint);

                    // 绘制人体姿态骨骼（仅姿态估计模型会带 KeyPoints）
                    if (obj.KeyPoints != null && obj.KeyPoints.Count > 0)
                    {
                        DrawPoseSkeleton(canvas, obj.KeyPoints);
                    }

                    // 绘制实例分割掩膜（仅分割模型会带 Mask）
                    if (obj.Mask != null)
                    {
                        var color = SEG_MASK_COLORS[obj.ClassId % SEG_MASK_COLORS.Length];
                        // 掩膜按原始检测框尺寸位打包，绘制必须用 MaskBox（此处检测结果未经平滑，与 PixelBoundingBox 一致）
                        var maskBox = obj.MaskBox ?? new SKRectI(
                            (int)obj.BoundingBox[0], (int)obj.BoundingBox[1],
                            (int)(obj.BoundingBox[0] + obj.BoundingBox[2]), (int)(obj.BoundingBox[1] + obj.BoundingBox[3]));
                        DrawSegmentationMask(canvas, obj.Mask,
                            maskBox.Left, maskBox.Top,
                            maskBox.Width, maskBox.Height, color);
                    }
                }
            }
            
            return resultBitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"绘制检测结果异常: {ex.Message}");
            return null;
        }
    }
    
    #region SOP实时检测

    /// <summary>
    /// 执行 SOP 实时检测（每帧调用）
    /// </summary>
    private async Task PerformSOPDetectionAsync(SKBitmap bitmap)
    {
        // 使用信号量防止并发
        if (!await _sopInferenceLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_sopModule == null)
            {
                return;
            }

            // 构建帧数据
            var frames = new Dictionary<string, CaptureFrame>
            {
                ["main_camera"] = new CaptureFrame
                {
                    CameraId = "main_camera",
                    Image = bitmap,
                    Timestamp = DateTime.Now,
                    FrameNumber = _frameCount++
                }
            };

            // 执行 SOP 检测
            var result = await _sopModule.ProcessAsync(frames);

            if (result is SOPModuleResult sopResult)
            {
                // 在 UI 线程更新（只更新数据字段，不再覆盖画面）
                // 关键修复：之前这里会用"推理旧帧 + 检测框"覆盖 CurrentImage，
                // 导致画面退回上一帧、看起来一卡一卡。
                // 现在改为：缓存 sopResult，让 OnCameraImageGrabbed 收到下一帧新画面时再叠加渲染。
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isDisposed) return;

                    // 缓存 SOP 检测结果（供 OnCameraImageGrabbed 在最新帧上叠加）
                    _lastSopResult = sopResult;
                    _noSopResultFrameCount = 0;

                    // 更新检测框时序平滑器（仅在推理出新结果时更新，渲染时读取）
                    _detectionSmoother.Update(sopResult.Detections);

                    // 缓存手部骨架结果（保留原行为，作为 fallback）
                    if (sopResult.HandPoseResult != null && sopResult.HandPoseResult.Hands.Count > 0)
                    {
                        _lastHandPoseResult = sopResult.HandPoseResult;
                        _noHandsFrameCount = 0;
                    }
                    else
                    {
                        _noHandsFrameCount++;
                        if (_noHandsFrameCount >= MaxNoHandsFrames)
                        {
                            _lastHandPoseResult = null;
                        }
                    }

                    // 更新状态栏
                    SopStatus = sopResult.StepResults.Message;
                    CurrentStep = sopResult.StepResults.CurrentStep;
                    TotalSteps = sopResult.StepResults.TotalSteps;

                    // 更新检测结果列表
                    DetectionResults = sopResult.Detections
                        .Select(d => new DetectedObject
                        {
                            ClassName = d.Label?.Name ?? "unknown",
                            Confidence = (float)d.Confidence,
                            BoundingBox = new float[]
                            {
                                d.BoundingBox.Left,
                                d.BoundingBox.Top,
                                d.BoundingBox.Width,
                                d.BoundingBox.Height
                            },
                            PixelBoundingBox = d.BoundingBox
                        })
                        .ToList();

                    HighConfidenceCount = DetectionResults.Count(o => o.Confidence >= 0.5);

                    // 区域监控：在标定区域内检测"未戴安全帽的人"，命中则播放 ng.wav
                    EvaluateRegionMonitoring(sopResult);

                    // 不再在此处覆盖 RoiEditorViewModel.CurrentImage：
                    // 让 OnCameraImageGrabbed 在收到下一帧新画面时统一叠加渲染，
                    // 画面永远是"最新相机帧 + 最新检测结果"，消除卡顿和上一帧残留。

                    // 释放原始bitmap（推理队列里克隆的）
                    bitmap.Dispose();

                    // 计算推理 FPS
                    _inferenceFrameCount++;
                    var elapsed = DateTime.Now - _lastInferenceTime;
                    if (elapsed.TotalSeconds >= 1)
                    {
                        InferenceFps = _inferenceFrameCount / elapsed.TotalSeconds;
                        _inferenceFrameCount = 0;
                        _lastInferenceTime = DateTime.Now;
                    }

                    Status = $"SOP检测中 | 步骤 {CurrentStep}/{TotalSteps} | " +
                             $"检测到 {DetectionResults.Count} 个对象 | " +
                             $"FPS: {InferenceFps:F1} | " +
                             $"耗时: {sopResult.ElapsedMs}ms";
                }));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消（包括TaskCanceledException），不输出错误
            DebugLog($"[SOPDetection] 检测被取消");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOP实时检测异常: {ex.Message}");
        }
        finally
        {
            _sopInferenceLock.Release();
        }
    }

    /// <summary>
    /// 区域监控：在「🎯 标定区域」标定的 ROI 区域内，若出现"未戴安全帽的人"，播放 ng.wav。
    /// 判定标准：
    ///   1. 人体框中心点落入任一监控区域 → 认为该区域出现人；
    ///   2. 该人的头部区域（人体框上部 35%）没有与任何安全帽框相交 → 判为"未戴安全帽"。
    /// 报警带冷却时间（SOPMonitoringConfig.CooldownSeconds），防止每帧连续播放造成噪音。
    /// 人体/安全帽类别名由 YAML 的 sop.monitoring 配置（与模型 classes 一致）。
    /// </summary>
    private void EvaluateRegionMonitoring(SOPModuleResult sopResult)
    {
        try
        {
            var workflow = _sopModule?.CurrentWorkflow;
            var cfg = workflow?.Monitoring;
            if (workflow == null || cfg == null || !cfg.Enabled) return;
            if (workflow.Regions == null || workflow.Regions.Count == 0) return;

            // 过滤需要监控的区域（cfg.Regions 为空 = 监控全部标定区域）
            var zones = workflow.Regions;
            if (cfg.Regions is { Count: > 0 })
            {
                zones = zones
                    .Where(z => cfg.Regions.Contains(z.ZoneId, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            if (zones.Count == 0) return;

            var personNames = cfg.PersonClasses ?? new List<string>();
            var helmetNames = cfg.HelmetClasses ?? new List<string>();
            if (personNames.Count == 0) return;

            // 类别名匹配（不区分大小写）
            static bool IsClass(string? name, List<string> names) =>
                !string.IsNullOrEmpty(name) && names.Contains(name, StringComparer.OrdinalIgnoreCase);

            var persons = sopResult.Detections
                .Where(d => d.Confidence >= cfg.MinConfidence && IsClass(d.Label?.Name, personNames))
                .ToList();
            var helmets = sopResult.Detections
                .Where(d => d.Confidence >= cfg.MinConfidence && IsClass(d.Label?.Name, helmetNames))
                .ToList();

            bool violation = false;
            string detail = "";
            foreach (var person in persons)
            {
                var pBox = person.BoundingBox;
                var centerX = pBox.MidX;
                var centerY = pBox.MidY;

                foreach (var zone in zones)
                {
                    var zoneRect = new SKRect(zone.X, zone.Y, zone.X + zone.Width, zone.Y + zone.Height);
                    if (!zoneRect.Contains(centerX, centerY)) continue;

                    // 头部区域 = 人体框上部 35%
                    var headRect = new SKRect(
                        pBox.Left,
                        pBox.Top,
                        pBox.Right,
                        pBox.Top + pBox.Height * 0.35f);

                    var hasHelmet = helmets.Any(h =>
                    {
                        var hBox = h.BoundingBox;
                        return new SKRect(hBox.Left, hBox.Top, hBox.Right, hBox.Bottom)
                            .IntersectsWith(headRect);
                    });

                    if (!hasHelmet)
                    {
                        violation = true;
                        detail = $"区域[{zone.Name}] 出现未戴安全帽的人(person={person.Label?.Name}, conf={person.Confidence:F2})";
                        break;
                    }
                }
                if (violation) break;
            }

            if (violation)
            {
                // 冷却时间内不重复报警，避免每帧连续播放 ng.wav
                if (!_regionAlertActive ||
                    (DateTime.Now - _regionAlertLastTime).TotalSeconds >= cfg.CooldownSeconds)
                {
                    _regionAlertLastTime = DateTime.Now;
                    _regionAlertActive = true;
                    SopStatus = "⚠ 区域监控: 未戴安全帽！";
                    Status = $"⚠ {detail}";
                    DebugLog($"[RegionMonitor] {detail} -> 播放 ng.wav");
                    PlayNgSound();
                }
            }
            else
            {
                _regionAlertActive = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"区域监控异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 绘制 SOP 检测结果（检测框 + 步骤状态 + 违规警告）
    /// </summary>
    private SKBitmap DrawSOPDetectionResults(SKBitmap sourceBitmap, SOPModuleResult sopResult)
    {
        try
        {
            // 修复：创建新的bitmap并绘制，避免canvas释放问题
            var resultBitmap = sourceBitmap.Copy();
            using var canvas = new SKCanvas(resultBitmap);
            DrawSOPDetectionOverlay(canvas, resultBitmap.Info, sopResult);
            return resultBitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"绘制SOP结果异常: {ex.Message}");
            return sourceBitmap;
        }
    }

    /// <summary>
    /// 在已存在的 canvas 上叠加 SOP 检测结果（检测框 + 手部骨架 + 违规警告）。
    /// 抽出此方法是为了让"最新相机帧到达时"也能复用同一份渲染逻辑，
    /// 避免"推理旧帧 + 检测框覆盖 CurrentImage"导致的画面退回上一帧。
    /// </summary>
    private void DrawSOPDetectionOverlay(SKCanvas canvas, SKImageInfo info, SOPModuleResult sopResult)
    {
        try
        {
            // 1. 绘制检测框（使用时序平滑后的轨迹，消除闪烁/跳动）
            foreach (var track in _detectionSmoother.GetActiveBoxes())
            {
                var label = track.Label;
                var conf = track.Confidence;
                var box = track.Box;

                // 框颜色：高置信度绿色，低置信度黄色
                var color = conf > 0.7 ? SKColors.LimeGreen :
                           conf > 0.5 ? SKColors.Yellow : SKColors.Orange;

                using var boxPaint = new SKPaint
                {
                    Color = color,
                    StrokeWidth = 3,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke
                };
                canvas.DrawRect(box, boxPaint);

                // 标签
                var labelText = $"{label} {conf * 100:F0}%";
                using var textPaint = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 18,
                    IsAntialias = true,
                    FakeBoldText = true
                };
                using var bgPaint = new SKPaint
                {
                    Color = color.WithAlpha(200),
                    Style = SKPaintStyle.Fill
                };

                var textBounds = new SKRect();
                textPaint.MeasureText(labelText, ref textBounds);
                canvas.DrawRect(box.Left, box.Top - textBounds.Height - 6, textBounds.Width + 8, textBounds.Height + 6, bgPaint);
                canvas.DrawText(labelText, box.Left + 4, box.Top - 4, textPaint);
            }

            // 2. 绘制手部关键点（如果启用了手部检测）
            if (sopResult.HandPoseResult?.Hands.Count > 0)
            {
                try
                {
                    DrawHandPoses(canvas, sopResult.HandPoseResult);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DrawSOP] 手部绘制异常: {ex.Message}");
                }
            }

            // 3. 违规警告（如果有）
            if (sopResult.Violations.Count > 0)
            {
                var lastViolation = sopResult.Violations.Last();
                using var warnBg = new SKPaint { Color = new SKColor(255, 0, 0, 160), Style = SKPaintStyle.Fill };
                canvas.DrawRect(0, info.Height - 50, info.Width, 50, warnBg);

                using var warnText = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 20,
                    IsAntialias = true,
                    FakeBoldText = true,
                    Typeface = SKTypeface.FromFamilyName("Microsoft YaHei")
                };
                canvas.DrawText($"违规: {lastViolation.Description}", 15, info.Height - 18, warnText);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"叠加SOP结果异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 仅绘制的关键手部关键点（约 50% 降采样：手腕 + 5 个掌指关节 MCP + 5 个指尖 = 11 点）。
    /// 去掉 PIP/DIP 等中间关节，减少视觉杂乱，同时保留手势结构所需的全部关键位置。
    /// </summary>
    private static readonly HandKeypointType[] KeyHandLandmarks = new[]
    {
        HandKeypointType.Wrist,
        HandKeypointType.ThumbMCP, HandKeypointType.ThumbTip,
        HandKeypointType.IndexFingerMCP, HandKeypointType.IndexFingerTip,
        HandKeypointType.MiddleFingerMCP, HandKeypointType.MiddleFingerTip,
        HandKeypointType.RingFingerMCP, HandKeypointType.RingFingerTip,
        HandKeypointType.PinkyMCP, HandKeypointType.PinkyTip
    };

    /// <summary>
    /// 简化骨架连线（星形：手腕→各 MCP→各指尖），配合关键关键点绘制，减少凌乱感。
    /// </summary>
    private static readonly (HandKeypointType, HandKeypointType)[] ReducedHandSkeleton = new[]
    {
        (HandKeypointType.Wrist, HandKeypointType.ThumbMCP),
        (HandKeypointType.ThumbMCP, HandKeypointType.ThumbTip),
        (HandKeypointType.Wrist, HandKeypointType.IndexFingerMCP),
        (HandKeypointType.IndexFingerMCP, HandKeypointType.IndexFingerTip),
        (HandKeypointType.Wrist, HandKeypointType.MiddleFingerMCP),
        (HandKeypointType.MiddleFingerMCP, HandKeypointType.MiddleFingerTip),
        (HandKeypointType.Wrist, HandKeypointType.RingFingerMCP),
        (HandKeypointType.RingFingerMCP, HandKeypointType.RingFingerTip),
        (HandKeypointType.Wrist, HandKeypointType.PinkyMCP),
        (HandKeypointType.PinkyMCP, HandKeypointType.PinkyTip)
    };

    /// <summary>
    /// 绘制手部姿态（简化骨架线 + 关键关键点）
    /// </summary>
    private void DrawHandPoses(SKCanvas canvas, HandPoseEstimationResult handResult)
    {
        // 获取画布大小
        var canvasSize = canvas.LocalClipBounds;

        // 保存画布状态
        canvas.Save();

        // 注意：DWPose输出的坐标与SkiaSharp坐标系一致（原点在左上角）
        // 不需要翻转Y轴

        // 骨架线画笔
        using var skeletonPaint = new SKPaint
        {
            Color = SKColors.LimeGreen,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // 指尖关键点画笔（红色）
        using var tipPaint = new SKPaint
        {
            Color = SKColors.Red,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 关节关键点画笔（黄色）
        using var jointPaint = new SKPaint
        {
            Color = SKColors.Yellow,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 手腕关键点画笔（青色）
        using var wristPaint = new SKPaint
        {
            Color = SKColors.Cyan,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // 关键点外圈（白色描边，增加对比度）
        using var keypointOutlinePaint = new SKPaint
        {
            Color = SKColors.White,
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // 关键点标签画笔
        using var labelPaint = new SKPaint
        {
            Color = SKColors.Yellow,
            TextSize = 12,
            IsAntialias = true,
            FakeBoldText = true
        };

        // 手部边界框画笔（青色）
        using var handBoxPaint = new SKPaint
        {
            Color = SKColors.Cyan,
            StrokeWidth = 4,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        foreach (var hand in handResult.Hands)
        {
            int validCount = hand.Keypoints.Count(k => k.Confidence >= SkeletonConfidenceThreshold);
            float avgConf = hand.Keypoints.Count > 0 ? hand.Keypoints.Average(k => k.Confidence) : 0f;
            System.Diagnostics.Debug.WriteLine($"[DrawHandPoses] Hand {hand.TrackId}({hand.HandType}): Keypoints={hand.Keypoints.Count}, Valid(>={SkeletonConfidenceThreshold:F1})={validCount}, AvgConf={avgConf:F3}");

            // 绘制手部边界框
            var bbox = hand.BoundingBox;
            canvas.DrawRect(bbox, handBoxPaint);

            // 绘制简化骨架线（星形，仅关键连接）
            DrawHandSkeleton(canvas, hand, skeletonPaint);

            // 仅绘制 11 个关键关键点（手腕 + 5 MCP + 5 指尖），约减少 50% 绘制点
            foreach (var type in KeyHandLandmarks)
            {
                var kp = hand.GetKeypoint(type);
                if (kp == null || kp.Confidence < SkeletonConfidenceThreshold) continue;

                bool isTip = type == HandKeypointType.ThumbTip ||
                             type == HandKeypointType.IndexFingerTip ||
                             type == HandKeypointType.MiddleFingerTip ||
                             type == HandKeypointType.RingFingerTip ||
                             type == HandKeypointType.PinkyTip;

                bool isWrist = type == HandKeypointType.Wrist;
                // MCP 略小于指尖，便于区分层级
                // 关键点半径缩小约 50%（原 tip=7/wrist=8/mcp=6 → 现 tip=4/wrist=4/mcp=3）
                float radius = isTip ? 4 : (isWrist ? 4 : 3);
                var paint = isTip ? tipPaint : (isWrist ? wristPaint : jointPaint);

                // 绘制关键点外圈（白色描边，半径同步缩小）
                canvas.DrawCircle(kp.X, kp.Y, radius + 2, keypointOutlinePaint);
                // 绘制关键点
                canvas.DrawCircle(kp.X, kp.Y, radius, paint);

                // 只在指尖旁边绘制标签，避免杂乱
                if (isTip)
                {
                    canvas.DrawText(type.ToString(), kp.X + 10, kp.Y, labelPaint);
                }
            }
        }

        // 恢复画布状态
        canvas.Restore();
    }

    /// <summary>
    /// 绘制手部骨架线（使用 ReducedHandSkeleton 简化连接，仅关键关键点之间）
    /// </summary>
    // 骨架绘制置信度阈值（低于此值的关键点不绘制，减少闪烁）
    public float SkeletonConfidenceThreshold { get; set; } = 0.1f;

    private void DrawHandSkeleton(SKCanvas canvas, HandPose hand, SKPaint paint)
    {
        foreach (var (startType, endType) in ReducedHandSkeleton)
        {
            var start = hand.GetKeypoint(startType);
            var end = hand.GetKeypoint(endType);

            if (start == null || end == null) continue;
            if (start.Confidence < SkeletonConfidenceThreshold || end.Confidence < SkeletonConfidenceThreshold) continue;

            canvas.DrawLine(start.X, start.Y, end.X, end.Y, paint);
        }
    }

    #endregion

    /// <summary>
    /// 相机连接状态改变回调
    /// </summary>
    private void OnCameraConnectionStatusChanged(object? sender, bool isConnected)
    {
        // 检查应用程序是否仍在运行
        if (System.Windows.Application.Current == null || _isDisposed)
            return;
            
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_isDisposed)
            {
                if (isConnected)
                {
                    var cam = _cameraManager.CurrentCamera;
                    string model = !string.IsNullOrEmpty(cam?.Model) ? cam.Model
                                 : !string.IsNullOrEmpty(cam?.Name) ? cam.Name
                                 : "未知";
                    CameraModelText = $"📷 {model}";
                    Status = "相机已连接";
                }
                else
                {
                    CameraModelText = "";
                    Status = "相机已断开";
                }
            }
        });
    }
    
    /// <summary>
    /// 将相机图像数据转换为 SKBitmap
    /// </summary>
    private SKBitmap? ConvertCameraImageToSKBitmap(CameraImageData imageData)
    {
        try
        {
            if (imageData?.Data == null || imageData.Data.Length == 0)
                return null;

            var info = new SKImageInfo(
                imageData.Width, 
                imageData.Height, 
                SKColorType.Bgra8888);
            
            var bitmap = new SKBitmap(info);
            
            if (imageData.IsColor && imageData.Channels == 3)
            {
                // RGB24 -> BGRA32 转换
                ConvertRgb24ToBgra32(imageData.Data, bitmap, imageData.Width, imageData.Height);
            }
            else
            {
                // 灰度图像，需要转换为 BGRA
                ConvertGray8ToBgra32(imageData.Data, bitmap, imageData.Width, imageData.Height);
            }
            
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"转换图像到SKBitmap异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将 RGB24 数据转换为 BGRA32
    /// </summary>
    private unsafe void ConvertRgb24ToBgra32(byte[] rgbData, SKBitmap bitmap, int width, int height)
    {
        byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcIndex = (y * width + x) * 3;
                int dstIndex = (y * width + x) * 4;
                
                // RGB -> BGRA
                ptr[dstIndex] = rgbData[srcIndex + 2];     // B
                ptr[dstIndex + 1] = rgbData[srcIndex + 1]; // G
                ptr[dstIndex + 2] = rgbData[srcIndex];     // R
                ptr[dstIndex + 3] = 255;                   // A
            }
        }
    }

    /// <summary>
    /// 将 Gray8 数据转换为 BGRA32
    /// </summary>
    private unsafe void ConvertGray8ToBgra32(byte[] grayData, SKBitmap bitmap, int width, int height)
    {
        byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int srcIndex = y * width + x;
                int dstIndex = srcIndex * 4;
                byte gray = grayData[srcIndex];
                
                // Gray -> BGRA
                ptr[dstIndex] = gray;     // B
                ptr[dstIndex + 1] = gray; // G
                ptr[dstIndex + 2] = gray; // R
                ptr[dstIndex + 3] = 255;  // A
            }
        }
    }

    private void OnDetectionError(object? sender, string e)
    {
        _lastDetectionError = e;
    }

    /// <summary>
    /// 保存SOP检测模式配置（由SOP配置界面调用）
    /// </summary>
    public void SaveSOPDetectionConfig(string detectionMode, bool enableHandPose, int maxNumHands = 2,
        bool enableFaceFilter = true, float faceFilterUpperRatio = 0.38f,
        bool enableHandStructureCheck = true, float handStructureWristTipRatio = 0.18f,
        float detectionConfidenceThreshold = 0.08f, float minBoxAreaRatio = 0.0005f,
        bool rotationAugmentation = true)
    {
        _savedSOPDetectionConfig = (detectionMode, enableHandPose, maxNumHands,
            enableFaceFilter, faceFilterUpperRatio,
            enableHandStructureCheck, handStructureWristTipRatio,
            detectionConfidenceThreshold, minBoxAreaRatio,
            rotationAugmentation);
        Console.WriteLine($"[MainViewModel] SOP检测配置已保存: 模式={detectionMode}, 手部检测={enableHandPose}");
    }

    /// <summary>
    /// 切换主检测计算设备（GPU/CPU）。立即释放并重建主检测服务（实时相机检测的推理引擎），
    /// 同时同步 SOP 模块，并持久化到 configs/sop_config.json。
    /// </summary>
    [RelayCommand]
    private async Task ToggleGpuAsync()
    {
        bool next = !UseGpu;
        bool finalGpu = next;

        // 1) 切换主检测服务（实时相机检测走 _detectionService）
        if (_loadedModel != null && _detectionService.IsInitialized)
        {
            // 避免重建期间与正在处理的帧竞争
            if (IsRealTimeDetecting)
                StopRealTimeDetection();

            _loadedModel.UseGpu = next;
            if (await _detectionService.InitializeAsync(_loadedModel))
            {
                finalGpu = next;
            }
            else
            {
                // CUDA 初始化失败 → 回退 CPU
                finalGpu = false;
                _loadedModel.UseGpu = false;
                await _detectionService.InitializeAsync(_loadedModel);
            }
        }

        // 2) 同步 SOP 模块（若已初始化）
        if (_sopModule != null)
        {
            finalGpu = await _sopModule.SetUseGpuAsync(finalGpu);
        }

        UseGpu = finalGpu;
        SaveUseGpuToConfig(finalGpu);
        Status = finalGpu ? "主检测已切换到 GPU (CUDA)" : "主检测运行在 CPU";
    }

    /// <summary>把主检测的 UseGpu 选择写回运行时配置文件 configs/sop_config.json</summary>
    private void SaveUseGpuToConfig(bool useGpu)
    {
        try
        {
            const string path = "configs/sop_config.json";
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text);
            if (node?["SOPModule"] is JsonObject sop)
            {
                sop["UseGpu"] = useGpu;
                File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel] 保存 UseGpu 到配置失败: {ex.Message}");
        }
    }

    /// <summary>同步 SOP 模块的计算设备状态到界面，并订阅变化/失败事件</summary>
    private void AttachSopModuleEvents(SOPModule module)
    {
        UseGpu = module.IsUsingGpu;
        module.UseGpuChanged += (_, gpu) => UseGpu = gpu;
        module.GpuSwitchFailed += (_, _) => Status = "⚠️ 当前设备 CUDA 不可用，已回退到 CPU 运行";
    }

    [RelayCommand]
    public async Task InitializeSOPModuleAsync()
    {
        var logFile = "sop_init_debug.log";
        try
        {
            File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} [MainViewModel] 开始初始化SOP模块...\n");
            
            IsBusy = true;
            Status = "初始化SOP模块...";

            // 检查配置文件是否存在
            string configPath = "configs/sop_config.json";
            if (!File.Exists(configPath))
            {
                File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} [MainViewModel] 配置文件不存在，使用默认配置\n");
                
                // 尝试使用备选配置（内存配置）
                Status = "使用默认配置初始化SOP模块...";
                var defaultConfig = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["SOPModule:ModelPath"] = "yolo_models/yolov8s.onnx",
                        ["SOPModule:UseGpu"] = "false",
                        ["SOPModule:ConfidenceThreshold"] = "0.6",
                        ["SOPModule:IouThreshold"] = "0.45",
                        ["SOPModule:PoseEstimation:Enabled"] = "true",
                        ["SOPModule:PoseEstimation:ModelPath"] = "yolo_models/yolov8s-pose.onnx",
                        ["SOPModule:HandPoseEstimation:ModelPath"] = "models",
                        ["SOPModule:HandPoseEstimation:ConfidenceThreshold"] = "0.5",
                        ["SOPModule:HandPoseEstimation:MaxNumHands"] = "2",
                        ["SOPModule:HandPoseEstimation:UseGpu"] = "true"
                    })
                    .Build();

                File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} [MainViewModel] 创建SOPModule实例...\n");
                _sopModule = new SOPModule();
                
                File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} [MainViewModel] 调用InitializeAsync...\n");
                await _sopModule.InitializeAsync(defaultConfig, _cameraManager.CurrentCameraService!);
                File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} [MainViewModel] InitializeAsync完成，State={_sopModule.State}\n");
                AttachSopModuleEvents(_sopModule);
            }
            else
            {
                // 使用配置文件，但添加手部检测配置
                var config = new ConfigurationBuilder()
                    .AddJsonFile(configPath, optional: true)
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["SOPModule:HandPoseEstimation:ModelPath"] = "models",
                        ["SOPModule:HandPoseEstimation:ConfidenceThreshold"] = "0.5",
                        ["SOPModule:HandPoseEstimation:MaxNumHands"] = "2",
                        ["SOPModule:HandPoseEstimation:UseGpu"] = "true"
                    })
                    .Build();

                _sopModule = new SOPModule();
                await _sopModule.InitializeAsync(config, _cameraManager.CurrentCameraService!);
                AttachSopModuleEvents(_sopModule);
            }

            // 加载区域配置（如果存在）
            string regionConfigPath = "configs/sop/regions/phone_usage_regions.json";
            if (File.Exists(regionConfigPath))
            {
                Status = "加载区域配置...";
                // 区域配置会在SOPDetectionStarter中加载，这里仅检查存在性
            }

            // 加载SOP流程配置（如果存在）
            string workflowPath = "configs/sop/sop_phone_usage.yaml";
            if (File.Exists(workflowPath))
            {
                Status = "加载SOP流程配置...";
                _sopModule.StartWorkflowFromYaml(workflowPath);
            }

            SopStatus = "SOP模块初始化成功";
            Status = "SOP模块初始化完成";
        }
        catch (Exception ex)
        {
            var fullError = $"SOP模块初始化失败: {ex.Message}\n\n堆栈跟踪:\n{ex.StackTrace}";
            if (ex.InnerException != null)
            {
                fullError += $"\n\n内部异常: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
            }
            Console.WriteLine($"[ERROR] {fullError}");
            SopStatus = $"SOP模块初始化失败: {ex.Message}";
            Status = $"错误: {ex.Message}";
            MessageBox.Show(fullError, "SOP初始化错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RunSOPDetectionAsync()
    {
        try
        {
            IsBusy = true;
            Status = "运行SOP检测...";

            if (_sopModule == null)
            {
                Status = "SOP模块未初始化";
                return;
            }

            // 连接相机
            await _cameraManager.ConnectAsync(new CameraInfo { Id = "main_camera", Name = "主相机" });

            // 捕获图像
            var frames = new Dictionary<string, CaptureFrame>();
            // 使用模拟图像进行测试
            var testImage = CreateTestImage();
            frames["main_camera"] = new CaptureFrame
            {
                CameraId = "main_camera",
                Image = testImage,
                Timestamp = DateTime.Now,
                FrameNumber = 0
            };

            // 执行检测
            var result = await _sopModule.ProcessAsync(frames);
            if (result is SOPModuleResult sopResult)
            {
                SopStatus = sopResult.StepResults.Message;
                CurrentStep = sopResult.StepResults.CurrentStep;
                TotalSteps = sopResult.StepResults.TotalSteps;

                // 显示检测结果
                if (frames.TryGetValue("main_camera", out var mainFrame))
                {
                    CurrentImage = mainFrame.Image;
                    RoiEditorViewModel.CurrentImage = mainFrame.Image;
                }
            }

            Status = "SOP检测完成";
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    #region SOP实时检测命令

    /// <summary>
    /// 初始化并启动 SOP 实时检测
    /// </summary>
    [RelayCommand]
    public async Task StartSOPDetectionAsync()
    {
        try
        {
            IsBusy = true;

            // 1. 检查相机
            if (!_cameraManager.IsConnected)
            {
                MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_cameraManager.IsGrabbing)
            {
                MessageBox.Show("请先开始相机采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Status = "正在初始化 SOP 模块...";

            // 2. 初始化 SOP 模块（如果还没初始化）
            if (_sopModule == null)
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["SOPModule:ModelPath"] = "yolo_models/yolov8s.onnx",
                        ["SOPModule:UseGpu"] = "true",
                        ["SOPModule:ConfidenceThreshold"] = "0.6",
                        ["SOPModule:IouThreshold"] = "0.45",
                        ["SOPModule:PoseEstimation:Enabled"] = "true",
                        ["SOPModule:HandPoseEstimation:ModelPath"] = "models",
                        ["SOPModule:HandPoseEstimation:ConfidenceThreshold"] = "0.5",
                        ["SOPModule:HandPoseEstimation:MaxNumHands"] = "2",
                        ["SOPModule:HandPoseEstimation:UseGpu"] = "true"
                    })
                    .Build();

                _sopModule = new SOPModule();
                await _sopModule.InitializeAsync(config, _cameraManager.CurrentCameraService!);
                AttachSopModuleEvents(_sopModule);

                Console.WriteLine($"[MainViewModel] SOP模块创建完成，当前模式: {_sopModule.DetectionMode}");
                Console.WriteLine($"[MainViewModel] 保存的配置: {(_savedSOPDetectionConfig.HasValue ? "存在" : "不存在")}");

                // 应用保存的检测模式配置（如果存在）
                if (_savedSOPDetectionConfig.HasValue)
                {
                    var savedConfig = _savedSOPDetectionConfig.Value;
                    Console.WriteLine($"[MainViewModel] 应用保存配置: 模式={savedConfig.DetectionMode}, 手部检测={savedConfig.EnableHandPose}");

                    // 统一检测模式：所有配置都映射到 UnifiedDetection
                    var mode = SOPDetectionMode.UnifiedDetection;
                    _sopModule.UpdateDetectionMode(mode, savedConfig.EnableHandPose);
                    Console.WriteLine($"[MainViewModel] UpdateDetectionMode后，模式: {_sopModule.DetectionMode}");

                    if (savedConfig.EnableHandPose)
                    {
                        _sopModule.UpdateHandPoseConfig(new HandPoseEstimationConfig
                        {
                            MaxNumHands = savedConfig.MaxNumHands,
                            ConfidenceThreshold = 0.5f,
                            UseGpu = true,
                            EnableFaceFilter = savedConfig.EnableFaceFilter,
                            FaceFilterUpperRatio = savedConfig.FaceFilterUpperRatio,
                            EnableHandStructureCheck = savedConfig.EnableHandStructureCheck,
                            HandStructureWristTipRatio = savedConfig.HandStructureWristTipRatio,
                            DetectionConfidenceThreshold = savedConfig.DetectionConfidenceThreshold,
                            MinBoxAreaRatio = savedConfig.MinBoxAreaRatio,
                            RotationAugmentation = savedConfig.RotationAugmentation
                        });
                    }
                }
            }

            // 确保 MainViewModel 侧事件订阅始终挂上（无论 SOP 模块是新建还是复用）。
            // 之前这些订阅写在 if (_sopModule == null) 块内，模块已存在走复用分支时不执行，
            // 导致跳步违规事件无法到达 OnSOPViolationDetected -> 不播 ng.wav。
            // 先退订再订阅，避免多次启动检测造成重复订阅、重复播放音频。
            _sopModule.StepChanged -= OnSOPStepChanged;
            _sopModule.ViolationDetected -= OnSOPViolationDetected;
            _sopModule.WorkflowCompleted -= OnSOPWorkflowCompleted;
            _sopModule.ModelWarning -= OnSOPModelWarning;
            _sopModule.StepChanged += OnSOPStepChanged;
            _sopModule.ViolationDetected += OnSOPViolationDetected;
            _sopModule.WorkflowCompleted += OnSOPWorkflowCompleted;
            _sopModule.ModelWarning += OnSOPModelWarning;
            Console.WriteLine($"[SOP-Audio][DEBUG] MainViewModel 已(重新)订阅 SOP 事件(含 ViolationDetected)，实例={_sopModule?.GetHashCode()}");

            // 3. 确保手部姿态估计服务已准备好（等待异步初始化完成）
            Console.WriteLine($"[MainViewModel] 确保手部服务准备就绪...");
            await _sopModule.EnsureHandPoseServiceReadyAsync();
            Console.WriteLine($"[MainViewModel] 手部服务已就绪, 模式: {_sopModule.DetectionMode}");

            // 4. 加载工作流（优先从YAML，否则使用空工作流）
            Status = "正在启动检测...";
            DebugLog($"[StartSOP] 正在加载工作流...");
            if (!string.IsNullOrEmpty(SopWorkflowPath) && File.Exists(SopWorkflowPath))
            {
                DebugLog($"[StartSOP] 从YAML加载工作流: {SopWorkflowPath}");
                _sopModule.StartWorkflowFromYaml(SopWorkflowPath);
            }
            else
            {
                DebugLog($"[StartSOP] YAML路径为空或文件不存在，使用空工作流");
                var workflow = new SOPWorkflow
                {
                    Id = "default",
                    Name = "实时检测",
                    Description = "基于配置的实时检测工作流",
                    Steps = new List<SOPStep>(),
                    Regions = new List<ZoneDefinition>()
                };
                _sopModule.StartWorkflow(workflow);
            }
            DebugLog($"[StartSOP] StartWorkflow 完成");

            // 安全帽监控任务状态提示
            var monitorCfg = _sopModule?.CurrentWorkflow?.Monitoring;
            if (monitorCfg != null && monitorCfg.Enabled)
            {
                var zoneCount = _sopModule?.CurrentWorkflow?.Regions?.Count ?? 0;
                DebugLog($"[StartSOP] 安全帽监控任务已启用: person={string.Join(",", monitorCfg.PersonClasses)} | helmet={string.Join(",", monitorCfg.HelmetClasses)} | 标定区域数={zoneCount}");
                if (zoneCount == 0)
                    DebugLog("[StartSOP] 警告: 未标定任何区域，安全帽监控将不生效（请用「🎯 标定区域」定义）");
            }
            else
            {
                DebugLog("[StartSOP] 当前配方未启用安全帽监控（仅按 SOP 步骤任务运行）");
            }

            // 5. 启动实时检测
            IsSOPDetecting = true;
            DebugLog($"[StartSOP] IsSOPDetecting 设置为: {IsSOPDetecting}");
            IsRealTimeDetecting = false; // 关闭通用检测，避免冲突
            _inferenceFrameCount = 0;
            _lastInferenceTime = DateTime.Now;
            
            // 修复：启动推理工作线程
            StartInferenceWorker();
            DebugLog($"[StartSOP] 推理工作线程已启动");

            SopStatus = "SOP 实时检测运行中";
            Status = $"SOP 实时检测已启动 | 模式: {_sopModule.DetectionMode}";
            DebugLog($"[StartSOP] SOP启动完成，等待相机帧...");
        }
        catch (Exception ex)
        {
            SopStatus = $"启动失败: {ex.Message}";
            Status = $"SOP 启动错误: {ex.Message}";
            MessageBox.Show($"SOP 启动失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 停止 SOP 实时检测 - 修复：停止推理工作线程
    /// </summary>
    [RelayCommand]
    public async Task StopSOPDetectionAsync()
    {
        IsSOPDetecting = false;
        
        // 修复：停止推理工作线程
        await StopInferenceWorkerAsync();
        DebugLog($"[StopSOP] 推理工作线程已停止");
        
        _sopModule?.StopWorkflow();
        _detectionSmoother.Clear();
        _lastSopResult = null;
        _noSopResultFrameCount = 0;
        // 重置区域监控报警状态
        _regionAlertActive = false;
        _regionAlertLastTime = DateTime.MinValue;
        InferenceFps = 0;
        SopStatus = "SOP 检测已停止";
        Status = "SOP 检测已停止";
    }

    /// <summary>
    /// 重置当前 SOP 工作流（从第一步重新开始）
    /// </summary>
    [RelayCommand]
    public void ResetSOPWorkflow()
    {
        _sopModule?.ResetWorkflow();
        SopStatus = "SOP 工作流已重置";
        Status = "SOP 工作流已重置，从第一步重新开始";
    }

    /// <summary>
    /// 切换 SOP 工作流（选择另一个 YAML）
    /// </summary>
    [RelayCommand]
    public async Task SwitchSOPWorkflowAsync()
    {
        // 先停止当前检测
        var wasDetecting = IsSOPDetecting;
        if (wasDetecting) await StopSOPDetectionAsync();

        // 清空路径，让 StartSOPDetectionAsync 重新弹文件选择
        SopWorkflowPath = "";

        // 重新启动
        if (wasDetecting)
        {
            await StartSOPDetectionAsync();
        }
    }

    // --- SOP 事件处理 ---

    private void OnSOPStepChanged(object? sender, StepChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            CurrentStep = e.CurrentStepId;
            SopStatus = $"步骤 {e.CurrentStepId}: {e.StepName}";
            Status = $"SOP 步骤推进: {e.PreviousStepId} → {e.CurrentStepId} ({e.StepName})";
            // 每步完成后播放 ok.wav（初始 0→1 是"开始"，不算完成，不播）
            if (e.PreviousStepId != 0)
                PlayOkSound();
        });
    }

    private void OnSOPViolationDetected(object? sender, ViolationEventArgs e)
    {
        Console.WriteLine($"[SOP-Audio][DEBUG] OnSOPViolationDetected 触发: type={e.Violation.Type} desc={e.Violation.Description} | Application.Current={(System.Windows.Application.Current == null ? "NULL(不播)" : "OK")}");
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            SopStatus = $"⚠ 违规: {e.Violation.Description}";
            Status = $"⚠ SOP 违规: [{e.Violation.Type}] {e.Violation.Description}";
            // 跳步（中间跳了工序）→ 播放 ng.wav
            if (e.Violation.Type == ViolationType.SkipStep)
            {
                EnsureSopAudioPaths();
                Console.WriteLine($"[SOP-Audio][DEBUG] 跳步 -> 请求播放 ng.wav | _ngWavPath='{_ngWavPath}' exists={(_ngWavPath != null && File.Exists(_ngWavPath))}");
                PlayNgSound();
            }
        });
    }

    private void OnSOPWorkflowCompleted(object? sender, SOPCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            var isPass = !e.HasViolations;
            SopStatus = isPass ? "✅ SOP 全部通过" : "❌ SOP 检测不通过";
            Status = isPass
                ? $"SOP 完成: 全部 {TotalSteps} 步通过"
                : $"SOP 完成: 有 {e.Violations.Count} 个违规";
            // 最后一步完成也播放 ok.wav
            PlayOkSound();
        });
    }

    #region SOP 音频反馈（每步完成 ok.wav / 跳步 ng.wav）

    private string? _okWavPath;
    private string? _ngWavPath;

    /// <summary>
    /// 音频播放器缓存：基于 NAudio 的 WaveOutEvent + AudioFileReader，支持任意位深 PCM
    /// （8/16/24/32-bit）及 IEEE float，因此 24-bit/44100Hz/立体声的 WAV 也能原生播放，
    /// 不再需要事先转成 16-bit。播放器常驻缓存、永不释放，避免重复创建与截断。
    /// </summary>
    private readonly object _sopAudioLock = new();
    private readonly Dictionary<string, (WaveOutEvent output, AudioFileReader reader)> _sopAudioCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 定位 ok.wav / ng.wav：优先程序运行目录，其次从程序目录向上逐层查找
    /// （音频文件一般放在项目根目录，运行时工作目录可能是 bin/Debug/...）。
    /// </summary>
    private void EnsureSopAudioPaths()
    {
        if (_okWavPath != null && _ngWavPath != null) return;

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AppContext.BaseDirectory,
            AppDomain.CurrentDomain.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var root in roots)
        {
            var dir = root;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                TryResolveAudio(ref _okWavPath, Path.Combine(dir, "ok.wav"));
                TryResolveAudio(ref _ngWavPath, Path.Combine(dir, "ng.wav"));
                if (_okWavPath != null && _ngWavPath != null) return;
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static void TryResolveAudio(ref string? store, string path)
    {
        if (store == null && File.Exists(path)) store = path;
    }

    /// <summary>
    /// 播放音频（NAudio WaveOutEvent 异步播放，不阻塞 UI）。文件不存在时静默跳过。
    /// 设计要点：
    /// 1. 用 NAudio.WaveOutEvent + AudioFileReader 播放——底层走 WaveOut / Media Foundation，
    ///    原生支持 8/16/24/32-bit PCM 与 IEEE float，无需把 24-bit WAV 预先转成 16-bit。
    /// 2. 播放器（output+reader）常驻缓存 _sopAudioCache，重复播放只需 reader.Position=0 后
    ///    Play()，避免重复创建；若正在播放则先 Stop() 再重播，不会叠加成杂音。
    /// 3. [调试] 打印 WAV 格式信息（任意位深 PCM 均支持）。
    /// </summary>
    private void PlaySopSound(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("[SOP-Audio][DEBUG] 跳过播放：音频路径为空（EnsureSopAudioPaths 未解析到文件）");
            return;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine($"[SOP-Audio][DEBUG] 跳过播放：文件不存在 -> {path}");
            return;
        }
        try
        {
            // 调试：打印 WAV 格式与是否被播放器支持
            var info = GetWavFormatInfo(path);
            long size = new FileInfo(path).Length;
            Console.WriteLine($"[SOP-Audio][DEBUG] 准备播放: {path} | 大小={size}字节 | {info}");

            (WaveOutEvent output, AudioFileReader reader) entry;
            lock (_sopAudioLock)
            {
                if (!_sopAudioCache.TryGetValue(path, out entry))
                {
                    var afReader = new AudioFileReader(path);
                    var waveOut = new WaveOutEvent();
                    waveOut.Init(afReader);
                    entry = (waveOut, afReader);
                    _sopAudioCache[path] = entry;
                    Console.WriteLine($"[SOP-Audio][DEBUG] 创建播放器成功（支持任意位深 PCM）");
                }

                // 重置到开头，避免上次播放到结尾后无法再播；若正在播放则先停再重播
                if (entry.reader.Position != 0) entry.reader.Position = 0;
                if (entry.output.PlaybackState == PlaybackState.Playing)
                    entry.output.Stop();
                entry.output.Play();
            }
            Console.WriteLine($"[SOP-Audio][DEBUG] 已发起 Play() 调用（等待设备出声）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOP-Audio][ERROR] 播放失败 '{path}': {ex}");
        }
    }

    /// <summary>
    /// 解析 WAV 的 fmt 块，返回可读格式信息及播放器支持性提示（调试用）。
    /// </summary>
    private static string GetWavFormatInfo(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (br.ReadInt32() != 0x46464952) return "非RIFF/WAV头";
            br.ReadInt32(); // RIFF size
            if (br.ReadInt32() != 0x45564157) return "非WAVE";
            while (fs.Position < fs.Length - 8)
            {
                int chunkId = br.ReadInt32();
                int chunkSize = br.ReadInt32();
                if (chunkId == 0x20746D66) // 'fmt '
                {
                    short audioFormat = br.ReadInt16(); // 1=PCM
                    short channels = br.ReadInt16();
                    int sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byteRate
                    br.ReadInt16(); // blockAlign
                    short bits = br.ReadInt16();
                    string fmt = audioFormat == 1 ? "PCM" : (audioFormat == 3 ? "IEEE-float" : $"fmt={audioFormat}");
                    // NAudio（WaveOutEvent+AudioFileReader）原生支持 PCM 任意位深与 IEEE float；
                    // 仅非 PCM 的压缩格式（如 MP3/AAC，audioFormat 不为 1 也不为 3）才无法直接播放。
                    bool supported = audioFormat == 1 || audioFormat == 3;
                    return $"{fmt} {channels}ch {bits}bit {sampleRate}Hz {(supported ? "[播放器支持]" : "[播放器不支持:非PCM压缩格式]")}";
                }
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
            return "未找到fmt块";
        }
        catch (Exception ex)
        {
            return $"解析失败:{ex.Message}";
        }
    }

    private void PlayOkSound()
    {
        EnsureSopAudioPaths();
        PlaySopSound(_okWavPath);
    }

    private void PlayNgSound()
    {
        EnsureSopAudioPaths();
        PlaySopSound(_ngWavPath);
    }

    #endregion

    /// <summary>
    /// 模型加载告警：专用模型缺失/回退时弹窗提示，避免"什么都没识别到却无提示"
    /// </summary>
    private void OnSOPModelWarning(object? sender, string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            SopStatus = "⚠ 模型告警: " + message;
            Status = "⚠ SOP 模型告警（见弹窗）";
            MessageBox.Show(message, "SOP 模型告警", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    #endregion

    [RelayCommand]
    public async Task LoadImageAsync()
    {
        try
        {
            IsBusy = true;
            Status = "加载图像...";

            // 打开文件选择对话框
            var openFileDialog = new OpenFileDialog
            {
                Title = "选择图像文件",
                Filter = "图像文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                await Task.Run(() =>
                {
                    using var stream = new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read);
                    CurrentImage = SKBitmap.Decode(stream);
                });
                
                RoiEditorViewModel.CurrentImage = CurrentImage;
                Status = $"图像加载成功: {Path.GetFileName(openFileDialog.FileName)}";
            }
            else
            {
                Status = "取消加载图像";
            }
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
            MessageBox.Show($"加载图像失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void SaveROIs()
    {
        try
        {
            IsBusy = true;
            Status = "保存ROI...";

            // 这里应该将ROI保存到配置文件
            var rois = _roiManager.ROIs;
            // 序列化ROIs到JSON

            Status = $"ROI保存成功，共 {rois.Count} 个";
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ClearROIs()
    {
        RoiEditorViewModel.ClearAllROIsCommand.Execute(null);
        Status = "ROI已清空";
    }

    /// <summary>
    /// 创建测试图像
    /// </summary>
    private SKBitmap CreateTestImage()
    {
        var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        // 绘制一些测试内容
        using var paint = new SKPaint
        {
            Color = SKColors.Blue,
            StrokeWidth = 2,
            IsAntialias = true
        };

        // 绘制矩形
        canvas.DrawRect(100, 100, 200, 150, paint);

        // 绘制圆形
        paint.Color = SKColors.Red;
        canvas.DrawCircle(400, 300, 80, paint);

        // 绘制文本
        paint.Color = SKColors.Black;
        paint.TextSize = 24;
        canvas.DrawText("SOP Test Image", 50, 50, paint);

        return bitmap;
    }

    #region YOLO检测功能

    [RelayCommand]
    public async Task LoadModelAsync()
    {
        try
        {
            // 如果正在视频推理，先停止
            if (IsVideoPlaying)
            {
                Status = "正在停止视频推理...";
                StopVideoInference();
                // 等待一小段时间确保资源释放
                await Task.Delay(500);
            }

            IsBusy = true;
            Status = "加载模型...";

            // 打开模型选择对话框
            var dialog = new Views.ModelSelectionDialog();
            if (Application.Current.MainWindow != null)
            {
                dialog.Owner = Application.Current.MainWindow;
            }

            var result = dialog.ShowDialog();
            if (result != true)
            {
                Status = "取消加载模型";
                return;
            }

            _loadedModel = dialog.SelectedModel;
            if (_loadedModel == null)
            {
                Status = "未选择模型";
                return;
            }

            // 诊断日志：输出选中模型的完整信息
            System.Diagnostics.Debug.WriteLine($"[LoadModel] ====== 模型加载开始 ======");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 选中模型名称: {_loadedModel.Name}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 模型文件路径: {_loadedModel.ModelPath}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 文件是否存在: {File.Exists(_loadedModel.ModelPath)}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 模型类型: {_loadedModel.Type}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 使用GPU: {_loadedModel.UseGpu}, GPU ID: {_loadedModel.GpuId}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 置信度阈值: {_loadedModel.ConfidenceThreshold}, IoU阈值: {_loadedModel.IouThreshold}");
            System.Diagnostics.Debug.WriteLine($"[LoadModel] 平滑参数: EMA={_loadedModel.SmoothEma:F2}, Confirm={_loadedModel.SmoothConfirmHits}, MaxMissed={_loadedModel.SmoothMaxMissed}, Iou={_loadedModel.SmoothIouThreshold:F2}");

            // 初始化检测服务
            if (await _detectionService.InitializeAsync(_loadedModel))
            {
                IsModelLoaded = true;
                LoadedModelName = _loadedModel.Name;

                // 根据当前模型配置的平滑参数重建实时检测平滑器
                _realtimeSmoother = new DetectionTrackSmoother(
                    iouThreshold: _loadedModel.SmoothIouThreshold,
                    ema: _loadedModel.SmoothEma,
                    confirmHits: _loadedModel.SmoothConfirmHits,
                    maxMissed: _loadedModel.SmoothMaxMissed);

                var actualClasses = _detectionService.GetClasses();
                Status = $"模型加载成功: {_loadedModel.Name} | 路径: {_loadedModel.ModelPath} | 实际类别数: {actualClasses.Count}";
                System.Diagnostics.Debug.WriteLine($"[LoadModel] 初始化成功! 实际类别数: {actualClasses.Count}");
                if (actualClasses.Count > 0)
                    System.Diagnostics.Debug.WriteLine($"[LoadModel] 前5个类别: {string.Join(", ", actualClasses.Take(5))}");
            }
            else
            {
                // 修复：加载失败时必须重置 IsModelLoaded，否则会残留上一次成功加载的状态
                IsModelLoaded = false;
                LoadedModelName = "未加载模型";
                Status = $"模型加载失败: {_loadedModel.Name} | 路径: {_loadedModel.ModelPath}";
                System.Diagnostics.Debug.WriteLine($"[LoadModel] 初始化失败! 路径: {_loadedModel.ModelPath}");
            }
        }
        catch (Exception ex)
        {
            Status = $"加载模型失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RunDetectionAsync()
    {
        try
        {
            if (!IsModelLoaded)
            {
                MessageBox.Show("请先加载模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CurrentImage == null)
            {
                MessageBox.Show("请先加载图像", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            Status = "正在检测...";

            // 执行检测
            var result = await _detectionService.DetectAsync(CurrentImage);

            // 保存检测结果
            DetectionResults = result.Objects;
            HighConfidenceCount = result.Objects.Count(o => o.Confidence >= 0.5);

            // 绘制检测结果到图像
            DetectionResultImage = DrawDetectionResults(CurrentImage, result.Objects);

            // 更新显示
            RoiEditorViewModel.CurrentImage = DetectionResultImage;

            Status = $"检测完成，发现 {result.Objects.Count} 个对象，耗时 {result.ProcessingTimeMs:F1}ms";
        }
        catch (Exception ex)
        {
            Status = $"检测错误: {ex.Message}";
            MessageBox.Show($"检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 绘制检测结果到图像
    /// </summary>
    // COCO 17关键点骨骼连接（0-indexed：nose, left_eye, right_eye, left_ear, right_ear,
    // left_shoulder, right_shoulder, left_elbow, right_elbow, left_wrist, right_wrist,
    // left_hip, right_hip, left_knee, right_knee, left_ankle, right_ankle）
    private static readonly int[][] COCO_SKELETON =
    {
        new[] { 15, 13 }, new[] { 13, 11 }, new[] { 16, 14 }, new[] { 14, 12 },
        new[] { 11, 12 }, new[] { 5, 11 }, new[] { 6, 12 }, new[] { 5, 6 },
        new[] { 5, 7 }, new[] { 7, 9 }, new[] { 6, 8 }, new[] { 8, 10 },
        new[] { 1, 3 }, new[] { 0, 2 }, new[] { 0, 1 }, new[] { 2, 4 }, new[] { 3, 5 }
    };
    // 关键点绘制的最低置信度
    private const float POSE_KEYPOINT_CONF = 0.3f;
    // 分割掩膜颜色调色板（按类别索引取色，半透明叠加）
    private static readonly SKColor[] SEG_MASK_COLORS =
    {
        new SKColor(0, 200, 255, 110),   // 青
        new SKColor(255, 120, 0, 110),   // 橙
        new SKColor(0, 220, 120, 110),   // 绿
        new SKColor(255, 80, 160, 110),  // 粉
        new SKColor(180, 120, 255, 110), // 紫
        new SKColor(255, 220, 0, 110),   // 黄
        new SKColor(80, 160, 255, 110),  // 蓝
        new SKColor(255, 80, 80, 110)    // 红
    };

    private SKBitmap DrawDetectionResults(SKBitmap originalImage, List<DetectedObject> objects, float textSize = 24f)
    {
        // 创建副本
        var resultBitmap = originalImage.Copy();
        using var canvas = new SKCanvas(resultBitmap);
        DrawDetectedObjects(canvas, objects, textSize);
        return resultBitmap;
    }

    /// <summary>
    /// 在已有画布上叠加绘制检测结果（边界框 + 标签 + 姿态骨骼 + 分割掩膜）。
    /// <para>提取自 <see cref="DrawDetectionResults"/>，便于在相机最新帧上直接叠加（与 SOP 渲染架构一致），
    /// 避免「无框原始帧」与「有框帧」交替造成的实时检测闪烁。</para>
    /// </summary>
    private void DrawDetectedObjects(SKCanvas canvas, List<DetectedObject> objects, float textSize = 24f)
    {
        foreach (var obj in objects)
        {
            // 根据置信度选择颜色
            var color = obj.Confidence > 0.7 ? SKColors.Green :
                       obj.Confidence > 0.5 ? SKColors.Yellow : SKColors.Red;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = 4,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // 绘制边界框（使用PixelBoundingBox）
            canvas.DrawRect(obj.PixelBoundingBox, paint);

            // 绘制标签背景
            using var textPaint = new SKPaint
            {
                Color = color,
                TextSize = textSize,
                IsAntialias = true
            };

            var label = $"{obj.ClassName} {obj.Confidence:P0}";
            var textBounds = new SKRect();
            textPaint.MeasureText(label, ref textBounds);

            // 绘制标签背景
            using var bgPaint = new SKPaint
            {
                Color = color.WithAlpha(200),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRect(
                obj.PixelBoundingBox.Left,
                obj.PixelBoundingBox.Top - textBounds.Height - 4,
                textBounds.Width + 8,
                textBounds.Height + 4,
                bgPaint);

            // 绘制标签文字
            textPaint.Color = SKColors.White;
            canvas.DrawText(label,
                obj.PixelBoundingBox.Left + 4,
                obj.PixelBoundingBox.Top - 4,
                textPaint);

            // 绘制人体姿态骨骼（仅姿态估计模型会带 KeyPoints）
            if (obj.KeyPoints != null && obj.KeyPoints.Count > 0)
            {
                DrawPoseSkeleton(canvas, obj.KeyPoints);
            }

            // 绘制实例分割掩膜（仅分割模型会带 Mask）
            if (obj.Mask != null)
            {
                var maskColor = SEG_MASK_COLORS[obj.ClassId % SEG_MASK_COLORS.Length];
                // 掩膜按原始检测框尺寸位打包，必须用 MaskBox（而非平滑后的 PixelBoundingBox）绘制，否则错位/变形
                var maskBox = obj.MaskBox ?? obj.PixelBoundingBox;
                DrawSegmentationMask(canvas, obj.Mask,
                    maskBox.Left, maskBox.Top,
                    maskBox.Width, maskBox.Height, maskColor);
            }
        }
    }

    /// <summary>
    /// 在画布上绘制 COCO 姿态关键点与骨骼连线
    /// </summary>
    private void DrawPoseSkeleton(SKCanvas canvas, List<KeyPoint> kps)
    {
        // 按置信度过滤不可靠的关键点
        var valid = kps.Where(k => k.Confidence >= POSE_KEYPOINT_CONF).ToList();
        if (valid.Count == 0) return;

        // 先绘制骨骼连线
        using var linePaint = new SKPaint
        {
            Color = SKColors.Lime,
            StrokeWidth = 3,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        foreach (var pair in COCO_SKELETON)
        {
            var a = valid.FirstOrDefault(k => k.Index == pair[0]);
            var b = valid.FirstOrDefault(k => k.Index == pair[1]);
            if (a != null && b != null)
            {
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, linePaint);
            }
        }

        // 再绘制关键点（黄点 + 黑色描边）
        using var pointPaint = new SKPaint
        {
            Color = SKColors.Yellow,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var pointOutline = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = 1.5f,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        foreach (var k in valid)
        {
            canvas.DrawCircle(k.X, k.Y, 4, pointPaint);
            canvas.DrawCircle(k.X, k.Y, 4, pointOutline);
        }
    }

    /// <summary>
    /// 在画布上叠加实例分割掩膜（半透明着色区域）
    /// </summary>
    private void DrawSegmentationMask(SKCanvas canvas, byte[]? packedMask, int boxX, int boxY, int boxW, int boxH, SKColor color)
    {
        if (packedMask == null || packedMask.Length == 0 || boxW <= 0 || boxH <= 0)
            return;

        // 将位打包掩膜解包为彩色半透明位图，并绘制到边界框位置
        using var maskBmp = UnpackSegmentationMask(packedMask, boxW, boxH, color);
        canvas.DrawBitmap(maskBmp, boxX, boxY);
    }

    /// <summary>
    /// 把 YoloDotNet 的位打包像素掩膜解包为彩色半透明 SKBitmap（尺寸 = 边界框宽高）。
    /// 位序与 YoloDotNet 一致：byteIndex = i&gt;&gt;3, bitIndex = i &amp; 7。
    /// </summary>
    private unsafe SKBitmap UnpackSegmentationMask(byte[] packed, int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        int total = w * h;
        byte* ptr = (byte*)bmp.GetPixels().ToPointer();

        for (int i = 0; i < total; i++)
        {
            int byteIndex = i >> 3;     // i / 8
            int bitIndex = i & 7;       // i % 8
            bool isOn = (packed[byteIndex] & (1 << bitIndex)) != 0;

            int offset = i * 4;
            if (isOn)
            {
                ptr[offset] = color.Blue;
                ptr[offset + 1] = color.Green;
                ptr[offset + 2] = color.Red;
                ptr[offset + 3] = color.Alpha;
            }
            else
            {
                ptr[offset] = 0;
                ptr[offset + 1] = 0;
                ptr[offset + 2] = 0;
                ptr[offset + 3] = 0;
            }
        }
        return bmp;
    }

    [RelayCommand]
    public void ClearDetectionResults()
    {
        DetectionResults.Clear();
        HighConfidenceCount = 0;
        DetectionResultImage = null;
        if (CurrentImage != null)
        {
            RoiEditorViewModel.CurrentImage = CurrentImage;
        }
        Status = "检测结果已清除";
    }

    [RelayCommand]
    public void CopyDetectionResults()
    {
        if (DetectionResults.Count == 0)
        {
            Status = "没有检测结果可复制";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("类别\t置信度\t位置X\t位置Y\t宽度\t高度");
        foreach (var obj in DetectionResults)
        {
            sb.AppendLine($"{obj.ClassName}\t{obj.Confidence:P2}\t{obj.BoundingBox[0]:F1}\t{obj.BoundingBox[1]:F1}\t{obj.BoundingBox[2]:F1}\t{obj.BoundingBox[3]:F1}");
        }
        Clipboard.SetText(sb.ToString());
        Status = $"已复制 {DetectionResults.Count} 条检测结果到剪贴板";
    }

    [RelayCommand]
    public void ExportDetectionResults()
    {
        if (DetectionResults.Count == 0)
        {
            Status = "没有检测结果可导出";
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Title = "导出检测结果",
            Filter = "CSV文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt",
            FileName = $"检测结果_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("类别,置信度,位置X,位置Y,宽度,高度");
                foreach (var obj in DetectionResults)
                {
                    sb.AppendLine($"{obj.ClassName},{obj.Confidence:F4},{obj.BoundingBox[0]:F2},{obj.BoundingBox[1]:F2},{obj.BoundingBox[2]:F2},{obj.BoundingBox[3]:F2}");
                }
                File.WriteAllText(saveFileDialog.FileName, sb.ToString());
                Status = $"检测结果已导出到: {saveFileDialog.FileName}";
            }
            catch (Exception ex)
            {
                Status = $"导出失败: {ex.Message}";
            }
        }
    }

    #endregion

    #region 视频推理功能

    [RelayCommand]
    public async Task LoadVideoAsync()
    {
        try
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "选择视频文件",
                Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv|所有文件|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                VideoPath = openFileDialog.FileName;
                Status = $"正在加载视频: {Path.GetFileName(VideoPath)}...";

                // 加载并显示视频第一帧
                await Task.Run(() =>
                {
                    try
                    {
                        // 使用FFmpeg或视频库提取第一帧
                        var videoCapture = new OpenCvSharp.VideoCapture(VideoPath);
                        if (videoCapture.IsOpened())
                        {
                            using var frame = new OpenCvSharp.Mat();
                            if (videoCapture.Read(frame))
                            {
                                // 转换OpenCV Mat为SKBitmap
                                var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(frame);
                                using var ms = new MemoryStream();
                                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                ms.Position = 0;
                                
                                var skBitmap = SKBitmap.Decode(ms);
                                
                                // 在UI线程更新图像
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    CurrentImage = skBitmap;
                                    RoiEditorViewModel.CurrentImage = skBitmap;
                                    Status = $"视频已加载: {Path.GetFileName(VideoPath)}";
                                });
                            }
                            videoCapture.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Status = $"加载视频预览失败: {ex.Message}";
                        });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Status = $"加载视频失败: {ex.Message}";
            MessageBox.Show($"加载视频失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public async Task StartVideoInferenceAsync()
    {
        try
        {
            if (!IsModelLoaded)
            {
                MessageBox.Show("请先加载模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(VideoPath))
            {
                MessageBox.Show("请先加载视频", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            IsVideoPlaying = true;
            Status = "正在初始化视频推理...";

            // 初始化视频推理
            var options = new VideoInferenceOptions
            {
                VideoPath = VideoPath,
                FrameInterval = 0,  // 处理所有帧
                StartTimeSeconds = 0,
                DurationSeconds = 0
            };

            // 清空之前的错误信息
            _lastDetectionError = "";

            if (!_detectionService.InitializeVideoInference(options))
            {
                IsVideoPlaying = false;
                
                // 检查是否是FFmpeg未安装的问题
                if (_lastDetectionError.Contains("FFmpeg"))
                {
                    Status = "FFmpeg未安装";
                    var result = MessageBox.Show(
                        "视频推理需要FFmpeg支持。\n\n" +
                        "FFmpeg未安装或未添加到系统PATH。\n\n" +
                        "是否打开FFmpeg下载页面？",
                        "缺少FFmpeg",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        // 打开FFmpeg下载页面
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "https://ffmpeg.org/download.html",
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    Status = $"视频推理初始化失败: {_lastDetectionError}";
                    MessageBox.Show($"视频推理初始化失败:\n{_lastDetectionError}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            // 订阅视频帧检测事件
            _detectionService.VideoFrameDetected += OnVideoFrameDetected;
            _detectionService.VideoInferenceCompleted += OnVideoInferenceCompleted;

            Status = "开始视频推理...";
            _detectionService.StartVideoInference();
        }
        catch (Exception ex)
        {
            Status = $"视频推理错误: {ex.Message}";
            MessageBox.Show($"视频推理失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            IsVideoPlaying = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void StopVideoInference()
    {
        try
        {
            _detectionService.StopVideoInference();
            _detectionService.VideoFrameDetected -= OnVideoFrameDetected;
            _detectionService.VideoInferenceCompleted -= OnVideoInferenceCompleted;
            IsVideoPlaying = false;
            Status = "视频推理已停止";
        }
        catch (Exception ex)
        {
            Status = $"停止视频推理失败: {ex.Message}";
        }
    }

    private void OnVideoFrameDetected(object? sender, VideoFrameResult e)
    {
        // 在UI线程更新
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentFrameIndex = e.FrameIndex;
            DetectionResults = e.DetectionResult.Objects;
            HighConfidenceCount = e.DetectionResult.Objects.Count(o => o.Confidence >= 0.5);

            // 计算FPS
            _frameCount++;
            var now = DateTime.Now;
            var elapsed = now - _lastFrameTime;
            if (elapsed.TotalSeconds >= 1)
            {
                Fps = Math.Round(_frameCount / elapsed.TotalSeconds, 1);
                _frameCount = 0;
                _lastFrameTime = now;
            }

            // 绘制检测结果
            if (e.DetectionResult.Objects.Count > 0)
            {
                var resultImage = DrawDetectionResults(e.Frame, e.DetectionResult.Objects);
                RoiEditorViewModel.CurrentImage = resultImage;
            }
            else
            {
                RoiEditorViewModel.CurrentImage = e.Frame;
            }

            Status = $"处理帧 {e.FrameIndex}，检测到 {e.DetectionResult.Objects.Count} 个对象，FPS: {Fps:F1}";
        });
    }

    private void OnVideoInferenceCompleted(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsVideoPlaying = false;
            Status = "视频推理完成";
            _detectionService.VideoFrameDetected -= OnVideoFrameDetected;
            _detectionService.VideoInferenceCompleted -= OnVideoInferenceCompleted;
        });
    }

    #endregion

    #region 实时相机检测

    /// <summary>
    /// 启动实时相机检测
    /// </summary>
    [RelayCommand]
    public void StartRealTimeDetection()
    {
        try
        {
            if (!IsModelLoaded)
            {
                MessageBox.Show("请先加载模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_cameraManager.IsConnected)
            {
                MessageBox.Show("请先连接相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_cameraManager.IsGrabbing)
            {
                MessageBox.Show("请先开始相机采集", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsRealTimeDetecting = true;
            _inferenceFrameCount = 0;
            _lastInferenceTime = DateTime.Now;
            Status = "实时检测已启动";
        }
        catch (Exception ex)
        {
            Status = $"启动实时检测失败: {ex.Message}";
            MessageBox.Show($"启动实时检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 停止实时相机检测
    /// </summary>
    [RelayCommand]
    public void StopRealTimeDetection()
    {
        try
        {
            IsRealTimeDetecting = false;
            _realtimeSmoother.Clear();
            _lastRealtimeObjects = null;
            _noRealtimeResultFrameCount = 0;
            InferenceFps = 0;
            Status = "实时检测已停止";
        }
        catch (Exception ex)
        {
            Status = $"停止实时检测失败: {ex.Message}";
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// 释放资源 - 安全关闭相机和清理资源
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            
            // 取消订阅相机/检测/SOP 事件，避免关闭过程中收到回调
            _cameraManager.ImageGrabbed -= OnCameraImageGrabbed;
            _cameraManager.ConnectionStatusChanged -= OnCameraConnectionStatusChanged;
            _detectionService.DetectionError -= OnDetectionError;
            if (_sopModule != null)
            {
                _sopModule.StepChanged -= OnSOPStepChanged;
                _sopModule.ViolationDetected -= OnSOPViolationDetected;
                _sopModule.WorkflowCompleted -= OnSOPWorkflowCompleted;
            }

            // ⭐ 先停止相机抓取，避免持续产生新帧
            try { SafeShutdownCamera(); } catch (Exception) { }

            // ⭐ 再停止后台推理任务，并等待其真正结束，避免原生会话被释放后仍被线程访问
            try
            {
                _inferenceCts?.Cancel();
                _inferenceQueue?.Writer.TryComplete();
                _inferenceWorkerTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 忽略等待/取消异常
            }

            // 释放 SOP 模块（内部 StopWorkflow 并释放 _yolo / 手部服务），此时推理线程已停止
            if (_sopModule != null)
            {
                try { _sopModule.Dispose(); } catch (Exception) { }
                _sopModule = null;
            }

            // 释放图像资源
            CurrentImage?.Dispose();
            DetectionResultImage?.Dispose();

            // 释放检测服务（YOLO 推理会话）
            try { _detectionService?.Dispose(); } catch (Exception) { }
        }
    }
    
    /// <summary>
    /// 安全关闭相机 - 确保相机正确停止并断开连接
    /// </summary>
    private void SafeShutdownCamera()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("开始安全关闭相机...");
            
            // 使用CameraManager的Shutdown方法进行完整关闭
            _cameraManager.Shutdown();
            
            System.Diagnostics.Debug.WriteLine("相机已安全关闭");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"安全关闭相机时发生异常: {ex.Message}");
        }
    }

    #endregion
}
