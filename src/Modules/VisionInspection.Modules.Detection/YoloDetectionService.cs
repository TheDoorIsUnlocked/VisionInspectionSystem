using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionInspection.Core.Services;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.ExecutionProvider.Cuda;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;
using YoloDotNet.Video;

namespace VisionInspection.Modules.Detection
{
    /// <summary>
    /// YOLO检测服务实现
    /// </summary>
    public class YoloDetectionService : IDetectionService
    {
        private Yolo? _yolo;
        private ModelInfo? _modelInfo;
        private readonly object _lockObject = new();
        private CancellationTokenSource? _videoInferenceCts;
        private VideoInferenceOptions? _videoOptions;
        private bool _isVideoProcessing = false;

        public bool IsInitialized => _yolo != null;

        public event EventHandler<DetectionResult>? DetectionCompleted;
        public event EventHandler<string>? DetectionError;
        public event EventHandler<VideoFrameResult>? VideoFrameDetected;
        public event EventHandler? VideoInferenceCompleted;

        public async Task<bool> InitializeAsync(ModelInfo modelInfo)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (_lockObject)
                    {
                        // 如果正在视频推理，先停止
                        if (_isVideoProcessing)
                        {
                            StopVideoInference();
                            // 等待视频处理完全停止
                            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                            while (_isVideoProcessing && stopwatch.ElapsedMilliseconds < 5000)
                            {
                                Thread.Sleep(100);
                            }
                            // 额外等待确保资源释放
                            Thread.Sleep(500);
                        }

                        // 确保取消所有视频相关的回调
                        _yolo = null;
                        _videoOptions = null;
                        
                        // 强制GC回收，确保非托管资源释放
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        _modelInfo = modelInfo;

                        // 诊断日志
                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] ====== InitializeAsync ======");
                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 模型路径: {modelInfo.ModelPath}");
                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 文件存在: {File.Exists(modelInfo.ModelPath)}");

