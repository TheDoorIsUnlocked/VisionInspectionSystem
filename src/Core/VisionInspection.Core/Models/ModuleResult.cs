namespace VisionInspection.Core.Models;

/// <summary>
/// 模块检测结果的基类
/// </summary>
public abstract class ModuleResult
{
    /// <summary>
    /// 模块名称
    /// </summary>
    public string ModuleName { get; set; } = "";

    /// <summary>
    /// 是否成功完成检测
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// 错误信息（Success=false时）
    /// </summary>
    public string ErrorMessage { get; set; } = "";

    /// <summary>
    /// 检测耗时（毫秒）
    /// </summary>
    public long ElapsedMs { get; set; }

    /// <summary>
    /// 综合判定级别
    /// </summary>
    public DefectLevel Level { get; set; } = DefectLevel.Good;

    /// <summary>
    /// 详细结果数据（会被序列化保存）
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// 判定级别
/// </summary>
public enum DefectLevel
{
    Good = 0,
    Minor = 1,
    Major = 2,
    Critical = 3
}

/// <summary>
/// 捕获帧
/// </summary>
public class CaptureFrame
{
    public string CameraId { get; set; } = "";
    public SKBitmap Image { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public int FrameNumber { get; set; }
}

/// <summary>
/// 可视化叠加层
/// </summary>
public class VisualOverlay
{
    public List<OverlayItem> Items { get; set; } = new();
}

/// <summary>
/// 叠加项
/// </summary>
public class OverlayItem
{
    public OverlayType Type { get; set; }
    public SKRect Bounds { get; set; }
    public SKColor Color { get; set; }
    public string Label { get; set; } = "";
    public float Confidence { get; set; }
}

public enum OverlayType
{
    Rectangle,
    Circle,
    Polygon,
    Text,
    Line
}
