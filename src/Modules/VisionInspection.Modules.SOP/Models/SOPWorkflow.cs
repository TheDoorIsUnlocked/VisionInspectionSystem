namespace VisionInspection.Modules.SOP.Models;

/// <summary>
/// SOP 流程定义（一个完整的作业指导书）
/// </summary>
public class SOPWorkflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<SOPStep> Steps { get; set; } = new();
    public SOPGlobalSettings Settings { get; set; } = new();
    public List<ZoneDefinition> Regions { get; set; } = new();

    // ⭐ 新增：该工作流配置的 YOLO 模型
    public SOPModelConfig? Model { get; set; }

    // ⭐ 新增：区域监控配置（在标定区域内检测"未戴安全帽的人"）
    public SOPMonitoringConfig? Monitoring { get; set; }

    // ⭐ 新增：跨相机协同规则配置（多相机分别检测后按规则组合报警）
    public SOPCrossCameraConfig? CrossCamera { get; set; }
}

/// <summary>
/// SOP 步骤定义
/// </summary>
public class SOPStep
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public string Description { get; set; } = "";
    public int ExpectedDurationSec { get; set; } = 10;
    public int TimeoutSec { get; set; } = 30;
    public int Order { get; set; }
    public List<StepCondition> PassConditions { get; set; } = new();
    public List<ViolationRule> ViolationRules { get; set; } = new();
    public List<int> RequiredPreviousSteps { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();

    /// <summary>
    /// 该步骤使用的相机 ID（如 main_camera / cam_2），缺省主相机。
    /// 状态机评估该步检测条件时使用该相机画面的检测结果。
    /// </summary>
    public string CameraId { get; set; } = "main_camera";

    /// <summary>
    /// 该步骤使用的 YOLO 模型路径；null = 使用全局模型（sop.model.path）。
    /// 支持给每个相机/步骤指定不同模型。
    /// </summary>
    public string? ModelPath { get; set; }
}

/// <summary>
/// 步骤通过条件
/// </summary>
public class StepCondition
{
    public ConditionType Type { get; set; }
    public string TargetObject { get; set; } = "";
    public string? ZoneId { get; set; }
    public float MinConfidence { get; set; } = 0.7f;
    public int StableFrames { get; set; } = 5;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public enum ConditionType
{
    ObjectPresent,
    ObjectInZone,
    ObjectStable,
    ObjectAbsent,
    SequenceComplete,
    TimeElapsed,
    // ===== 手部动作条件（基于 DWPose 21 点手部结果）=====
    HandInRegion,       // 指定手进入某区域
    HandNotInRegion,    // 指定手不在某区域
    HandStable,         // 指定手在区域内稳定 N 帧（用于"放置/保持"确认）
    HandMoveFromTo,     // 指定手从区域 A 移动到区域 B（取料→放料）
    HandNearObject,     // 指定手靠近某目标物体（手-物交互）
    Custom
}

/// <summary>
/// 违规规则
/// </summary>
public class ViolationRule
{
    public ViolationType Type { get; set; }
    public string Description { get; set; } = "";
    public int Severity { get; set; } = 1;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public enum ViolationType
{
    SkipStep,
    WrongOrder,
    Timeout,
    ForbiddenObject,
    ObjectRemoved,
    ZoneIntrusion,
    /// <summary>
    /// 漏放 / 缺料：最终校验时某个必须放置的物料缺失（与 ObjectRemoved 不同——后者是"曾经存在后被移除"）
    /// 该规则为通用的数据驱动规则：每个配方在 YAML 的 required_objects 中声明自己的必放物料，
    /// 切换产品即加载对应 YAML，同一引擎自动复用，无需改代码。
    /// </summary>
    MissingRequiredObject,
    Custom
}

/// <summary>
/// SOP 全局设置
/// </summary>
public class SOPGlobalSettings
{
    public bool EnableSkipDetection { get; set; } = true;
    public bool EnableTimeoutDetection { get; set; } = true;
    public int StableFrameCount { get; set; } = 5;
    public float PositionTolerance { get; set; } = 20f;
    public bool AutoResetOnComplete { get; set; } = true;
    public int ResetDelaySec { get; set; } = 1;
}

/// <summary>
/// 区域定义
/// </summary>
public class ZoneDefinition
{
    public string ZoneId { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    /// <summary>区域所属相机 ID（缺省主相机；跨相机规则按 (CameraId, ZoneId) 匹配）</summary>
    public string CameraId { get; set; } = "main_camera";
}

/// <summary>
/// 跨相机协同规则配置：多相机分别检测后，按条件组合（全部满足）触发报警。
/// 由 YAML 的 sop.cross_camera 节点解析而来。
/// </summary>
public class SOPCrossCameraConfig
{
    /// <summary>是否启用跨相机协同规则</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>报警冷却时间（秒），防止每帧连续播放 ng.wav，默认 5 秒</summary>
    public double CooldownSeconds { get; set; } = 5;

