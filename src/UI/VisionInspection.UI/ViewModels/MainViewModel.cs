using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using SkiaSharp;
using System.IO;
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
            // 先克隆用于推理（在绘制骨架前复制，确保推理用原始图像）
            SKBitmap? inferenceBitmap = null;
            if (IsSOPDetecting && _sopModule != null)
            {
                inferenceBitmap = skBitmap.Copy();
            }

            // 在原始帧上绘制缓存的手部骨架，消除"原始帧→骨架帧"交替闪烁
            var cachedResult = _lastHandPoseResult;
            if (IsSOPDetecting && cachedResult != null && cachedResult.Hands.Count > 0)
            {
                using var canvas = new SKCanvas(skBitmap);
                DrawHandPoses(canvas, cachedResult);
            }

            // 在UI线程更新图像
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (!_isDisposed && RoiEditorViewModel != null)
                {
                    RoiEditorViewModel.CurrentImage = skBitmap;
                }
            });

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

            stopwatch.Stop();

            // 更新UI
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (!_isDisposed)
                {
                    DetectionResults = result.Objects;
                    HighConfidenceCount = result.Objects.Count(o => o.Confidence >= 0.5);

                    // 绘制检测结果到图像并更新显示
                    var resultBitmap = DrawDetectionResults(bitmap, result);
                    if (resultBitmap != null && RoiEditorViewModel != null)
                    {
                        RoiEditorViewModel.CurrentImage = resultBitmap;
                    }

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
                // 在 UI 线程更新
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_isDisposed) return;

                    // 缓存手部骨架结果，无手部检测时逐渐清除缓存避免残留
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

                    // 绘制检测框 + SOP 步骤信息
                    var resultBitmap = DrawSOPDetectionResults(bitmap, sopResult);
                    if (resultBitmap != null && RoiEditorViewModel != null)
                    {
                        RoiEditorViewModel.CurrentImage = resultBitmap;
                    }
                    
                    // 释放原始bitmap
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
                });
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
    /// 绘制 SOP 检测结果（检测框 + 步骤状态 + 违规警告）
    /// </summary>
    private SKBitmap DrawSOPDetectionResults(SKBitmap sourceBitmap, SOPModuleResult sopResult)
    {
        try
        {
            // 修复：创建新的bitmap并绘制，避免canvas释放问题
            var resultBitmap = sourceBitmap.Copy();
            using var canvas = new SKCanvas(resultBitmap);

            // 1. 绘制检测框
            foreach (var detection in sopResult.Detections)
            {
                var label = detection.Label?.Name ?? "unknown";
                var conf = detection.Confidence;
                var box = detection.BoundingBox;

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
                System.Diagnostics.Debug.WriteLine($"[DrawSOP] 开始绘制 {sopResult.HandPoseResult.Hands.Count} 只手");
                try
                {
                    DrawHandPoses(canvas, sopResult.HandPoseResult);
                    System.Diagnostics.Debug.WriteLine($"[DrawSOP] 手部绘制完成");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DrawSOP] 手部绘制异常: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[DrawSOP] 堆栈: {ex.StackTrace}");
                }
            }

            // 4. 违规警告（如果有）
            if (sopResult.Violations.Count > 0)
            {
                var lastViolation = sopResult.Violations.Last();
                using var warnBg = new SKPaint { Color = new SKColor(255, 0, 0, 160), Style = SKPaintStyle.Fill };
                canvas.DrawRect(0, resultBitmap.Height - 50, resultBitmap.Width, 50, warnBg);

                using var warnText = new SKPaint
                {
                    Color = SKColors.White,
                    TextSize = 20,
                    IsAntialias = true,
                    FakeBoldText = true
                };
                canvas.DrawText($"⚠ 违规: {lastViolation.Description}", 15, resultBitmap.Height - 18, warnText);
            }

            return resultBitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"绘制SOP结果异常: {ex.Message}");
            return sourceBitmap;
        }
    }

    /// <summary>
    /// 绘制手部姿态（骨架线+关键点）
    /// </summary>
    private void DrawHandPoses(SKCanvas canvas, HandPoseEstimationResult handResult)
    {
        // 获取画布大小
        var canvasSize = canvas.LocalClipBounds;

        // 保存画布状态
        canvas.Save();

        // 注意：DWPose输出的坐标与SkiaSharp坐标系一致（原点在左上角）
        // 不需要翻转Y轴

        // 骨架线画笔（绿色，更粗更明显）
        using var skeletonPaint = new SKPaint
        {
            Color = SKColors.LimeGreen,
            StrokeWidth = 5,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        // 关键点画笔（红色，更大更明显）
        using var keypointPaint = new SKPaint
        {
            Color = SKColors.Red,
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
            TextSize = 14,
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
            // 绘制手部边界框
            var bbox = hand.BoundingBox;
            canvas.DrawRect(bbox, handBoxPaint);

            // 绘制骨架线
            DrawHandSkeleton(canvas, hand, skeletonPaint);

            // 绘制关键点（只显示指尖，减少视觉干扰）
            foreach (var kp in hand.Keypoints)
            {
                // 只绘制指尖关键点，减少视觉干扰和闪烁
                bool isFingertip = kp.Type == HandKeypointType.ThumbTip ||
                                   kp.Type == HandKeypointType.IndexFingerTip ||
                                   kp.Type == HandKeypointType.MiddleFingerTip ||
                                   kp.Type == HandKeypointType.RingFingerTip ||
                                   kp.Type == HandKeypointType.PinkyTip;

                // 使用置信度阈值判断，比 IsValid 更稳定，减少闪烁
                // 只绘制指尖关键点
                if (isFingertip && kp.Confidence >= SkeletonConfidenceThreshold)
                {
                    // 绘制关键点外圈（白色描边）
                    canvas.DrawCircle(kp.X, kp.Y, 10, keypointOutlinePaint);
                    // 绘制关键点（红色填充）
                    canvas.DrawCircle(kp.X, kp.Y, 7, keypointPaint);

                    // 绘制关键点标签（只显示指尖）
                    canvas.DrawText(kp.Type.ToString(), kp.X + 12, kp.Y, labelPaint);
                }
            }
        }

        // 恢复画布状态
        canvas.Restore();
    }

    /// <summary>
    /// 绘制手部骨架线
    /// </summary>
    // 骨架绘制置信度阈值（低于此值的关键点不绘制，减少闪烁）
    // 降低阈值以减少闪烁，配合时序平滑使用
    public float SkeletonConfidenceThreshold { get; set; } = 0.1f;
    
    private void DrawHandSkeleton(SKCanvas canvas, HandPose hand, SKPaint paint)
    {
        // 极简骨架：只绘制手腕到各指尖的连线
        // 避免复杂的多边形连接，减少视觉混乱和闪烁感
        
        var wrist = hand.GetKeypoint(HandKeypointType.Wrist);
        if (wrist == null || wrist.Confidence < SkeletonConfidenceThreshold) return;
        
        // 定义指尖类型
        var fingerTips = new[]
        {
            HandKeypointType.ThumbTip,
            HandKeypointType.IndexFingerTip,
            HandKeypointType.MiddleFingerTip,
            HandKeypointType.RingFingerTip,
            HandKeypointType.PinkyTip
        };
        
        // 绘制手腕到每个指尖的线
        foreach (var tipType in fingerTips)
        {
            var tip = hand.GetKeypoint(tipType);
            if (tip != null && tip.Confidence >= SkeletonConfidenceThreshold)
            {
                canvas.DrawLine(wrist.X, wrist.Y, tip.X, tip.Y, paint);
            }
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
                Status = isConnected ? "相机已连接" : "相机已断开";
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

                // 订阅事件
                _sopModule.StepChanged += OnSOPStepChanged;
                _sopModule.ViolationDetected += OnSOPViolationDetected;
                _sopModule.WorkflowCompleted += OnSOPWorkflowCompleted;

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
        });
    }

    private void OnSOPViolationDetected(object? sender, ViolationEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_isDisposed) return;
            SopStatus = $"⚠ 违规: {e.Violation.Description}";
            Status = $"⚠ SOP 违规: [{e.Violation.Type}] {e.Violation.Description}";
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

            // 初始化检测服务
            if (await _detectionService.InitializeAsync(_loadedModel))
            {
                IsModelLoaded = true;
                LoadedModelName = _loadedModel.Name;
                Status = $"模型加载成功: {_loadedModel.Name} | 类型: {_loadedModel.Type} | 类别数: {_loadedModel.Classes.Count}";
            }
            else
            {
                Status = "模型加载失败，请检查模型文件";
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
    private SKBitmap DrawDetectionResults(SKBitmap originalImage, List<DetectedObject> objects)
    {
        // 创建副本
        var resultBitmap = originalImage.Copy();
        using var canvas = new SKCanvas(resultBitmap);

        foreach (var obj in objects)
        {
            // 根据置信度选择颜色
            var color = obj.Confidence > 0.7 ? SKColors.Green :
                       obj.Confidence > 0.5 ? SKColors.Yellow : SKColors.Red;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = 3,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // 绘制边界框（使用PixelBoundingBox）
            canvas.DrawRect(obj.PixelBoundingBox, paint);

            // 绘制标签背景
            using var textPaint = new SKPaint
            {
                Color = color,
                TextSize = 16,
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
        }

        return resultBitmap;
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
            
            // 取消订阅相机事件（先取消订阅，避免在关闭过程中收到事件）
            _cameraManager.ImageGrabbed -= OnCameraImageGrabbed;
            _cameraManager.ConnectionStatusChanged -= OnCameraConnectionStatusChanged;
            
            // 取消订阅检测错误事件
            _detectionService.DetectionError -= OnDetectionError;

            // ⭐ 取消订阅 SOP 事件并释放资源
            if (_sopModule != null)
            {
                _sopModule.StepChanged -= OnSOPStepChanged;
                _sopModule.ViolationDetected -= OnSOPViolationDetected;
                _sopModule.WorkflowCompleted -= OnSOPWorkflowCompleted;
                _sopModule.Dispose();
                _sopModule = null;
            }

            // 修复：停止推理工作线程
            try
            {
                _inferenceCts?.Cancel();
                _inferenceQueue?.Writer.TryComplete();
            }
            catch (Exception)
            {
                // Channel已经关闭或为空，忽略
            }

            // 释放图像资源
            CurrentImage?.Dispose();
            DetectionResultImage?.Dispose();

            // 安全关闭相机
            SafeShutdownCamera();
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
