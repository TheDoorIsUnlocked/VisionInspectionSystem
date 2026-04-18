using System.Text.Json.Serialization;

namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// 区域配置文件模型（JSON格式）
/// </summary>
public class RegionConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("imageSize")]
    public ImageSize ImageSize { get; set; } = new();

    [JsonPropertyName("regions")]
    public Dictionary<string, RegionDefinition> Regions { get; set; } = new();

    [JsonPropertyName("keypoints")]
    public Dictionary<string, KeypointDefinition> Keypoints { get; set; } = new();

    [JsonPropertyName("rules")]
    public Dictionary<string, ActionRule> Rules { get; set; } = new();

    [JsonPropertyName("forbiddenZones")]
    public Dictionary<string, ForbiddenZone> ForbiddenZones { get; set; } = new();

    [JsonPropertyName("calibration")]
    public CalibrationInfo Calibration { get; set; } = new();
}

/// <summary>
/// 图像尺寸
/// </summary>
public class ImageSize
{
    [JsonPropertyName("width")]
    public int Width { get; set; } = 1280;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 720;
}

/// <summary>
/// 区域定义
/// </summary>
public class RegionDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "rectangle";

    [JsonPropertyName("coordinates")]
    public RegionCoordinates Coordinates { get; set; } = new();

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#4CAF50";

    [JsonPropertyName("allowedActions")]
    public List<string> AllowedActions { get; set; } = new();
}

/// <summary>
/// 区域坐标
/// </summary>
public class RegionCoordinates
{
    [JsonPropertyName("x1")]
    public float X1 { get; set; }

    [JsonPropertyName("y1")]
    public float Y1 { get; set; }

    [JsonPropertyName("x2")]
    public float X2 { get; set; }

    [JsonPropertyName("y2")]
    public float Y2 { get; set; }

    /// <summary>
    /// 转换为SKRect
    /// </summary>
    public SkiaSharp.SKRect ToSKRect()
    {
        return new SkiaSharp.SKRect(X1, Y1, X2, Y2);
    }
}

/// <summary>
/// 关键点定义
/// </summary>
public class KeypointDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("keypointType")]
    public string KeypointType { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// 动作规则
/// </summary>
public class ActionRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("conditions")]
    public List<RuleCondition> Conditions { get; set; } = new();
}

/// <summary>
/// 规则条件
/// </summary>
public class RuleCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("hand")]
    public string? Hand { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>
/// 禁区定义
/// </summary>
public class ForbiddenZone
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("coordinates")]
    public RegionCoordinates Coordinates { get; set; } = new();

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "warning";
}

/// <summary>
/// 校准信息
/// </summary>
public class CalibrationInfo
{
    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("adjustmentGuide")]
    public string AdjustmentGuide { get; set; } = "";
}
