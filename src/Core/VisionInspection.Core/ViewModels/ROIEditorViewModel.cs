using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;

namespace VisionInspection.Core.ViewModels;

public partial class ROIEditorViewModel : ViewModelBase
{
    private readonly ROIManager _roiManager;

    [ObservableProperty]
    private ROI? _selectedROI;

    [ObservableProperty]
    private ROIShapeType _currentShapeType = ROIShapeType.Rectangle;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private SKPoint _startPoint;

    [ObservableProperty]
    private SKPoint _endPoint;

    [ObservableProperty]
    private SKBitmap? _currentImage;

    [ObservableProperty]
    private float _imageScale = 1.0f;

    [ObservableProperty]
    private bool _isCreatingROI = false;

    public IReadOnlyList<ROI> ROIs => _roiManager.ROIs;

    public ROIEditorViewModel(ROIManager roiManager)
    {
        _roiManager = roiManager;
        _roiManager.ROISelected += OnROISelected;
        _roiManager.ROIChanged += OnROIChanged;
    }

    private void OnROISelected(object? sender, ROISelectedEventArgs e)
    {
        SelectedROI = e.ROI;
    }

    private void OnROIChanged(object? sender, ROIChangedEventArgs e)
    {
        // 触发ROIs属性变更通知
        OnPropertyChanged(nameof(ROIs));
    }

    [RelayCommand]
    public void StartDrawing(SKPoint point)
    {
        IsEditing = true;
        StartPoint = point;
        EndPoint = point;
    }

    [RelayCommand]
    public void UpdateDrawing(SKPoint point)
    {
        if (IsEditing)
        {
            EndPoint = point;
        }
    }

    [RelayCommand]
    public void EndDrawing()
    {
        if (IsEditing && StartPoint != EndPoint)
        {
            ROI newROI = CurrentShapeType switch
            {
                ROIShapeType.Rectangle => CreateRectangleROI(),
                ROIShapeType.Circle => CreateCircleROI(),
                _ => CreateRectangleROI()
            };

            _roiManager.AddROI(newROI);
            _roiManager.SelectROI(newROI.ROIId);
        }

        IsEditing = false;
    }

    private RectangleROI CreateRectangleROI()
    {
        var left = Math.Min(StartPoint.X, EndPoint.X);
        var top = Math.Min(StartPoint.Y, EndPoint.Y);
        var right = Math.Max(StartPoint.X, EndPoint.X);
        var bottom = Math.Max(StartPoint.Y, EndPoint.Y);

        return new RectangleROI
        {
            ROIName = $"矩形ROI_{ROIs.Count + 1}",
            Rect = new SKRectI((int)left, (int)top, (int)right, (int)bottom)
        };
    }

    private CircleROI CreateCircleROI()
    {
        var center = new SKPoint(
            (StartPoint.X + EndPoint.X) / 2,
            (StartPoint.Y + EndPoint.Y) / 2
        );

        var radius = (float)Math.Sqrt(
            Math.Pow(EndPoint.X - StartPoint.X, 2) +
            Math.Pow(EndPoint.Y - StartPoint.Y, 2)
        ) / 2;

        return new CircleROI
        {
            ROIName = $"圆形ROI_{ROIs.Count + 1}",
            Center = center,
            Radius = radius
        };
    }

    [RelayCommand]
    public void DeleteSelectedROI()
    {
        if (SelectedROI != null)
        {
            _roiManager.RemoveROI(SelectedROI.ROIId);
        }
    }

    [RelayCommand]
    public void ClearAllROIs()
    {
        _roiManager.Clear();
    }

    [RelayCommand]
    public void SetShapeType(string shapeType)
    {
        if (Enum.TryParse<ROIShapeType>(shapeType, true, out var type))
        {
            CurrentShapeType = type;
        }
    }

    [RelayCommand]
    public void StartCreatingROI()
    {
        IsCreatingROI = true;
    }

    [RelayCommand]
    public void CancelCreatingROI()
    {
        IsCreatingROI = false;
        IsEditing = false;
    }
}
