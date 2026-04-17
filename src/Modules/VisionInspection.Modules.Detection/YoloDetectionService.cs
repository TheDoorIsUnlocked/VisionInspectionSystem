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
                        // 释放之前的实例
                        _yolo?.Dispose();
                        _yolo = null;

                        _modelInfo = modelInfo;

                        // 检查模型文件是否存在
                        if (!File.Exists(modelInfo.ModelPath))
                        {
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

                        _yolo = new Yolo(options);

                        // 更新模型信息中的类别列表
                        if (_yolo.OnnxModel?.Labels != null)
                        {
                            _modelInfo.Classes = _yolo.OnnxModel.Labels.Select(l => l.Name).ToList();
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
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
                ApplyROIFilter(detectedObjects, rois);
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
            foreach (var obj in objects)
            {
                obj.IsInRoi = false;
                obj.RoiId = null;

                var objCenterX = obj.PixelBoundingBox.MidX;
                var objCenterY = obj.PixelBoundingBox.MidY;

                foreach (var roi in rois)
                {
                    if (IsPointInROI(objCenterX, objCenterY, roi))
                    {
                        obj.IsInRoi = true;
                        obj.RoiId = roi.Id;
                        break;
                    }
                }
            }
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

            // 检查FFmpeg是否已安装
            if (!IsFFmpegInstalled())
            {
                DetectionError?.Invoke(this, "FFmpeg未安装或未添加到系统PATH。请安装FFmpeg并确保ffmpeg.exe和ffprobe.exe在系统PATH中。");
                return false;
            }

            try
            {
                _videoOptions = options;

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
        /// 检查FFmpeg是否已安装
        /// </summary>
        private bool IsFFmpegInstalled()
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
                    // 找到了，检查能否运行
                    try
                    {
                        using var ffmpegProcess = new System.Diagnostics.Process();
                        ffmpegProcess.StartInfo.FileName = ffmpegPath;
                        ffmpegProcess.StartInfo.Arguments = "-version";
                        ffmpegProcess.StartInfo.UseShellExecute = false;
                        ffmpegProcess.StartInfo.RedirectStandardOutput = true;
                        ffmpegProcess.StartInfo.CreateNoWindow = true;
                        ffmpegProcess.Start();
                        ffmpegProcess.WaitForExit(2000);

                        if (ffmpegProcess.ExitCode == 0)
                            return true;
                    }
                    catch
                    {
                        // 继续检查其他路径
                    }
                }
            }

            // 最后尝试从PATH环境变量中查找
            try
            {
                // 检查ffmpeg
                using var ffmpegProcess = new System.Diagnostics.Process();
                ffmpegProcess.StartInfo.FileName = "ffmpeg";
                ffmpegProcess.StartInfo.Arguments = "-version";
                ffmpegProcess.StartInfo.UseShellExecute = false;
                ffmpegProcess.StartInfo.RedirectStandardOutput = true;
                ffmpegProcess.StartInfo.CreateNoWindow = true;
                ffmpegProcess.Start();
                ffmpegProcess.WaitForExit(2000);

                if (ffmpegProcess.ExitCode != 0)
                    return false;

                // 检查ffprobe
                using var ffprobeProcess = new System.Diagnostics.Process();
                ffprobeProcess.StartInfo.FileName = "ffprobe";
                ffprobeProcess.StartInfo.Arguments = "-version";
                ffprobeProcess.StartInfo.UseShellExecute = false;
                ffprobeProcess.StartInfo.RedirectStandardOutput = true;
                ffprobeProcess.StartInfo.CreateNoWindow = true;
                ffprobeProcess.Start();
                ffprobeProcess.WaitForExit(2000);

                return ffprobeProcess.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public void StartVideoInference()
        {
            if (_yolo == null || _videoOptions == null)
            {
                DetectionError?.Invoke(this, "视频推理未初始化");
                return;
            }

            _videoInferenceCts = new CancellationTokenSource();

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
            }, _videoInferenceCts.Token);
        }

        public void StopVideoInference()
        {
            _videoInferenceCts?.Cancel();
            _videoInferenceCts?.Dispose();
            _videoInferenceCts = null;
        }

        private void OnVideoFrameReceived(SKBitmap frame, long frameIndex)
        {
            if (_yolo == null || _modelInfo == null) return;

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
