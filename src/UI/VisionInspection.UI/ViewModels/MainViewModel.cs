using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using SkiaSharp;
using System.IO;
using System.Windows;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;
using VisionInspection.Modules.Detection;
using VisionInspection.Modules.SOP;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ROIManager _roiManager;
    private readonly ICameraService _cameraService;
    private readonly YoloDetectionService _detectionService;
    private readonly ModelManager _modelManager;
    private SOPModule? _sopModule;
    private ModelInfo? _loadedModel;

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
    private string _videoPath = "";

    [ObservableProperty]
    private bool _isVideoPlaying = false;

    [ObservableProperty]
    private long _currentFrameIndex = 0;

    [ObservableProperty]
    private long _totalFrames = 0;

    public MainViewModel()
    {
        _roiManager = new ROIManager();
        RoiEditorViewModel = new ROIEditorViewModel(_roiManager);
        _cameraService = new MockCameraService();
        _detectionService = new YoloDetectionService();
        _modelManager = new ModelManager();
    }

    [RelayCommand]
    public async Task InitializeSOPModuleAsync()
    {
        try
        {
            IsBusy = true;
            Status = "初始化SOP模块...";

            // 创建配置
            var config = new ConfigurationBuilder()
                .AddJsonFile("configs/products/sample_product.json")
                .Build();

            // 初始化SOP模块
            _sopModule = new SOPModule();
            await _sopModule.InitializeAsync(config, _cameraService);

            SopStatus = "SOP模块初始化成功";
            Status = "SOP模块初始化完成";
        }
        catch (Exception ex)
        {
            SopStatus = $"SOP模块初始化失败: {ex.Message}";
            Status = $"错误: {ex.Message}";
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
            await _cameraService.ConnectAsync(new CameraInfo { Id = "main_camera", Name = "主相机" });

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
                Status = $"模型加载成功: {_loadedModel.Name}";
                MessageBox.Show($"模型 '{_loadedModel.Name}' 加载成功！\n类型: {_loadedModel.Type}\n类别数: {_loadedModel.Classes.Count}", 
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Status = "模型加载失败";
                MessageBox.Show("模型加载失败，请检查模型文件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Status = $"错误: {ex.Message}";
            MessageBox.Show($"加载模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
        DetectionResultImage = null;
        if (CurrentImage != null)
        {
            RoiEditorViewModel.CurrentImage = CurrentImage;
        }
        Status = "检测结果已清除";
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

            if (!_detectionService.InitializeVideoInference(options))
            {
                Status = "视频推理初始化失败";
                IsVideoPlaying = false;
                
                // 检查是否是FFmpeg未安装的问题
                if (Status.Contains("FFmpeg"))
                {
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
                    MessageBox.Show("视频推理初始化失败，请检查视频文件格式是否支持。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

            Status = $"处理帧 {e.FrameIndex}，检测到 {e.DetectionResult.Objects.Count} 个对象";
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
}
