using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using System.IO;
using VisionInspection.Core.Interfaces;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;
using VisionInspection.Modules.SOP;

namespace VisionInspection.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ROIManager _roiManager;
    private readonly CameraManager _cameraManager;
    private SOPModule? _sopModule;

    [ObservableProperty]
    private ROIEditorViewModel _roiEditorViewModel = null!;

    [ObservableProperty]
    private SKBitmap? _currentImage;

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private string _sopStatus = "SOP模块未初始化";

    [ObservableProperty]
    private int _currentStep = 0;

    [ObservableProperty]
    private int _totalSteps = 0;

    public MainViewModel()
    {
        _roiManager = new ROIManager();
        RoiEditorViewModel = new ROIEditorViewModel(_roiManager);
        _cameraManager = new CameraManager();

        // 添加模拟相机
        _cameraManager.AddCamera(new MockCamera("main_camera", "主相机"));
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
            await _sopModule.InitializeAsync(config, _cameraManager);

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
            await _cameraManager.ConnectAllAsync();

            // 捕获图像
            var frames = new Dictionary<string, CaptureFrame>();
            foreach (var camera in _cameraManager.GetAllCameras())
            {
                var image = await camera.CaptureAsync();
                if (image != null)
                {
                    frames[camera.Id] = new CaptureFrame
                    {
                        CameraId = camera.Id,
                        Image = image,
                        Timestamp = DateTime.Now,
                        FrameNumber = 0
                    };
                }
            }

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

            // 这里应该使用OpenFileDialog，为了简化先使用测试图像
            using var stream = new FileStream("test_image.jpg", FileMode.Open, FileAccess.Read);
            CurrentImage = SKBitmap.Decode(stream);
            RoiEditorViewModel.CurrentImage = CurrentImage;

            Status = "图像加载成功";
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
}