                        // 检查模型文件是否存在
                        if (!File.Exists(modelInfo.ModelPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 模型文件不存在!");
                            DetectionError?.Invoke(this, $"模型文件不存在: {modelInfo.ModelPath}");
                            return false;
                        }

                        // 创建YOLO实例
                        var options = new YoloOptions
                        {
                            ExecutionProvider = modelInfo.UseGpu
                                ? new CudaExecutionProvider(modelInfo.ModelPath, modelInfo.GpuId)
                                : new CpuExecutionProvider(modelInfo.ModelPath),
                            ImageResize = ImageResize.Proportional,
                            SamplingOptions = new(SKFilterMode.Nearest, SKMipmapMode.None)
                        };

                        System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 执行设备: {(modelInfo.UseGpu ? $"CUDA (GPU {modelInfo.GpuId})" : "CPU")}");

                        _yolo = new Yolo(options);

                        // 更新模型信息中的类别列表
                        if (_yolo.OnnxModel?.Labels != null)
                        {
                            _modelInfo.Classes = _yolo.OnnxModel.Labels.Select(l => l.Name).ToList();
                            System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] Yolo实例创建成功, 标签数: {_modelInfo.Classes.Count}");
                            if (_modelInfo.Classes.Count > 0)
                                System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 前5个标签: {string.Join(", ", _modelInfo.Classes.Take(5))}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] Yolo实例创建成功, 但无标签元数据");
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] InitializeAsync 异常: {ex.GetType().Name}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[YoloDetectionService] 堆栈: {ex.StackTrace}");
                    DetectionError?.Invoke(this, $"初始化YOLO模型失败: {ex.Message}");
                    return false;
                }
            });
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                _yolo?.Dispose();
                _yolo = null;
                _modelInfo = null;
            }
        }

        public async Task<DetectionResult> DetectAsync(SKBitmap image)
        {
            return await DetectAsync(image, new List<ROIInfo>());
        }

        public async Task<DetectionResult> DetectAsync(SKBitmap image, List<ROIInfo> rois)
        {
            if (_yolo == null || _modelInfo == null)
            {
                throw new InvalidOperationException("检测服务未初始化");
            }

            return await Task.Run(() =>
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    List<DetectedObject> detectedObjects = new();

                    // 根据模型类型执行不同的检测
                    switch (_modelInfo.Type)
                    {
                        case Core.Services.ModelType.ObjectDetection:
                            detectedObjects = RunObjectDetection(image, rois);
                            break;
                        case Core.Services.ModelType.Segmentation:
                            detectedObjects = RunSegmentation(image, rois);
                            break;
                        case Core.Services.ModelType.Classification:
                            detectedObjects = RunClassification(image);
                            break;
                        case Core.Services.ModelType.PoseEstimation:
                            detectedObjects = RunPoseEstimation(image, rois);
                            break;
                        default:
                            detectedObjects = RunObjectDetection(image, rois);
                            break;
                    }

                    stopwatch.Stop();

                    var result = new DetectionResult
                    {
                        Objects = detectedObjects,
                        ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                        ImageWidth = image.Width,
                        ImageHeight = image.Height,
                        Timestamp = DateTime.Now
                    };

                    DetectionCompleted?.Invoke(this, result);
                    return result;
                }
                catch (Exception ex)
                {
                    DetectionError?.Invoke(this, $"检测失败: {ex.Message}");
                    throw;
                }
            });
        }

        private List<DetectedObject> RunObjectDetection(SKBitmap image, List<ROIInfo> rois)
        {
            var results = _yolo!.RunObjectDetection(
                image,
                confidence: _modelInfo!.ConfidenceThreshold,
                iou: _modelInfo.IouThreshold);

            var detectedObjects = results.Select(r => new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { (float)r.BoundingBox.Left, (float)r.BoundingBox.Top, (float)r.BoundingBox.Width, (float)r.BoundingBox.Height },
                PixelBoundingBox = r.BoundingBox
            }).ToList();

            // 应用ROI过滤
            if (rois.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"应用ROI过滤前: {detectedObjects.Count} 个对象");
                ApplyROIFilter(detectedObjects, rois);
                System.Diagnostics.Debug.WriteLine($"应用ROI过滤后: {detectedObjects.Count} 个对象");
            }

            return detectedObjects;
        }

        private List<DetectedObject> RunSegmentation(SKBitmap image, List<ROIInfo> rois)
        {
            var results = _yolo!.RunSegmentation(
                image,
                confidence: _modelInfo!.ConfidenceThreshold,
                iou: _modelInfo.IouThreshold);

            var detectedObjects = results.Select(r => new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { (float)r.BoundingBox.Left, (float)r.BoundingBox.Top, (float)r.BoundingBox.Width, (float)r.BoundingBox.Height },
                PixelBoundingBox = r.BoundingBox,
                Mask = r.BitPackedPixelMask
            }).ToList();

            if (rois.Count > 0)
            {
                ApplyROIFilter(detectedObjects, rois);
            }

            return detectedObjects;
        }

        private List<DetectedObject> RunClassification(SKBitmap image)
        {
            var results = _yolo!.RunClassification(image);

            return results.Select(r => new DetectedObject
            {
                ClassId = 0,
                ClassName = r.Label,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { 0f, 0f, 1f, 1f },
                PixelBoundingBox = new SKRectI(0, 0, image.Width, image.Height)
            }).ToList();
        }

        private List<DetectedObject> RunPoseEstimation(SKBitmap image, List<ROIInfo> rois)
        {
            var results = _yolo!.RunPoseEstimation(
                image,
                confidence: _modelInfo!.ConfidenceThreshold,
                iou: _modelInfo.IouThreshold);

            var detectedObjects = results.Select(r => new DetectedObject
            {
                ClassId = r.Label.Index,
                ClassName = r.Label.Name,
                Confidence = (float)r.Confidence,
                BoundingBox = new[] { (float)r.BoundingBox.Left, (float)r.BoundingBox.Top, (float)r.BoundingBox.Width, (float)r.BoundingBox.Height },
                PixelBoundingBox = r.BoundingBox,
                KeyPoints = r.KeyPoints.Select((kp, idx) => new Core.Services.KeyPoint
                {
                    Index = idx,
                    X = kp.X,
                    Y = kp.Y,
                    Confidence = (float)kp.Confidence
                }).ToList()
            }).ToList();

            if (rois.Count > 0)
            {
                ApplyROIFilter(detectedObjects, rois);
            }

            return detectedObjects;
        }

        private void ApplyROIFilter(List<DetectedObject> objects, List<ROIInfo> rois)
        {
            // 过滤掉不在任何ROI内的对象
            objects.RemoveAll(obj =>
            {
                var objCenterX = obj.PixelBoundingBox.MidX;
                var objCenterY = obj.PixelBoundingBox.MidY;
                
                System.Diagnostics.Debug.WriteLine($"检查对象: {obj.ClassName} 中心点({objCenterX:F1}, {objCenterY:F1})");

                foreach (var roi in rois)
                {
                    System.Diagnostics.Debug.WriteLine($"  检查ROI: {roi.Name} 区域({roi.X}, {roi.Y}, {roi.Width}, {roi.Height})");
                    if (IsPointInROI(objCenterX, objCenterY, roi))
                    {
                        obj.IsInRoi = true;
                        obj.RoiId = roi.Id;
                        System.Diagnostics.Debug.WriteLine($"  -> 在ROI内，保留");
                        return false; // 保留此对象
                    }
                }
                System.Diagnostics.Debug.WriteLine($"  -> 不在任何ROI内，移除");
                return true; // 移除此对象
            });
        }

        private bool IsPointInROI(float x, float y, ROIInfo roi)
        {
            switch (roi.ShapeType)
            {
                case ShapeType.Rectangle:
                    return x >= roi.X && x <= roi.X + roi.Width &&
                           y >= roi.Y && y <= roi.Y + roi.Height;

                case ShapeType.Circle:
                    var centerX = roi.X + roi.Width / 2;
                    var centerY = roi.Y + roi.Height / 2;
                    var radius = Math.Min(roi.Width, roi.Height) / 2;
                    var distance = Math.Sqrt(Math.Pow(x - centerX, 2) + Math.Pow(y - centerY, 2));
                    return distance <= radius;

                default:
                    return false;
            }
        }

        public void SetConfidenceThreshold(float threshold)
        {
            if (_modelInfo != null)
            {
                _modelInfo.ConfidenceThreshold = Math.Clamp(threshold, 0.01f, 1.0f);
            }
        }

        public void SetIouThreshold(float threshold)
        {
            if (_modelInfo != null)
            {
                _modelInfo.IouThreshold = Math.Clamp(threshold, 0.01f, 1.0f);
            }
        }

        public List<string> GetClasses()
        {
            return _modelInfo?.Classes ?? new List<string>();
        }

        #region 视频推理

        public bool InitializeVideoInference(VideoInferenceOptions options)
        {
            if (_yolo == null || _modelInfo == null)
            {
                DetectionError?.Invoke(this, "检测服务未初始化");
                return false;
            }

            if (!File.Exists(options.VideoPath))
            {
                DetectionError?.Invoke(this, $"视频文件不存在: {options.VideoPath}");
                return false;
            }

            // 检查FFmpeg是否已安装并获取路径
            var ffmpegPath = GetFFmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                DetectionError?.Invoke(this, "FFmpeg未安装或未添加到系统PATH。请安装FFmpeg并确保ffmpeg.exe和ffprobe.exe在系统PATH中。");
                return false;
            }

            try
            {
                _videoOptions = options;

                // 设置环境变量，确保YoloDotNet能找到FFmpeg
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!currentPath.Contains(ffmpegPath))
                {
                    Environment.SetEnvironmentVariable("PATH", $"{ffmpegPath};{currentPath}");
                }

                // 创建视频选项
                var videoOptions = new VideoOptions
                {
                    VideoInput = options.VideoPath,
                    FrameInterval = options.FrameInterval,
                    Width = 0,  // 使用原始宽度
                    Height = 0  // 使用原始高度
                };

                // 设置可选参数
                if (!string.IsNullOrEmpty(options.OutputPath))
                {
                    videoOptions.VideoOutput = options.OutputPath;
                }

                if (options.StartTimeSeconds > 0)
                {
                    videoOptions.StartTimeSeconds = options.StartTimeSeconds;
                }

                if (options.DurationSeconds > 0)
                {
                    videoOptions.DurationSeconds = options.DurationSeconds;
                }

                // 初始化视频
                _yolo.InitializeVideo(videoOptions);

                // 设置帧接收处理
                _yolo.OnVideoFrameReceived = OnVideoFrameReceived;
                _yolo.OnVideoEnd = OnVideoEnd;

                return true;
            }
            catch (Exception ex)
            {
                DetectionError?.Invoke(this, $"初始化视频推理失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取FFmpeg路径
        /// </summary>
        private string? GetFFmpegPath()
        {
            // 首先检查常见路径
            var commonPaths = new[]
            {
                @"E:\yolo\YoloDotNet-master\VisionInspectionSystem\ffmpeg\bin",
                @"C:\ffmpeg\bin",
                @"C:\Program Files\ffmpeg\bin",
                @"C:\Program Files (x86)\ffmpeg\bin"
            };

            foreach (var path in commonPaths)
            {
                var ffmpegPath = System.IO.Path.Combine(path, "ffmpeg.exe");
                var ffprobePath = System.IO.Path.Combine(path, "ffprobe.exe");

                if (System.IO.File.Exists(ffmpegPath) && System.IO.File.Exists(ffprobePath))
                {
                    return path;
                }
            }

            // 尝试从PATH环境变量中查找
            try
            {
                using var ffmpegProcess = new System.Diagnostics.Process();
                ffmpegProcess.StartInfo.FileName = "ffmpeg";
                ffmpegProcess.StartInfo.Arguments = "-version";
                ffmpegProcess.StartInfo.UseShellExecute = false;
                ffmpegProcess.StartInfo.RedirectStandardOutput = true;
                ffmpegProcess.StartInfo.CreateNoWindow = true;
                ffmpegProcess.Start();
                ffmpegProcess.WaitForExit(2000);

                if (ffmpegProcess.ExitCode == 0)
                {
                    // 从PATH中找到，返回空字符串表示使用系统PATH
                    return "";
                }
            }
            catch
            {
                // 未找到
            }

            return null;
        }

        /// <summary>
        /// 检查FFmpeg是否已安装
        /// </summary>
        private bool IsFFmpegInstalled()
        {
            return !string.IsNullOrEmpty(GetFFmpegPath());
        }

        public void StartVideoInference()
        {
            if (_yolo == null || _videoOptions == null)
            {
                DetectionError?.Invoke(this, "视频推理未初始化");
                return;
            }

            _videoInferenceCts = new CancellationTokenSource();
            _isVideoProcessing = true;

            Task.Run(() =>
            {
                try
                {
                    _yolo.StartVideoProcessing();
                }
                catch (Exception ex)
                {
                    DetectionError?.Invoke(this, $"视频推理失败: {ex.Message}");
                }
                finally
                {
                    _isVideoProcessing = false;
                }
            }, _videoInferenceCts.Token);
        }

        public void StopVideoInference()
        {
            try
            {
                // 先设置标志，让回调知道要停止
                _isVideoProcessing = false;

                // 取消令牌
                _videoInferenceCts?.Cancel();

                // 停止视频处理（这会中断StartVideoProcessing的阻塞调用）
                _yolo?.StopVideoProcessing();

                // 等待一小段时间让处理完全停止
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                DetectionError?.Invoke(this, $"停止视频推理时出错: {ex.Message}");
            }
            finally
            {
                _videoInferenceCts?.Dispose();
                _videoInferenceCts = null;
                _isVideoProcessing = false;
            }
        }

        private void OnVideoFrameReceived(SKBitmap frame, long frameIndex)
        {
            // 如果正在停止或已停止，不再处理帧
            if (!_isVideoProcessing || _yolo == null || _modelInfo == null) return;

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 执行检测
                List<DetectedObject> detectedObjects = _modelInfo.Type switch
                {
                    Core.Services.ModelType.ObjectDetection => RunObjectDetection(frame, new List<ROIInfo>()),
                    Core.Services.ModelType.Segmentation => RunSegmentation(frame, new List<ROIInfo>()),
                    Core.Services.ModelType.Classification => RunClassification(frame),
                    Core.Services.ModelType.PoseEstimation => RunPoseEstimation(frame, new List<ROIInfo>()),
                    _ => RunObjectDetection(frame, new List<ROIInfo>())
                };

                stopwatch.Stop();

                var result = new DetectionResult
                {
                    Objects = detectedObjects,
                    ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    ImageWidth = frame.Width,
                    ImageHeight = frame.Height,
                    Timestamp = DateTime.Now
                };

                var frameResult = new VideoFrameResult
                {
                    FrameIndex = frameIndex,
                    Frame = frame,
                    DetectionResult = result
                };

                VideoFrameDetected?.Invoke(this, frameResult);
                DetectionCompleted?.Invoke(this, result);
            }
            catch (Exception ex)
            {
                DetectionError?.Invoke(this, $"处理帧 {frameIndex} 失败: {ex.Message}");
            }
        }

        private void OnVideoEnd()
        {
            VideoInferenceCompleted?.Invoke(this, EventArgs.Empty);
        }

        #endregion
    }
}
