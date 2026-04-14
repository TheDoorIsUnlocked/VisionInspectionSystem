using System.Collections.ObjectModel;
using VisionInspection.Core.Models;

namespace VisionInspection.Core.Services;

/// <summary>
/// ROI 管理器
/// </summary>
public class ROIManager
{
    private readonly List<ROI> _rois = new();
    private ROI? _selectedROI;

    public IReadOnlyList<ROI> ROIs => _rois.AsReadOnly();
    public ROI? SelectedROI => _selectedROI;

    public event EventHandler<ROIChangedEventArgs>? ROIChanged;
    public event EventHandler<ROISelectedEventArgs>? ROISelected;

    /// <summary>
    /// 添加 ROI
    /// </summary>
    public void AddROI(ROI roi)
    {
        _rois.Add(roi);
        ROIChanged?.Invoke(this, new ROIChangedEventArgs(roi, ROIChangeType.Added));
    }

    /// <summary>
    /// 移除 ROI
    /// </summary>
    public bool RemoveROI(string roiId)
    {
        var roi = _rois.FirstOrDefault(r => r.ROIId == roiId);
        if (roi != null)
        {
            _rois.Remove(roi);
            ROIChanged?.Invoke(this, new ROIChangedEventArgs(roi, ROIChangeType.Removed));
            return true;
        }
        return false;
    }

    /// <summary>
    /// 选择 ROI
    /// </summary>
    public void SelectROI(string? roiId)
    {
        _selectedROI = roiId != null ? _rois.FirstOrDefault(r => r.ROIId == roiId) : null;
        ROISelected?.Invoke(this, new ROISelectedEventArgs(_selectedROI));
    }

    /// <summary>
    /// 根据ID获取 ROI
    /// </summary>
    public ROI? GetROI(string roiId)
    {
        return _rois.FirstOrDefault(r => r.ROIId == roiId);
    }

    /// <summary>
    /// 获取指定类型的所有 ROI
    /// </summary>
    public IEnumerable<ROI> GetROIsByType(ROIShapeType type)
    {
        return _rois.Where(r => r.ShapeType == type);
    }

    /// <summary>
    /// 清除所有 ROI
    /// </summary>
    public void Clear()
    {
        _rois.Clear();
        _selectedROI = null;
        ROIChanged?.Invoke(this, new ROIChangedEventArgs(null!, ROIChangeType.Cleared));
    }

    /// <summary>
    /// 在指定 ROI 区域内进行检测
    /// </summary>
    public SKBitmap ExtractROIImage(SKBitmap image, string roiId)
    {
        var roi = GetROI(roiId);
        if (roi == null)
            throw new ArgumentException($"ROI {roiId} not found");

        return roi.ExtractROI(image);
    }
}

/// <summary>
/// ROI 改变事件参数
/// </summary>
public class ROIChangedEventArgs : EventArgs
{
    public ROI ROI { get; }
    public ROIChangeType ChangeType { get; }

    public ROIChangedEventArgs(ROI roi, ROIChangeType changeType)
    {
        ROI = roi;
        ChangeType = changeType;
    }
}

/// <summary>
/// ROI 选择事件参数
/// </summary>
public class ROISelectedEventArgs : EventArgs
{
    public ROI? ROI { get; }

    public ROISelectedEventArgs(ROI? roi)
    {
        ROI = roi;
    }
}

/// <summary>
/// ROI 改变类型
/// </summary>
public enum ROIChangeType
{
    Added,
    Removed,
    Modified,
    Cleared
}
