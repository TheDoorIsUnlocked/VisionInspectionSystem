namespace VisionInspection.Modules.SOP;

public class SOPConfig
{
    /// <summary>
    /// YOLO模型路径
    /// </summary>
    public string ModelPath { get; set; } = "models/sop_yolov8.onnx";

    /// <summary>
    /// 是否使用GPU
    /// </summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// 置信度阈值
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.6f;

    /// <summary>
    /// NMS阈值
    /// </summary>
    public float IouThreshold { get; set; } = 0.45f;

    /// <summary>
    /// 检测步骤列表
    /// </summary>
    public List<SOPStepConfig> Steps { get; set; } = new();

    /// <summary>
    /// 全局禁区配置
    /// </summary>
    public List<ForbiddenZoneConfig> ForbiddenZones { get; set; } = new();

    /// <summary>
    /// 步骤通过判定参数
    /// </summary>
    public StepValidationParams ValidationParams { get; set; } = new();
}

public class SOPStepConfig
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// 步骤通过条件（多条件组合）
    /// </summary>
    public List<StepCondition> Conditions { get; set; } = new();

    /// <summary>
    /// 步骤超时时间（秒）
    /// </summary>
    public int TimeoutSec { get; set; } = 30;

    /// <summary>
    /// 禁止出现的对象（违规检测）
    /// </summary>
    public List<string> ForbiddenObjects { get; set; } = new();

    /// <summary>
    /// 必须保持的对象（防止已装零件被移动）
    /// </summary>
    public List<string> MustKeepObjects { get; set; } = new();
}

public class StepCondition
{
    /// <summary>
    /// 条件类型: object_present / object_in_zone / object_stable
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// 目标YOLO类别
    /// </summary>
    public string ObjectClass { get; set; } = "";

    /// <summary>
    /// 区域ID（object_in_zone时使用）
    /// </summary>
    public string ZoneId { get; set; } = "";

    /// <summary>
    /// 稳定时间（秒，object_stable时使用）
    /// </summary>
    public float StableDurationSec { get; set; } = 2.0f;

    /// <summary>
    /// 最小置信度
    /// </summary>
    public float MinConfidence { get; set; } = 0.7f;
}

public class ForbiddenZoneConfig
{
    public string ZoneId { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public string CameraId { get; set; } = "";

    /// <summary>
    /// 区域坐标 [x, y, width, height]（归一化0-1或像素）
    /// </summary>
    public float[] Coordinates { get; set; } = Array.Empty<float>();

    /// <summary>
    /// 禁止的对象类别
    /// </summary>
    public List<string> ForbiddenClasses { get; set; } = new() { "hand" };
}

public class StepValidationParams
{
    /// <summary>
    /// 对象稳定判定：连续多少帧检测到算稳定
    /// </summary>
    public int StableFrameCount { get; set; } = 10;

    /// <summary>
    /// 对象稳定判定：位置漂移容差（像素）
    /// </summary>
    public float PositionTolerance { get; set; } = 20f;

    /// <summary>
    /// 跳步检测：是否启用
    /// </summary>
    public bool EnableSkipDetection { get; set; } = true;
}
