using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using System.IO;
using VisionInspection.Core.Services;
using VisionInspection.Core.ViewModels;

namespace VisionInspection.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ROIManager _roiManager;

    [ObservableProperty]
    private ROIEditorViewModel _roiEditorViewModel = null!;

    [ObservableProperty]
    private SKBitmap? _currentImage;

    [ObservableProperty]
    private string _status = "就绪";

    public MainViewModel()
    {
        _roiManager = new ROIManager();
        _roiEditorViewModel = new ROIEditorViewModel(_roiManager);
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
            _roiEditorViewModel.CurrentImage = CurrentImage;

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
        _roiEditorViewModel.ClearAllROIsCommand.Execute(null);
        Status = "ROI已清空";
    }
}