    public List<SOPCrossCameraRule> Rules { get; set; } = new();
}

/// <summary>
/// 跨相机规则：一组条件全部满足即命中。
/// 例如：相机1区域1有人 且 相机2区域2有人 → 报警。
/// </summary>
public class SOPCrossCameraRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Severity { get; set; } = 1;
    public List<SOPCrossCameraCondition> Conditions { get; set; } = new();
}

/// <summary>
/// 跨相机规则条件：指定相机在指定区域内出现/不出现某类别。
/// </summary>
public class SOPCrossCameraCondition
{
    /// <summary>相机 ID（如 main_camera / cam_2），detections 按此取</summary>
    public string CameraId { get; set; } = "main_camera";

    /// <summary>区域 ID（与 regions 中的 ZoneId 对应）</summary>
    public string RegionId { get; set; } = "";

    /// <summary>目标类别名（与模型 classes 一致，如 person / cup / cell phone）</summary>
    public string ObjectClass { get; set; } = "";

    /// <summary>true=该类别出现在区域内触发；false=该类别未出现在区域内触发</summary>
    public bool Present { get; set; } = true;

    /// <summary>检测置信度阈值（0-1），默认 0.5</summary>
    public float MinConfidence { get; set; } = 0.5f;
}

/// <summary>
/// SOP 检测模型配置
/// 定义该工作流/产品使用哪一个 YOLO 模型
/// </summary>
public class SOPModelConfig
{
    /// <summary>
    /// ONNX 模型文件路径
    /// </summary>
    public string Path { get; set; } = "yolo_models/sop_yolov8n.onnx";

    /// <summary>
    /// 模型类型标识
    /// </summary>
    public string Type { get; set; } = "ObjectDetection";

    /// <summary>
    /// 检测置信度阈值 (0-1)
    /// </summary>
    public float Confidence { get; set; } = 0.6f;

    /// <summary>
    /// NMS IoU 阈值 (0-1)
    /// </summary>
    public float Iou { get; set; } = 0.45f;

    /// <summary>
    /// 是否使用 GPU 加速
    /// </summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// GPU 设备 ID
    /// </summary>
    public int GpuId { get; set; } = 0;

    /// <summary>
    /// 该模型可检测的类别名称列表
    /// 用于 SOP 条件评估时做类别名称校验
    /// </summary>
    public List<string> Classes { get; set; } = new();
}

/// <summary>
/// 区域监控配置：在标定的 ROI 区域内，检测是否出现"未戴安全帽的人"，一旦出现播放 ng.wav。
/// 由 YAML 的 sop.monitoring 节点解析而来。
/// </summary>
public class SOPMonitoringConfig
{
    /// <summary>是否启用区域监控</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 人体类别名（与模型 classes 一致），如 ["person"]。
    /// 判定标准：人体框中心落入标定区域内即视为"该区域出现人"。
    /// </summary>
    public List<string> PersonClasses { get; set; } = new() { "person" };

    /// <summary>
    /// 安全帽类别名（与模型 classes 一致），如 ["helmet", "safe_helmet", "hard_hat"]。
    /// 只要任一安全帽框与人体头部区域（人体框上部 35%）相交，即视为"戴了安全帽"。
    /// </summary>
    public List<string> HelmetClasses { get; set; } = new() { "helmet", "safe_helmet", "hard_hat" };

    /// <summary>
    /// 需要监控的区域 ID 列表；为空表示监控全部标定区域。
    /// </summary>
    public List<string>? Regions { get; set; }

    /// <summary>检测置信度阈值（0-1），默认 0.5</summary>
    public float MinConfidence { get; set; } = 0.5f;

    /// <summary>报警冷却时间（秒），防止每帧连续播放 ng.wav 造成噪音，默认 5 秒</summary>
    public double CooldownSeconds { get; set; } = 5;
}
